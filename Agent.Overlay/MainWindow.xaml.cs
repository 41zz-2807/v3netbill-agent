using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
/// - When Locked: Window becomes fullscreen/topmost, keyboard hook ENABLED, countdown visible
/// - When Unlocked: Window hidden/minimized, keyboard hook DISABLED, login form visible
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
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing — just hide (standby mode)
        if (_stateProxy.Locked)
        {
            e.Cancel = true;
        }
        else
        {
            Hide();
            e.Cancel = true;
        }
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
        CountdownCard.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        LoginCard.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        OverlayBackground.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateWindowState()
    {
        if (_stateProxy.Locked)
        {
            // Fullscreen lock mode
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            _keyboardHook.Enable();
            _logger.LogInformation("Overlay → LOCKED (fullscreen, hook enabled)");
        }
        else
        {
            // Standby mode — hidden but process alive
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Minimized;
            Topmost = false;
            ShowInTaskbar = false;
            _keyboardHook.Disable();
            _logger.LogInformation("Overlay → UNLOCKED (minimized, hook disabled)");
        }
    }

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