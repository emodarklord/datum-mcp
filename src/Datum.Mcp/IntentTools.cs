using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class IntentTools
{
    private const string Toolset = "intent";

    [Toolset(Toolset)]
    [McpServerTool(Name = "intent_export", Destructive = false, OpenWorld = false)]
    [Description("Write this session's modelling steps to a JSON file in the Datum folder, in the order they were made. Gateway calls are tagged solidworks. Intent v0 can replay sketches on planes and features that use feature handles; steps that name a face or edge can't be replayed yet.")]
    public static async Task<CallToolResult> Export(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Output file, e.g. 'bracket.intent.json'; relative paths go in the Datum folder")] string path,
        CancellationToken cancellationToken = default)
    {
        return await ToolErrors.Run(async () =>
        {
            var full = workspace.ResolveWritePath(path);
            await File.WriteAllTextAsync(full, solidWorks.Intent.Export(), cancellationToken).ConfigureAwait(false);
            return $"Wrote {solidWorks.Intent.Steps.Count} steps to {full}\n";
        }).ConfigureAwait(false);
    }
}
