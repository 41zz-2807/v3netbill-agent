using System;
using System.IO;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Path flag yang dipakai BERSAMA oleh service, overlay, dan watchdog.cmd.
/// </summary>
/// <remarks>
/// Ada satu definisi, bukan satu per file. Kalau tiap pihak menghitung
/// sendiri, penyimpangan sekecil apa pun akan merusak alur: service melihat
/// flag yang berbeda dari yang ditulis overlay, sehingga tidak pernah berhenti.
/// Pola yang sama dipakai untuk nama channel notifikasi FCM, yang harus sama di
/// tiga tempat.
/// <para>
/// ⚠️ <b>Jangan pakai <c>Environment.SpecialFolder.CommonDocuments</c>.</b>
/// Overlay berjalan sebagai user interaktif, jadi nilainya
/// <c>C:\Users\Public\Documents</c>. Service berjalan sebagai LocalSystem, jadi
/// untuk panggilan yang sama nilainya
/// <c>C:\Windows\System32\config\systemprofile\Documents</c> — BERBEDA.
/// Path-nya ditulis eksplisit karena memang harus sama di semua akun, dan
/// sudah dipakai bersama oleh uninstall-old-agent.bat lewat %PUBLIC%.
/// </para>
/// </remarks>
public static class FlagPaths
{
    private static string PublicDocuments
    {
        get
        {
            string? pub = Environment.GetEnvironmentVariable("PUBLIC");
            return !string.IsNullOrEmpty(pub) ? pub! : @"C:\Users\Public";
        }
    }

    /// <summary>
    /// Flag mode maintenance. Ditulis overlay saat emergency stop, dibaca
    /// service (watchdog) dan watchdog.cmd.
    /// </summary>
    public static string StopFlag => Path.Combine(PublicDocuments, "v3netbill-agent-stop.flag");

    /// <summary>
    /// Penanda "uninstall ini sah, PIN-nya sudah diterima". Ditulis
    /// UninstallGuardWindow, dibaca watchdog.cmd supaya peringatan ke Telegram
    /// hanya terkirim untuk uninstall paksa.
    /// </summary>
    public static string UninstallSahFlag =>
        Path.Combine(PublicDocuments, "v3netbill-agent-uninstall-sah.flag");

    /// <summary>Tulis penanda di path yang sama, aman kalau folder belum ada.</summary>
    public static void Tulis(string path, string isi)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllText(path, isi);
        }
        catch
        {
            // Penanda hanya memengaruhi notifikasi. Kegagalannya tidak boleh
            // membatalkan operasi yang sedang berjalan.
        }
    }
}