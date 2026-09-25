using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Verifikasi PIN Uninstall sebelum agent benar-benar dihapus.
/// Dipanggil sebagai custom action MSI (Agent.Overlay.exe --uninstall-guard)
/// tepat sebelum RemoveFiles. PIN admin (pin_uninstall_hash) diverifikasi ke
/// backend via POST /api/settings/verify-pin.
/// </summary>
public partial class UninstallGuardWindow : Window
{
    private const string RegistryKeyPath = @"Software\v3Netbill\Agent";
    private readonly HttpClient _http;
    private bool _done;

    public UninstallGuardWindow()
    {
        InitializeComponent();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Loaded += (_, _) => PinBox.Focus();
    }

    private void OnPinKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = RunVerifyAsync();
    }

    private void OnVerify(object sender, RoutedEventArgs e) => _ = RunVerifyAsync();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Non-zero → MSI Return="check" membatalkan uninstall.
        Finish(2);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_done) Finish(2);
    }

    private void Finish(int exitCode)
    {
        if (_done) return;
        _done = true;
        try { _http.Dispose(); } catch { /* abaikan */ }
        Environment.Exit(exitCode);
    }

    private async Task RunVerifyAsync()
    {
        var pin = PinBox.Password?.Trim() ?? "";
        if (pin.Length == 0)
        {
            ShowError("PIN tidak boleh kosong.");
            return;
        }

        string? serverUrl = ReadConfig("Server:Url", "ServerUrl");
        string? pcId = ReadConfig("Agent:PcId", "PcId");
        string? agentToken = ReadConfig("Agent:Token", "AgentToken");
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(pcId) || string.IsNullOrWhiteSpace(agentToken))
        {
            ShowError("Konfigurasi agent tidak ditemukan (ServerUrl/PcId/AgentToken).");
            return;
        }

        VerifyButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SetStatus("Memverifikasi PIN ke server...", "#50F6FF");

        try
        {
            var payload = JsonConvert.SerializeObject(new { pcId, agentToken, pin });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/settings/verify-pin")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ShowError($"Server menolak ({((int)resp.StatusCode)}). Token/PC tidak valid atau server error.");
                return;
            }
            var result = JsonConvert.DeserializeObject<VerifyResult>(body);
            if (result == null)
            {
                ShowError("Respons server tidak valid.");
                return;
            }

            if (result.Valid)
            {
                SetStatus("PIN benar — melanjutkan uninstall.", "#7EF59B");
                Finish(0);
                return;
            }

            if (!result.Configured)
            {
                // PIN belum pernah diset admin — tidak ada proteksi, lanjut hapus.
                SetStatus("PIN Uninstall belum diset — melanjutkan uninstall.", "#7EF59B");
                Finish(0);
                return;
            }

            ShowError("PIN salah. Coba lagi.");
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Uninstall guard: server tidak terjangkau");
            ShowError("Server tidak terjangkau. Uninstall dibatalkan agar tetap aman.");
        }
        finally
        {
            VerifyButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            PinBox.Focus();
        }
    }

    private string? ReadConfig(string appKey, string registryValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            var reg = key?.GetValue(registryValue) as string;
            if (!string.IsNullOrWhiteSpace(reg)) return reg;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Uninstall guard: baca registry config");
        }
        try
        {
            var cb = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            return cb.Build()[appKey];
        }
        catch
        {
            return null;
        }
    }

    private void SetStatus(string text, string brush = "#FFD166")
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(brush));
        StatusText.Visibility = Visibility.Visible;
    }

    private void ShowError(string message) => SetStatus(message, "#FF7A7A");

    private sealed class VerifyResult
    {
        public bool Valid { get; set; }
        public bool Configured { get; set; }
    }
}