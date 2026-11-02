using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class StatusTools
{
    [McpServerTool(Name = "sw_status", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("SOLIDWORKS version, license and add-ins. Call first to check SOLIDWORKS is reachable.")]
    public static Task<CallToolResult> Status(SolidWorksConnection solidWorks, CancellationToken cancellationToken) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client => DiagnoseReport.Format(await client.ReadInfoAsync(cancellationToken).ConfigureAwait(false)),
            cancellationToken));
}
