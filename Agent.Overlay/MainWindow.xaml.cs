using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// MainWindow — Fullscreen lock overlay + login form.
/// 
/// FLOW:
/// - Receives state updates from Service via named pipe (PipeClient.MessageReceived)
/// - When Locked (idle/no session): Window fullscreen/topmost, keyboard hook ENABLED, login form visible
/// - When session active: Window = small countdown chip top-right, desktop usable, hook DISABLED
/// - Session ends → Locked again (fullscreen login)
/// - Login: sends LoginRequest via PipeClient → Service → ServerConnection
/// - Technician shortcut: Ctrl+Alt+Shift+F12 → PIN dialog → verify via Service pipe
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly PipeClient _pipeClient;
    private readonly KeyboardHook _keyboardHook;
    private readonly SessionStateProxy _stateProxy;
    private readonly IConfiguration _config;
    private CancellationTokenSource? _cts;
    private MemoryStream? _wallpaperStream;
    // True kalau wallpaper benar-benar berhasil dimuat. Veil putih hanya
    // ditampilkan kalau ada gambar di bawahnya — kalau tidak, latarnya sudah
    // berupa gradien terang dan veil tidak ada gunanya.
    private bool _wallpaperDimuat;
    private bool _emergencyExit;
    private DispatcherTimer? _countdownTimer;
    private BuatPasswordDialogWindow? _dialogGantiPassword;

    // PIN bypass cadangan. Dipakai HANYA kalau admin belum menyetel PIN bypass
    // di halaman Pengaturan, karena hash yang diset di server tidak ada di PC
    // ini. Kalau admin sudah menyetelnya, hash-nya yang dipakai dan PIN ini
    // tidak berlaku lagi.
    private const string EmergencyPin = "123456";
    private static readonly string StopFlagPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "v3netbill-agent-stop.flag");

    public MainWindow(ILogger<MainWindow> logger, PipeClient pipeClient, KeyboardHook keyboardHook, SessionStateProxy stateProxy, IConfiguration config)
    {
        _logger = logger;
        _pipeClient = pipeClient;
        _keyboardHook = keyboardHook;
        _stateProxy = stateProxy;
        _config = config;

        InitializeComponent();
        DataContext = _stateProxy;

        // Wire events
        _pipeClient.MessageReceived += OnPipeMessage;
        _stateProxy.PropertyChanged += OnStatePropertyChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown; // for technician shortcut

    }


    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        // Jangan blocking: ConnectAsync sekarang loop reconnect terus-menerus.
        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeClient.ConnectAsync(_cts.Token);
                _logger.LogInformation("Overlay connected to Service");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Service");
            }
        });

        // Initial UI state
        UpdateVisibility();
        UpdateWindowState();

        // Timer cadangan countdown: kalau tick dari server terlewat (pipe rekanan lambat),
        // sisa waktu tetap berkurang setiap detik sehingga tampilan countdown tidak "beku".
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) =>
        {
            if (_stateProxy.IsTickStale()) _stateProxy.DecaySisaDetik();
        };
        _countdownTimer.Start();

        // Ambil wallpaper dari backend (jika ada) untuk background layar kunci.
        _ = LoadWallpaperAsync();
    }

    private string GetServerUrl()
    {
        var url = "http://localhost:3000";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\v3Netbill\Agent");
            var reg = key?.GetValue("ServerUrl") as string;
            if (!string.IsNullOrWhiteSpace(reg)) url = reg;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry tak terbaca, pakai default");
        }
        return url;
    }

    private async Task LoadWallpaperAsync()
    {
        try
        {
            string serverUrl = GetServerUrl();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync($"{serverUrl.TrimEnd('/')}/api/settings/wallpaper");
            if (!resp.IsSuccessStatusCode) return;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return;

            var bitmap = new BitmapImage();
            _wallpaperStream = new MemoryStream(bytes);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = _wallpaperStream;
            bitmap.EndInit();
            bitmap.Freeze();

            Dispatcher.Invoke(() =>
            {
                OverlayBackground.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                _wallpaperDimuat = true;
                // Veil langsung ikut status shown/collapsed-nya overlay.
                SinkronkanVeil();
            });
            AgentLog.Write($"Wallpaper diterapkan ({bytes.Length} bytes)");
            _logger.LogInformation("Wallpaper diterapkan ({N} bytes)", bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal memuat wallpaper");
        }
    }

    /// <summary>
    /// Veil ikut mengikuti apakah overlay sedang tampil. Kalau disembunyikan
    /// tanpa wallpaper pun tetap aman: saat di-lock dia ikut Visible,
    /// jadi tidak pernah ada wallpaper yang tergambar tanpa veil.
    /// </summary>
    private void SinkronkanVeil()
    {
        bool tampil = OverlayBackground.Visibility == Visibility.Visible;
        WallpaperVeil.Visibility = (tampil && _wallpaperDimuat)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Overlay harus selalu hidup (idle-lock) — jangan ditutup.
        // Kecuali saat SHUTDOWN DARURAT (PIN 123456) yang sengaja keluar.
        if (_emergencyExit) return;
        e.Cancel = true;
    }

    private void OnStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionStateProxy.Locked))
        {
            Dispatcher.Invoke(() =>
            {
                // Dialog ganti password milik sesi yang sedang berjalan. Kalau
                // sesi selesai (waktu habis atau di-stop), tidak ada lagi akun
                // yang bisa diganti, jadi dialognya ditutup supaya tidak
                // menggantung di layar tanpa ada yang bisa dilakukan.
                if (_stateProxy.Locked)
                {
                    _dialogGantiPassword?.Close();
                    _dialogGantiPassword = null;
                }

                UpdateVisibility();
                UpdateWindowState();
            });
        }
    }

    private void UpdateVisibility()
    {
        bool locked = _stateProxy.Locked;
        bool sessionActive = _stateProxy.SisaDetik > 0;

        if (locked)
        {
            // Fullscreen login (idle)
            OverlayBackground.Visibility = Visibility.Visible;
            ContentPanel.Visibility = Visibility.Visible;
            LoginCard.Visibility = Visibility.Visible;
            MiniPanel.Visibility = Visibility.Collapsed;
            AdminPinButton.Visibility = Visibility.Visible;
        }
        else if (sessionActive)
        {
            // Desktop usable + mini window interaktif
            OverlayBackground.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Visible;
            AdminPinButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Unknown — hide everything
            OverlayBackground.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Collapsed;
            AdminPinButton.Visibility = Visibility.Collapsed;
        }

        // ⚠️ WAJIB di luar cabang, dipanggil sekali di akhir. Versi awal
        // memanggilnya hanya di cabang `locked`, jadi begitu pengguna login
        // dan OverlayBackground di-Collapsed, veil putih 62% TETAP menggantung
        // di atas seluruh desktop — layar jadi terlihatwashed out.
        SinkronkanVeil();
    }

    private void UpdateWindowState()
    {
        if (_stateProxy.Locked)
        {
            // Fullscreen lock mode
            SetNoActivate(false);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            _keyboardHook.Enable();
            _logger.LogInformation("Overlay → LOCKED (fullscreen login, hook enabled)");
        }
        else if (_stateProxy.SisaDetik > 0)
        {
            // Mini window — desktop tetap bisa dipakai, window bisa di-minimize tapi tidak bisa di-close
            SetNoActivate(false);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Width = 340;
            Height = 268;
            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = 16;
            Topmost = true;
            ShowInTaskbar = true;   // biar bisa di-restore dari taskbar setelah di-minimize
            _keyboardHook.Disable();
            _logger.LogInformation("Overlay → SESSION (mini window, hook disabled)");
        }
        else
        {
            // Standby
            SetNoActivate(false);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Minimized;
            Topmost = false;
            ShowInTaskbar = false;
            _keyboardHook.Disable();
            _logger.LogInformation("Overlay → STANDBY (minimized, hook disabled)");
        }
    }

    private void SetNoActivate(bool noActivate)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            bool has = (exStyle & WS_EX_NOACTIVATE) != 0;
            if (noActivate && !has) SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            else if (!noActivate && has) SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetNoActivate gagal");
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void OnPipeMessage(PipeMessage msg)
    {
        Dispatcher.Invoke(() =>
        {
            switch (msg.Type)
            {
                case PipeMessageType.StateUpdate:
                    _stateProxy.ApplyStateUpdate(msg.Payload);
                    // Refresh eksplisit setelah state penuh ter-set (independen dari
                    // PropertyChanged Locked) — jamin mini-window muncul tepat waktu.
                    UpdateVisibility();
                    UpdateWindowState();
                    break;
                case PipeMessageType.SessionTick:
                    _stateProxy.ApplySessionTick(msg.Payload);
                    break;
                case PipeMessageType.SessionStarted:
                    _stateProxy.ApplySessionStarted(msg.Payload);
                    break;
                case PipeMessageType.SessionStopped:
                    _stateProxy.ApplySessionStopped(msg.Payload);
                    break;
                case PipeMessageType.LoginResult:
                    HandleLoginResult(msg.Payload);
                    break;
                case PipeMessageType.PinVerifyResult:
                    HandlePinVerifyResult(msg.Payload);
                    break;
                case PipeMessageType.ServerLink:
                    _stateProxy.ApplyServerLink(msg.Payload);
                    break;
                case PipeMessageType.OtpResult:
                    HandleOtpResult(msg.Payload);
                    break;
                case PipeMessageType.CreatePasswordResult:
                    HandleCreatePasswordResult(msg.Payload);
                    break;
            }
        });
    }

    private async void HandleLoginResult(string json)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<LoginResultPayload>(json);
            if (result == null) return;

            if (result.Sukses)
            {
                ErrorText.Visibility = Visibility.Collapsed;
        ErrorText.Foreground = Brushes.IndianRed;
                AgentLog.Write("LoginResult: SUKSES dari service");
                // Jaring pengaman: minta state terkini agar window mini pasti muncul
                // meski ada StateUpdate yang sempat gagal/tertukar.
                await _pipeClient.RequestStateAsync();
            }
            else
            {
                ErrorText.Text = result.Alasan ?? "Login gagal";
                ErrorText.Visibility = Visibility.Visible;
                AgentLog.Write($"LoginResult: GAGAL — {result.Alasan}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login result parse error");
        }
    }

    private void HandlePinVerifyResult(string json)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<PinVerifyPayload>(json);
            if (result?.Sukses == true)
            {
                TutupDialogPin();
                _keyboardHook.Disable(); // allow desktop access
                // Sembunyikan overlay agar desktop terlihat (akses teknisi)
                WindowState = WindowState.Minimized;
                AgentLog.Write(result.ViaOtp == true
                    ? "OTP maintenance terverifikasi — akses desktop diberikan"
                    : "PIN teknisi terverifikasi — akses desktop diberikan");
                _logger.LogInformation("PIN/OTP verified — desktop access granted");
            }
            else
            {
                // OTP salah / kedaluwarsa / sudah dipakai → tampilkan alasannya.
                if (!string.IsNullOrWhiteSpace(result?.Pesan))
                {
                    PinHintText.Text = result.Pesan;
                    PinHintText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    PinHintText.Visibility = Visibility.Visible;
                    AgentLog.Write($"PIN/OTP ditolak: {result.Pesan}");
                }
                else
                {
                    PinHintText.Text = "PIN salah.";
                    PinHintText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    PinHintText.Visibility = Visibility.Visible;
                    _logger.LogWarning("Technician PIN invalid");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PIN verify result parse error");
        }
    }

    /// <summary>Minta service mengirim OTP maintenance ke Telegram.</summary>
    private async void OtpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_pipeClient.IsConnected)
        {
            PinHintText.Text = "Agent service belum terhubung.";
            PinHintText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            PinHintText.Visibility = Visibility.Visible;
            AgentLog.Write("Kirim OTP ditolak — pipe ke service belum terhubung");
            return;
        }

        OtpButton.IsEnabled = false;
        OtpButton.Content = "MENGIRIM...";
        try
        {
            await _pipeClient.SendOtpRequestAsync();
        }
        finally
        {
            OtpButton.IsEnabled = true;
            OtpButton.Content = "KIRIM OTP KE TELEGRAM";
        }
    }

    private void HandleOtpResult(string json)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<OtpResultPayload>(json);
            if (result == null) return;

            if (result.Sukses)
            {
                PinHintText.Text = "OTP dikirim ke Telegram. Masukkan kodenya di bawah (berlaku 5 menit).";
                PinHintText.Foreground = System.Windows.Media.Brushes.Green;
                PinBox.Password = "";
                PinBox.Focus();
                AgentLog.Write("OTP berhasil dikirim ke Telegram");
            }
            else
            {
                PinHintText.Text = result.Pesan ?? "Gagal mengirim OTP.";
                PinHintText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                AgentLog.Write($"Kirim OTP gagal: {result.Pesan}");
            }
            PinHintText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OTP result parse error");
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Technician shortcut: Ctrl+Alt+Shift+F12
        if (e.Key == Key.F12 &&
            (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0 &&
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ShowPinDialog();
            e.Handled = true;
        }
    }

    private void ShowPinDialog()
    {
        PinBox.Password = "";
        PinDialog.Visibility = Visibility.Visible;
        // Kartu login HARUS disembunyikan. Keduanya anak dari ContentPanel
        // yang sama, jadi tanpa ini keduanya tampil bertumpuk vertikal —
        // persis yang terlihat di tangkapan layar: kartu login di atas,
        // dialog PIN di bawahnya.
        LoginCard.Visibility = Visibility.Collapsed;
        PinBox.Focus();
    }

    private void AdminPinButton_Click(object sender, RoutedEventArgs e)
    {
        AgentLog.Write("Admin PIN dibuka via tombol layar");
        ShowPinDialog();
    }

    private void MinimizeMiniButton_Click(object sender, RoutedEventArgs e)
    {
        // Hanya minimize — window tidak boleh di-close (OnClosing selalu cancel).
        WindowState = WindowState.Minimized;
        AgentLog.Write("Sesi: window mini di-minimize");
    }

    private async void StopSesiButton_Click(object sender, RoutedEventArgs e)
    {
        // Satu klik langsung stop — user bisa menghentikan sesinya sendiri tanpa
        // langkah konfirmasi tambahan.
        MiniStatusText.Visibility = Visibility.Collapsed;

        if (!_pipeClient.IsConnected)
        {
            MiniStatusText.Text = "Belum tersambung ke service — coba lagi";
            MiniStatusText.Visibility = Visibility.Visible;
            AgentLog.Write("Stop sesi dicegah: pipe belum terhubung");
            return;
        }

        AgentLog.Write("Kirim StopSessionRequest ke service (satu klik, tanpa konfirmasi)");
        await _pipeClient.SendStopSessionRequestAsync();
    }

    // Button click handlers
    /// <summary>
    /// Buka dialog ganti password. Dialog adalah jendela terpisah supaya tidak
    /// ikut terpotong oleh ukuran mini window (340x268) saat sesi berjalan.
    /// </summary>
    private void BuatPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        // Kode diambil dari akun yang sedang dipakai sesi ini, jadi pengguna
        // tidak perlu mengetiknya lagi. Password untuk member adalah namanya.
        // Voucher memakai kode uniknya, member tidak punya kode sehingga memakai
        // nama. Ini sama dengan cara backend mencari akun dari kode.
        string kode = _stateProxy.AkunKode ?? _stateProxy.AkunNama;
        if (string.IsNullOrWhiteSpace(kode))
        {
            AgentLog.Write("Ganti password tidak bisa dibuka: kode akun tidak terbaca");
            MiniStatusText.Text = "Kode akun tidak terbaca. Coba login ulang.";
            MiniStatusText.Visibility = Visibility.Visible;
            return;
        }

        AgentLog.Write("Tombol GANTI PASSWORD diklik — membuka dialog");
        _dialogGantiPassword = new BuatPasswordDialogWindow
        {
            Kode = kode,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        _dialogGantiPassword.Submit += (_, lama, baru) => KirimGantiPassword(lama, baru);
        _dialogGantiPassword.ShowDialog();
        _dialogGantiPassword = null;
    }

    private async void KirimGantiPassword(string lama, string baru)
    {
        var dlg = _dialogGantiPassword;
        string ulang = dlg?.PasswordUlangi ?? string.Empty;

        if (lama.Length == 0)
        {
            dlg?.TampilkanGalat("Isi password lama dulu. default 0000 kalau belum pernah ganti.");
            return;
        }
        if (baru.Length < 4)
        {
            dlg?.TampilkanGalat("Password baru minimal 4 karakter.");
            return;
        }
        if (baru != ulang)
        {
            dlg?.TampilkanGalat("Ulangi password tidak sama.");
            return;
        }
        if (baru == lama)
        {
            dlg?.TampilkanGalat("Password baru harus berbeda dari yang lama.");
            return;
        }
        if (!_pipeClient.IsConnected)
        {
            dlg?.TampilkanGalat("Belum tersambung ke service - tunggu beberapa saat lalu coba lagi.");
            return;
        }

        dlg?.TampilkanGalat("Mengirim ke server...");
        AgentLog.Write("Kirim create_password ke service");
        await _pipeClient.SendCreatePasswordRequestAsync(dlg?.Kode ?? string.Empty, lama, baru);
    }

    private void HandleCreatePasswordResult(string json)
    {
        var dlg = _dialogGantiPassword;
        try
        {
            var hasil = JsonConvert.DeserializeObject<CreatePasswordResultPayload>(json);
            if (hasil == null) return;

            if (hasil.Sukses)
            {
                AgentLog.Write("create_password berhasil");
                dlg?.SelesaiKirim(true);
                // Pesan muncul di mini window, karena di layar sedang berjalan.
                MiniStatusText.Text = "Password berhasil diganti.";
                MiniStatusText.Foreground = Brushes.MediumSeaGreen;
                MiniStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                AgentLog.Write($"create_password gagal: {hasil.Pesan}");
                dlg?.SelesaiKirim(false);
                dlg?.TampilkanGalat(hasil.Pesan ?? "Gagal mengganti password.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal membaca hasil create_password");
            dlg?.SelesaiKirim(false);
            dlg?.TampilkanGalat("Gagal membaca jawaban server.");
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        string kode = KodeTextBox.Text.Trim();
        string password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(kode) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Kode dan password wajib diisi";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;

        if (!_pipeClient.IsConnected)
        {
            ErrorText.Text = "Belum tersambung ke service — tunggu beberapa saat lalu coba lagi";
            ErrorText.Visibility = Visibility.Visible;
            AgentLog.Write($"Login dicegah: pipe belum terhubung (IsConnected=false) kode='{kode}'");
            _logger.LogWarning("Login dicegah: pipe ke service belum terhubung");
            return;
        }

        AgentLog.Write($"Kirim login_request ke service: kode='{kode}'");
        await _pipeClient.SendLoginRequestAsync(kode, password);
    }

    private async void PinOkButton_Click(object sender, RoutedEventArgs e)
    {
        string pin = PinBox.Password;
        if (string.IsNullOrWhiteSpace(pin)) return;

        // PIN bypass diverifikasi LOKAL dulu (tidak butuh pipe/backend), supaya
        // masih bisa dipakai ketika server mati.
        if (PinBypassTepat(pin))
        {
            PinHintText.Text = "PIN darurat benar. Klik STOP AGENT untuk berhenti (mode maintenance).";
            PinHintText.Visibility = Visibility.Visible;
            StopAgentButton.Visibility = Visibility.Visible;
            _logger.LogInformation("Emergency PIN diterima — tombol STOP tersedia");
            return;
        }

        // PIN bukan darurat → verifikasi normal via backend sampai hasilnya datang.
        PinHintText.Visibility = Visibility.Collapsed;
        StopAgentButton.Visibility = Visibility.Collapsed;
        await _pipeClient.SendPinVerifyRequestAsync(pin);
    }

    /// <summary>
    /// Cek PIN bypass: kalau admin sudah menyetel PIN di Pengaturan, hash-nya
    /// didorong ke PC ini dan dicocokkan dengan bcrypt. Kalau belum diset,
    /// dipakai PIN emergency bawaan.
    /// </summary>
    private static bool PinBypassTepat(string pin)
    {
        var hash = BacaHashPinBypass();
        if (string.IsNullOrWhiteSpace(hash))
        {
            return pin == EmergencyPin;
        }

        try
        {
            return BCrypt.Net.BCrypt.Verify(pin, hash);
        }
        catch (Exception ex)
        {
            // Hash rusak di registry jangan membuat bypass gagal diam-diam.
            AgentLog.Write($"Hash PIN bypass tidak bisa dibaca ({ex.GetType().Name}) — pakai PIN emergency");
            return pin == EmergencyPin;
        }
    }

    /// <summary>Baca hash bcrypt PIN bypass dari registry (dikosongkan = belum diset).</summary>
    private static string BacaHashPinBypass()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            return key?.GetValue("BypassPinHash") as string ?? string.Empty;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Gagal baca hash PIN bypass dari registry: {ex.Message}");
            return string.Empty;
        }
    }

    private void StopAgentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(StopFlagPath, DateTime.Now.ToString("O"));
            _logger.LogWarning("Emergency STOP — flag ditulis: {Path}", StopFlagPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal menulis stop flag");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc",
                Arguments = "stop v3NetbillAgent",
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal memanggil sc stop (bukan masalah besar)");
        }

        _logger.LogWarning("EMERGENCY STOP — overlay ditutup, masuk mode maintenance");
        _emergencyExit = true;
        _countdownTimer?.Stop();
        _keyboardHook.Disable();
        Application.Current.Shutdown();
    }

    private void PinCancelButton_Click(object sender, RoutedEventArgs e)
    {
        TutupDialogPin();
    }

    /// <summary>
    /// Menutup dialog PIN dan mengembalikan kartu login. ShowPinDialog()
    /// menyembunyikan LoginCard supaya keduanya tidak bertumpuk, jadi setiap
    /// jalur yang menutup dialog HARUS mengembalikannya — kalau tidak,
    /// layar login kosong dan tidak ada yang bisa diklik untuk login lagi.
    /// </summary>
    private void TutupDialogPin()
    {
        PinDialog.Visibility = Visibility.Collapsed;
        LoginCard.Visibility = Visibility.Visible;
        KodeTextBox.Focus();
    }

    // Payload records
    private record LoginResultPayload(bool Sukses, string? Alasan);
    private record PinVerifyPayload(bool Sukses, bool? ViaOtp, string? Pesan);
    private record OtpResultPayload(bool Sukses, string? Pesan);

    private record CreatePasswordResultPayload(bool Sukses, string? Pesan);
}