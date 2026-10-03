using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Overlay;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AgentLog.FileName = "overlay.log";
        AgentLog.Write("=== Agent Overlay starting ===");

        // Mode UNINSTALL GUARD: dipanggil MSI (custom action Type 18) sebelum RemoveFiles.
        // Menampilkan verifikasi PIN admin; exit code menentukan lanjut/batal uninstall.
        foreach (var arg in e.Args)
        {
            // Mode CLEAR MAINTENANCE: dipanggil MSI sebagai custom action DEFERRED
            // (jalur sebagai SYSTEM) tepat sebelum service dinyalakan lagi.
            //
            // ⚠️ Exit code sengaja SELALU 0. Kegagalan membersihkan mode maintenance
            // tidak boleh menggagalkan instalasi agent — kalau tidak, operator yang
            // sedang memperbaiki PC bisa mendapat mesin yang tidak bisa dipakai.
            if (string.Equals(arg, "--clear-maintenance", StringComparison.OrdinalIgnoreCase))
            {
                AgentLog.Write("Mode CLEAR MAINTENANCE — membersihkan mode maintenance");
                try
                {
                    bool ada = FlagPaths.MaintenanceAktif();
                    bool adaFileLama = FlagPaths.BuangFlagLama();
                    FlagPaths.HapusMaintenance();
                    if (ada)
                    {
                        AgentLog.Write(
                            "Mode maintenance dihapus oleh installer — PC akan terkunci lagi");
                    }
                    if (adaFileLama)
                    {
                        AgentLog.Write(
                            $"Flag maintenance versi lama juga dibuang ({FlagPaths.StopFlagLama})");
                    }
                    if (!ada && !adaFileLama)
                    {
                        AgentLog.Write("Tidak ada mode maintenance — installer tetap dibersihkan");
                    }
                }
                catch (Exception ex)
                {
                    AgentLog.Write(ex, "Gagal membersihkan mode maintenance (diabaikan)");
                }
                Shutdown(0);
                return;
            }

            // Mode SET NEXTCLOUD: dipanggil MSI sebagai custom action DEFERRED
            // (jalur sebagai SYSTEM) sesudah file terpasang.
            //
            // ⚠️ Yang TIDAK ditulis kalau nilainya kosong — itu seluruh
            // alasan mode ini ada. Nilai properti MSI tidak bertahan antar
            // instalasi, jadi field dialog yang dikosongkan (keadaan normal
            // saat operator sok upgrade) akan menimpa nilai lama dengan string
            // kosong kalau ditulis apa adanya. Pola yang sama dulu menghapus
            // PcId dan membuat agent jatuh ke default "PC001".
            //
            // Upgrade dengan dialog dikosongkan = kredensial lama utuh.
            // Install baru dengan dialog dikosongkan = fitur tidak dipakai.
            if (string.Equals(arg, "--set-nextcloud", StringComparison.OrdinalIgnoreCase))
            {
                AgentLog.Write("Mode SET NEXTCLOUD — menyimpan kredensial dari installer");
                try
                {
                    string[] v = e.Args;
                    int mulai = Array.IndexOf(v, "--set-nextcloud") + 1;
                    string Ambil(int i) => mulai + i < v.Length ? v[mulai + i].Trim() : "";

                    string url = Ambil(0);
                    string user = Ambil(1);
                    string pass = Ambil(2);
                    string folder = Ambil(3);

                    int ditulis = 0;
                    void Simpan(string nama, string nilai)
                    {
                        // Kosong = biarkan nilai lama. INI inti dari mode ini.
                        if (string.IsNullOrWhiteSpace(nilai)) return;
                        FlagPaths.SimpanKonfigurasi(nama, nilai);
                        ditulis++;
                    }

                    Simpan("NextcloudUrl", url);
                    Simpan("NextcloudUser", user);
                    Simpan("NextcloudPassword", pass);
                    Simpan("NextcloudFolder", folder);

                    AgentLog.Write(
                        ditulis == 0
                            ? "Kredensial Nextcloud tidak diisi — nilai lama di registry dipertahankan"
                            : $"{ditulis} nilai Nextcloud disimpan dari installer");
                }
                catch (Exception ex)
                {
                    AgentLog.Write(ex, "Gagal menyimpan kredensial Nextcloud (diabaikan)");
                }
                Shutdown(0);
                return;
            }

            if (string.Equals(arg, "--uninstall-guard", StringComparison.OrdinalIgnoreCase))
            {
                AgentLog.Write("Mode UNINSTALL GUARD aktif — verifikasi PIN admin");
                var guard = new UninstallGuardWindow();
                guard.Show();
                return;
            }
        }

        // Mode maintenance: aktif di registry → jangan jalankan overlay.
        //
        // ⚠️ Mode ini berarti PC TERBUKA tanpa penagihan: service berhenti dan
        // overlay tidak jalan, jadi siapa pun yang lewat bisa memakai PC itu
        // gratis. Karena itu begitu mode ini aktif, log di agent.log WAJIB
        // menyebutnya — sebelumnya cabang ini hanya menulis ke overlay.log,
        // sehingga dari sisi service mode ini terlihat seperti "watchdog tidak
        // pernah jalan" dan tidak ada yang bisa tahu penyebabnya.
        try
        {
            FlagPaths.MigrasiFlagLama();
        }
        catch (Exception ex)
        {
            AgentLog.Write(ex, "Gagal memigrasi flag maintenance versi lama");
        }

        if (FlagPaths.MaintenanceAktif())
        {
            string sejak = FlagPaths.MaintenanceSince() ?? "tidak diketahui";
            string alasan = FlagPaths.MaintenanceReason() ?? "tidak diisi";
            AgentLog.Write($"Mode MAINTENANCE aktif (sejak {sejak}, alasan: {alasan}) — overlay keluar");
            Shutdown();
            return;
        }

        // Cegah dua instance overlay (watchdog + shortcut logon) berebut pipe.
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, @"Global\V3NetbillAgentOverlay", out createdNew);
        if (!createdNew)
        {
            // Instance lain sudah jalan — keluar diam-diam.
            AgentLog.Write("Instance overlay lain sudah jalan — keluar");
            Shutdown();
            return;
        }
        AgentLog.Write("Mutex utama didapat, lanjut Host + MainWindow.");

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c =>
            {
                c.SetBasePath(AppContext.BaseDirectory)
                 .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton<MainWindow>();
                services.AddSingleton<PipeClient>();
                services.AddSingleton<KeyboardHook>();
                services.AddSingleton<SessionStateProxy>();
            })
            .Build();

        _host.Start();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException) { }
        _singleInstanceMutex?.Dispose();
        _host?.Dispose();
        base.OnExit(e);
    }
}