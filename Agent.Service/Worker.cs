using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using Newtonsoft.Json;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Service;

/// <summary>
/// Named pipe message types for Service ↔ Overlay communication.
/// </summary>
internal enum PipeMessageType
{
    // Service → Overlay
    StateUpdate = 1,        // SessionState changed (Locked/Unlocked, sisaDetik, sessionId)
    SessionStarted = 2,     // Session started (durasiDetik)
    SessionStopped = 3,     // Session stopped (alasan)
    SessionTick = 4,        // Countdown tick (sisaDetik)
    LoginResult = 5,        // Login result (sukses, alasan)
    PinVerifyResult = 6,    // PIN verification result (sukses)

    // Overlay → Service
    LoginRequest = 100,     // Login request (kode, password)
    PinVerifyRequest = 101, // PIN verify request (pin)
    StateRequest = 102      // Overlay minta state terkini (setelah reconnect)
}

internal record PipeMessage(PipeMessageType Type, string Payload);

/// <summary>
/// BackgroundService worker: menjalankan ServerConnection + named pipe server + registry/watchdog.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private ServerConnection? _serverConnection;
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipeServer;
    private Task? _pipeListenerTask;
    private Timer? _watchdogTimer;
    private SessionState _currentState = new() { State = SessionState.LockState.Locked };
    private string _overlayExePath = string.Empty;
    private readonly object _stateLock = new();

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentLog.FileName = "agent.log";
        AgentLog.Write("=== v3Netbill Agent Service starting ===");
        _logger.LogInformation("v3Netbill Agent Service starting...");

        // Mode: PC terkunci saat idle (tanpa sesi). Mulai dengan blokir Task Manager.
        SetTaskManagerBlocked(true);

        var serverUrl = GetConfig("Server:Url", "ServerUrl", "http://localhost:3000");
        var pcId = GetConfig("Agent:PcId", "PcId", "PC001");
        var agentToken = GetConfig("Agent:Token", "AgentToken", "CHANGE_ME");
        var cfgExe = _configuration["Overlay:ExePath"];
        _overlayExePath = string.IsNullOrWhiteSpace(cfgExe)
            ? Path.Combine(AppContext.BaseDirectory, "..", "Agent.Overlay", "Agent.Overlay.exe")
            : cfgExe!;
        AgentLog.Write($"Config: serverUrl={serverUrl}, pcId={pcId}, overlayExe={_overlayExePath}");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _serverConnection = new ServerConnection(serverUrl, pcId, agentToken, _logger);

        // Subscribe ServerConnection events
        _serverConnection.ClientLoginResultReceived += OnClientLoginResult;
        _serverConnection.SessionStarted += OnSessionStarted;
        _serverConnection.SessionTicked += OnSessionTicked;
        _serverConnection.SessionStopped += OnSessionStopped;

        try
        {
            await _serverConnection.ConnectAsync(_cts.Token);
            _logger.LogInformation("Agent connected & registered: {PcId}", pcId);
            AgentLog.Write($"Connected & registered ke backend: pcId={pcId}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect agent");
            AgentLog.Write(ex, "Gagal connect ke backend");
        }

        // Start named pipe server
        _ = StartPipeServerAsync(_cts.Token);

        // Start watchdog timer (check every 5 seconds)
        _watchdogTimer = new Timer(WatchdogCallback, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Keep alive until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _watchdogTimer?.Dispose();
            _pipeServer?.Dispose();
            if (_serverConnection != null)
            {
                await _serverConnection.DisposeAsync();
            }
        }
    }

    private void OnClientLoginResult(object? sender, ClientLoginResultEventArgs e)
    {
        var payload = new { sukses = e.Payload.Sukses, alasan = e.Payload.Alasan };
        _ = SendToOverlayAsync(new PipeMessage(PipeMessageType.LoginResult, JsonConvert.SerializeObject(payload)));
    }

    private void OnSessionStarted(object? sender, SessionStartEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.SessionId = e.Payload.SessionId;
            _currentState.DurasiDetik = e.Payload.DurasiDetik;
            _currentState.SisaDetik = e.Payload.DurasiDetik;
            _currentState.State = SessionState.LockState.Unlocked;
        }
        SetTaskManagerBlocked(false);
        _ = SendStateUpdateAsync();
    }

    private void OnSessionTicked(object? sender, SessionTickEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.SisaDetik = e.Payload.SisaDetik;
        }
        _ = SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionTick, JsonConvert.SerializeObject(new { sisaDetik = e.Payload.SisaDetik })));
    }

    private void OnSessionStopped(object? sender, SessionStopEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.State = SessionState.LockState.Locked;
            _currentState.SessionId = null;
            _currentState.DurasiDetik = 0;
            _currentState.SisaDetik = 0;
        }
        SetTaskManagerBlocked(true);
        _ = SendStateUpdateAsync();
        _ = SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionStopped, JsonConvert.SerializeObject(new { alasan = e.Payload.Alasan })));
    }

    private async Task SendStateUpdateAsync()
    {
        SessionState snapshot;
        lock (_stateLock)
        {
            snapshot = new SessionState
            {
                SessionId = _currentState.SessionId,
                DurasiDetik = _currentState.DurasiDetik,
                SisaDetik = _currentState.SisaDetik,
                State = _currentState.State
            };
        }
        var payload = JsonConvert.SerializeObject(new
        {
            locked = snapshot.State == SessionState.LockState.Locked,
            sessionId = snapshot.SessionId,
            durasiDetik = snapshot.DurasiDetik,
            sisaDetik = snapshot.SisaDetik
        });
        await SendToOverlayAsync(new PipeMessage(PipeMessageType.StateUpdate, payload));
    }

    private async Task StartPipeServerAsync(CancellationToken ct)
    {
        const string pipeName = "v3netbill-agent";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                try
                {
                    // Izinkan semua user (termasuk user sesi interaktif) untuk connect ke pipe service.
                    SecureNamedPipe.GrantEveryoneAccess(_pipeServer.SafePipeHandle, pipeName);
                    AgentLog.Write("ACL pipe selesai diproses (server siap menerima koneksi)");
                }
                catch (Exception aclEx)
                {
                    // Jangan sampai memblokir server — tanpa ACL overlay mungkin gagal connect,
                    // tapi server tetap harus menunggu koneksi.
                    _logger.LogWarning(aclEx, "Gagal set ACL pada pipe — overlay mungkin tidak bisa connect");
                    AgentLog.Write(aclEx, "Gagal set ACL pada pipe (overlay mungkin tidak bisa connect)");
                }
                _logger.LogDebug("Named pipe server waiting for connection...");
                await _pipeServer.WaitForConnectionAsync(ct);
                _logger.LogInformation("Overlay connected via named pipe");
                AgentLog.Write("OVERLAY TERHUBUNG via named pipe");
                _pipeListenerTask = Task.Run(() => PipeListenerAsync(_pipeServer, ct), ct);
                await _pipeListenerTask;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe error, retrying in 2s...");
                AgentLog.Write(ex, "Named pipe server error, retry 2s");
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task PipeListenerAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            try
            {
                int bytesRead = await pipe.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var msg = JsonConvert.DeserializeObject<PipeMessage>(json);
                if (msg == null) continue;

                await HandleOverlayMessageAsync(msg, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe read error");
                break;
            }
        }
        _logger.LogInformation("Overlay disconnected");
    }

    private async Task HandleOverlayMessageAsync(PipeMessage msg, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case PipeMessageType.LoginRequest:
                {
                    var req = JsonConvert.DeserializeObject<LoginRequestPayload>(msg.Payload);
                    AgentLog.Write($"Terima login_request dari overlay: kode='{req?.Kode}'");
                    if (req != null && _serverConnection != null)
                    {
                        await _serverConnection.SendLoginRequestAsync(req.Kode, req.Password, ct);
                    }
                    break;
                }
            case PipeMessageType.PinVerifyRequest:
                {
                    var req = JsonConvert.DeserializeObject<PinVerifyRequestPayload>(msg.Payload);
                    if (req != null)
                    {
                        bool ok = await VerifyPinWithBackendAsync(req.Pin, ct);
                        var resp = new { sukses = ok };
                        await SendToOverlayAsync(new PipeMessage(PipeMessageType.PinVerifyResult, JsonConvert.SerializeObject(resp)));
                    }
                    break;
                }
            case PipeMessageType.StateRequest:
                {
                    await SendStateUpdateAsync();
                    break;
                }
        }
    }

    private async Task<bool> VerifyPinWithBackendAsync(string pin, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var serverUrl = GetConfig("Server:Url", "ServerUrl", "http://localhost:3000");
            var pcId = GetConfig("Agent:PcId", "PcId", "PC001");
            var agentToken = GetConfig("Agent:Token", "AgentToken", "");

            var payload = new { pcId, pin };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"{serverUrl}/api/settings/verify-pin", content, ct);
            if (!response.IsSuccessStatusCode) return false;

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonConvert.DeserializeObject<PinVerifyResponse>(json);
            return result?.Sukses == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PIN verify failed");
            return false;
        }
    }

    private async Task SendToOverlayAsync(PipeMessage msg, CancellationToken ct = default)
    {
        if (_pipeServer == null || !_pipeServer.IsConnected) return;
        try
        {
            string json = JsonConvert.SerializeObject(msg);
            byte[] data = Encoding.UTF8.GetBytes(json);
            await _pipeServer.WriteAsync(data, ct);
            await _pipeServer.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to overlay");
        }
    }

    private void WatchdogCallback(object? state)
    {
        if (_currentState.State != SessionState.LockState.Locked) return;

        // Mode maintenance (emergency stop dari overlay): jangan luncurkan ulang.
        string stopFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "v3netbill-agent-stop.flag");
        if (File.Exists(stopFlag))
        {
            _logger.LogInformation("Stop flag ada — overlay tidak diluncurkan ulang (mode maintenance)");
            return;
        }

        try
        {
            var processes = Process.GetProcessesByName("Agent.Overlay");
            if (processes.Length == 0)
            {
                _logger.LogWarning("Agent.Overlay not running during locked session — restarting...");
                AgentLog.Write("Watchdog: Agent.Overlay tidak jalan dalam keadaan Locked — coba luncurkan");
                RestartOverlay();
            }
            foreach (var p in processes) p.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog error");
            AgentLog.Write(ex, "Watchdog error");
        }
    }

    private void RestartOverlay()
    {
        try
        {
            string workingDir = Path.GetDirectoryName(_overlayExePath) ?? string.Empty;
            if (InteractiveProcess.Launch(_overlayExePath, workingDir))
            {
                _logger.LogInformation("Agent.Overlay restarted (interactive session)");
                AgentLog.Write("Agent.Overlay diluncurkan OK (sesi interaktif)");
            }
            else
            {
                _logger.LogInformation("Agent.Overlay tidak diluncurkan (belum ada sesi interaktif aktif)");
                AgentLog.Write("Agent.Overlay TIDAK diluncurkan (belum ada sesi interaktif / token gagal)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart Agent.Overlay");
            AgentLog.Write(ex, "Gagal restart Agent.Overlay");
        }
    }

    private void SetTaskManagerBlocked(bool block)
    {
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
            const string valueName = "DisableTaskMgr";
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, true);
            key.SetValue(valueName, block ? 1 : 0, RegistryValueKind.DWord);
            _logger.LogInformation("Task Manager {Action}", block ? "blocked" : "unblocked");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Action} Task Manager", block ? "block" : "unblock");
        }
    }

    /// <summary>
    /// Baca konfigurasi: Registry HKLM (ditulis installer) > appsettings.json > default.
    /// </summary>
    private string GetConfig(string configKey, string registryValueName, string defaultValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            var fromRegistry = key?.GetValue(registryValueName) as string;
            if (!string.IsNullOrWhiteSpace(fromRegistry)) return fromRegistry;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal membaca registry {Value}", registryValueName);
        }

        var fromFile = _configuration[configKey];
        if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;

        return defaultValue;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("v3Netbill Agent Service stopping...");
        SetTaskManagerBlocked(false);
        _cts?.Cancel();
        await base.StopAsync(cancellationToken);
    }

    // Payload records
    private record LoginRequestPayload(string Kode, string Password);
    private record PinVerifyRequestPayload(string Pin);
    private record PinVerifyResponse(bool Sukses);
}