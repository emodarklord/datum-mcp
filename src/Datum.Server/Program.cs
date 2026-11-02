using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["diagnose", .. var diagnoseOptions])
{
    return await DiagnoseAsync(launch: diagnoseOptions.Contains("--launch"));
}

if (ParseServeOptions(args) is not { } serveOptions)
{
    Console.Error.WriteLine("""
        usage:
          datum [--all-tools] [--allow <folder>]...   run the MCP server on stdio
              --all-tools        list every tool up front instead of loading toolsets on demand
              --allow <folder>   folder Datum may write to (repeatable; also DATUM_ALLOW, ;-separated;
                                 default Documents\Datum)
          datum diagnose [--launch]                   print SOLIDWORKS version, license and add-ins
        """);
    return 1;
}

await ServeAsync(serveOptions);
return 0;

static DatumOptions? ParseServeOptions(string[] args)
{
    var allTools = false;
    var allowed = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--all-tools":
                allTools = true;
                break;
            case "--allow" when i + 1 < args.Length:
                allowed.Add(args[++i]);
                break;
            default:
                return null;
        }
    }

    return new DatumOptions { AllTools = allTools, Workspace = Workspace.Create(allowed, Environment.GetEnvironmentVariable("DATUM_ALLOW")) };
}

// stdout carries the MCP protocol, so every log line has to go to stderr.
static async Task ServeAsync(DatumOptions options)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddDatum(() => new ComBackend(), options).WithStdioServerTransport();
    await builder.Build().RunAsync();
}

static async Task<int> DiagnoseAsync(bool launch)
{
    try
    {
        using var client = await SolidWorksClient.ConnectAsync(new ComBackend(), launch, TimeSpan.FromSeconds(15));
        Console.Write(DiagnoseReport.Format(await client.ReadInfoAsync()));
        return 0;
    }
    catch (DatumException e)
    {
        Console.Error.WriteLine(e);
        if (e.Code == ErrorCode.NotRunning)
        {
            Console.Error.WriteLine("Or run 'datum diagnose --launch' to start it.");
        }

        return e.Code is ErrorCode.NotRunning or ErrorCode.Unreachable ? 2 : 3;
    }
}
