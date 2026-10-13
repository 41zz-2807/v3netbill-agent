using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Klien HTTP untuk dua endpoint pembaruan otomatis di backend.
/// </summary>
/// <remarks>
/// <para>
/// Ada di <c>Agent.Core</c> (net8.0 biasa) supaya parser dan penanganan
/// kesalahannya bisa diuji dari Linux tanpa PC Windows.
/// </para>
/// <para>
/// ⚠️ <b>Tidak ada satu pun method yang melempar.</b> Semua kegagalan jaringan,
/// JSON rusak, dan timeout menghasilkan <c>null</c>. Pemanggilnya adalah
/// <c>Timer</c> di service yang tidak di-await, dan penolakan yang lolos
/// menjadi unhandled rejection — service yang mati berarti PC berhenti billing.
/// </para>
/// </remarks>
public sealed class PembaruanClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _pcId;
    private readonly string _agentToken;

    public PembaruanClient(string baseUrl, string pcId, string agentToken)
    {
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        _pcId = pcId ?? string.Empty;
        _agentToken = agentToken ?? string.Empty;

        // ⚠️ Timeout unduhan harus jauh lebih besar daripada yang biasa.
        // MSI berisi RustDesk dan berukuran puluhan MB; 20 detik default
        // akan selalu gagal di koneksi warnet yang lambat.
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(AutoUpdater.TimeoutUnduhDetik),
        };
    }

    private Uri Alamat(string path) => new($"{_baseUrl}/api/settings/agent/{path}");

    private StringContent IsiIdentitas() => new(
        JsonSerializer.Serialize(new { pcId = _pcId, agentToken = _agentToken }),
        System.Text.Encoding.UTF8,
        "application/json");

    /// <summary>
    /// Tanya server: ada versi baru? dan sakelarnya nyala?
    /// Null berarti gagal — TIDAK PERNAH melempar.
    /// </summary>
    public async Task<InfoPembaruan?> CekAsync(CancellationToken cancel = default)
    {
        try
        {
            using var res = await _http.PostAsync(Alamat("cek-pembaruan"), IsiIdentitas(), cancel);
            if (!res.IsSuccessStatusCode)
            {
                AgentLog.Write($"Auto-update: cek ditolak HTTP {(int)res.StatusCode}");
                return null;
            }

            var teks = await res.Content.ReadAsStringAsync(cancel);
            var info = ParseInfo(teks);
            if (info == null)
            {
                AgentLog.Write("Auto-update: balasan cek-pembaruan tidak bisa dibaca");
            }
            return info;
        }
        catch (OperationCanceledException)
        {
            AgentLog.Write("Auto-update: cek dibatalkan");
            return null;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"Auto-update: cek gagal: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Unduh MSI ke berkas sementara, kembalikan path-nya, atau null.
    /// </summary>
    /// <remarks>
    /// ⚠️ SHA-256 TIDAK diperiksa di sini. Itu tugas pemanggil dan harus
    /// terjadi SEBELUM apa pun diserahkan ke msiexec.
    /// </remarks>
    public async Task<string?> UnduhAsync(
        string? versi,
        long? ukuranHarapan,
        CancellationToken cancel = default)
    {
        string? path = null;
        try
        {
            using var res = await _http.PostAsync(Alamat("unduh-installer"), IsiIdentitas(), cancel);
            if (!res.IsSuccessStatusCode)
            {
                AgentLog.Write($"Auto-update: unduh ditolak HTTP {(int)res.StatusCode}");
                return null;
            }

            var folder = Path.Combine(Path.GetTempPath(), "v3netbill-update");
            Directory.CreateDirectory(folder);

            // Nama berkas memakai versi supaya unduhan berikutnya tidak tertukar
            // dengan berkas lama yang masih tertinggal.
            var aman = new string(Array.FindAll(
                (versi ?? "unknown").ToCharArray(),
                c => char.IsAsciiLetterOrDigit(c) || c == '.'));
            path = Path.Combine(folder, $"v3netbill-{aman}.msi");

            await using (var asal = await res.Content.ReadAsStreamAsync(cancel))
            await using (var tujuan = File.Create(path))
            {
                await asal.CopyToAsync(tujuan, cancel);
            }

            // Ukuran hanya pagar cepat: berkas terpotong biasanya punya ukuran
            // aneh. Yang benar-benar mengikat tetap SHA-256.
            var ukuran = new FileInfo(path).Length;
            if (ukuranHarapan is > 0 && ukuran != ukuranHarapan.Value)
            {
                AgentLog.Write(
                    $"Auto-update: ukuran {ukuran} berbeda dari server {ukuranHarapan.Value} — berkas dibuang");
                File.Delete(path);
                return null;
            }

            return path;
        }
        catch (OperationCanceledException)
        {
            HapusBersih(path);
            return null;
        }
        catch (Exception ex)
        {
            HapusBersih(path);
            AgentLog.Write($"Auto-update: unduh gagal: {ex.Message}");
            return null;
        }
    }

    /// <summary>Ubah JSON server menjadi <see cref="InfoPembaruan"/>.</summary>
    /// <remarks>
    /// Dipisah supaya bisa diuji tanpa jaringan — dan supaya bentuk JSON yang
    /// salah diam-diam tidak jadi default yang menipu operator.
    /// </remarks>
    public static InfoPembaruan? ParseInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var dok = JsonDocument.Parse(json);
            var akar = dok.RootElement;
            if (akar.ValueKind != JsonValueKind.Object) return null;

            bool AmbilBool(string nama) =>
                akar.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.True;

            string? AmbilStr(string nama) =>
                akar.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            long? AmbilPanjang(string nama) =>
                akar.TryGetProperty(nama, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64()
                    : null;

            return new InfoPembaruan
            {
                AutoUpdate = AmbilBool("autoUpdate"),
                Ada = AmbilBool("ada"),
                Versi = AmbilStr("versi"),
                Sha256 = AmbilStr("sha256"),
                UkuranBytes = AmbilPanjang("ukuranBytes"),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void HapusBersih(string? path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Berkas sementara yang gagal dihapus tidak boleh menjatuhkan
            // service — billing lebih penting dari kebersihan folder temp.
        }
    }

    public void Dispose() => _http.Dispose();
}