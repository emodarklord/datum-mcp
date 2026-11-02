using Datum.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Datum.Mcp;

public static class DatumServer
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public static IMcpServerBuilder AddDatum(this IServiceCollection services, Func<ISolidWorksBackend> backendFactory)
    {
        services.AddSingleton(_ => new SolidWorksConnection(backendFactory, CallTimeout));
        return services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "datum",
                Version = typeof(DatumServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            })
            .WithTools([typeof(StatusTools)]);
    }
}
