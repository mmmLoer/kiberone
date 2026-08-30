using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kiberone.Vpn;

public static class VpnBridgeHost
{
    public static void Run(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = VpnBridgeConstants.ServiceName);
        builder.Services.AddSingleton<VpnBridgeServer>();
        builder.Services.AddHostedService<VpnBridgeWorker>();
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = VpnBridgeConstants.ServiceDisplayName;
        });
        builder.Build().Run();
    }

    private sealed class VpnBridgeWorker(VpnBridgeServer server, ILogger<VpnBridgeWorker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("KIBERone Student VPN bridge started.");
            await server.RunAsync(stoppingToken);
        }
    }
}
