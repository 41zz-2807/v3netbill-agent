using System;
using System.Diagnostics;
using System.Globalization;
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
/// <summary>
/// ⚠️ SALINAN dari enum yang sama di <c>Agent.Overlay/PipeClient.cs</c>. Lihat
/// catatan di sana: nilai baru wajib ditambah di kedua file.
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
    ServerLink = 7,         // Status koneksi ke server berubah (terhubung/terputus)
    OtpResult = 8,          // Hasil permintaan OTP (berhasil/gagal + pesan)
    CreatePasswordResult = 9, // Hasil permintaan buat/ganti password
    PowerResult = 10,        // Hasil permintaan restart/mematikan PC
    UpdateProgress = 11,       // Progres pembaruan otomatis (pesan, persen)

    // Overlay → Service
    LoginRequest = 100,     // Login request (kode, password)
    PinVerifyRequest = 101, // PIN verify request (pin)
    StateRequest = 102,     // Overlay minta state terkini (setelah reconnect)
    StopSessionRequest = 103, // Overlay minta hentikan sesi yang berjalan (stop sendiri)
    OtpRequest = 104,       // Overlay minta kirim OTP ke Telegram
    CreatePasswordRequest = 105, // Overlay minta dibuatkan password baru untuk sebuah kode
    PowerRequest = 106,      // Overlay minta restart / matikan PC ini
    BatalUpdateRequest = 107 // Teknisi membatalkan pembaruan yang sedang berjalan
}

internal record PipeMessage(PipeMessageType Type, string Payload);

