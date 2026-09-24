using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Named pipe client for Overlay → Service communication.
/// Connects to the Service's named pipe server.
/// </summary>
public class PipeClient : IDisposable
{
    private readonly ILogger<PipeClient> _logger;
    private readonly IConfiguration _config;
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private readonly object _lock = new();

    public event Action<PipeMessage>? MessageReceived;

    public bool IsConnected => _pipe != null && _pipe.IsConnected;

    public PipeClient(ILogger<PipeClient> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        const string pipeName = "v3netbill-agent";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _logger.LogInformation("Connecting to Service via named pipe...");
                await _pipe.ConnectAsync(5000, ct);
                _logger.LogInformation("Connected to Service");

                // Minta state terkini setelah (re)connect agar overlay sinkron
                await RequestStateAsync(ct);

                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
                await _readerTask;
                _logger.LogWarning("Pipa terputus — mencoba menyambungkan ulang...");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pipe connection failed, retrying in 2s...");
            }
            finally
            {
                _readerTask = null;
                _cts?.Dispose();
                _cts = null;
            }
            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { throw; }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && _pipe?.IsConnected == true)
        {
            try
            {
                int bytesRead = await _pipe.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var msg = JsonConvert.DeserializeObject<PipeMessage>(json);
                if (msg != null)
                {
                    MessageReceived?.Invoke(msg);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe read error");
                break;
            }
        }
        _logger.LogInformation("Disconnected from Service");
    }

    /// <summary>Minta state terkini dari Service (dipakai usai connect/reconnect).</summary>
    public async Task RequestStateAsync(CancellationToken ct = default)
    {
        await SendAsync(new PipeMessage(PipeMessageType.StateRequest, "{}"), ct);
    }

    public async Task SendAsync(PipeMessage msg, CancellationToken ct = default)
    {
        if (_pipe == null || !_pipe.IsConnected) return;
        lock (_lock)
        {
            try
            {
                string json = JsonConvert.SerializeObject(msg);
                byte[] data = Encoding.UTF8.GetBytes(json);
                _pipe.WriteAsync(data, ct).GetAwaiter().GetResult();
                _pipe.FlushAsync(ct).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send to Service");
            }
        }
    }

    public async Task SendLoginRequestAsync(string kode, string password, CancellationToken ct = default)
    {
        var payload = JsonConvert.SerializeObject(new { kode, password });
        await SendAsync(new PipeMessage(PipeMessageType.LoginRequest, payload), ct);
    }

    public async Task SendPinVerifyRequestAsync(string pin, CancellationToken ct = default)
    {
        var payload = JsonConvert.SerializeObject(new { pin });
        await SendAsync(new PipeMessage(PipeMessageType.PinVerifyRequest, payload), ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _readerTask?.Wait(1000);
        _pipe?.Dispose();
        _cts?.Dispose();
    }
}

// Shared message types (must match Service)
public enum PipeMessageType
{
    StateUpdate = 1,
    SessionStarted = 2,
    SessionStopped = 3,
    SessionTick = 4,
    LoginResult = 5,
    PinVerifyResult = 6,
    LoginRequest = 100,
    PinVerifyRequest = 101,
    StateRequest = 102
}

public record PipeMessage(PipeMessageType Type, string Payload);