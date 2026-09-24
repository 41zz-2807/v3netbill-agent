using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        }
        else if (sessionActive)
        {
            // Desktop usable + chip countdown
            OverlayBackground.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Unknown — hide everything
            OverlayBackground.Visibility = Visibility.Collapsed;
            ContentPanel.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Collapsed;
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
            // Chip countdown mode — desktop tetap bisa dipakai
            SetNoActivate(true);
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Width = 300;
            Height = 96;
            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = 16;
            Topmost = true;
            ShowInTaskbar = false;
            _keyboardHook.Disable();
            _logger.LogInformation("Overlay → SESSION (chip countdown, hook disabled)");
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
                    break;
                case PipeMessageType.SessionTick:
                    _stateProxy.ApplySessionTick(msg.Payload);
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

    private void HandleLoginResult(string json)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<LoginResultPayload>(json);
            if (result == null) return;

            if (result.Sukses)
            {
                ErrorText.Visibility = Visibility.Collapsed;
                // State will update via SessionStarted from Service
            }
            else
            {
                ErrorText.Text = result.Alasan ?? "Login gagal";
                ErrorText.Visibility = Visibility.Visible;
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
        await _pipeClient.SendLoginRequestAsync(kode, password);
    }

    private async void PinOkButton_Click(object sender, RoutedEventArgs e)
    {
        string pin = PinBox.Password;
        if (string.IsNullOrWhiteSpace(pin)) return;
        await _pipeClient.SendPinVerifyRequestAsync(pin);
    }

    private void PinCancelButton_Click(object sender, RoutedEventArgs e)
    {
        PinDialog.Visibility = Visibility.Collapsed;
    }

    // Payload records
    private record LoginResultPayload(bool Sukses, string? Alasan);
    private record PinVerifyPayload(bool Sukses);
}