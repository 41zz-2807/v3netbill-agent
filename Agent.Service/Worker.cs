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
    StateRequest = 102,     // Overlay minta state terkini (setelah reconnect)
    StopSessionRequest = 103 // Overlay minta hentikan sesi yang berjalan (stop sendiri)
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
    private Task? _maintainTask;
    private SessionState _currentState = new() { State = SessionState.LockState.Locked };
    private string _overlayExePath = string.Empty;
    private readonly object _stateLock = new();
    // NamedPipeServerStream TIDAK thread-safe untuk write bersamaan:
    // dua WriteAsync pada instance sama tanpa sinkronisasi dapat korup/gagal.
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);

    /// <summary>Jeda antar percobaan connect ulang ke backend.</summary>
    private const int RECONNECT_INTERVAL_DETIK = 5;

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

        // Watchdog scheduled task: jalankan tiap menit; kalau service berhenti (di-stop manual),
        // hasilkan kembali. Bertahan dari reboot & menjadikan agent sulit dimatikan.
        EnsureWatchdogScheduledTask();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _serverConnection = new ServerConnection(serverUrl, pcId, agentToken, _logger);

        // Subscribe ServerConnection events
        _serverConnection.ClientLoginResultReceived += OnClientLoginResult;
        _serverConnection.SessionStarted += OnSessionStarted;
        _serverConnection.SessionTicked += OnSessionTicked;
        _serverConnection.SessionStopped += OnSessionStopped;
        _serverConnection.AdminLockReceived += OnAdminLock;
        _serverConnection.AdminShutdownReceived += OnAdminShutdown;

        // Supervisor koneksi. SocketIOClient 4.x hanya mencoba retry SELAMA
        // ConnectAsync() masih berjalan (ReconnectionAttempts=30); begitu koneksi
        // sukses lalu putus — mis. backend restart/deploy — TIDAK ada retry lagi
        // dan ConnectAsync() tidak pernah dipanggil ulang. Akibatnya agent offline
        // permanen. Loop di bawah yang menutup celah tersebut.
        _maintainTask = MaintainConnectionAsync(pcId, _cts.Token);

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
            if (_maintainTask != null)
            {
                try { await _maintainTask.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* dibatalkan atau tidak selesai — abaikan */ }
            }
            if (_serverConnection != null)
            {
                await _serverConnection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Jaga koneksi tetap hidup: connect pertama kali, lalu connect ulang tiap
    /// <see cref="RECONNECT_INTERVAL_DETIK"/> selama <see cref="_serverConnection"/> tidak terhubung.
    /// Berhenti saat service di-stop.
    /// </summary>
    private async Task MaintainConnectionAsync(string pcId, CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            if (_serverConnection != null && !_serverConnection.IsConnected)
            {
                if (first)
                {
                    _logger.LogInformation("Menghubungkan agent ke backend: {PcId}", pcId);
                }
                else
                {
                    _logger.LogInformation("Koneksi terputus — mencoba connect ulang ke backend...");
                    AgentLog.Write("Reconnect: mencoba connect ulang ke backend...");
                }

                try
                {
                    await _serverConnection.ConnectAsync(ct);
                    _logger.LogInformation("Agent connected & registered: {PcId}", pcId);
                    AgentLog.Write($"Connected & registered ke backend: pcId={pcId}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Gagal connect ke backend — akan dicoba lagi tiap {Detik} detik",
                        RECONNECT_INTERVAL_DETIK);
                    AgentLog.Write(ex, "Gagal connect ke backend");
                }
            }
            first = false;

            try { await Task.Delay(TimeSpan.FromSeconds(RECONNECT_INTERVAL_DETIK), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnClientLoginResult(object? sender, ClientLoginResultEventArgs e)
    {
        var payload = new { sukses = e.Payload.Sukses, alasan = e.Payload.Alasan };
        _ = SendToOverlayAsync(new PipeMessage(PipeMessageType.LoginResult, JsonConvert.SerializeObject(payload)));
    }

    private async void OnSessionStarted(object? sender, SessionStartEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.SessionId = e.Payload.SessionId;
            _currentState.DurasiDetik = e.Payload.DurasiDetik;
            _currentState.SisaDetik = e.Payload.DurasiDetik;
            _currentState.State = SessionState.LockState.Unlocked;
        }
        SetTaskManagerBlocked(false);
        // StateUpdate (locked=false) HARUS dikirim & tiba dulu, baru identitas akun.
        // Sequential + semaphore = tidak pernah korup/tertukar.
        var stateSent = await SendStateUpdateAsync();
        if (stateSent && _currentState.State != SessionState.LockState.Unlocked)
        {
            // Aman: kalau sudah berubah (mis. stop tiba-tiba), jangan kirim akun.
            return;
        }
        // Kirim identitas akun ke overlay (untuk window mini: "login sebagai siapa").
        var akun = e.Payload.Account;
        if (akun != null)
        {
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionStarted, JsonConvert.SerializeObject(new
            {
                kodeUnik = akun.KodeUnik,
                nama = akun.Nama,
                tipe = akun.Tipe,
            })));
        }
        AgentLog.Write($"SessionStarted: sessionId={e.Payload.SessionId}, durasi={e.Payload.DurasiDetik}s, akun={(akun?.Nama ?? akun?.KodeUnik ?? "?")} ({akun?.Tipe ?? "?"})");
    }

    private void OnSessionTicked(object? sender, SessionTickEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.SisaDetik = e.Payload.SisaDetik;
        }
        _ = SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionTick, JsonConvert.SerializeObject(new { sisaDetik = e.Payload.SisaDetik })));
    }

    private async void OnSessionStopped(object? sender, SessionStopEventArgs e)
    {
        lock (_stateLock)
        {
            _currentState.State = SessionState.LockState.Locked;
            _currentState.SessionId = null;
            _currentState.DurasiDetik = 0;
            _currentState.SisaDetik = 0;
        }
        SetTaskManagerBlocked(true);
        // Sequential: StateUpdate (locked=true) dulu, baru pesan alasan.
        await SendStateUpdateAsync();
        await SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionStopped, JsonConvert.SerializeObject(new { alasan = e.Payload.Alasan })));
    }

    /// <summary>Dashboard/backend memerintahkan kunci layar PC sekarang (di luar alur sesi).</summary>
    private async void OnAdminLock(object? sender, AdminLockEventArgs e)
    {
        AgentLog.Write($"ADMIN-LOCK diterima untuk PC {e.Payload.PcId}");
        await ForceLockScreenAsync();
    }

    /// <summary>Dashboard/backend memerintahkan matikan PC; hentikan sesi aktif dulu, lalu shutdown.</summary>
    private async void OnAdminShutdown(object? sender, AdminShutdownEventArgs e)
    {
        AgentLog.Write($"ADMIN-SHUTDOWN diterima untuk PC {e.Payload.PcId} — stop sesi lalu matikan PC");
        try
        {
            if (_serverConnection != null)
            {
                await _serverConnection.SendStopSessionAsync(_cts?.Token ?? CancellationToken.None);
            }
            await ForceLockScreenAsync();
            // Beri waktu 10 detik sebelum mati. /f = force (tidak menunggu aplikasi lain
            // menutup) sehingga PC pasti mati walau ada program yang blocking.
            var psi = new ProcessStartInfo("shutdown", "/s /f /t 10 /c \"v3Netbill: PC dimatikan oleh admin\"")
            {
                UseShellExecute = false,
            };
            Process.Start(psi);
            AgentLog.Write("Shutdown terjadwal (10 detik, force) — PC dimatikan admin");
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal menjalankan perintah shutdown");
        }
    }

    private async Task ForceLockScreenAsync()
    {
        lock (_stateLock)
        {
            _currentState.State = SessionState.LockState.Locked;
            _currentState.SessionId = null;
            _currentState.DurasiDetik = 0;
            _currentState.SisaDetik = 0;
        }
        SetTaskManagerBlocked(true);
        await SendStateUpdateAsync();
        await SendToOverlayAsync(new PipeMessage(PipeMessageType.SessionStopped, JsonConvert.SerializeObject(new { alasan = "manual" })));
    }

    private async Task<bool> SendStateUpdateAsync()
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
        return snapshot.State == SessionState.LockState.Unlocked;
    }

    private async Task StartPipeServerAsync(CancellationToken ct)
    {
        const string pipeName = "v3netbill-agent";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = SecureNamedPipe.CreateServer(pipeName, 1, out string? pipeErr);
                if (pipe == null)
                {
                    AgentLog.Write($"Pipe server gagal dibuat: {pipeErr}");
                    await Task.Delay(2000, ct);
                    continue;
                }
                _pipeServer = pipe;
                _logger.LogDebug("Named pipe server waiting for connection...");
                await pipe.WaitForConnectionAsync(ct);
                _logger.LogInformation("Overlay connected via named pipe");
                AgentLog.Write("OVERLAY TERHUBUNG via named pipe");
                _pipeListenerTask = Task.Run(() => PipeListenerAsync(pipe, ct), ct);
                await _pipeListenerTask;
                AgentLog.Write("Overlay disconnect — instance pipe ditutup, siap koneksi baru");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Named pipe error, retrying in 2s...");
                AgentLog.Write(ex, "Named pipe server error, retry 2s");
            }
            finally
            {
                // WAJIB: dispose instance lama sebelum membuat instance baru.
                // Kalau tidak, nama pipe tetap "All pipe instances are busy" selamanya.
                try { _pipeServer?.Dispose(); } catch { }
                _pipeServer = null;
            }
            try { if (!ct.IsCancellationRequested) await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
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
            case PipeMessageType.StopSessionRequest:
                {
                    AgentLog.Write("Terima StopSessionRequest dari overlay — minta stop sesi ke backend");
                    if (_serverConnection != null)
                    {
                        await _serverConnection.SendStopSessionAsync(ct);
                    }
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
        var pipe = _pipeServer;
        if (pipe == null || !pipe.IsConnected) return;
        await _pipeWriteLock.WaitAsync(ct);
        try
        {
            string json = JsonConvert.SerializeObject(msg);
            byte[] data = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(data, ct);
            await pipe.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send to overlay");
        }
        finally
        {
            _pipeWriteLock.Release();
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

    private void EnsureWatchdogScheduledTask()
    {
        try
        {
            const string taskName = @"\v3Netbill\Agent Watchdog";
            string scriptPath = Path.Combine(AppContext.BaseDirectory, "watchdog.cmd");
            string queryArgs = $"/Query /TN \"{taskName}\"";
            using (var chk = Process.Start(new ProcessStartInfo("schtasks", queryArgs)
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                if (chk != null)
                {
                    chk.WaitForExit(5000);
                    if (chk.ExitCode == 0)
                    {
                        AgentLog.Write("Watchdog scheduled task sudah ada — tidak perlu dibuat ulang");
                        return;
                    }
                }
            }

            string createArgs =
                $"/Create /F /TN \"{taskName}\" /TR \"{scriptPath}\" " +
                "/SC MINUTE /MO 1 /RU SYSTEM /RL HIGHEST";
            using (var pr = Process.Start(new ProcessStartInfo("schtasks", createArgs)
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                if (pr == null)
                {
                    AgentLog.Write("Watchdog scheduled task GAGAL dibuat (Process.Start null)");
                    return;
                }
                pr.WaitForExit(8000);
                AgentLog.Write(pr.ExitCode == 0
                    ? "Watchdog scheduled task berhasil dibuat (tiap 1 menit, SYSTEM)"
                    : $"Watchdog scheduled task GAGAL dibuat: {pr.ExitCode} {pr.StandardError.ReadToEnd()}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure watchdog scheduled task");
            AgentLog.Write(ex, "Gagal membuat watchdog scheduled task");
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