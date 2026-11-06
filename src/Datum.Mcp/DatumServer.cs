using Datum.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

public sealed record DatumOptions
{
    // Lists every tool up front and never changes the list, for clients that mishandle
    // notifications/tools/list_changed.
    public bool AllTools { get; init; }

    public Workspace? Workspace { get; init; }

    // Lets invoke make calls that may change a model; reads work either way.
    public bool AllowInvokeWrites { get; init; }
}

public static class DatumServer
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public static IMcpServerBuilder AddDatum(this IServiceCollection services, Func<ISolidWorksBackend> backendFactory, DatumOptions? options = null)
    {
        options ??= new DatumOptions();
        services.AddSingleton(options);
        services.AddSingleton(options.Workspace ?? Workspace.Create([], Environment.GetEnvironmentVariable("DATUM_ALLOW")));
        services.AddSingleton(_ => new SolidWorksConnection(backendFactory, CallTimeout));
        services.AddSingleton(provider =>
        {
            var catalog = new ToolCatalog(options.AllTools);
            provider.GetRequiredService<SolidWorksConnection>().CapabilitiesChanged += catalog.Update;
            return catalog;
        });
        services.AddOptions<McpServerOptions>()
            .PostConfigure<ToolCatalog>((serverOptions, catalog) => catalog.Attach(serverOptions.ToolCollection ??= []));

        return services
            .AddMcpServer(serverOptions => serverOptions.ServerInfo = new Implementation
            {
                Name = "datum",
                Version = typeof(DatumServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            })
            .WithTools([typeof(StatusTools), typeof(ToolsetTools), typeof(DocumentTools), typeof(InspectionTools), typeof(SketchTools), typeof(FeatureTools), typeof(EditTools), typeof(ViewTools), typeof(CheckTools), typeof(AssemblyTools), typeof(DrawingTools), typeof(MotionTools), typeof(ApiTools)]);
    }
}
