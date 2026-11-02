using System.Diagnostics;
using System.Runtime.InteropServices;
using Datum.Core;
using Datum.SolidWorks;

if (args is ["diagnose", .. var options])
{
    return RunOnStaThread(() => Diagnose(launch: options.Contains("--launch")));
}

Console.Error.WriteLine("""
    usage: datum diagnose [--launch]

    The MCP server itself isn't implemented yet.
    """);
return 1;

static int Diagnose(bool launch)
{
    try
    {
        using var session = SolidWorksSession.TryAttach() ?? (launch ? SolidWorksSession.Launch() : null);
        if (session is null)
        {
            Console.Error.WriteLine(Process.GetProcessesByName("SLDWORKS").Length > 0
                ? "SOLIDWORKS is running but can't be reached. If it's still starting, wait and retry. "
                  + "Otherwise run datum and SOLIDWORKS at the same privilege level (both as administrator, or neither)."
                : "SOLIDWORKS is not running. Start it, or run 'datum diagnose --launch'.");
            return 2;
        }

        Console.Write(DiagnoseReport.Format(session.ReadInfo()));
        return 0;
    }
    catch (COMException e)
    {
        Console.Error.WriteLine($"SOLIDWORKS COM error 0x{e.HResult:X8}: {e.Message}");
        return 3;
    }
}

// SOLIDWORKS is an STA COM server; the full dispatcher with timeouts is milestone 1.2.
static int RunOnStaThread(Func<int> work)
{
    var exitCode = 0;
    var thread = new Thread(() => exitCode = work());
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return exitCode;
}
