using System;
using System.Diagnostics;
using System.IO;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Membaca ID RustDesk dari PC ini supaya teknisi bisa masuk lewat remote repair
/// tanpa harus naik ke PC dan melihat aplikasinya.
/// </summary>
/// <remarks>
/// ⚠️ <para>
/// ID diambil dengan menjalankan <c>RustDesk.exe --get-id</c>, bukan dibaca dari
/// berkas config. RustDesk sendiri yang memiliki ID itu, dan lokasi config-nya
/// berubah antar versi (registry, folder roaming, folder program) — dan di
/// beberapa versi ID-nya dienkripsi. Membaca dari satu lokasi yang "kebetulan"
/// dipakai installer berarti diam-diam mengembalikan kosong untuk PC lain.
/// </para>
/// <para>
/// <b>Wajib memakai redirection stdout.</b> Dokumentasi resmi RustDesk menyatakan
/// perintah ini "does not have output by default" dan harus dipanggil sebagai
/// <c>rustdesk.exe --get-id | Out-String</c> untuk mencetak apa pun — jadi
/// proses harus launched dengan stdout-nya dialihkan, bukan memakai
/// <c>UseShellExecute = true</c>.
/// </para>
/// <para>
/// Semua kegagalan menghasilkan <c>null</c>, tidak pernah melempar: agent harus
/// tetap konek dan menagih PC ini walaupun remote repair belum siap. PC yang
/// ID-nya kosong tetap muncul di dashboard — hanya kolomnya yang kosong.
/// </para>
/// </remarks>
public static class RustdeskId
{
    /// <summary>Lokasi executable RustDesk setelah instalasi default.</summary>
    public static string? EksecutablePath()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(pf)) return null;
        var exe = Path.Combine(pf, "RustDesk", "RustDesk.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Jalankan <c>--get-id</c> dan kembalikan ID-nya tanpa spasi, atau
    /// <c>null</c> kalau RustDesk belum terpasang / belum memberi ID.
    /// </summary>
    /// <param name="timeoutMs">
    /// Batas waktu. Sengaja pendek: proses ini jalan di jalur register, jadi
    /// tidak boleh menahan koneksi ke server bila RustDesk sedang macet.
    /// </param>
    public static string? Ambil(int timeoutMs = 8000)
    {
        var exe = EksecutablePath();
        if (exe == null) return null;

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--get-id");

            using var proses = Process.Start(psi);
            if (proses == null) return null;

            // ⚠️ WAJIB baca kedua stream sampai habis DAN sebelum WaitForExit.
            // Kalau hanya WaitForExit lalu baca, proses yang menulis banyak ke
            // stdout bisa deadlock di buffer penuh. Baca asinkron lebih dulu.
            var stdout = proses.StandardOutput.ReadToEndAsync();
            var stderr = proses.StandardError.ReadToEndAsync();

            if (!proses.WaitForExit(timeoutMs))
            {
                AgentLog.Write("RustdeskId: --get-id melewati batas waktu, ID tidak diambil.");
                return null;
            }

            var keluar = Bersihkan(stdout.Result);
            if (keluar != null) return keluar;

            AgentLog.Write(
                "RustdeskId: --get-id tidak menghasilkan ID. " +
                $"stdout={(Ringkas(stdout.Result))} stderr={(Ringkas(stderr.Result))}");
            return null;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"RustdeskId gagal: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ambil ID dari teks bebas. Menerima format bergaris seperti
    /// "123 456 789" maupun tanpa spasi, dan membuang sisa seperti
    /// "Your ID: ..." supaya yang tampil di dashboard benar-benar angkanya.
    /// </summary>
    public static string? Bersihkan(string? mentah)
    {
        if (string.IsNullOrWhiteSpace(mentah)) return null;

        // Ambil baris terakhir yang isinya hanya digit dan/atau spasi. RustDesk
        // bisa mencetak baris lain (pesan verbose) sebelum ID-nya.
        foreach (var baris in mentah.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Array.FindAll sudah mengembalikan array, jadi TIDAK perlu .ToArray()
            // (memanggilnya menghasilkan error CS1061).
            var kandidat = new string(Array.FindAll(baris.ToCharArray(),
                c => char.IsAsciiDigit(c) || c == ' '));
            var digits = kandidat.Replace(" ", "");
            // ID RustDesk normalnya 9 digit. Batas bawah mencegah baris seperti
            // versi atau tahun ikut dipakai; batas atas mencegah log bocor jadi ID.
            if (digits.Length >= 6 && digits.Length <= 12) return digits;
        }
        return null;
    }

    /// <summary>Potong teks panjang supaya tidak membanjiri log agent.</summary>
    private static string Ringkas(string? s)
        => string.IsNullOrWhiteSpace(s) ? "(kosong)" : (s.Length > 120 ? s[..120] + "..." : s);
}