using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace V3Netbill.Agent.Service;

/// <summary>
/// OTP untuk bypass layar lock saat mode maintenance.
///
/// Alur:
///   1. Admin menyimpan bot token + chat id di halaman Pengaturan → server mendorong
///      config ke agent (event <c>agent:otp_config</c>) → disimpan di registry PC.
///   2. Admin/teknisi menekan tombol "Kirim OTP" di layar lock.
///   3. Service membuat OTP 6 digit, mengirimkannya ke Telegram, dan menyimpan
///      salinannya di memory bersama masa berlaku 5 menit.
///   4. OTP yang diketik di dialog PIN dicocokkan; sekali dipakai langsung hangus
///      (single-use) walau belum kedaluwarsa.
///
/// Sifat offline: config dibaca dari registry, bukan dari server. Jadi OTP tetap bisa
/// dikirim ke Telegram walaupun backend sedang mati — justru itu tujuan fiturnya.
///
/// Rollback: kalau admin mengosongkan bot token di Pengaturan, config dihapus dari
/// registry, <see cref="IsConfigured"/> jadi false, dan agent kembali ke PIN emergency
/// bawaan (<c>123456</c>) seperti sebelum fitur ini ada.
/// </summary>
public sealed class OtpService
{
    /// <summary>Masa berlaku OTP sejak dikirim.</summary>
    private static readonly TimeSpan MasaBerlaku = TimeSpan.FromMinutes(5);

    private readonly ILogger<OtpService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _kunci = new(1, 1);

    private string? _otpAktif;
    private DateTime _kadaluarsaUtc = DateTime.MinValue;
    private bool _sudahDipakai;

    public OtpService(ILogger<OtpService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>True bila bot token + chat id tersimpan di registry PC.</summary>
    public bool IsConfigured => BacaConfig() is (string token, string chatId, true);

    /// <summary>
    /// Simpan (atau hapus, bila nilai kosong) config OTP dari server ke registry PC
    /// supaya tetap tersedia saat server mati.
    /// </summary>
    public void SimpanConfig(string botToken, string chatId)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\v3Netbill\Agent", true);
            if (key == null)
            {
                _logger.LogWarning("Registry HKLM tidak bisa dibuka — config OTP tidak disimpan");
                return;
            }

            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            {
                // Nilai kosong dari Pengaturan = rollback ke PIN emergency bawaan.
                key.DeleteValue("OtpBotToken", false);
                key.DeleteValue("OtpChatId", false);
                _logger.LogInformation("Config OTP dikosongkan — fitur OTP dimatikan, kembali ke PIN emergency");
                return;
            }

            key.SetValue("OtpBotToken", botToken, RegistryValueKind.String);
            key.SetValue("OtpChatId", chatId, RegistryValueKind.String);
            _logger.LogInformation("Config OTP disimpan ke registry PC (chat {ChatId})", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal menyimpan config OTP ke registry");
        }
    }

    /// <summary>
    /// Buat OTP baru, kirim ke Telegram, dan simpan sebagai OTP aktif.
    /// Mengembalikan null bila gagal (belum dikonfigurasi atau Telegram menolak).
    /// </summary>
    public async Task<string?> KirimOtpAsync(string pcId, CancellationToken ct = default)
    {
        var (token, chatId, ok) = BacaConfig();
        if (!ok)
        {
            _logger.LogWarning("Permintaan OTP ditolak — bot token/chat id belum diatur di Pengaturan");
            return null;
        }

        string otp;
        await _kunci.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 6 digit, angka nol di depan dipertahankan (contoh: 007321).
            otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _otpAktif = otp;
            _kadaluarsaUtc = DateTime.UtcNow.Add(MasaBerlaku);
            _sudahDipakai = false;
        }
        finally
        {
            _kunci.Release();
        }

        string pesan =
            $"v3Netbill — OTP maintenance PC {pcId}\n\n" +
            $"Kode: {otp}\n" +
            $"Berlaku 5 menit, hanya bisa dipakai sekali.";

        bool terkirim = await KirimKeTelegramAsync(token, chatId, pesan, ct).ConfigureAwait(false);
        if (!terkirim)
        {
            // Jangan tinggalkan OTP aktif yang tidak sampai ke admin — kalau tidak,
            // nilainya masih bisa ditebak dari layar.
            await _kunci.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_otpAktif == otp) _otpAktif = null;
            }
            finally
            {
                _kunci.Release();
            }
            return null;
        }

        return otp;
    }

    /// <summary>
    /// Cocokkan input dengan OTP aktif. Menghancurkan OTP dipakai/diKedaluwarsa
    /// sehingga tidak bisa dipakai ulang.
    /// </summary>
    public async Task<(bool Berhasil, string? Pesan)> VerifikasiAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input)) return (false, null);

        await _kunci.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Urutan penting: cek "sudah dipakai" DULUAN. Setelah berhasil dipakai,
            // _otpAktif sengaja di-null-kan supaya nilainya tidak bisa dicocokkan lagi.
            if (_sudahDipakai) return (false, "OTP ini sudah dipakai. Minta OTP baru.");
            if (_otpAktif == null) return (false, "Belum ada OTP yang diminta. Tekan \"Kirim OTP\" dulu.");
            if (DateTime.UtcNow > _kadaluarsaUtc)
            {
                _otpAktif = null;
                return (false, "OTP kedaluwarsa (5 menit). Minta OTP baru.");
            }

            if (!string.Equals(input.Trim(), _otpAktif, StringComparison.Ordinal))
            {
                return (false, "OTP salah.");
            }

            // Single-use: langsung hangus meski belum kedaluwarsa.
            _otpAktif = null;
            _sudahDipakai = true;
            return (true, null);
        }
        finally
        {
            _kunci.Release();
        }
    }

    private async Task<bool> KirimKeTelegramAsync(string token, string chatId, string pesan, CancellationToken ct)
    {
        // Token dan chat id masuk URL, jadi harus di-encode.
        string url = $"https://api.telegram.org/bot{token}/sendMessage?chat_id={Uri.EscapeDataString(chatId)}" +
                     $"&text={Uri.EscapeDataString(pesan)}";
        try
        {
            using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogError("Telegram menolak kirim OTP: HTTP {Code}", (int)res.StatusCode);
                return false;
            }
            _logger.LogInformation("OTP berhasil dikirim ke Telegram");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gagal mengirim OTP ke Telegram");
            return false;
        }
    }

    /// <summary>Baca bot token + chat id dari registry. isOk=false bila salah satu kosong.</summary>
    private (string token, string chatId, bool isOk) BacaConfig()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\v3Netbill\Agent");
            var token = key?.GetValue("OtpBotToken") as string ?? "";
            var chatId = key?.GetValue("OtpChatId") as string ?? "";
            bool ok = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chatId);
            return (token, chatId, ok);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gagal membaca config OTP dari registry");
            return ("", "", false);
        }
    }
}
