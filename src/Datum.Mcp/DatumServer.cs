using Datum.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

public static class DatumServer
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    // allTools lists every tool up front and never changes the list, for clients that
    // mishandle notifications/tools/list_changed.
    public static IMcpServerBuilder AddDatum(this IServiceCollection services, Func<ISolidWorksBackend> backendFactory, bool allTools = false)
    {
        services.AddSingleton(_ => new SolidWorksConnection(backendFactory, CallTimeout));
        services.AddSingleton(provider =>
        {
            var catalog = new ToolCatalog(allTools);
            provider.GetRequiredService<SolidWorksConnection>().CapabilitiesChanged += catalog.Update;
            return catalog;
        });
        services.AddOptions<McpServerOptions>()
            .PostConfigure<ToolCatalog>((options, catalog) => catalog.Attach(options.ToolCollection ??= []));

        return services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "datum",
                Version = typeof(DatumServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            })
            .WithTools([typeof(StatusTools), typeof(ToolsetTools)]);
    }
}
