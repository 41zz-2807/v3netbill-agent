using System;
using System.Collections.Generic;
using System.IO;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Sumber kebenaran mode maintenance, plus jalur keluar darinya.
/// </summary>
/// <remarks>
/// <para>
/// Dulunya mode maintenance memakai <b>file</b> di <c>%PUBLIC%</c>. Sekarang memakai
/// <b>registry</b>. Alasannya bukan selera, tapi tiga hal yang semuanya sudah terbukti
/// menyakitkan:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Tidak ada yang menghapus file itu.</b> Hanya tombol emergency yang menulisnya, dan
/// satu-satunya jalan menghapusnya adalah manusia yang mengetik <c>del</c> di cmd.
/// Flag tertinggal bukan kebetulan — itu konsekuensi desain. Selama tertinggal, PC itu
/// terbuka tanpa penagihan sama sekali.
/// </description></item>
/// <item><description>
/// <b>MSI tidak bisa menghapus file itu.</b> <c>&lt;RemoveFile&gt;</c> hanya bisa menghapus
/// file di Directory milik komponennya sendiri, dan pohon Directory MSI ini tidak punya
/// <c>%PUBLIC%</c> — WiX tidak punya standard directory untuk itu. Di registry,
/// <c>RemoveRegistryValue</c> native dan bisa dijalankan sebagai bagian dari install.
/// </description></item>
/// <item><description>
/// <b>Path file berbeda antara user interaktif dan LocalSystem.</b> Site
/// <c>Environment.SpecialFolder.CommonDocuments</c> bernilai <c>C:\Users\Public\Documents</c>
/// untuk user, tapi <c>C:\Windows\System32\config\systemprofile\Documents</c> untuk
/// LocalSystem. Service dan overlay karena itu bisa melihat file yang berbeda sama sekali —
/// dan itu persis bug yang membuat mode maintenance tidak pernah terdeteksi di
/// <c>agent.log</c>.
/// </description></item>
/// </list>
/// <para>
/// <b>Registry</b> menutup ketiganya: service dan overlay membaca key yang sama, MSI bisa
/// membersihkannya dengan satu elemen, dan <c>reg query</c> di key yang sama sudah dipakai
/// operator untuk <c>AgentVersion</c> dan <c>PcId</c>.
/// </para>
/// </remarks>
public static class FlagPaths
{
    /// <summary>Key HKLM tempat mode maintenance disimpan.</summary>
    public const string RegistryKey = @"Software\v3Netbill\Agent";

    public const string MaintenanceValue = "MaintenanceMode";
    public const string MaintenanceSinceValue = "MaintenanceSince";
    public const string MaintenanceReasonValue = "MaintenanceReason";

    private static string PublicFolder
    {
        get
        {
            string? pub = Environment.GetEnvironmentVariable("PUBLIC");
            return !string.IsNullOrEmpty(pub) ? pub! : @"C:\Users\Public";
        }
    }

    /// <summary>
    /// Path file flag versi lama. Hanya dipakai oleh <see cref="MigrasiFlagLama"/>, dan
    /// sengaja TIDAK dipakai pembaca mana pun lagi.
    /// </summary>
    public static string StopFlagLama => Path.Combine(PublicFolder, "v3netbill-agent-stop.flag");

    /// <summary>
    /// Penanda "uninstall ini sah, PIN-nya sudah diterima". Ditulis
    /// UninstallGuardWindow, dibaca watchdog.cmd supaya peringatan ke Telegram hanya
    /// terkirim untuk uninstall paksa.
    /// </summary>
    public static string UninstallSahFlag =>
        Path.Combine(PublicFolder, "v3netbill-agent-uninstall-sah.flag");

