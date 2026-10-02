using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Mengumpulkan log + status agent lalu mengirimkannya ke backend.
/// </summary>
/// <remarks>
/// Dipanggil otomatis dari <c>ServerConnection.TandaiSocketMati</c> — yaitu
/// ketika socket ke server dinyatakan mati, kondisi yang pada insiden
/// 2 Okt 2026 membuat PC offline selama 8 jam tanpa-gebyar.
/// <para>
/// <b>Batas keras supaya tidak jadi penyimpan data.</b> File yang dikirim hanya
/// log dan status. Registry dan appsettings.json TIDAK pernah ikut — registry
/// agent memuat <c>AgentToken</c> (mengizinkan create_password dan stop_session)
/// serta <c>OtpBotToken</c> yang merupakan token bot Telegram aktif.
/// </para>
/// </remarks>
public static class AgentDiagnostics
{
    /// <summary>Jeda minimum antar pengiriman, dalam menit.</summary>
    public const int JedaAntarKirimMenit = 30;

    /// <summary>Maksimal pengiriman dalam satu hari kalender.</summary>
    public const int MaksPerHari = 4;

    /// <summary>Batas ukuran zip, dalam byte.</summary>
    public const long MaksBytesZip = 4_000_000;

    /// <summary>File lebih besar dari ini dilewati (satu log raksasa tidak berguna).</summary>
    private const long MaksBytesPerFile = 1_500_000;

    private const int BatasJalanDetik = 90;
    private const string Endpoint = "/api/diagnosa";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _terkirimUtc = DateTime.MinValue;
    private static int _jumlahHariIni;
    private static DateTime _hariDihitung = DateTime.MinValue;

    /// <summary>Folder staging yang ditulis kumpul-log.bat.</summary>
    private static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "v3NetbillAgent", "diagnosa");

    /// <summary>
    /// Kumpulkan lalu kirim. Mengembalikan true kalau benar-benar terkirim.
    /// Tidak pernah melempar: kegagalan diagnosa tidak boleh mengganggu apa pun.
    /// </summary>
    public static async Task<bool> KumpulkanDanKirim(
        string serverBaseUrl,
        string pcId,
        string agentToken,
        CancellationToken ct = default)
    {
        if (!BolehKirim()) return false;

        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            string zip = Path.Combine(Path.GetTempPath(),
                $"v3netbill-diagnosa-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            try
            {
                if (!await JalankanBatAsync(ct).ConfigureAwait(false)) return false;
                if (!BuatZip(zip)) return false;
                bool ok = await KirimAsync(serverBaseUrl, pcId, agentToken, zip, ct)
                    .ConfigureAwait(false);
                if (ok)
                {
                    _terkirimUtc = DateTime.UtcNow;
                    AgentLog.Write($"Diagnosa terkirim ke server ({new FileInfo(zip).Length} byte)");
                }
                return ok;
            }
            catch (Exception ex)
            {
                AgentLog.Write(ex, "Diagnosa gagal");
                return false;
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Cooldown + batas harian. Dicek tanpa lock supaya murah.</summary>
    private static bool BolehKirim()
    {
        DateTime now = DateTime.UtcNow;
        if (_hariDihitung.Date != now.Date)
        {
            _hariDihitung = now.Date;
            _jumlahHariIni = 0;
        }
        if (_jumlahHariIni >= MaksPerHari) return false;
        if (_terkirimUtc != DateTime.MinValue &&
            (now - _terkirimUtc).TotalMinutes < JedaAntarKirimMenit)
        {
            return false;
        }
        return true;
    }

    private static async Task<bool> JalankanBatAsync(CancellationToken ct)
    {
        string bat = Path.Combine(AppContext.BaseDirectory, "kumpul-log.bat");
        if (!File.Exists(bat))
        {
            AgentLog.Write($"kumpul-log.bat tidak ada di {bat} — diagnosa dilewati");
            return false;
        }

        // createNoWindow WAJIB: tanpa itu cmd.exe akan berkedip di layar PC kasir.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{bat}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try
        {
            using var proses = Process.Start(psi);
            if (proses == null) return false;
            using var batas = CancellationTokenSource.CreateLinkedTokenSource(ct);
            batas.CancelAfter(TimeSpan.FromSeconds(BatasJalanDetik));
            await proses.WaitForExitAsync(batas.Token).ConfigureAwait(false);
            return proses.ExitCode == 0 && Directory.Exists(WorkDir);
        }
        catch (OperationCanceledException)
        {
            AgentLog.Write($"kumpul-log.bat melebihi {BatasJalanDetik} detik — diagnosa dilewati");
            return false;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal menjalankan kumpul-log.bat");
            return false;
        }
    }

    /// <summary>Zip folder staging; berhenti menambahkan file begitu batas ukuran terlampaui.</summary>
    private static bool BuatZip(string zipPath)
    {
        if (!Directory.Exists(WorkDir)) return false;

        long total = 0;
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var arsip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.GetFiles(WorkDir))
            {
                var fi = new FileInfo(file);
                if (fi.Length > MaksBytesPerFile)
                {
                    AgentLog.Write($"Diagnosa: {fi.Name} ({fi.Length} byte) dilewati, kelewat besar");
                    continue;
                }
                if (total + fi.Length > MaksBytesZip) break;
                arsip.CreateEntryFromFile(file, fi.Name, CompressionLevel.Fastest);
                total += fi.Length;
            }
            if (total == 0) return false;
        }
        return true;
    }

    private static async Task<bool> KirimAsync(
        string serverBaseUrl, string pcId, string agentToken, string zipPath, CancellationToken ct)
    {
        try
        {
            using var klien = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var isi = new MultipartFormDataContent();
            isi.Add(new StringContent(pcId), "pcId");
            isi.Add(new StringContent(agentToken), "agentToken");

            byte[] data = File.ReadAllBytes(zipPath);
            var bagian = new ByteArrayContent(data);
            bagian.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            isi.Add(bagian, "file", Path.GetFileName(zipPath));

            var respons = await klien
                .PostAsync($"{serverBaseUrl.TrimEnd('/')}{Endpoint}", isi, ct)
                .ConfigureAwait(false);
            if (!respons.IsSuccessStatusCode)
            {
                AgentLog.Write($"Diagnosa ditolak server: HTTP {(int)respons.StatusCode}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal mengunggah diagnosa");
            return false;
        }
    }
}