using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using SocketIOClient.Common;
using SocketIOClient.Common.Messages;
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

    /// <summary>
    /// Batas menunggu balasan ack dari server. Satuannya milidetik karena itu
    /// yang dipakai CancellationTokenSource.CancelAfter.
    /// </summary>
    private const int ACK_TIMEOUT_MILIDETIK = 10_000;

    /// <summary>
    /// Berapa lama boleh tanpa bukti hidup apa pun sebelum socket dianggap
    /// half-open. 60 detik = 4 heartbeat. Amber server 30 detik
    /// (<c>HEARTBEAT_INTERVAL_DETIK</c> = 15), jadi kita-circle selama 2x.
    /// </summary>
    private const int STALE_TOTAL_DETIK = 60;

    /// <summary>
    /// Berapa heartbeat gagal beruntun sebelum socket dinyatakan mati.
    /// Dua = 30 detik. Cukup lama untuk membiarkan satu hiccup lewat tanpa
    /// reconnect yang tidak perlu, cukup cepat untuk tidak milik connect.
    /// </summary>
    private const int GAGAL_HEARTBEAT_UNTUK_MATI = 2;

    /// <summary>
    /// Tiap berapa tick sehat yang dicatat. 20 tick = 5 menit.
    /// <para>
    /// Dulu tiap tick (15 detik) dicatat, jadi 5.760 baris sehari — dan
    /// 318 baris dari failed tick itulah yang memenuhi 686 KB agent.log pada
    /// insiden 2 Okt. Kegagalan tetap dicatat SEMUA, karena itu yang dicari.
    /// </para>
    /// </summary>
    private const int TICK_LOG_INTERVAL = 20;

    private SocketIO? _client;
    private readonly ILogger _logger;
    private readonly string _serverBaseUrl;
    private readonly string _pcId;
    private readonly string _agentToken;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Timer? _heartbeatTimer;
    private readonly object _heartbeatLock = new();
    private readonly object _klienLock = new();
    private DateTime _lastWarnNotConnected = DateTime.MinValue;
    private int _heartbeatTickCount;
    private bool _registered;
    private bool _disposed;

    /// <summary>Bukti terakhir socket ini benar-benar hidup (UTC).</summary>
    private DateTime _aktivitasSuksesUtc = DateTime.MinValue;

    /// <summary>Berapa heartbeat terakhir gagal beruntun.</summary>
    private int _gagalHeartbeat;

    /// <summary>
    /// Socket dinyatakan mati oleh kita sendiri karena pengiriman heartbeat
    /// gagal.
    /// <para>
    /// <b>Kenapa flag ini harus ada.</b> <c>SocketIO.Connected</c> hanya
    /// berubah jadi false kalau library menerima close frame atau error
    /// transport. Kalau middlebox (Cloudflare tunnel, NAT, firewall) drop flow
    /// tanpa FIN/RST — yang happened pada 2 Okt 2026 jam 03:28 WIB — client
    /// tidak akan pernah diberi tahu, dan flag itu tetap <c>true</c> selamanya.
    /// Supervisor reconnect Meanwhile memeriksa flag itu, jadi tidak pernah
    /// memanggil <c>ConnectAsync</c> lagi: PC offline 8 jam 7 menit.
    /// </para>
    /// </summary>
    private volatile bool _socketMati;

    /// <summary>Event: server membalas <c>client:login_result</c>.</summary>
    public event EventHandler<ClientLoginResultEventArgs>? ClientLoginResultReceived;

    /// <summary>Event: server kirim <c>session:start</c> (sesi voucher/member mulai).</summary>
    public event EventHandler<SessionStartEventArgs>? SessionStarted;

    /// <summary>Event: server kirim <c>session:tick</c> (update countdown).</summary>
    public event EventHandler<SessionTickEventArgs>? SessionTicked;

    /// <summary>Event: server kirim <c>session:stop</c> (sesi berhenti).</summary>
    public event EventHandler<SessionStopEventArgs>? SessionStopped;

    /// <summary>Event: dashboard kirim <c>admin:lock</c> (perintah kunci layar).</summary>
    public event EventHandler<AdminLockEventArgs>? AdminLockReceived;

    /// <summary>Event: dashboard kirim <c>admin:shutdown</c> (perintah matikan PC).</summary>
    public event EventHandler<AdminShutdownEventArgs>? AdminShutdownReceived;

    /// <summary>Koneksi Socket.IO berubah — dipakai overlay untuk indikator "terhubung".</summary>
    public event EventHandler<ServerLinkEventArgs>? ServerLinkChanged;

    /// <summary>Server mendorong konfigurasi OTP Telegram baru.</summary>
    public event EventHandler<OtpConfigEventArgs>? OtpConfigReceived;

    /// <summary>Konfigurasi Nextcloud; lihat <see cref="NextcloudConfigPayload"/>.</summary>
    public event EventHandler<NextcloudConfigEventArgs>? NextcloudConfigReceived;

    /// <summary>Server mendorong hash PIN bypass/maintenance baru.</summary>
    public event EventHandler<BypassConfigEventArgs>? BypassConfigReceived;

    /// <summary>
    /// Benar hanya kalau socket benar-benar bisa dipakai: hidup menurut library
    /// <b>dan</b> belum dinyatakan mati oleh pemeriksaan heartbeat kita.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            var c = _client;
            return c != null && c.Connected && !_socketMati && !SudahStale;
        }
    }

    /// <summary>
    /// True kalau socket sudah lama tidak memberi bukti hidup apa pun.
    /// <para>
    /// Ini jaring pengaman bagi heartbeat yang somehow tidak pernah melempar:
    /// kalau semua pengiriman diam-diam hilang (kuota, middlebox yang
    /// memorize), tidak ada exception yang bisa kita tangkap.
    /// </para>
    /// </summary>
    private bool SudahStale =>
        _aktivitasSuksesUtc != DateTime.MinValue &&
        (DateTime.UtcNow - _aktivitasSuksesUtc).TotalSeconds > STALE_TOTAL_DETIK;

    /// <summary>Buat instance Socket.IO baru dengan konfigurasi yang sama.</summary>
    private SocketIO BuatClient()
    {
        // SocketIOClient membaca namespace dari URL path → "http://host:3000/session"
        var uri = new Uri($"{_serverBaseUrl.TrimEnd('/')}{SESSION_NAMESPACE}");
        return new SocketIO(uri, new SocketIOOptions
        {
            // polling dulu, upgrade otomatis ke WebSocket (sesuai Socket.IO v4 / EIO=4)
            EIO = EngineIO.V4,
            // PENTING: reconnection library DIMATIKAN. SocketIOClient 4.x memang
            // auto-reconnect pada disconnect (SocketIO.InvokeOnDisconnected), tetapi
            // hanya ReconnectionAttempts=30 x delay acak <= ReconnectionDelayMax —
            // budget ~2 menit, lalu menyerah PERMANEN tanpa ada callback lagi.
            // Kalau menyalakan Reconnection bersama supervisor di Agent.Service.Worker,
            // keduanya memanggil ConnectAsync() bersamaan pada satu instance SocketIO
            // -> dua session hidup -> server saling menendang socket -> connection
            // flapping. Jadi biarkan supervisor satu-satunya yang memicu reconnect.
            Reconnection = false,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AutoUpgrade = true,
            Query = new System.Collections.Specialized.NameValueCollection
            {
                { "pcId", _pcId },
                { "agentToken", _agentToken },
            },
        });
    }

    /// <summary>Buat koneksi baru. Belum connect sampai <see cref="ConnectAsync"/> dipanggil.</summary>
    public ServerConnection(string serverBaseUrl, string pcId, string agentToken, ILogger logger)
    {
        _serverBaseUrl = serverBaseUrl;
        _pcId = pcId;
        _agentToken = agentToken;
        _logger = logger;

        lock (_klienLock)
        {
            _client = BuatClient();
        }
        HookEvents(_client);
    }

    private void HookEvents(SocketIO client)
    {
        client.OnConnected += OnConnected;
        client.OnDisconnected += OnDisconnected;
        client.OnError += (_, err) =>
            _logger.LogError("Socket.IO error: {Message}", err);

        client.On("client:login_result", ctx =>
        {
            var payload = ctx.GetValue<ClientLoginResultPayload>(0);
            ClientLoginResultReceived?.Invoke(this, new ClientLoginResultEventArgs(payload));
            return Task.CompletedTask;
        });

        client.On("session:start", ctx =>
        {
            var payload = ctx.GetValue<SessionStartPayload>(0);
            SessionStarted?.Invoke(this, new SessionStartEventArgs(payload));
            return Task.CompletedTask;
        });

        client.On("session:tick", ctx =>
        {
            var payload = ctx.GetValue<SessionTickPayload>(0);
            SessionTicked?.Invoke(this, new SessionTickEventArgs(payload));
            return Task.CompletedTask;
        });

        client.On("session:stop", ctx =>
        {
            var payload = ctx.GetValue<SessionStopPayload>(0);
            SessionStopped?.Invoke(this, new SessionStopEventArgs(payload));
            return Task.CompletedTask;
        });

        client.On("admin:lock", ctx =>
        {
            var payload = ctx.GetValue<AdminLockPayload>(0);
            AdminLockReceived?.Invoke(this, new AdminLockEventArgs(payload));
            return Task.CompletedTask;
        });

        // Konfigurasi OTP Telegram yang didorong server saat admin menyimpannya
        // di halaman Pengaturan. Agent menyimpannya ke disk agar tetap bisa
        // mengirim OTP ke Telegram walaupun server sedang mati.
        client.On("agent:otp_config", ctx =>
        {
            var payload = ctx.GetValue<OtpConfigPayload>(0);
            if (payload != null)
            {
                OtpConfigReceived?.Invoke(this, new OtpConfigEventArgs(payload.BotToken ?? "", payload.ChatId ?? ""));
            }
            return Task.CompletedTask;
        });

        // Konfigurasi tujuan upload log. Disimpan ke registry supaya tetap
          // dipakai walaupun server sedang tak terjangkau.
          client.On("agent:nextcloud_config", ctx =>
          {
              var payload = ctx.GetValue<NextcloudConfigPayload>(0);
              if (payload != null)
              {
                  NextcloudConfigReceived?.Invoke(this, new NextcloudConfigEventArgs(
                      payload.Url ?? "", payload.User ?? "", payload.Pass ?? "",
                      payload.Folder ?? "", payload.Nama ?? ""));
              }
              return Task.CompletedTask;
          });

        client.On("agent:bypass_config", ctx =>
        {
            var payload = ctx.GetValue<BypassConfigPayload>(0);
            if (payload != null)
            {
                BypassConfigReceived?.Invoke(this, new BypassConfigEventArgs(payload.Hash ?? ""));
            }
            return Task.CompletedTask;
        });

        client.On("admin:shutdown", ctx =>
        {
            var payload = ctx.GetValue<AdminShutdownPayload>(0);
            AdminShutdownReceived?.Invoke(this, new AdminShutdownEventArgs(payload));
            return Task.CompletedTask;
        });
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        _aktivitasSuksesUtc = DateTime.UtcNow;
        _gagalHeartbeat = 0;
        _socketMati = false;
        _logger.LogInformation("Terhubung ke server ({Namespace}) — registrasi agent...", SESSION_NAMESPACE);
        ServerLinkChanged?.Invoke(this, new ServerLinkEventArgs(true));
        // Jangan `_ = RegisterAsync(...)` telanjang. RegisterAsync melempar
        // kalau socket mati di tengah, dan task yang tidak di-await akan
        // menjadi unobserved task exception.
        _ = RegisterAsyncAman();
    }

    /// <summary>Jalankan <see cref="RegisterAsync"/> tanpa exception yang terlantar.</summary>
    private async Task RegisterAsyncAman()
    {
        try
        {
            await RegisterAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal mengirim agent:register");
            AgentLog.Write(ex, "Gagal kirim agent:register");
            // Supervisor akan mencoba lagi: RegisterAsync sudah mengembalikan
            // _registered ke false, jadi pemanggilan berikutnya tidak di-skip.
        }
    }

    private void OnDisconnected(object? sender, string reason)
    {
        // CATATAN: library (Reconnection=false) TIDAK reconnect di sini, dan memang
        // tidak boleh — reconnect dikelola satu otoritas oleh supervisor di
        // Agent.Service.Worker (MaintainConnectionAsync, polling tiap 5 detik).
        _logger.LogWarning("Terputus dari server: {Reason} — supervisor akan mencoba connect ulang.", reason);
        // Wajib ke AgentLog juga: kalau hanya _logger, alasannya hanya ada di Windows
        // Event Log sehingga tidak pernah terlihat saat membaca agent.log.
        AgentLog.Write($"Terputus dari server: {reason} — supervisor akan mencoba connect ulang");
        ServerLinkChanged?.Invoke(this, new ServerLinkEventArgs(false, reason));
        StopHeartbeat();
        _registered = false; // izinkan register ulang saat reconnect berikutnya
        _socketMati = true;  // instance ini sudah tidak bisa dipakai lagi
    }

    /// <summary>Connect + register + mulai heartbeat. Idempotent.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Socket yang dinyatakan mati TIDAK bisa dipakai ulang: state internal
        // library masih menyimpan session half-open, dan ConnectAsync() pada
        // instance itu bisa melempar atau — lebih buruk — membuat session kedua
        // tanpa menutup yang pertama (server lalu saling menendang). Jadi buat
        // instance baru dari nol.
        if (_socketMati || SudahStale)
        {
            AgentLog.Write("Socket lama dinyatakan mati — buat instance Socket.IO baru");
            await GantiClientAsync();
        }

        var client = _client;
        if (client == null) throw new InvalidOperationException("Client Socket.IO belum dibuat.");

        await client.ConnectAsync();
        _aktivitasSuksesUtc = DateTime.UtcNow;
        _gagalHeartbeat = 0;
        _socketMati = false;
        await RegisterAsync(ct);
        StartHeartbeat();
    }

    /// <summary>
    /// Buat instance <see cref="SocketIO"/> baru dan pasang ulang semua handler.
    /// Instance lama dibuang lebih dulu supaya tidak ada dua session hidup.
    /// </summary>
    private async Task GantiClientAsync()
    {
        SocketIO? lama;
        SocketIO baru;
        lock (_klienLock)
        {
            lama = _client;
            baru = BuatClient();
            _client = baru;
        }
        HookEvents(baru);

        if (lama == null) return;
        try
        {
            // DisconnectAsync kadang melempar kalau transport-nya sudah mati —
            // itu justru kondisi yang diharapkan di sini, jadi jangan biarkan
            // exception-nya membatalkan pembuatan client baru.
            if (lama.Connected) await lama.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Disconnect client lama gagal (diabaikan): {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            try { lama.Dispose(); } catch { }
        }
    }

    /// <summary>Kirim <c>agent:register</c> ke server.</summary>
    public async Task RegisterAsync(CancellationToken ct = default)
    {
        var client = _client;
        // `_registered` di-set SEBELUM await, bukan sesudah. Kalau di-set
        // sesudah, OnConnected dan ConnectAsync bisa sama-sama membaca
        // false lalu mengirim register dua kali — persis yang terlihat di log
        // server (2-3 "registered with socket" untuk satu koneksi).
        if (_registered || client == null) return;
        _registered = true;
        try
        {
            await client.EmitAsync("agent:register",
                [ new { pcId = _pcId, agentToken = _agentToken } ], ct);
            _aktivitasSuksesUtc = DateTime.UtcNow;
            _logger.LogInformation("agent:register terkirim untuk PC {PcId}", _pcId);
        }
        catch
        {
            _registered = false; // boleh coba ulang di pemanggilan berikutnya
            throw;
        }
    }

    /// <summary>
    /// Nyatakan socket mati karena pengiriman heartbeat gagal.
    /// </summary>
    /// <remarks>
    /// Ini action item yang hilang pada insiden 2 Okt 2026: heartbeat gagal
    /// 318 kali berturut-turut, tapi <c>catch</c> hanya menulis ke log dan
    /// tidak mengubah apa pun. Karena supervisor reconnect memeriksa
    /// <see cref="IsConnected"/> — yang tetap true karena library tidak pernah
    /// menerima close frame — tidak ada yang terjadi selamanya.
    /// <para>
    /// Sebaiknya ini juga memberi tahu overlay supaya kartu di PC menunjukkan
    /// "server terputus" alih-alih menampilkan hitung mundur yang sudah mati.
    /// </para>
    /// </remarks>
    private void TandaiSocketMati(string alasan)
    {
        bool baruMati = !_socketMati;
        _socketMati = true;
        StopHeartbeat();
        _registered = false;
        if (!baruMati) return;

        _logger.LogError("Socket dinyatakan mati: {Alasan} — supervisor akan connect ulang", alasan);
        AgentLog.Write($"SOCKET MATI: {alasan} — supervisor akan buat instance baru lalu connect ulang");
        ServerLinkChanged?.Invoke(this, new ServerLinkEventArgs(false, "koneksi tidak ada dengan server"));

        // Kumpulkan log + status lalu kirim ke server. Socket yang baru saja
        // dinyatakan mati adalah bukti yang jelas ada masalah, dan itu momen
        // terbaik untuk mengambil log: isinya masih mencakup urutan kejadian
        // dari awal sampai reconnect berikutnya berhasil.
        // Cooldown + batas harian ada di AgentDiagnostics, jadi ini tidak
        // menjadi floods kalau socket sering mati.
        _ = KirimDiagnosaTerjadwal();
    }

    /// <summary>
    /// Jeda singkat lalu kirim diagnosa, supaya log ikut memuat
    /// proses kesembuhannya (reconnect berhasil beberapa detik kemudian).
    /// </summary>
    private async Task KirimDiagnosaTerjadwal()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), _lifetimeCts.Token).ConfigureAwait(false);
            await AgentDiagnostics
                .KumpulkanDanKirim(_serverBaseUrl, _pcId, _agentToken, _lifetimeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Diagnosa terjadwal gagal");
        }
    }

    /// <summary>Kirim <c>agent:heartbeat</c> manual (dipakai bila mau dipicu selain timer).</summary>
    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client == null || !client.Connected)
        {
            // Jangan senyap: dulu baris ini return tanpa log apa pun sehingga heartbeat
            // bisa mati tanpa jejak sementara socket masih ada di sisi server.
            if ((DateTime.UtcNow - _lastWarnNotConnected).TotalSeconds >= 60)
            {
                _lastWarnNotConnected = DateTime.UtcNow;
                _logger.LogWarning("Heartbeat dilewati: socket lokal dianggap tidak terhubung");
                AgentLog.Write("Heartbeat dilewati: socket lokal dianggap tidak terhubung");
            }
            _socketMati = true;
            return;
        }
        try
        {
            await client.EmitAsync("agent:heartbeat", [ new { pcId = _pcId } ], ct);
            _aktivitasSuksesUtc = DateTime.UtcNow;
            _gagalHeartbeat = 0;
            _logger.LogDebug("agent:heartbeat → PC {PcId}", _pcId);
        }
        catch (Exception ex)
        {
            // Tanpa catch ini, exception dilempar dari dalam async void TimerCallback
            // dan hilang tanpa trace.
            _logger.LogError(ex, "Gagal mengirim agent:heartbeat untuk PC {PcId}", _pcId);
            AgentLog.Write(ex, "Gagal kirim agent:heartbeat");
            _gagalHeartbeat++;
// JANGAN hanya melog: itu yang membuat PC offline 8 jam. Setelah
            // dua gagal beruntun (30 detik) socket dinyatakan mati supaya
            // supervisor benar-benar connect ulang dengan instance baru.
            if (_gagalHeartbeat >= GAGAL_HEARTBEAT_UNTUK_MATI)
            {
                TandaiSocketMati(
                    $"{_gagalHeartbeat}x heartbeat gagal: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }

    /// <summary>Client (overlay) minta login voucher/member ke server.</summary>
    public async Task SendLoginRequestAsync(string kode, string password, CancellationToken ct = default)
    {
        var client = _client;
        if (client == null || !client.Connected)
        {
            AgentLog.Write("client:login_request ditolak — socket tidak terhubung");
            throw new InvalidOperationException("Belum terhubung ke server.");
        }
        try
        {
            await client.EmitAsync("client:login_request", [ new
            {
                pcId = _pcId,
                kredensial = new { kode, password },
            } ], ct);
            _aktivitasSuksesUtc = DateTime.UtcNow;
            _logger.LogInformation("client:login_request untuk kode {Kode}", kode);
        }
        catch (Exception ex)
        {
            // Jangan biarkan exception lepas ke pemanggil. Jalur ini dipanggil
            // dari NamedPipeServer yang tidak punya try/catch per-pesan, jadi
            // satu EmitAsync yang gagal akan MEMBUAT PIPA MATI — bukan hanya
            // gagal satu perintah.
            _logger.LogError(ex, "Gagal mengirim client:login_request");
            AgentLog.Write(ex, "Gagal kirim client:login_request");
            throw new InvalidOperationException("Gagal mengirim permintaan login ke server.", ex);
        }
    }

    /// <summary>
    /// Client (overlay) minta dibuatkan password baru untuk sebuah kode.
    /// </summary>
    /// <remarks>
    /// Password lama ikut dikirim dan dicocokkan di server, karena yang memakai
    /// komputer adalah pemilik akun itu sendiri.
    /// </remarks>
    public async Task<CreatePasswordResultPayload?> SendCreatePasswordAsync(
        string kode,
        string passwordLama,
        string password,
        CancellationToken ct = default)
    {
        if (_client == null || !_client.Connected) return null;

        // SocketIOClient 4.x tidak punya EmitWithAckAsync. Ack diambil lewat
        // overload EmitAsync yang menerima callback.
        //
        // PENTING: await EmitAsync(...) hanya menunggu paket terkirim, TIDAK
        // menunggu balasan. Callback dipanggil sekitar setengah detik kemudian,
        // jadi kalau hasilnya langsung dibaca setelah await, nilainya selalu
        // null. Karena itu balasan ditunggu lewat TaskCompletionSource.
        var tcs = new TaskCompletionSource<CreatePasswordResultPayload?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _client.EmitAsync(
                AgentEvents.ClientCreatePassword,
                new object[] { new { pcId = _pcId, kode, passwordLama, password } },
                response =>
                {
                    tcs.TrySetResult(BacaAck(response));
                    return Task.CompletedTask;
                },
                ct);
        }
        catch (Exception ex)
        {
            // Tanpa try/catch, socket half-open membuat pipe mati (lihat
            // SendStopSessionAsync). Pemanggil sudah menampilkan
            // "Gagal mengganti password" dari nilai null.
            AgentLog.Write(ex, "Gagal kirim client:create_password");
            return null;
        }

        // PENTING: overload CancelAfter(int) satuannya MILIDETIK. dalam
        // milidetik membuat balasan dibatalkan sebelum sempat datang, dan
        // hasilnya null tanpa ada pesan apa pun.
        using var batasWaktu = new CancellationTokenSource(ACK_TIMEOUT_MILIDETIK);
        try
        {
            using var gabung = CancellationTokenSource.CreateLinkedTokenSource(ct, batasWaktu.Token);
            return await tcs.Task.WaitAsync(gabung.Token);
        }
        catch (OperationCanceledException)
        {
            AgentLog.Write("create_password: tidak ada balasan server sebelum batas waktu");
            return null;
        }
    }

    /// <summary>Baca balasan ack dari server.</summary>
    /// <remarks>
    /// Argumen ack dari NestJS adalah objek, bukan string. Karena itu
    /// <c>GetValue&lt;string&gt;(0)</c> melempar JsonException dan tidak pernah
    /// sampai ke cabang fallback. Jawaban yang dikembalikan objek itu
    /// { success: true } tanpa message, dan karena readers tidak punya isinya,
    /// hasil sukses maupun gagal sama-sama terlihat sebagai kegagalan.
    /// <para>
    /// <c>RawText</c> juga tidak bisa dipakai: isinya berbentuk larik
    /// [{ ... }], sedangkan yang dibutuhkan objek tunggal.
    /// </para>
    /// </remarks>
    private static CreatePasswordResultPayload? BacaAck(IDataMessage? response)
    {
        if (response == null) return null;
        try
        {
            var json = response.GetValue<JsonElement>(0).GetRawText();
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<CreatePasswordResultPayload>(json);
        }
        catch (Exception ex)
        {
            // Jangan diam: tanpa baris ini, kegagalan baca ack sama sekali
            // tidak terlihat dan gejalanya hanya "gagal" generik.
            AgentLog.Write($"BacaAck gagal: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    /// <summary>Client (overlay) minta berhenti dari sesi yang sedang berjalan.</summary>
    public async Task SendStopSessionAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client == null || !client.Connected)
        {
            AgentLog.Write("client:stop_session ditolak — socket tidak terhubung");
            throw new InvalidOperationException("Belum terhubung ke server.");
        }
        try
        {
            await client.EmitAsync("client:stop_session", [ new { pcId = _pcId } ], ct);
            _aktivitasSuksesUtc = DateTime.UtcNow;
            _logger.LogInformation("client:stop_session dikirim untuk PC {PcId}", _pcId);
        }
        catch (Exception ex)
        {
            // WAJIB. Tanpa try/catch, socket yang sudah half-open (Connected
            // true tapi transport mati) membuat EmitAsync melempar, dan
            // exception-nya naik sampai PipeListenerAsync yang lalu menutup
            // pipe. Gejalanya persis "klik tombol STOP tidak berfungsi" —
            // tercatat di agent.log: stop diterima 11:30:56.532, pipe putus
            // 11:30:56.558.
            _logger.LogError(ex, "Gagal mengirim client:stop_session untuk PC {PcId}", _pcId);
            AgentLog.Write(ex, "Gagal kirim client:stop_session");
            throw new InvalidOperationException("Gagal menghentikan sesi ke server.", ex);
        }
    }

    private void StartHeartbeat()
    {
        lock (_heartbeatLock)
        {
            // Selalu buat timer baru, jangan ??=: StartHeartbeat dipanggil ulang dari
            // supervisor setiap reconnect, sementara timer sebelumnya masih non-null.
            _heartbeatTimer?.Dispose();
            // PANGGIL sinkron yang membungkus try/catch — bukan `async _ => ...`.
            // TimerCallback bertipe void, jadi lambda async jadi async void dan
            // setiap exception di dalamnya hilang tanpa trace.
            _heartbeatTimer = new Timer(
                _ => HeartbeatTick(),
                null,
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_DETIK),
                TimeSpan.FromSeconds(HEARTBEAT_INTERVAL_DETIK));
        }
        _logger.LogInformation("Heartbeat aktif: tiap {Interval}s", HEARTBEAT_INTERVAL_DETIK);
    }

    private void HeartbeatTick()
    {
        try
        {
            _heartbeatTickCount++;
            // Bukti timer masih hidup dicatat tiap TICK_LOG_INTERVAL tick, bukan
            // tiap tick — lihat catatan pada konstanta itu.
            //
            // `hidup=` memakai IsConnected (yang sudah bisa dipercaya), bukan
            // SocketIO.Connected mentah. Pada insiden 2 Okt keduanya berbeda:
            // library bilang true sementara kita sudah tahu socket itu mati.
            //
            // Selama heartbeat sedang gagal, tick dicatat setiap saat — justru
            // ini yang dicari saat menelusuri insiden.
            if (_gagalHeartbeat > 0 || _heartbeatTickCount % TICK_LOG_INTERVAL == 1)
            {
                AgentLog.WriteRutin(
                    $"Heartbeat tick #{_heartbeatTickCount} (hidup={IsConnected}, " +
                    $"gagalBerturut={_gagalHeartbeat})");
            }
            SendHeartbeatAsync(_lifetimeCts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heartbeat tick gagal untuk PC {PcId}", _pcId);
            AgentLog.Write(ex, "Heartbeat tick gagal");
        }
    }

    /// <summary>
    /// Matikan heartbeat DAN lepaskan timer-nya. Wajib mengosongkan referensi:
    /// kalau hanya <c>Change(Timeout.Infinite, ...)</c> maka objek Timer tetap non-null
    /// sehingga <c>StartHeartbeat</c> berikutnya menganggap timer masih hidup dan
    /// tidak pernah membuatnya — heartbeat mati permanen setelah reconnect, padahal
    /// socket masih connect dan log tetap bilang "Heartbeat aktif".
    /// </summary>
    private void StopHeartbeat()
    {
        lock (_heartbeatLock)
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopHeartbeat();
        _lifetimeCts.Cancel();

        var client = _client;
        _client = null;
        if (client != null)
        {
            try
            {
                if (client.Connected) await client.DisconnectAsync();
            }
            catch { /* socket sudah mati — tidak ada yang perlu dibersihkan */ }
            try { client.Dispose(); } catch { }
        }
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

public sealed class AdminLockEventArgs : EventArgs
{
    public AdminLockEventArgs(AdminLockPayload payload) => Payload = payload;
    public AdminLockPayload Payload { get; }
}

public sealed class AdminShutdownEventArgs : EventArgs
{
    public AdminShutdownEventArgs(AdminShutdownPayload payload) => Payload = payload;
    public AdminShutdownPayload Payload { get; }
}

public sealed class ServerLinkEventArgs : EventArgs
{
    public ServerLinkEventArgs(bool connected, string? reason = null)
    {
        Connected = connected;
        Reason = reason;
    }

    /// <summary>true = tersambung ke server, false = terputus.</summary>
    public bool Connected { get; }

    /// <summary>Alasan putusnya koneksi (hanya diisi saat Connected=false).</summary>
    public string? Reason { get; }
}

/// <summary>Hash PIN bypass yang dikirim server.</summary>
public sealed class BypassConfigEventArgs : EventArgs
{
    public BypassConfigEventArgs(string hash) => Hash = hash;

    /// <summary>Hash bcrypt PIN bypass. Kosong = kembali ke PIN emergency bawaan.</summary>
    public string Hash { get; }
}

public sealed class NextcloudConfigEventArgs : EventArgs
{
      public NextcloudConfigEventArgs(string url, string user, string pass, string folder, string nama)
      {
          Url = url;
          User = user;
          Pass = pass;
          Folder = folder;
          Nama = nama;
      }

      /// <summary>Nama PC yang bisa dibaca manusia. Kosong = pakai pcId.</summary>
      public string Nama { get; }

      /// <summary>URL dasar Nextcloud. Kosong = fitur log ke Nextcloud dimatikan.</summary>
      public string Url { get; }

      /// <summary>Username Nextcloud. Kosong = fitur dimatikan.</summary>
      public string User { get; }

      /// <summary>Password Nextcloud.</summary>
      public string Pass { get; }

      /// <summary>Folder tujuan. Kosong = agent memakai folder bawaannya.</summary>
      public string Folder { get; }
}

public sealed class OtpConfigEventArgs : EventArgs
{
    public OtpConfigEventArgs(string botToken, string chatId)
    {
        BotToken = botToken;
        ChatId = chatId;
    }

    /// <summary>Token bot Telegram. Kosong = fitur OTP dimatikan.</summary>
    public string BotToken { get; }

    /// <summary>Chat id tujuan. Kosong = fitur OTP dimatikan.</summary>
    public string ChatId { get; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}