    /// <summary>True kalau PC sedang dalam mode maintenance.</summary>
    public static bool MaintenanceAktif()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue(MaintenanceValue) is int n && n != 0;
        }
        catch
        {
            // Registry tidak terbaca = tidak ada maintenance. Mengasumsikan sebaliknya
            // akan mengunci PC kasir, dan itu kesalahan yang jauh lebih mahal
            // daripada PC yang kebudian terbuka.
            return false;
        }
    }

    /// <summary>Kapan mode maintenance dimulai (ISO 8601), atau null.</summary>
    public static string? MaintenanceSince()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue(MaintenanceSinceValue) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Alasan yang diisi operator, atau null.</summary>
    public static string? MaintenanceReason()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue(MaintenanceReasonValue) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Masuk mode maintenance, atau perbarui pencatatannya kalau sudah aktif.</summary>
    public static void TulisMaintenance(string? alasan)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RegistryKey, true)
                        ?? throw new InvalidOperationException("Key registry tidak bisa dibuat");
        key.SetValue(MaintenanceValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue(MaintenanceSinceValue,
            MaintenanceSince() ?? DateTime.Now.ToString("O"),
            Microsoft.Win32.RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(alasan))
        {
            key.SetValue(MaintenanceReasonValue, alasan.Trim(),
                Microsoft.Win32.RegistryValueKind.String);
        }
    }

    /// <summary>Keluar dari mode maintenance. Selalu idempoten.</summary>
    public static void HapusMaintenance()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(RegistryKey, true);
            if (key is null) return;
            key.DeleteValue(MaintenanceValue, throwOnMissingValue: false);
            key.DeleteValue(MaintenanceSinceValue, throwOnMissingValue: false);
            key.DeleteValue(MaintenanceReasonValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal menghapus mode maintenance dari registry");
        }
    }

    /// <summary>
    /// Pindahkan flag versi lama ke registry sekali saja.
    /// </summary>
    /// <remarks>
    /// PC yang sudah terjebak mode maintenance **tidak bisa diselamatkan** tanpa ini:
    /// registry-nya kosong, jadi service mengira tidak ada maintenance, sementara filenya
    /// masih ada dan memblokir segalanya. Migrasi membuat keadaan keduanya konsisten, dan
    /// upgrade berikutnya sudah cukup untuk memulihkannya — tanpa perlu `del` manual.
    /// </remarks>
    /// <returns>True kalau migrasi benar-benar dijalankan.</returns>
    public static bool MigrasiFlagLama()
    {
        try
        {
            if (!File.Exists(StopFlagLama)) return false;
            if (MaintenanceAktif())
            {
                // Sudah aktif di registry; file cuma sisa. Buang saja.
                File.Delete(StopFlagLama);
                return false;
            }

            string sejak = "tidak diketahui";
            try
            {
                sejak = File.ReadAllText(StopFlagLama).Trim();
                if (string.IsNullOrEmpty(sejak)) sejak = "tidak diketahui";
            }
            catch
            {
                // Isi file tidak wajib — yang penting filenya ada.
            }

            TulisMaintenance(null);
            File.Delete(StopFlagLama);
            AgentLog.Write(
                $"Migrasi mode maintenance: flag lama {StopFlagLama} dipindahkan ke registry " +
                $"(isi file: {sejak})");
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal memigrasi flag maintenance versi lama");
            return false;
        }
    }

    /// <summary>
    /// Buang file flag versi lama tanpa memigrasikannya.
    /// </summary>
    /// <remarks>
    /// Dipakai installer saat mode maintenance diakhiri. <b>Jangan</b> pakai
    /// <see cref="MigrasiFlagLama"/> di sini: migrasi justru menulis mode
    /// maintenance ke registry, jadi installer yang baru saja mengakhiri
    /// maintenance akan mengaktifkannya lagi satu detik kemudian.
    /// </remarks>
    public static bool BuangFlagLama()
    {
        try
        {
            if (!File.Exists(StopFlagLama)) return false;
            File.Delete(StopFlagLama);
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal membuang flag maintenance versi lama");
            return false;
        }
    }

    /// <summary>
    /// Simpan satu nilai konfigurasi ke key agent, tanpa menimpa nilai kosong.
    /// </summary>
    /// <remarks>
    /// Dipakai installer lewat <c>Agent.Overlay.exe --set-nextcloud</c>. Nama
    /// nilai dibatasi allow-list supaya argumen baris perintah tidak bisa
    /// menulis key registry sembarang — custom action dijalankan sebagai SYSTEM.
    /// </remarks>
    public static bool SimpanKonfigurasi(string nama, string nilai)
    {
        if (!KonfigurasiDiizinkan.Contains(nama)) return false;
        if (string.IsNullOrWhiteSpace(nilai)) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(RegistryKey, true)
                            ?? throw new InvalidOperationException("Key registry tidak bisa dibuat");
            key.SetValue(nama, nilai.Trim(), Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, $"Gagal menyimpan konfigurasi {nama}");
            return false;
        }
    }

    /// <summary>Nama nilai yang boleh ditulis installer.</summary>
    private static readonly HashSet<string> KonfigurasiDiizinkan = new(StringComparer.OrdinalIgnoreCase)
    {
        "NextcloudUrl", "NextcloudUser", "NextcloudPassword", "NextcloudFolder",
    };

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
