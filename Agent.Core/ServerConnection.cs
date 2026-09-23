using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Koneksi Socket.IO agent → server v3Netbill (namespace <c>/session</c>).
///
/// <para>
/// BERTANGGUNG JAWAB HANYA atas yang di scope FASE 7:
///  - connect + auto-reconnect (dengan agentToken di query string, supaya server
///    bisa me-validasi agent di <c>SessionGateway.handleConnection</c>);
///  - <c>agent:register</c> { pcId, agentToken } saat connect pertama kali;
///  - heartbeat <c>agent:heartbeat</c> { pcId } tiap 15 detik (System.Threading.Timer);
///  - NOTIFIKASI EVENT C# untuk tiap pesan masuk.
/// </para>
/// <para>
/// TIDAK ada logic lock/password/kunci layar — itu FASE 8 (Overlay/LockScreen).
/// Semua payload JSON SODA memakai camelCase & dibungkus sebagai objek
/// <see cref="SocketIOResponse"/>; di-Deserialisir dengan
/// <see cref="SocketIOClient.Serialization.System.Text.Json"/> default.
/// </para>
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    public const double HEARTBEAT_INTERVAL_DETIK = 15.0;
    public const string SESSION_NAMESPACE = "/session";

    private readonly SocketIO _client;
    private readonly ILogger _logger;
    private readonly string _pcId;
    private readonly string _agentToken;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Timer? _heartbeatTimer;
    private bool _registered;
    private bool _disposed;

    /// <summary>Event: server membalas <c>client:login_result</c>.</summary>
    public event EventHandler<ClientLoginResultEventArgs>? ClientLoginResultReceived;

    /// <summary>Event: server kirim <c>session:start</c> (sesi voucher/member mulai).</summary>
    public event EventHandler<SessionStartEventArgs>? SessionStarted;

    /// <summary>Event: server kirim <c>session:tick</c> (update countdown).</summary>
    public event EventHandler<SessionTickEventArgs>? SessionTicked;

    /// <summary>Event: server kirim <c>session:stop</c> (sesi berhenti).</summary>
    public event EventHandler<SessionStopEventArgs>? SessionStopped;

    public bool IsConnected => _client.Connected;

    /// <summary>Buat koneksi baru. Belum connect sampai <see cref="ConnectAsync"/> dipanggil.</summary>
    public ServerConnection(string serverBaseUrl, string pcId, string agentToken, ILogger logger)
    {
        _pcId = pcId;
        _agentToken = agentToken;
        _logger = logger;

        // SocketIOClient membaca namespace dari URL path → "http://host:3000/session"
        var uri = new Uri($"{serverBaseUrl.TrimEnd('/')}{SESSION_NAMESPACE}");
        _client = new SocketIO(uri, new SocketIOOptions
        {
            // polling dulu, upgrade otomatis ke WebSocket (sesuai Socket.IO v4 / EIO=4)
            EIO = 4,
            Reconnection = true,
            ReconnectionAttempts = 30,
            ReconnectionDelay = 1000,
            ReconnectionDelayMax = 5000,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AutoUpgrade = true,
            Query = new System.Collections.Specialized.NameValueCollection
            {
                { "pcId", pcId },
                { "agentToken", agentToken },
            },
        });

        HookEvents();
    }

    private void HookEvents()
    {
        _client.OnConnected += OnConnected;
        _client.OnDisconnected += OnDisconnected;
        _client.OnError += (_, err) =>
            _logger.LogError("Socket.IO error: {Message}", err.Message);

        _client.On("client:login_result", ctx =>
        {
            var payload = ctx.GetValue<ClientLoginResultPayload>(0);
            ClientLoginResultReceived?.Invoke(this, new ClientLoginResultEventArgs(payload));
            return Task.CompletedTask;
        });

        _client.On("session:start", ctx =>
        {
            var payload = ctx.GetValue<SessionStartPayload>(0);
            SessionStarted?.Invoke(this, new SessionStartEventArgs(payload));
            return Task.CompletedTask;
        });

        _client.On("session:tick", ctx =>
        {
            var payload = ctx.GetValue<SessionTickPayload>(0);
            SessionTicked?.Invoke(this, new SessionTickEventArgs(payload));
            return Task.CompletedTask;
        });

        _client.On("session:stop", ctx =>
        {
            var payload = ctx.GetValue<SessionStopPayload>(0);
            SessionStopped?.Invoke(this, new SessionStopEventArgs(payload));
            return Task.CompletedTask;
        });
    }

    private Task OnConnected(object sender, object e)
    {
        _logger.LogInformation("Terhubung ke server ({Namespace}) — registrasi agent...", SESSION_NAMESPACE);
        return RegisterAsync(_lifetimeCts.Token);
    }

    private Task OnDisconnected(object sender, string reason)
    {
        _logger.LogWarning("Terputus dari server: {Reason} — akan reconnect otomatis.", reason);
        StopHeartbeat();
        return Task.CompletedTask;
    }

    /// <summary>Connect + register + mulai heartbeat. Idempotent.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync();
        await RegisterAsync(ct);
        StartHeartbeat();
    }

    /// <summary>Kirim <c>agent:register</c> ke server.</summary>
    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_registered) return; // hanya sekali per koneksi
        await _client.EmitAsync("agent:register",
            new { pcId = _pcId, agentToken = _agentToken }, ct);
        _registered = true;
        _logger.LogInformation("agent:register terkirim untuk PC {PcId}", _pcId);
    }

    /// <summary>Kirim <c>agent:heartbeat</c> manual (dipakai bila mau dipicu selain timer).</summary>
    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        if (!_client.Connected) return;
        await _client.EmitAsync("agent:heartbeat", new { pcId = _pcId }, ct);
        _logger.LogDebug("agent:heartbeat → PC {PcId}", _pcId);
    }

    /// <summary>Client (overlay) minta login voucher/member ke server.</summary>
    public async Task SendLoginRequestAsync(string kode, string password, CancellationToken ct = default)
    {
        await _client.EmitAsync("client:login_request", new
        {
            pcId = _pcId,
            kredensial = new { kode, password },
        }, ct);
        _logger.LogInformation("client:login_request untuk kode {Kode}", kode);
    }

    private void StartHeartbeat()
    {
        _heartbeatTimer ??= new Timer(
            async _ => await SendHeartbeatAsync(_lifetimeCts.Token),
            null,
            TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_DETIK),
            TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_DETIK));
        _logger.LogInformation("Heartbeat aktif: tiap {Interval}s", HEARTBEAT_INTERVAL_DETIK);
    }

    private void StopHeartbeat() => _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopHeartbeat();
        _heartbeatTimer?.Dispose();
        _lifetimeCts.Cancel();

        if (_client.Connected)
        {
            await _client.DisconnectAsync();
        }

        _client.Dispose();
        _lifetimeCts.Dispose();
    }
}

// ==================== EVENT ARGS ====================

public sealed class ClientLoginResultEventArgs : EventArgs
{
    public ClientLoginResultEventArgs(ClientLoginResultPayload payload) => Payload = payload;
    public ClientLoginResultPayload Payload { get; }
}

public sealed class SessionStartEventArgs : EventArgs
{
    public SessionStartEventArgs(SessionStartPayload payload) => Payload = payload;
    public SessionStartPayload Payload { get; }
}

public sealed class SessionTickEventArgs : EventArgs
{
    public SessionTickEventArgs(SessionTickPayload payload) => Payload = payload;
    public SessionTickPayload Payload { get; }
}

public sealed class SessionStopEventArgs : EventArgs
{
    public SessionStopEventArgs(SessionStopPayload payload) => Payload = payload;
    public SessionStopPayload Payload { get; }
}
