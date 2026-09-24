using System.Threading;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        // Cegah dua instance overlay (watchdog + shortcut logon) berebut pipe.
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, @"Global\V3NetbillAgentOverlay", out createdNew);
        if (!createdNew)
        {
            // Instance lain sudah jalan — keluar diam-diam.
            Shutdown();
            return;
        }

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