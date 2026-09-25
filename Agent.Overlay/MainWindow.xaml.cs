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
    private bool _emergencyExit;
    private DispatcherTimer? _countdownTimer;

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
        var url = "https://v3netbill.<domain>";
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
            });
            AgentLog.Write($"Wallpaper diterapkan ({bytes.Length} bytes)");
            _logger.LogInformation("Wallpaper diterapkan ({N} bytes)", bytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal memuat wallpaper");
        }
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
            CountdownCard.Visibility = Visibility.Collapsed;
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
                PinDialog.Visibility = Visibility.Collapsed;
                _keyboardHook.Disable(); // allow desktop access
                // Sembunyikan overlay agar desktop terlihat (akses teknisi)
                WindowState = WindowState.Minimized;
                _logger.LogInformation("Technician PIN verified — desktop access granted");
            }
            else
            {
                // Show error briefly
                _logger.LogWarning("Technician PIN invalid");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PIN verify result parse error");
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

    private bool _stopConfirmArmed;
    private DateTime _stopConfirmAt;

    private async void StopSesiButton_Click(object sender, RoutedEventArgs e)
    {
        // Konfirmasi dua langkah: klik sekali → status, klik lagi → kirim stop.
        if (!_stopConfirmArmed || (DateTime.Now - _stopConfirmAt).TotalSeconds > 5)
        {
            _stopConfirmArmed = true;
            _stopConfirmAt = DateTime.Now;
            MiniStatusText.Text = "Klik STOP SESI sekali lagi untuk konfirmasi...";
            MiniStatusText.Visibility = Visibility.Visible;
            AgentLog.Write("Stop sesi: minta konfirmasi (klik kedua)");
            return;
        }

        _stopConfirmArmed = false;
        MiniStatusText.Visibility = Visibility.Collapsed;

        if (!_pipeClient.IsConnected)
        {
            MiniStatusText.Text = "Belum tersambung ke service — coba lagi";
            MiniStatusText.Visibility = Visibility.Visible;
            AgentLog.Write("Stop sesi dicegah: pipe belum terhubung");
            return;
        }

        AgentLog.Write("Kirim StopSessionRequest ke service");
        await _pipeClient.SendStopSessionRequestAsync();
    }

    // Button click handlers
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

        // PIN darurat diverifikasi LOKAL dulu (tidak butuh pipe/backend).
        if (pin == EmergencyPin)
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
        PinDialog.Visibility = Visibility.Collapsed;
    }

    // Payload records
    private record LoginResultPayload(bool Sukses, string? Alasan);
    private record PinVerifyPayload(bool Sukses);
}