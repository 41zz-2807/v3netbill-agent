using System.IO;
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
            if (string.Equals(arg, "--uninstall-guard", StringComparison.OrdinalIgnoreCase))
            {
                AgentLog.Write("Mode UNINSTALL GUARD aktif — verifikasi PIN admin");
                var guard = new UninstallGuardWindow();
                guard.Show();
                return;
            }
        }

        // Mode maintenance: flag stop ada → jangan jalankan overlay.
        string stopFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "v3netbill-agent-stop.flag");
        if (File.Exists(stopFlag))
        {
            AgentLog.Write($"Stop flag '{stopFlag}' ada — overlay keluar (mode maintenance)");
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