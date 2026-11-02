using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = "sw_status", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("SOLIDWORKS version, license and loaded add-ins. Call first to check SOLIDWORKS is reachable.")]
    public static Task<CallToolResult> Status(SolidWorksConnection solidWorks, CancellationToken cancellationToken) =>
        ToolErrors.Run(async () =>
        {
            var info = await ReadAsync(solidWorks, cancellationToken).ConfigureAwait(false);
            var loaded = info.AddIns.Where(a => a.Loaded).Select(a => a.Title).ToList();
            return $"{DiagnoseReport.Header(info)}Add-ins: {info.AddIns.Count} registered, {loaded.Count} loaded"
                + (loaded.Count > 0 ? $" ({string.Join(", ", loaded)})" : "")
                + ". Full list: sw_addins in toolset 'diagnostics'\n";
        });

    [Toolset("diagnostics")]
    [McpServerTool(Name = "sw_addins", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List every registered SOLIDWORKS add-in with its GUID and whether it is loaded right now. Names are unreliable; use GUIDs to identify add-ins.")]
    public static Task<CallToolResult> AddIns(SolidWorksConnection solidWorks, CancellationToken cancellationToken) =>
        ToolErrors.Run(async () =>
        {
            var info = await ReadAsync(solidWorks, cancellationToken).ConfigureAwait(false);
            var report = new StringBuilder();
            foreach (var addIn in info.AddIns)
            {
                report.Append(CultureInfo.InvariantCulture, $"[{(addIn.Loaded ? 'x' : ' ')}] {addIn.Title} {addIn.Id:B}\n");
            }

            return report.ToString();
        });

    private static async Task<SolidWorksInfo> ReadAsync(SolidWorksConnection solidWorks, CancellationToken cancellationToken)
    {
        var info = await solidWorks.UseAsync(client => client.ReadInfoAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        solidWorks.Refresh(info);
        return info;
    }
}