/// <summary>
/// BackgroundService worker: menjalankan ServerConnection + named pipe server + registry/watchdog.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly OtpService _otp;
    private ServerConnection? _serverConnection;
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipeServer;
    private Task? _pipeListenerTask;
    private Timer? _watchdogTimer;
        private Timer? _nextcloudTimer;
    private Timer? _autoUpdateTimer;
    private bool _autoUpdateAktif;
    private volatile bool _autoUpdateBatal;
    /// <summary>Penanda timer berkala sedang dimatikan.</summary>
    /// <remarks>
    /// <c>System.Threading.Timer</c> tidak punya properti <c>Enabled</c>, jadi
    /// statusnya harus disimpan sendiri. Tanpa penanda ini, menyalakan ulang
    /// timer setelah flag dimatikan tidak bisa dibedakan dari tidak pernah
    /// dimatikan — dan hasilnya timer yang dijadwalkan dua kali.
    /// </remarks>
    private bool _timerAutoUpdateMati;
    private volatile string _autoUpdateTahap = "";
    private int _autoUpdateGagalBeruntun;
    // ⚠️ WAJIB ada. Tanpa ini, timer 6 jam dan event dari server bisa masuk
    // bersamaan lalu dua msiexec jalan bersamaan untuk satu MSI yang sama —
    // hasil yang paling mungkin: instalasi gagal setengah jalan dan PC tidak
    // bisa billing.
    private readonly SemaphoreSlim _autoUpdateLock = new(1, 1);
    private Task? _maintainTask;
    private SessionState _currentState = new() { State = SessionState.LockState.Locked };
    private string _overlayExePath = string.Empty;
    private readonly object _stateLock = new();
    // NamedPipeServerStream TIDAK thread-safe untuk write bersamaan:
    // dua WriteAsync pada instance sama tanpa sinkronisasi dapat korup/gagal.
    private readonly SemaphoreSlim _pipeWriteLock = new(1, 1);

    /// <summary>Jeda antar percobaan connect ulang ke backend.</summary>
    private const int RECONNECT_INTERVAL_DETIK = 5;

    /// <summary>
    /// Batas waktu satu percobaan connect. WAJIB ada: SocketIOClient.ConnectAsync()
    /// tidak menerima CancellationToken, jadi dengan Reconnection=true satu panggilan
    /// bisa memblokir totalTimeout = ConnectionTimeout x ReconnectionAttempts
    /// (30s x 30 = 15 menit) dan supervisor ikut macet. Dengan Reconnection=false
    /// (lihat ServerConnection) satu attempt selesai dalam ~30s, jadi 45 detik
    /// hanya jaring pengaman.
    /// </summary>
    private const int RECONNECT_TIMEOUT_DETIK = 45;

    public Worker(ILogger<Worker> logger, IConfiguration configuration, OtpService otp)
    {
        _logger = logger;
        _configuration = configuration;
        _otp = otp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentLog.FileName = "agent.log";
        AgentLog.Write("=== v3Netbill Agent Service starting ===");
        _logger.LogInformation("v3Netbill Agent Service starting...");

        // Migrasi flag versi lama, lalu cek mode maintenance SEBELUM apa pun.
        //
        // ⚠️ Urutannya penting. Dulu service selalu memblokir Task Manager saat
        // start, jadi ketika MSI memasang ulang agent di PC yang sedang
        // maintenance, Task Manager ikut terkunci — padahal justru alat pertama
        // yang dipakai teknisi untuk mencari masalah. Mode maintenance tidak
        // pernah boleh memblokir Task Manager.
        bool maintenance = false;
        try
        {
            FlagPaths.MigrasiFlagLama();
            maintenance = FlagPaths.MaintenanceAktif();
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal memeriksa mode maintenance saat start");
        }

        if (maintenance)
        {
            AgentLog.Write(
                $"MODE MAINTENANCE AKTIF saat start (sejak {FlagPaths.MaintenanceSince() ?? "tidak diketahui"}, " +
                $"alasan: {FlagPaths.MaintenanceReason() ?? "tidak diisi"}) — " +
                "PC TERBUKA tanpa billing; Task Manager dibiarkan terbuka");
        }

        // Mode: PC terkunci saat idle (tanpa sesi). Mulai dengan blokir Task Manager.
        SetTaskManagerBlocked(!maintenance);

        // Terapkan pengaturan daya PC (sleep / screen off / screensaver).
        // Default "true": PC warnet tidak boleh tidur / mati layar / muncul
        // screensaver, karena ketiganya membuat pelanggan berhenti billing tanpa
        // tahu kenapa.
        var preventSleep = GetConfig("Agent:PreventSleep", "PreventSleep", "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        ApplyPowerSettings(preventSleep);

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
        _serverConnection.ServerLinkChanged += OnServerLinkChanged;
        _serverConnection.OtpConfigReceived += OnOtpConfigReceived;
        _serverConnection.NextcloudConfigReceived += OnNextcloudConfigReceived;
        _serverConnection.BypassConfigReceived += OnBypassConfigReceived;
        _serverConnection.PowerConfigReceived += OnPowerConfigReceived;
        _serverConnection.AutoUpdateConfigReceived += OnAutoUpdateConfigReceived;

        // Supervisor koneksi. SocketIOClient 4.x hanya mencoba retry SELAMA
        // ConnectAsync() masih berjalan (ReconnectionAttempts=30); begitu koneksi
        // sukses lalu putus — mis. backend restart/deploy — TIDAK ada retry lagi
        // dan ConnectAsync() tidak pernah dipanggil ulang. Akibatnya agent offline
        // permanen. Loop di bawah yang menutup celah tersebut.
        _maintainTask = MaintainConnectionAsync(pcId, _cts.Token);

        // Start named pipe server
        _ = StartPipeServerAsync(_cts.Token);

        // Kirim log ke Nextcloud tiap 5 menit. Berdiri sendiri dari koin log
        // billing, jadi log tetap terkirim walau server v3netbill tak terjangkau.
        _nextcloudTimer = new Timer(
            _ => _ = KirimLogNextcloud(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        // Start watchdog timer (check every 5 seconds)
        _watchdogTimer = new Timer(WatchdogCallback, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Pembaruan otomatis (self-update). Berkala, dan SENGAJA jeda awalnya
        // lama supaya service tidak menabrak server tepat saat start.
        //
        // ⚠️ Default mati: _autoUpdateAktif hanya jadi true kalau server
        // mengatakan sakelarnya nyata. Kalau server tidak bisa dihubungi, field
        // ini tetap false dan TIDAK ADA percobaan install sama sekali.
        _autoUpdateTimer = new Timer(
            _ => _ = CekPembaruanOtomatis(_cts?.Token ?? default),
            null,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(6));

        // ⚠️ Baca flag dari registry SEBELUM timer dipasang.
        //
        // Mati listrik di tengah unduhan berarti service mati mendadak, dan
        // tanpa registry flag yang ada hanya di memori hilang bersama proses.
        // PC lalu kembali normal, update tidak dilanjutkan, dan operator
        // mengira update sudah berjalan padahal tidak pernah selesai.
        _autoUpdateAktif = BacaAutoUpdateDariRegistry();
        if (_autoUpdateAktif)
        {
            AgentLog.Write("Auto-update: flag ditemukan di registry — dilanjutkan setelah start");
            // 🔴 Timer berkala DINONAKTIFKAN selama flag nyala (lihat AturTimerAutoUpdate).
        }
        AturTimerAutoUpdate();

        // Keep alive until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _watchdogTimer?.Dispose();
        _nextcloudTimer?.Dispose();
            _autoUpdateTimer?.Dispose();
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
                    // WaitAsync(timeSpan) membuka await-if ini kalau ConnectAsync()
                    // menggantung, supaya supervisor tidak pernah macet.
                    await _serverConnection
                        .ConnectAsync(ct)
                        .WaitAsync(TimeSpan.FromSeconds(RECONNECT_TIMEOUT_DETIK));
                    _logger.LogInformation("Agent connected & registered: {PcId}", pcId);
                    AgentLog.Write($"Connected & registered ke backend: pcId={pcId}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    _logger.LogError("Percobaan connect ke backend menggantung > {Detik} detik — dicoba lagi",
                        RECONNECT_TIMEOUT_DETIK);
                    AgentLog.Write($"Connect ke backend menggantung > {RECONNECT_TIMEOUT_DETIK} detik");
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
        // ⚠️ WAJIB true. Baris ini pernah `false` sejak commit 5f75dd3
        // (fitur idle-lock) — logikanya terbalik: Task Manager justru DIBUKA
        // justru saat pelanggan mulai sesi berbayar.
        //
        // Efeknya kebalikan dari yang diinginkan warnet: selama sesi berjalan
        // pelanggan bisa membuka Task Manager dan mematikan agent, jadi hitung
        // mundur berhenti tanpa tercatat dan PC tetap bisa dipakai. Dilaporkan
        // dari pemakaian nyata, dan lolos dari `dotnet build` maupun dari semua
        // log — karena tidak ada yang gagal, task manager-nya diam-diam boleh
        // dibuka saja.
        //
        // `false` hanya benar di dua tempat: service berhenti, dan mode
        // maintenance (lihat SetTaskManagerBlocked).

        SetTaskManagerBlocked(true);
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

        // 🔴 Ini yang membuat toggle per-PC berguna. Tanpa baris ini, update
        // hanya berjalan kalau timer 6 jam kebetulan jatuh — operator
        // menyalakan flag lalu menunggu tidak tahu berapa lama.
        //
        // Baris ini TIDAK awaits: method ini async void, jadi await di sini
        // menahan event handler. Dan ceknya `SesiSedangBerjalan()` yang baru
        // di atas sudah membaca _stateLock, yang di atas sudah dilepas.
        if (_autoUpdateAktif)
        {
            _ = CekPembaruanOtomatis(_cts?.Token ?? default);
        }
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

    /// <summary>Server mendorong config OTP baru → simpan ke registry PC (offline).</summary>
    /// <summary>
    /// Simpan hash PIN bypass yang didorong server ke registry, supaya overlay
    /// bisa memverifikasi PIN secara lokal tanpa perlu server.
    /// </summary>
    private void OnBypassConfigReceived(object? sender, BypassConfigEventArgs e)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            if (key == null)
            {
                _logger.LogWarning("Registry HKLM tidak bisa dibuka — PIN bypass tidak disimpan");
                return;
            }

            if (string.IsNullOrWhiteSpace(e.Hash))
            {
                key.DeleteValue("BypassPinHash", false);
                AgentLog.Write("PIN bypass dikosongkan dari server — kembali ke PIN emergency bawaan");
                return;
            }

            key.SetValue("BypassPinHash", e.Hash, RegistryValueKind.String);
            AgentLog.Write("Hash PIN bypass diterima dari server — disimpan ke registry");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal menyimpan hash PIN bypass");
        }
    }

    /// <summary>
    /// Terima dorongan konfigurasi daya dari server, simpan ke registry, lalu
    /// terapkan ke PC ini.
    /// </summary>
    private void OnPowerConfigReceived(object? sender, PowerConfigEventArgs e)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            key?.SetValue("PreventSleep", e.PreventSleep ? "true" : "false", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal menyimpan PreventSleep ke registry");
        }

        ApplyPowerSettings(e.PreventSleep);
    }

    /// <summary>
    /// Terapkan pengaturan daya PC: sleep, screen off, dan screensaver.
    ///
    /// <para>
    /// <c>preventSleep = true</c> berarti:
    /// <list type="bullet">
    /// <item>PC tidak tidur (standby timeout = 0 untuk AC dan DC)</item>
    /// <item>Layar tidak mati sendiri (monitor timeout = 0 untuk AC dan DC)</item>
    /// <item>Screensaver tidak muncul (ScreenSaveActive = 0 di semua profil user)</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <c>preventSleep = false</c> mengembalikan ke pengaturan Windows normal
    /// (standby 20/10 menit, monitor 10/5 menit, screensaver dibiarkan).
    /// </para>
    ///
    /// <remarks>
    /// ⚠️ <b>powercfg mengubah skema daya AKTIF untuk semua pengguna.</b> Ini
    /// perubahan sistem-wide, bukan per-user. Kalau PC ini dipakai untuk hal lain
    /// di luar warnet, pengikutannya akan terasa — itu konsekuensi yang disengaja.
    ///
    /// ⚠️ <b>Screensaver ditulis ke SEMUA profil di HKEY_USERS</b>, sama seperti
    /// pola <see cref="SetTaskManagerBlocked"/>. Service jalan sebagai SYSTEM, jadi
    /// Registry.CurrentUser menunjuk hive SYSTEM dan tidak pernah sampai ke akun
    /// pelanggan yang sedang login.
    /// </remarks>
    /// </summary>
    private void ApplyPowerSettings(bool preventSleep)
    {
        try
        {
            if (preventSleep)
            {
                // Simpan nilai asli SEBELUM diubah, supaya bisa dikembalikan.
                SimpanNilaiDayaAsli();

                // Standby timeout 0 = tidak pernah tidur.
                JalankanPowerCfg("/change", "standby-timeout-ac", "0");
                JalankanPowerCfg("/change", "standby-timeout-dc", "0");
                // Monitor timeout 0 = layar tidak pernah mati sendiri.
                JalankanPowerCfg("/change", "monitor-timeout-ac", "0");
                JalankanPowerCfg("/change", "monitor-timeout-dc", "0");

                SetScreensaver(false);
                AgentLog.Write("Pengaturan daya: sleep OFF, screen off OFF, screensaver OFF");
            }
            else
            {
                // Kembalikan nilai asli kalau pernah disimpan.
                if (PulihkanNilaiDayaAsli())
                {
                    SetScreensaver(true);
                    AgentLog.Write("Pengaturan daya: dikembalikan ke nilai asli Windows");
                }
                else
                {
                    // Belum pernah disimpan = PC belum pernah diubah, biarkan.
                    AgentLog.Write("Pengaturan daya: tidak ada nilai asli yang perlu dipulihkan");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal menerapkan pengaturan daya");
            AgentLog.Write(ex, "Gagal menerapkan pengaturan daya");
        }
    }

    // ==================== PEMBARUAN OTOMATIS (SELF-UPDATE) ====================

    /// <summary>
    /// Terima status sakelar auto-update dari server.
    /// </summary>
    /// <remarks>
    /// Yang dikirim cuma "sakelarnya nyala/tidak". TIDAK ada perintah install
    /// di sini, dan itu disengaja: yang tahu versinya sendiri, yang tahu
    /// sedang ada sesi, dan yang tahu teknisi sedang memperbaiki PC —
    /// semuanya ada di PC ini, bukan di server.
    /// </remarks>
    private void OnAutoUpdateConfigReceived(object? sender, AutoUpdateConfigEventArgs e)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            key?.SetValue("AutoUpdateEnabled", e.Aktif ? "true" : "false", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal menyimpan AutoUpdateEnabled ke registry");
        }

        if (_autoUpdateAktif == e.Aktif) return;
        _autoUpdateAktif = e.Aktif;
        _autoUpdateBatal = false;

        AgentLog.Write($"Auto-update {(e.Aktif ? "DINYALAKAN" : "dimatikan")} dari server");

        // 🔴 WAJIB setiap kali status berubah, BUKAN hanya saat dinyalakan.
        // Mematikan flag harus mengembalikan timer 6 jam; tanpa baris ini PC
        // yang pernah diberi flag tidak akan pernah ikut timer lagi seumur
        // program.
        AturTimerAutoUpdate();

        if (e.Aktif)
        {
            // Sakelar baru dinyalakan: cek sekarang, jangan tunggu timer 6 jam.
            _ = CekPembaruanOtomatis(_cts?.Token ?? default);
        }
        // Sakelar dimatikan TIDAK membuat apa-apa. Kalau sedang mengunduh, proses
        // yang sedang jalan menyelesaikan hashing-nya, lalu installation dicek
        // ulang di bawah — dan `_autoUpdateAktif` sudah false, jadi berhenti.
    }

    /// <summary>
    /// Alur lengkap: tanya server → putuskan → unduh → verifikasi → pasang.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Semua kegagalan DITELAN dan hanya ditulis ke log. Method ini dipanggil
    /// dari <c>Timer</c> yang tidak di-await, jadi penolakan apa pun akan jadi
    /// unhandled rejection — dan service yang mati berarti PC berhenti billing.
    /// </para>
    /// <para>
    /// <b>Kondisi PC dibaca DUA KALI.</b> Yang pertama sebelum unduhan, yang
    /// kedua tepat sebelum <c>msiexec</c>. Unduhan 89 MB bisa memakan puluhan
    /// detik, dan dalam selang itu pelanggan bisa saja login. Kalau hanya
    /// dibaca sekali di awal, installer akan berjalan di atas sesi yang baru
    /// dimulai.
    /// </para>
    /// </remarks>
    private async Task CekPembaruanOtomatis(CancellationToken cancel)
    {
        if (!_autoUpdateAktif) return;

        // Cegah dua proses tumpang tindih: timer dan event push bisa keduanya
        // memicu dalam waktu yang berdekatan.
        if (!await _autoUpdateLock.WaitAsync(0, cancel)) return;

        try
        {
            // 🔴 Batas percobaan DAN jeda, dua-duanya wajib.
            //
            // Tanpa batas dua kali, satu MSI yang rusak akan dicoba ulang
            // setiap 6 jam selama PC menyala — dan operator akan melihat
            // "PC nyala sendiri terus" tanpa pernah paham kenapa. Setelah
            // batas kedua, agent berhenti dan melapor; flag di server sengaja
            // TIDAK dilepas supaya operator pegang kendali.
            if (!AutoUpdater.MasihBolehPercobaan(
                    _autoUpdateGagalBeruntun, DateTime.UtcNow - BacaPercobaanTerakhir()))
            {
                if (_autoUpdateGagalBeruntun >= AutoUpdater.MaksPercobaanGagal)
                {
                    LaporkanProgres("gagal", null);
                    AgentLog.Write(
                        $"Auto-update: BERHENTI setelah {_autoUpdateGagalBeruntun}x gagal. " +
                        "Menunggu operator — flag di server sengaja tidak dilepas.");
                }
                return;
            }

            if (_autoUpdateBatal)
            {
                AgentLog.Write("Auto-update: dibatalkan teknisi");
                _autoUpdateAktif = false;
                return;
            }

            if (!BolehUpdate()) return;

            using var klien = KlienPembaruan();
            if (klien == null) return;

            var info = await klien.CekAsync(cancel);
            if (info == null)
            {
                CatatGagalUpdate("server tidak bisa dihubungi atau token ditolak");
                return;
            }

            var versiLokal = BacaVersiTerpasang();
            var keputusan = AutoUpdater.Putuskan(
                versiLokal, info, SesiSedangBerjalan(), FlagPaths.MaintenanceAktif(), PcTerkunci());

            if (keputusan != KeputusanUpdate.PerluUpdate)
            {
                // Berhasil bicara dengan server: agent sehat, reset penghitung.
                _autoUpdateGagalBeruntun = 0;
                if (keputusan == KeputusanUpdate.VersiTidakSah)
                {
                    AgentLog.Write(
                        "Auto-update: versi MSI di server tidak terbaca — dilewati, JANGAN menebak");
                }
                return;
            }

            AgentLog.Write(
                $"Auto-update: versi {info.Versi} tersedia (terpasang {versiLokal ?? "tidak diketahui"}). Mengunduh...");
            SimpanPercobaanTerakhir();

            LaporkanProgres("unduh", 0d);

            var berkas = await klien.UnduhAsync(
                info.Versi, info.UkuranBytes, cancel,
                new Progress<double>(r =>
                {
                    // ⚠️ Hanya dilompati kalau tahapnya masih "unduh".
                    // Unduhan bisa selesai setelah operator menekan batal, dan
                    // laporan "unduh" yang telat akan mengubah layar dari
                    // "dibatalkan" kembali jadi "sedang mengunduh".
                    if (_autoUpdateTahap == "unduh") LaporkanProgres("unduh", r);
                }));

            if (_autoUpdateBatal)
            {
                HapusBerkas(berkas);
                AgentLog.Write("Auto-update: dibatalkan teknisi saat mengunduh");
                _autoUpdateAktif = false;
                return;
            }

            if (berkas == null)
            {
                CatatGagalUpdate("unduhan gagal");
                return;
            }

            // ⚠️ WAJIB sebelum apa pun. Berkas terpotong atau diganti di jalan
            // akan membuat msiexec memasang apa saja ke PC ini.
            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                LaporkanProgres("verifikasi", null);
                var hash = await HitungSha256Async(berkas, cancel);
                if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    HapusBerkas(berkas);
                    CatatGagalUpdate(
                        $"sha256 tidak cocok (server {Ringkas(info.Sha256)}, lokal {Ringkas(hash)}) — berkas dibuang");
                    return;
                }
                AgentLog.Write($"Auto-update: sha256 cocok ({hash}).");
            }
            else
            {
                // Server tidak memberi hash = kita tidak punya dasar apa pun
                // untuk percaya pada berkas 89 MB dari jaringan. Menolak lebih
                // aman daripada menebak.
                HapusBerkas(berkas);
                CatatGagalUpdate("server tidak mengirim sha256 — dipasang");
                return;
            }

            // 🔴 PEMBACAAN KONDISI KEDUA. Ini yang melindungi sesi yang dimulai
            // selama unduhan berlangsung.
            if (!_autoUpdateAktif || !BolehUpdate())
            {
                HapusBerkas(berkas);
                AgentLog.Write("Auto-update: kondisi PC berubah saat mengunduh — dipasang");
                return;
            }

            LaporkanProgres("pasang", null);
            AgentLog.Write("Auto-update: memasang MSI (msiexec /qn, ForceReboot=1)...");
            if (!PasangInstaller(berkas))
            {
                HapusBerkas(berkas);
                CatatGagalUpdate("msiexec gagal");
                return;
            }

            // ⚠️ Baris ini sering TIDAK tercapai. MajorUpgrade menghentikan
            // service lebih dulu, jadi proses ini biasanya mati sebelum
            // msiexec selesai. Itu normal, bukan tanda kegagalan.
            AgentLog.Write("Auto-update: msiexec selesai tanpa menghentikan service.");
            _autoUpdateGagalBeruntun = 0;
            LaporkanProgres("selesai", null);
        }
        catch (OperationCanceledException)
        {
            AgentLog.Write("Auto-update: dibatalkan (service berhenti)");
        }
        catch (Exception ex)
        {
            // WAJIB: pemanggilnya Timer yang tidak ditunggu.
            CatatGagalUpdate(ex.Message);
        }
        finally
        {
            _autoUpdateLock.Release();
        }
    }

    /// <summary>
    /// PC boleh diperbarui sekarang? Semua syarat wajib terpenuhi.
    /// <summary>
    /// Nyalakan atau matikan timer berkala 6 jam.
    /// </summary>
    /// <remarks>
    /// <b>Timer WAJIB dimatikan selama flag auto-update nyala.</b>
    ///
    /// Tanpa penjaga ini, timer 6 jam bisa menyalakan update KEDUA sementara
    /// yang pertama belum selesai memasang: versi di server masih lebih baru
    /// karena MSI baru belum terpasang, dan msiexec yang pertama masih
    /// berjalan. Dua proses msiexec sekaligus berarti instalasi gagal setengah
    /// jalan, dan flag di server sudah terlanjur menandai "update sedang
    /// berjalan".
    ///
    /// Yang memicu update selama flag nyala hanya perintah server dan session
    /// yang baru berhenti — bukan waktu.
    /// </remarks>
    private void AturTimerAutoUpdate()
    {
        if (_autoUpdateTimer == null) return;
        try
        {
            if (_autoUpdateAktif)
            {
                // Tidak ada periode selama flag nyala. Yang memicu update
                // hanya perintah server dan session yang baru berhenti.
                _autoUpdateTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timerAutoUpdateMati = true;
                AgentLog.Write("Auto-update: timer berkala 6 jam DINONAKTIFKAN selama flag nyala");
            }
            else if (_timerAutoUpdateMati)
            {
                _autoUpdateTimer.Change(TimeSpan.FromMinutes(30), TimeSpan.FromHours(6));
                _timerAutoUpdateMati = false;
                AgentLog.Write("Auto-update: timer berkala 6 jam diaktifkan lagi");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal mengatur timer auto-update");
        }
    }

    /// <summary>Baca flag auto-update dari registry (bertahan melewati restart).</summary>
    private static bool BacaAutoUpdateDariRegistry()
    {
        var nilai = BacaReg("AutoUpdateEnabled", null);
        return string.Equals(nilai, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kirim progres ke DUA pihak: overlay lewat pipe, server lewat socket.
    /// </summary>
    /// <remarks>
    /// <b>Overlay harus diberi laporan, bukan server saja.</b> Orang yang
    /// duduk di depan PC adalah orang yang pertama perlu tahu sedang ada
    /// "--PC sedang diperbarui, jangan dimatikan".
    ///
    /// Tanpa laporan, overlay menampilkan layar login biasa sementara service
    /// sedang memasang — dan orang itu bisa mengira PC-nya macet lalu menekan
    /// tombol matikan.
    /// </remarks>
    private void LaporkanProgres(string tahap, double? rasioUnduhan)
    {
        var (persen, pesan) = AutoUpdater.TahapProgress(tahap, rasioUnduhan);
        _autoUpdateTahap = tahap;

        // Pipe dulu: overlay adalah yang dilihat orang di depan PC.
        _ = SendToOverlayAsync(new PipeMessage(
            PipeMessageType.UpdateProgress,
            Newtonsoft.Json.JsonConvert.SerializeObject(new { pesan, persen, tahap })));

        _serverConnection?.KirimAutoUpdateProgress(pesan, persen, tahap);
    }

    private bool BolehUpdate()
    {
        if (SesiSedangBerjalan())
        {
            // Ini kondisi NORMAL, bukan kegagalan. Jangan sentuh
            // `_autoUpdateGagalBeruntun` — kalau tidak, PC yang sedang dipakai
            // pelanggan akan terlihat "gagal" dan masuk jeda satu jam.
            return false;
        }
        if (!PcTerkunci()) return false;
        if (FlagPaths.MaintenanceAktif()) return false;
        return true;
    }

    private bool SesiSedangBerjalan()
    {
        lock (_stateLock)
        {
            return _currentState.SedangBerjalan;
        }
    }

    private bool PcTerkunci()
    {
        lock (_stateLock)
        {
            return _currentState.State == SessionState.LockState.Locked;
        }
    }

    private static string? BacaVersiTerpasang() => BacaReg("AgentVersion", null);

    private PembaruanClient? KlienPembaruan()
    {
        var serverUrl = GetConfig("Server:Url", "ServerUrl", "http://localhost:3000");
        var pcId = GetConfig("Agent:PcId", "PcId", "PC001");
        var agentToken = GetConfig("Agent:Token", "AgentToken", "");
        if (string.IsNullOrWhiteSpace(agentToken))
        {
            AgentLog.Write("Auto-update: AgentToken kosong — dilewati");
            return null;
        }
        return new PembaruanClient(serverUrl, pcId, agentToken);
    }

    /// <summary>
    /// Pasang MSI secara senyap.
    /// </summary>
    /// <returns>true kalau msiexec keluar dengan kode 0 atau 3010.</returns>
    /// <remarks>
    /// <para>
    /// ⚠️ <c>/qn</c> tanpa UI. Installer yang muncul di layar PC warnet akan
    /// menghalangi pelanggan; dan kalau ada yang menekan Cancel di tengah,
    /// MSI bisa tertinggal setengah terpasang — dan <b>PC itu tidak bisa billing
    /// sampai teknisi memperbaikinya</b>.
    /// </para>
    /// <para>
    /// ⚠️ <c>/norestart</c>: PC tidak boleh restart sendiri. Kalau memang perlu
    /// restart, <c>3010</c> (REBOOT_REQUIRED) diperlakukan sebagai sukses —
    ///Pemasangan sudah selesai, hanya restart yang tertunda.
    /// </para>
    /// </remarks>
    private bool PasangInstaller(string path)
    {
        var log = path + ".log";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                // 🔴 ForceReboot=1 itu WAJIB, bukan tambahan.
                //
                // Tanpa itu, MSI selesai tetapi PC tidak restart. Akibatnya
                // overlay masih versi lama, dan flag auto-update di server
                // tidak pernah lepas karena versi agent tidak naik — PC
                // terkunci selamanya: menolak sesi, tidak mau dimatikan
                // otomatis, dan tidak bisa update.
                //
                // REBOOT=ReallySuppress dipakai bersamaan supaya restart
                // dilakukan oleh MSI secara diam-diam, bukan lewat UI reboot
                // Windows yang bisa menggantung di depan layar.
                Arguments = $"/i \"{path}\" /qn /norestart REBOOT=ReallySuppress ForceReboot=1 /l*v \"{log}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proses = Process.Start(psi);
            if (proses == null)
            {
                AgentLog.Write("Auto-update: msiexec tidak bisa dijalankan");
                return false;
            }

            if (!proses.WaitForExit(AutoUpdater.TimeoutPasangDetik * 1000))
            {
                AgentLog.Write("Auto-update: msiexec melewati batas waktu");
                return false;
            }

            // 0 = sukses. 3010 = sukses tapi perlu restart (ditunda).
            if (proses.ExitCode is 0 or 3010)
            {
                AgentLog.Write($"Auto-update: msiexec keluar dengan kode {proses.ExitCode}. Log: {log}");
                return true;
            }

            AgentLog.Write($"Auto-update: msiexec keluar dengan kode {proses.ExitCode}. Log: {log}");
            return false;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Auto-update: menjalankan msiexec gagal: {ex.Message}");
            return false;
        }
    }

    private static async Task<string?> HitungSha256Async(string path, CancellationToken cancel)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            await using var stream = File.OpenRead(path);
            var hash = await sha.ComputeHashAsync(stream, cancel);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Auto-update: gagal menghitung sha256: {ex.Message}");
            return null;
        }
    }

    private static void HapusBerkas(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Auto-update: tidak bisa menghapus {path}: {ex.Message}");
        }
    }

    private static string Ringkas(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "(kosong)" : (s.Length > 20 ? s[..20] + "..." : s);

    private void CatatGagalUpdate(string alasan)
    {
        _autoUpdateGagalBeruntun++;
        SimpanPercobaanTerakhir();
        AgentLog.Write($"Auto-update DILEWATI ({_autoUpdateGagalBeruntun}x): {alasan}");
    }

    /// <summary>
    /// Waktu percobaan terakhir, dari registry.
    /// </summary>
    /// <remarks>
    /// ⚠️ WAJIB di registry, bukan hanya field di memory. Gagal update berarti
    /// service di-restart oleh MSI — dan kalau jeda hanya disimpan di memory,
    /// PC yang baru nyala akan langsung mencoba lagi tanpa jeda, berulang
    /// setiap restart. Itu persis yang membuat MSI rusak terasa seperti
    /// "PC nyala-nyal sendiri".
    /// </remarks>
    private static DateTime BacaPercobaanTerakhir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            var teks = key?.GetValue("AutoUpdateLastAttempt") as string;
            if (long.TryParse(teks, out var ticks))
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        catch
        {
            // registry tidak terbaca = belum pernah mencoba
        }
        return DateTime.MinValue;
    }

    private static void SimpanPercobaanTerakhir()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            key?.SetValue(
                "AutoUpdateLastAttempt",
                DateTime.UtcNow.Ticks.ToString(),
                RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Auto-update: tidak bisa menyimpan waktu percobaan: {ex.Message}");
        }
    }

    private void JalankanPowerCfg(string perintah, string nama, string nilai)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"{perintah} {nama} {nilai}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal menjalankan powercfg {Perintah} {Nama} {Nilai}", perintah, nama, nilai);
        }
    }

    /// <summary>
    /// Simpan nilai timeout daya asli ke registry SEBELUM diubah.
    ///
    /// Hanya disimpan sekali — kalau sudah ada, jangan ditimpa, supaya nilai
    /// asli dari Windows tidak hilang.
    /// </summary>
    private void SimpanNilaiDayaAsli()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            if (key == null) return;

            // Kalau sudah pernah disimpan, jangan timpa.
            if (key.GetValue("PowerOriginalStandbyAc") != null) return;

            // Baca nilai asli lewat powercfg /query.
            var standbyAc = BacaPowerCfgValue("standby-timeout-ac");
            var standbyDc = BacaPowerCfgValue("standby-timeout-dc");
            var monitorAc = BacaPowerCfgValue("monitor-timeout-ac");
            var monitorDc = BacaPowerCfgValue("monitor-timeout-dc");

            key.SetValue("PowerOriginalStandbyAc", standbyAc, RegistryValueKind.String);
            key.SetValue("PowerOriginalStandbyDc", standbyDc, RegistryValueKind.String);
            key.SetValue("PowerOriginalMonitorAc", monitorAc, RegistryValueKind.String);
            key.SetValue("PowerOriginalMonitorDc", monitorDc, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal menyimpan nilai daya asli");
        }
    }

    /// <summary>
    /// Pulihkan nilai timeout daya asli dari registry.
    /// </summary>
    /// <returns>true kalau ada nilai asli yang dipulihkan.</returns>
    private bool PulihkanNilaiDayaAsli()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            if (key == null) return false;

            var standbyAc = key.GetValue("PowerOriginalStandbyAc") as string;
            var standbyDc = key.GetValue("PowerOriginalStandbyDc") as string;
            var monitorAc = key.GetValue("PowerOriginalMonitorAc") as string;
            var monitorDc = key.GetValue("PowerOriginalMonitorDc") as string;

            if (string.IsNullOrEmpty(standbyAc) && string.IsNullOrEmpty(standbyDc) &&
                string.IsNullOrEmpty(monitorAc) && string.IsNullOrEmpty(monitorDc))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(standbyAc))
                JalankanPowerCfg("/change", "standby-timeout-ac", standbyAc);
            if (!string.IsNullOrEmpty(standbyDc))
                JalankanPowerCfg("/change", "standby-timeout-dc", standbyDc);
            if (!string.IsNullOrEmpty(monitorAc))
                JalankanPowerCfg("/change", "monitor-timeout-ac", monitorAc);
            if (!string.IsNullOrEmpty(monitorDc))
                JalankanPowerCfg("/change", "monitor-timeout-dc", monitorDc);

            // Bersihkan setelah dipulihkan.
            using var tulis = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            tulis?.DeleteValue("PowerOriginalStandbyAc", false);
            tulis?.DeleteValue("PowerOriginalStandbyDc", false);
            tulis?.DeleteValue("PowerOriginalMonitorAc", false);
            tulis?.DeleteValue("PowerOriginalMonitorDc", false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal memulihkan nilai daya asli");
            return false;
        }
    }

    /// <summary>Baca satu nilai timeout dari powercfg /query.</summary>
    private string BacaPowerCfgValue(string nama)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/query {nama}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(10000);

            // Cari baris yang mengandung "Current AC Power Setting Index" atau
            // "Current DC Power Setting Index" lalu ambil angka hex terakhir.
            var baris = output.Split('\n')
                .FirstOrDefault(b => b.Contains("Current AC Power Setting Index") ||
                                     b.Contains("Current DC Power Setting Index"));
            if (baris == null) return "0";

            // Format: "Current AC Power Setting Index: 0x00000014"
            var bagian = baris.Split(':');
            if (bagian.Length < 2) return "0";
            var hex = bagian[1].Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(hex.Substring(2), NumberStyles.HexNumber, null, out var desimal))
                    return desimal.ToString();
            }
            return "0";
        }
        catch
        {
            return "0";
        }
    }

    /// <summary>
    /// Aktifkan / nonaktifkan screensaver untuk SEMUA profil user yang login.
    ///
    /// Pola yang sama dengan <see cref="SetTaskManagerBlocked"/>: tulis ke tiap
    /// profil di HKEY_USERS, bukan CurrentUser (karena service jalan sebagai SYSTEM).
    /// </summary>
    private void SetScreensaver(bool aktif)
    {
        const string keyPath = @"Control Panel\Desktop";
        var tersentuh = 0;

        try
        {
            using (var hives = Registry.Users)
            {
                foreach (string sid in hives.GetSubKeyNames())
                {
                    if (sid.Equals("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sid.StartsWith(".", StringComparison.Ordinal)) continue;

                    try
                    {
                        using var profil = hives.OpenSubKey(sid);
                        if (profil == null) continue;
                        using var key = profil.CreateSubKey(keyPath, true);
                        if (key == null) continue;

                        if (aktif)
                        {
                            // Hapus nilai = Windows memakai default (screensaver aktif).
                            key.DeleteValue("ScreenSaveActive", false);
                            key.DeleteValue("ScreenSaverIsSecure", false);
                            key.DeleteValue("ScreenSaveTimeOut", false);
                        }
                        else
                        {
                            key.SetValue("ScreenSaveActive", "0", RegistryValueKind.String);
                            key.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);
                            key.SetValue("ScreenSaveTimeOut", "0", RegistryValueKind.String);
                        }
                        tersentuh++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Gagal menulis screensaver untuk profil {Sid}", sid);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal mengubah screensaver");
            return;
        }

        try { BroadcastPolicyChange(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Gagal menyiarkan WM_SETTINGCHANGE"); }

        AgentLog.Write($"Screensaver {(aktif ? "diaktifkan" : "dimatikan")} untuk {tersentuh} profil user");
    }

    /// <summary>
    /// Simpan konfigurasi Nextcloud yang didorong server, lalu langsung coba kirim.
    /// </summary>
    /// <remarks>
    /// Nilai KOSONG dari server berarti admin sengaja mematikan fitur ini, jadi
    /// registry ikut dibersihkan. Kalau hanya "jangan timpa yang sudah ada",
    /// maka mematikan dari halaman Pengaturan tidak akan pernah berlaku, dan
    /// orang tidak punya cara menonaktifkannya tanpa memasang ulang MSI.
    /// </remarks>
    private async void OnNextcloudConfigReceived(object? sender, NextcloudConfigEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e.Url) || string.IsNullOrWhiteSpace(e.User))
            {
                FlagPaths.HapusKonfigurasiNextcloud();
                AgentLog.Write("Config Nextcloud dikosongkan dari server — upload log ke Nextcloud dimatikan");
                return;
            }

            FlagPaths.SimpanKonfigurasi("NextcloudUrl", e.Url);
            FlagPaths.SimpanKonfigurasi("NextcloudUser", e.User);
            FlagPaths.SimpanKonfigurasi("NextcloudPassword", e.Pass);
            if (!string.IsNullOrWhiteSpace(e.Folder))
            {
                FlagPaths.SimpanKonfigurasi("NextcloudFolder", e.Folder);
            }
            if (!string.IsNullOrWhiteSpace(e.Nama))
            {
                // Nama PC dibaca manusia. pcId berupa UUID dan mustahil dipetakan
                // orang ke PC tertentu, jadi nama inilah yang jadi nama berkas.
                FlagPaths.SimpanKonfigurasi("PcName", e.Nama);
            }

            AgentLog.Write(
                $"Config Nextcloud diterima dari server (folder: {e.Folder}) — disimpan ke registry");

            // Langsung coba kirim, jadi admin tidak harus menunggu 5 menit
            // untuk tahu apakah passwordnya benar.
            await KirimLogNextcloud();
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal menyimpan config Nextcloud dari server");
        }
    }

    private void OnOtpConfigReceived(object? sender, OtpConfigEventArgs e)
    {
        _otp.SimpanConfig(e.BotToken, e.ChatId);
        AgentLog.Write(e.Enabled
            ? $"Config OTP diterima dari server (chat {e.ChatId}) — disimpan ke registry"
            : "Config OTP dikosongkan dari server — OTP dimatikan, kembali ke PIN emergency");
    }

    /// <summary>Kirim status koneksi server saat ini ke overlay (dipakai juga saat overlay baru connect).</summary>
    private async Task SendCurrentServerLink()
    {
        bool terhubung = _serverConnection?.IsConnected == true;
        await SendToOverlayAsync(new PipeMessage(PipeMessageType.ServerLink, JsonConvert.SerializeObject(new
        {
            terhubung,
            alasan = (string?)null,
        })));
    }

    /// <summary>Teruskan status koneksi server ke overlay (indikator hijau/merah di card login).</summary>
    private async void OnServerLinkChanged(object? sender, ServerLinkEventArgs e)
    {
        try
        {
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.ServerLink, JsonConvert.SerializeObject(new
            {
                terhubung = e.Connected,
                alasan = e.Reason,
            })));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal mengirim status koneksi ke overlay");
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
        // Sama seperti di atas: sesi yang dihentikan dari PC ini juga harus
        // langsung memicu update kalau PC-nya ditandai.
        if (_autoUpdateAktif)
        {
            _ = CekPembaruanOtomatis(_cts?.Token ?? default);
        }
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

                // WAJIB try/catch sendiri. Tanpa ini, satu pesan yang gagal
                // ditangani (mis. socket server sudah mati) membuat exception
                // naik ke catch di bawah, yang mengakhiri loop DAN membuang
                // pipe. Gejalanya: overlay kehilangan pipa setiap kali satu
                // perintah gagal — tercatat di agent.log 11:30:56.532 stop
                // diterima, 11:30:56.558 pipe putus.
                try
                {
                    await HandleOverlayMessageAsync(msg, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Gagal menangani pesan overlay {Tipe}", msg.Type);
                    AgentLog.Write(ex, $"Gagal menangani pesan overlay {msg.Type} - pipa tetap hidup");

                    // JANGAN membalas SessionStopped di sini. Stop yang gagal
                    // terkirim berarti server tetap menghitung sesi; mengunci
                    // layar karena perintah lokal akan berbohong soal kondisi
                    // PC. Indikator "Terputus dari server" sudah muncul dari
                    // sisi lain (ServerLinkChanged saat socket dinyatakan mati),
                    // dan overlay menolak mengirim perintah saat itu terjadi.
                    if (msg.Type == PipeMessageType.LoginRequest)
                    {
                        await SendToOverlayAsync(new PipeMessage(
                            PipeMessageType.LoginResult,
                            JsonConvert.SerializeObject(new
                            {
                                sukses = false,
                                alasan = "Belum terhubung ke server. Coba lagi sebentar.",
                            })));
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipe read error");
                AgentLog.Write(ex, "Pipe read error");
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
                        // OTP dicek dulu secara LOKAL: jalur ini harus tetap jalan
                        // walau server mati — justru dipakai untuk mode maintenance.
                        // Kalau bukan OTP, baru jatuh ke verifikasi PIN backend.
                        var (otpOk, otpPesan) = await _otp.VerifikasiAsync(req.Pin, ct);
                        if (otpOk)
                        {
                            AgentLog.Write("PIN diterima sebagai OTP maintenance — bypass diberikan");
                            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PinVerifyResult,
                                JsonConvert.SerializeObject(new { sukses = true, viaOtp = true })));
                            break;
                        }
                        if (otpPesan != null)
                        {
                            // Ada OTP aktif tapi input tidak cocok — laporkan errornya
                            // supaya pengguna tahu harus minta OTP baru.
                            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PinVerifyResult,
                                JsonConvert.SerializeObject(new { sukses = false, pesan = otpPesan })));
                            break;
                        }

                        bool ok = await VerifyPinWithBackendAsync(req.Pin, ct);
                        var resp = new { sukses = ok };
                        await SendToOverlayAsync(new PipeMessage(PipeMessageType.PinVerifyResult, JsonConvert.SerializeObject(resp)));
                    }
                    break;
                }
            case PipeMessageType.OtpRequest:
                {
                    string pcId = GetConfig("Agent:PcId", "PcId", "PC001");
                    var otp = await _otp.KirimOtpAsync(pcId, ct);
                    var res = otp != null
                        ? new { sukses = true, pesan = $"OTP dikirim ke Telegram. Berlaku 5 menit." }
                        : new
                        {
                            sukses = false,
                            pesan = "Gagal mengirim OTP. Pastikan bot token & chat id sudah diisi di Pengaturan.",
                        };
                    if (otp == null) AgentLog.Write("Permintaan OTP gagal — OTP tidak dibuat");
                    await SendToOverlayAsync(new PipeMessage(PipeMessageType.OtpResult, JsonConvert.SerializeObject(res)));
                    break;
                }
            case PipeMessageType.BatalUpdateRequest:
                {
                    // ⚠️ Batal TIDAK berarti kill. Unduhan 89 MB yang sudah
                    // berjalan dibiarkan selesai, lalu dibuang sebelum
                    // msiexec. Membunuh proses unduhan di tengah jalan
                    // menyisakan berkas separuh di disk.
                    _autoUpdateBatal = true;
                    AgentLog.Write("Auto-update: teknisi menekan BATALKAN update");

                    // 🔴 KIRIM KE SERVER, bukan hanya ke overlay.
                    //
                    // Tanpa baris ini, flag autoUpdate masih true di database
                    // sementara agent sudah menyerah. PC itu menolak sesi, tidak
                    // dimatikan otomatis, dan tidak akan pernah update lagi --
                    // terkunci sampai operator membuka Halaman PC. Ini satu-satunya
                    // tempat di mana "menyerah" tidak berarti "berhenti".
                    LaporkanProgres("batal", null);

                    // Sakelar dimatikan DI SINI juga, supaya unduhan yang sedang
                    // berjalan berhenti di titik berikutnya dan tidak sempat
                    // memanggil msiexec.
                    _autoUpdateAktif = false;
                    AturTimerAutoUpdate();
                    using (var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true))
                    {
                        key?.SetValue("AutoUpdateEnabled", "false", RegistryValueKind.String);
                    }

                    await SendToOverlayAsync(new PipeMessage(
                        PipeMessageType.UpdateProgress,
                        JsonConvert.SerializeObject(new
                        {
                            pesan = "Pembaruan dibatalkan teknisi.",
                            persen = 0,
                            tahap = "batal",
                        })));
                    break;
                }
            case PipeMessageType.StateRequest:
                {
                    await SendStateUpdateAsync();
                    // Overlay bisa connect terlambat, jadi status koneksi wajib ikut
                    // dikirim ulang — kalau tidak, indikator di card login stuck di
                    // "Menghubungkan ke server..." selamanya.
                    await SendCurrentServerLink();
                    break;
                }
            case PipeMessageType.CreatePasswordRequest:
                {
                    var req = JsonConvert.DeserializeObject<CreatePasswordPayload>(msg.Payload);
                    if (req == null || string.IsNullOrWhiteSpace(req.Kode))
                    {
                        await SendToOverlayAsync(new PipeMessage(
                            PipeMessageType.CreatePasswordResult,
                            JsonConvert.SerializeObject(new { sukses = false, pesan = "Kode wajib diisi." })));
                        break;
                    }
                    if (_serverConnection == null || !_serverConnection.IsConnected)
                    {
                        await SendToOverlayAsync(new PipeMessage(
                            PipeMessageType.CreatePasswordResult,
                            JsonConvert.SerializeObject(new { sukses = false, pesan = "Belum terhubung ke server." })));
                        break;
                    }
                    var hasil = await _serverConnection.SendCreatePasswordAsync(req.Kode, req.PasswordLama, req.Password, ct);
                    await SendToOverlayAsync(new PipeMessage(
                        PipeMessageType.CreatePasswordResult,
                        JsonConvert.SerializeObject(new
                        {
                            sukses = hasil?.Sukses == true,
                            pesan = hasil?.Sukses == true
                                ? "Password berhasil diganti. Silakan login dengan password baru."
                                : hasil?.Alasan ?? "Gagal mengganti password.",
                        })));
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
            case PipeMessageType.PowerRequest:
                await TanganiPowerRequestAsync(msg.Payload, ct);
                break;
        }
    }

    /// <summary>
    /// Jalankan perintah restart / matikan PC atas permintaan layar login.
    /// </summary>
    /// <remarks>
    /// ⚠️ Perintah HARUS dieksekusi dari service. Overlay jalan sebagai user biasa
    /// tanpa hak admin sehingga `shutdown` dari sana berakhir "Access is denied";
    /// service jalan sebagai LocalSystem sehingga perintah itu sah.
    ///
    /// ⚠️ Ditolak kalau masih ada sesi pelanggan yang berjalan. Restart mendadak
    /// akan memutus sesi tanpa membalikkan sisa waktu ke voucher, jadi kasir harus
    /// menghentikan sesi lewat tombol stop dulu — sama seperti aturan auto-matikan
    /// PC idle yang juga melewati PC yang sedang dipakai.
    /// </remarks>
    private async Task TanganiPowerRequestAsync(string payload, CancellationToken ct)
    {
        string aksi = "tidak-dikenal";
        bool restart = false;
        bool matikan = false;

        try
        {
            var hasil = JsonConvert.DeserializeObject<PowerRequestPayload>(payload);
            aksi = hasil?.Aksi?.Trim() ?? "";
            // ⚠️ Bandingkan dengan literal, bukan nameof(PowerAksi.X): enum itu
            // tinggal di project Overlay yang tidak dirujuk Service, jadi
            // nameof-nya tidak akan terkompilasi di sini.
            restart = string.Equals(aksi, "Restart", StringComparison.OrdinalIgnoreCase);
            matikan = string.Equals(aksi, "Matikan", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Payload PowerRequest tidak bisa dibaca");
        }

        if (!restart && !matikan)
        {
            AgentLog.Write($"PowerRequest ditolak — aksi tidak dikenal: \"{aksi}\"");
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PowerResult, JsonConvert.SerializeObject(new
            {
                sukses = false,
                pesan = "Aksi tidak dikenal.",
            })));
            return;
        }

        if (_currentState.SisaDetik > 0)
        {
            AgentLog.Write($"PowerRequest ({aksi}) DITOLAK — masih ada sesi berjalan, sisa {_currentState.SisaDetik} detik");
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PowerResult, JsonConvert.SerializeObject(new
            {
                sukses = false,
                pesan = "Masih ada pelanggan yang memakai PC ini. Hentikan sesi dulu.",
            })));
            return;
        }

        // 10 detik supaya operator sempat melepas tangan dari keyboard dan
        // MiddleMouse menghasilkan dialog "you're about to be signed out".
        var argumen = restart
            ? "/r /f /t 10 /c \"v3netbill: PC direstart dari layar login\""
            : "/s /f /t 10 /c \"v3netbill: PC dimatikan dari layar login\"";

        try
        {
            Process.Start(new ProcessStartInfo("shutdown", argumen) { UseShellExecute = false });
            AgentLog.Write($"{aksi} dijadwalkan 10 detik (force) dari layar login");
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PowerResult, JsonConvert.SerializeObject(new
            {
                sukses = true,
                pesan = restart ? "PC akan restart dalam 10 detik." : "PC akan mati dalam 10 detik.",
            })));
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, $"Gagal menjalankan perintah {aksi}");
            await SendToOverlayAsync(new PipeMessage(PipeMessageType.PowerResult, JsonConvert.SerializeObject(new
            {
                sukses = false,
                pesan = "Gagal menjalankan perintah. PC harus direstart manual.",
            })));
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

        // Migrasi flag versi lama TIDAK diulang di sini — sudah dilakukan
        // sekali di ExecuteAsync, dan FlagPaths.MigrasiFlagLama() idempoten
        // (file-nya sudah hilang setelah migrasi pertama).

        // Mode maintenance: PC sedang TERBUKA tanpa penagihan. Jangan luncurkan
        // overlay, dan pastikan ini terlihat di agent.log.
        //
        // ⚠️ Dulu cabang ini hanya menulis ke _logger, jadi dari agent.log
        // terlihat watchdog "tidak pernah jalan" — padahal justru sedang
        // menahan pelepasan. Itu sebabnya mode maintenance sulit sekali
        // ditemukan: tidak ada satu pun jejak di log service.
        if (FlagPaths.MaintenanceAktif())
        {
            string sejak = FlagPaths.MaintenanceSince() ?? "tidak diketahui";
            string alasan = FlagPaths.MaintenanceReason() ?? "tidak diisi";
            _logger.LogInformation("Mode maintenance aktif — overlay tidak dijalankan");
            AgentLog.Write(
                $"Watchdog: MODE MAINTENANCE aktif (sejak {sejak}, alasan: {alasan}) " +
                "— PC terbuka tanpa billing, overlay tidak dijalankan");
            return;
        }

        try
        {
            var processes = Process.GetProcessesByName("Agent.Overlay");

            // ⚠️ "Proses ada" BUKAN berarti overlay terlihat.
            // Overlay yang lahir di session 0 (desktop service) tidak pernah
            // terlihat user, tapi tetap muncul di tasklist — dan watchdog lama
            // menganggapnya sehat, sehingga tak terlihat bisa menggantung.
            //
            // ⚠️ Hanya session 0 yang dibunuh. Dulu criteria-nya "session id
            // berbeda dari sesi konsol", dan itu BAHA: begitu operator memakai
            // RDP, WTSGetActiveConsoleSessionId() mengembalikan sesi konsol
            // sementara overlay berjalan di sesi RDP — jadi overlay yang
            // benar-benar tampil justru dibunuh tiap 5 detik. Session 0 tidak
            // pernah bisa dilihat user mana pun, jadi itu satu-satunya
            // criteria yang aman.
            foreach (var p in processes)
            {
                try
                {
                    if (p.SessionId == 0)
                    {
                        _logger.LogWarning(
                            "Agent.Overlay berjalan di session 0 (desktop service, tak terlihat) — dibunuh dan diluncar ulang");
                        AgentLog.Write(
                            "Watchdog: Agent.Overlay di session 0 (tak terlihat) — bunuh, lalu luncurkan ulang di sesi interaktif");
                        p.Kill();
                    }
                }
                catch (Exception ex)
                {
                    AgentLog.Write(ex, "Watchdog: gagal memeriksa session Agent.Overlay");
                }
            }

            foreach (var p in processes) p.Dispose();

            bool masihAda = Process.GetProcessesByName("Agent.Overlay").Length > 0;
            if (!masihAda)
            {
                _logger.LogWarning("Agent.Overlay not running during locked session — restarting...");
                AgentLog.Write("Watchdog: Agent.Overlay tidak jalan dalam keadaan Locked — coba luncurkan");
                RestartOverlay();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watchdog error");
            AgentLog.Write(ex, "Watchdog error");
        }
    }

    /// <summary>
    /// Baca kredensial Nextcloud dari registry lalu kirim log agent + overlay.
    /// </summary>
    /// <remarks>
    /// Kredensial ditulis installer ke <c>HKLM\Software\v3Netbill\Agent</c>, bukan
    /// disimpan di appsettings.json atau di source — repo agent ini publik, jadi
    /// password apa pun yang diletakkan di sana akan terbaca seluruh dunia.
    /// <para>
    /// Kosong = fitur tidak dipakai. Itu kondisi normal dan tidak perlu dicatat
    /// ke log, supaya agent.log tidak penuh baris yang sama tiap menit.
    /// </para>
    /// </remarks>
    private async Task KirimLogNextcloud()
    {
        try
        {
            string url = BacaReg("NextcloudUrl", "");
            string user = BacaReg("NextcloudUser", "");
            string pass = BacaReg("NextcloudPassword", "");
            string folder = BacaReg("NextcloudFolder", NextcloudLogUploader.FolderBawaan);
            // Nama PC dibaca dari registry (dikirim server saat register).
            // Fallback ke pcId supaya nama berkas tidak pernah kosong.
            string pcId = GetConfig("Agent:PcId", "PcId", "PC001");
            string nama = BacaReg("PcName", pcId);

            await NextcloudLogUploader
                .Kirim(url, user, pass, folder, nama, _cts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "KirimLogNextcloud gagal");
        }
    }

    /// <summary>Baca satu nilai string dari key registry agent.</summary>
    /// <param name="bawaan">
    /// Nilai kalau key tidak ada. <c>null</c> berarti pemanggil SEDANG TANYA
    /// APAKAH ADA — beda dari "tidak ada, pakai string kosong".
    /// </param>
    private static string? BacaReg(string nama, string? bawaan)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            return key?.GetValue(nama) as string ?? bawaan;
        }
        catch
        {
            return bawaan;
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

    /// <summary>
    /// Blokir / buka Task Manager untuk SEMUA profil yang sedang login.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Registry.CurrentUser TIDAK bisa dipakai di sini.</b> Service ini
    /// jalan sebagai LocalSystem, jadi CurrentUser menunjuk
    /// <c>HKEY_USERS\S-1-5-18</c> — nilai DisableTaskMgr hanya berlaku untuk
    /// akun SYSTEM dan tidak pernah sampai ke akun pelanggan yang sedang login.
    /// Fitur ini sudah ada sejak awal tetapi tidak pernah benar-benar memblokir
    /// Task Manager; sekarang ditulis ke tiap profil yang ada di HKEY_USERS.
    /// <para>
    /// Nilai registry tidak langsung dibaca Task Manager yang sedang buka,
    /// jadi WM_SETTINGCHANGE disiarkan supaya tidak baru berlaku setelah
    /// Task Manager ditutup atau PC dinyalakan ulang.
    /// </para>
    /// </remarks>
    private void SetTaskManagerBlocked(bool block)
    {
        // ⚠️ Mode maintenance TIDAK BOLEH memblokir Task Manager.
        //
        // Kalau ada pemeriksaan di sini, satu tempat ini menutup semua pemanggil
        // (start, session berhenti, admin lock). Yang penting: mode maintenance
        // berarti PC sedang terbuka untuk diperbaiki — memblokir Task Manager
        // justru menutup alat pertama yang dipakai teknisi, dan tidak ada satu
        // pun tanda apa yang terjadi.
        if (block)
        {
            try
            {
                if (FlagPaths.MaintenanceAktif())
                {
                    AgentLog.Write(
                        "SetTaskManagerBlocked: dilewati — mode maintenance aktif, " +
                        "Task Manager dibiarkan terbuka untuk teknisi");
                    return;
                }
            }
            catch
            {
                // Registry tidak terbaca = anggap tidak ada maintenance, supaya tidak
                // membuka PC yang seharusnya terkunci.
            }
        }

        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        const string valueName = "DisableTaskMgr";
        int tersentuh = 0;

        try
        {
            using (var hives = Registry.Users)
            {
                foreach (string sid in hives.GetSubKeyNames())
                {
                    // _Classes dan apa pun yang diawali titik adalah class
                    // store / hive default, bukan profil orang.
                    if (sid.Equals("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sid.StartsWith(".", StringComparison.Ordinal)) continue;

                    try
                    {
                        using var profil = hives.OpenSubKey(sid);
                        if (profil == null) continue;
                        using var key = profil.CreateSubKey(keyPath, true);
                        if (key == null) continue;
                        key.SetValue(valueName, block ? 1 : 0, RegistryValueKind.DWord);
                        tersentuh++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Gagal menulis DisableTaskMgr untuk profil {Sid}", sid);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal {_Action} Task Manager", block ? "memblokir" : "membuka");
            return;
        }

        try { BroadcastPolicyChange(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Gagal menyiarkan WM_SETTINGCHANGE"); }

        _logger.LogInformation(
            "Task Manager {Action} untuk {Jumlah} profil", block ? "diblokir" : "dibuka", tersentuh);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    /// <summary>Siarkan WM_SETTINGCHANGE ke semua jendela agar kebijakan langsung berlaku.</summary>
    private static void BroadcastPolicyChange()
    {
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(
            (IntPtr)0xffff, WM_SETTINGCHANGE, UIntPtr.Zero, "Policy",
            SMTO_ABORTIFHUNG, 3000, out _);
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

    private record CreatePasswordPayload(string Kode, string PasswordLama, string Password);
    private record PinVerifyRequestPayload(string Pin);
    private record PinVerifyResponse(bool Sukses);
    private record PowerRequestPayload(string Aksi);
}