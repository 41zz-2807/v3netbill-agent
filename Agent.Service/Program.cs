using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace V3Netbill.Agent.Service;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configuration
        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

        // Services
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "v3NetbillAgent";
        });

        var host = builder.Build();
        await host.RunAsync();
        return 0;
    }
}