using Datum.Core;
using Datum.SolidWorks;

if (args is ["diagnose", .. var options])
{
    return await DiagnoseAsync(launch: options.Contains("--launch"));
}

Console.Error.WriteLine("""
    usage: datum diagnose [--launch]

    The MCP server itself isn't implemented yet.
    """);
return 1;

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
