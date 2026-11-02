using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

switch (args)
{
    case []:
        await ServeAsync();
        return 0;
    case ["diagnose", .. var options]:
        return await DiagnoseAsync(launch: options.Contains("--launch"));
    default:
        Console.Error.WriteLine("""
            usage:
              datum                       run the MCP server on stdio
              datum diagnose [--launch]   print SOLIDWORKS version, license and add-ins
            """);
        return 1;
}

// stdout carries the MCP protocol, so every log line has to go to stderr.
static async Task ServeAsync()
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddDatum(() => new ComBackend()).WithStdioServerTransport();
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
