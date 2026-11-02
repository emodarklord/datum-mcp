using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class ToolsetTools
{
    [McpServerTool(Name = "toolset_enable", Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Load a toolset so its tools appear in your tool list. Use find_tools to see which toolsets exist.")]
    public static Task<CallToolResult> Enable(ToolCatalog catalog, [Description("Toolset name, for example 'diagnostics'")] string name) =>
        ToolErrors.Run(() =>
        {
            var tools = catalog.Enable(name);
            var report = new StringBuilder($"Enabled {name}: {string.Join(", ", tools.Where(t => !t.Unavailable).Select(t => t.Name))}\n");
            foreach (var tool in tools.Where(t => t.Unavailable))
            {
                report.Append(CultureInfo.InvariantCulture, $"  {tool.Name} is {tool.Status}\n");
            }

            return Task.FromResult(report.ToString());
        });

    [McpServerTool(Name = "find_tools", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Search every Datum tool, including tools in toolsets that aren't loaded yet. Without a query, lists the toolsets.")]
    public static Task<CallToolResult> Find(
        ToolCatalog catalog,
        [Description("Words to match against tool names and descriptions, for example 'add-in guid'")] string? query = null) =>
        ToolErrors.Run(() => Task.FromResult(string.IsNullOrWhiteSpace(query) ? ListToolsets(catalog) : Search(catalog, query)));

    private static string ListToolsets(ToolCatalog catalog)
    {
        var report = new StringBuilder();
        foreach (var group in catalog.Entries().GroupBy(e => e.Toolset).OrderBy(g => g.Key != ToolCatalog.Core).ThenBy(g => g.Key))
        {
            report.Append(CultureInfo.InvariantCulture, $"{group.Key} ({group.Count()}): {string.Join(", ", group.Select(e => e.Name))}\n");
        }

        return report.ToString();
    }

    private static string Search(ToolCatalog catalog, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = catalog.Entries()
            .Where(e => words.All(w => $"{e.Name} {e.Toolset} {e.Description}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Name} [{e.Toolset}]: {e.Description} ({e.Status})")
            .ToList();

        return matches.Count > 0
            ? string.Join('\n', matches) + "\n"
            : $"No tools match '{query}'.\n{ListToolsets(catalog)}";
    }
}
