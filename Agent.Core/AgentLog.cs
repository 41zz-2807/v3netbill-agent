using System;
using System.IO;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Log sederhana ke file, dipakai service &amp; overlay agar masalah mudah
/// diinspeksi di lapangan (Windows Event Log tidak tersedia tanpa provider).
/// <para>
/// <b>Satu berkas per hari:</b> C:\ProgramData\v3NetbillAgent\logs\agent-2026-10-02.log
/// (dan overlay-2026-10-02.log). Berkas lebih dari
/// <see cref="RetensiHari"/> hari dihapus otomatis.
/// </para>
/// </summary>
public static class AgentLog
{
    private static readonly object Gate = new();

    /// <summary>Umur berkas log sebelum dihapus, dalam hari.</summary>
    public const int RetensiHari = 30;

    /// <summary>
    /// Batas aman per hari. Melebihi ini TIDAK memotong berkas — memotong
    /// berarti menghapus riwayat tanpa jejak, dan justru itulah yang membuat
    /// insiden 2 Okt 2026 mustahil ditelusuri: seluruh jendela 26 Sep 11:17
    /// sampai 2 Okt 10:14 hilang begitu <c>SetLength(250_000)</c> berjalan.
    /// Setelah batas ini, hanya baris "tick sehat" yang dilewati; kegagalan
    /// tetap ditulis karena itu yang dicari.
    /// </summary>
    private const long BatasBytesPerHari = 20_000_000;

    public static string FileName { get; set; } = "agent.log";

    private static DateTime _terakhirBersih = DateTime.MinValue;
    private static bool _capTercapai;

    private static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "v3NetbillAgent", "logs");

    /// <summary>Nama berkas log hari ini, mis. <c>agent-2026-10-02.log</c>.</summary>
    public static string LogPathToday =>
        Path.Combine(LogsDir, $"{Path.GetFileNameWithoutExtension(FileName)}-{DateTime.Now:yyyy-MM-dd}{Path.GetExtension(FileName)}");

    public static void Write(string message) => Tulis(message, bolehLewatBatas: false);

    /// <summary>
    /// Tulis baris yang hanya berguna sebagai bukti timer hidup (heartbeat sehat).
    /// Akan dilewati kalau berkas hari ini sudah melewati batas ukuran.
    /// </summary>
    public static void WriteRutin(string message) => Tulis(message, bolehLewatBatas: true);

    private static void Tulis(string message, bool bolehLewatBatas)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDir);
                BersihkanLogLamaJikaSudahGiliran();

                string path = LogPathToday;
                if (bolehLewatBatas && _capTercapai) return;
                if (bolehLewatBatas && new FileInfo(path).Length >= BatasBytesPerHari)
                {
                    _capTercapai = true;
                    File.AppendAllText(path,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} LOG mencapai batas {BatasBytesPerHari} byte — baris 'tick sehat' berikutnya dilewati, kegagalan tetap dicatat{Environment.NewLine}");
                    return;
                }

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Log tidak boleh menggagalkan proses utama.
        }
    }

    public static void Write(Exception? ex, string context)
    {
        string detail = ex == null
            ? string.Empty
            : $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8}){Environment.NewLine}  {ex.StackTrace ?? string.Empty}";
        Write($"{context}{Environment.NewLine}  => {detail}");
    }

    /// <summary>
    /// Hapus berkas log lebih tua dari <see cref="RetensiHari"/> hari.
    /// Dipanggil sekali per hari, otomatis dari <see cref="Write(string)"/>.
    /// </summary>
    public static void BersihkanLogLama(int hari = RetensiHari)
    {
        try
        {
            if (!Directory.Exists(LogsDir)) return;
            string pola = $"{Path.GetFileNameWithoutExtension(FileName)}-*{Path.GetExtension(FileName)}";
            DateTime batas = DateTime.Now.AddDays(-hari);
            foreach (string f in Directory.EnumerateFiles(LogsDir, pola))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < batas) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void BersihkanLogLamaJikaSudahGiliran()
    {
        DateTime hariIni = DateTime.Today;
        if (_terakhirBersih == hariIni) return;
        _terakhirBersih = hariIni;
        _capTercapai = false;
        BersihkanLogLama();
    }
}