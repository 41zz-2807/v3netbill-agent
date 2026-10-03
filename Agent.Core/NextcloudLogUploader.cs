using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Mengirim log agent + overlay ke folder Nextcloud lewat WebDAV.
/// </summary>
/// <remarks>
/// Tujuannya satu: kasir tidak perlu lagi naik ke PC klien untuk membaca log.
/// log sebelumnya hanya ada di <c>C:\ProgramData\v3NetbillAgent\logs</c>, jadi
/// satu-satunya cara melihatnya adalah Remote Desktop ke PC itu.
///
/// <para>
/// Dipakai WebDAV, bukan Nextcloud API, karena API butuh bearer token sementara
/// sedangkan WebDAV cukup HTTP Basic — dan kredensialnya sudah ada di registry
/// PC (ditulis installer), tidak perlu ditukar jadi token apa pun.
/// </para>
///
/// <para>
/// <b>Tidak pernah melempar.</b> Kegagalan pengiriman log tidak boleh mengganggu
/// operasi PC. Semua error ditangkap dan dicatat ke <see cref="AgentLog"/>.
/// </para>
/// </remarks>
public static class NextcloudLogUploader
{
    /// <summary>Nama folder di Nextcloud yang dibuat kalau belum ada.</summary>
    public const string FolderBawaan = "log-pc-warnet";

    private const int BatasJalanDetik = 120;

    /// <summary>Log yang lebih besar dari ini dilewati.</summary>
    private const long MaksBytesPerFile = 4_000_000;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _terkirimUtc = DateTime.MinValue;

    /// <summary>Folder log lokal.</summary>
    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "v3NetbillAgent", "logs");

    /// <summary>
    /// Kirim log hari ini ke Nextcloud. Mengembalikan true kalau ada yang benar-benar
    /// terkirim. Tidak pernah melempar.
    /// </summary>
    public static async Task<bool> Kirim(
        string baseUrl,
        string username,
        string password,
        string? folder,
        string pcId,
        CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            AgentLog.Write("Nextcloud: pengiriman sebelumnya masih jalan — dilewati");
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username))
            {
                // Dikonfigurasi kosong = fitur dimatikan. Itu kondisi normal,
                // jadi tidak perlu dicatat di log setiap 5 menit.
                return false;
            }

            // Cooldown 2 menit. Tanpa ini, watchdog yang jalan tiap menit akan
            // mengirim berkas yang sama berulang-ulang.
            if (_terkirimUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _terkirimUtc).TotalMinutes < 2)
            {
                return false;
            }

            var berkas = CariLogHariIni();
            if (berkas.Count == 0)
            {
                AgentLog.Write("Nextcloud: tidak ada log hari ini untuk dikirim");
                return false;
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(BatasJalanDetik),
            };
            var auth = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(username)}:{password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

            string remote = $"{baseUrl.TrimEnd('/')}/remote.php/dav/files/{Uri.EscapeDataString(username)}";
            string folderRemote = $"{remote}/{Uri.EscapeDataString(folder is { Length: > 0 } ? folder : FolderBawaan)}";

            await PastikanFolder(http, remote, folder is { Length: > 0 } ? folder : FolderBawaan, ct)
                .ConfigureAwait(false);

            int terkirim = 0;
            foreach (var f in berkas)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await KirimSatu(http, folderRemote, f, pcId, ct).ConfigureAwait(false);
                    terkirim++;
                }
                catch (Exception ex)
                {
                    AgentLog.Write(ex, $"Nextcloud: gagal meng-upload {Path.GetFileName(f)}");
                }
            }

            if (terkirim > 0)
            {
                _terkirimUtc = DateTime.UtcNow;
                AgentLog.Write($"Nextcloud: {terkirim} berkas log terkirim ke folder {folder ?? FolderBawaan}");
                return true;
            }

            AgentLog.Write("Nextcloud: tidak ada berkas yang berhasil dikirim");
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Nextcloud: pengiriman log gagal");
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Log agent + overlay untuk hari ini (nama berkas sudah bertanggal).</summary>
    private static List<string> CariLogHariIni()
    {
        var hasil = new List<string>();
        try
        {
            if (!Directory.Exists(LogDir)) return hasil;
            string hari = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var pola in new[] { $"agent-{hari}.log", $"overlay-{hari}.log" })
            {
                string path = Path.Combine(LogDir, pola);
                if (!File.Exists(path)) continue;

                var info = new FileInfo(path);
                if (info.Length == 0) continue;

                if (info.Length > MaksBytesPerFile)
                {
                    // Log harian bisa tumbuh sampai belasan MB. Yang dikirim hanya
                    // ekor file: bagian awal biasanya sudah tidak relevan dan
                    // yang dicari selalu kejadian terbaru.
                    byte[] ekor = BacaEkor(path, 512 * 1024);
                    if (ekor.Length > 0)
                    {
                        string potong = Path.Combine(LogDir, $"potong-{pola}");
                        File.WriteAllBytes(potong, ekor);
                        hasil.Add(potong);
                        AgentLog.Write(
                            $"Nextcloud: {pola} {info.Length} byte dipangkas ke 512 KB terakhir");
                    }
                    continue;
                }

                hasil.Add(path);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Nextcloud: gagal mencari berkas log");
        }
        return hasil;
    }

    private static byte[] BacaEkor(string path, int jumlahByte)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= jumlahByte) return File.ReadAllBytes(path);
        fs.Seek(-jumlahByte, SeekOrigin.End);
        byte[] buf = new byte[jumlahByte];
        int dibaca = fs.Read(buf, 0, jumlahByte);
        if (dibaca == jumlahByte) return buf;
        byte[] potong = new byte[dibaca];
        Array.Copy(buf, potong, dibaca);
        return potong;
    }

    private static async Task PastikanFolder(HttpClient http, string remote, string folder, CancellationToken ct)
    {
        string[] bagian = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var jalan = new StringBuilder(remote);
        foreach (var b in bagian)
        {
            string sebelum = jalan.ToString();
            jalan.Append('/').Append(Uri.EscapeDataString(b));
            using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), jalan.ToString());
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            // 405 = folder sudah ada. Itu kondisi normal, bukan error.
            if (res.StatusCode == HttpStatusCode.MethodNotAllowed) continue;
            if ((int)res.StatusCode >= 300 && (int)res.StatusCode < 400) continue;
            if (!res.IsSuccessStatusCode)
            {
                AgentLog.Write($"Nextcloud: MKCOL {sebelum} -> {(int)res.StatusCode}");
            }
        }
    }

    private static async Task KirimSatu(HttpClient http, string folderRemote, string path, string pcId, CancellationToken ct)
    {
        byte[] isi = File.ReadAllBytes(path);
        string nama = Path.GetFileName(path).Replace("potong-", "");
        // PC ID di depan nama supaya beberapa PC tidak saling menimpa berkas
        // dengan nama yang sama di folder yang sama.
        string target = $"{folderRemote}/{Uri.EscapeDataString($"{pcId}-{nama}")}";

        using var req = new HttpRequestMessage(HttpMethod.Put, target)
        {
            Content = new ByteArrayContent(isi),
        };
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            AgentLog.Write($"Nextcloud: PUT {nama} -> {(int)res.StatusCode} {res.ReasonPhrase}");
        }
    }
}
