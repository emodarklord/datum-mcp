using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public class StdioServerTests
{
    [Fact]
    public async Task ServesSwStatusOverStdio()
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable() }));

        var tools = await client.ListToolsAsync();
        var status = await client.CallToolAsync("sw_status");

        Assert.Equal(
            ["checkpoint_list", "checkpoint_restore", "doc_activate", "doc_allow_edit", "doc_close", "doc_export", "doc_list", "doc_new", "doc_open", "doc_save", "feature_extrude", "find_tools", "model_faces", "model_info", "model_select", "model_tree", "selection_get", "sketch_add", "sketch_begin", "sketch_constrain", "sketch_end", "sw_status", "toolset_enable", "view_capture"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, status.IsError);
        Assert.StartsWith("SOLIDWORKS 20", TextOf(status), StringComparison.Ordinal);
        Assert.Contains("License: ", TextOf(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsAddInGuidsThroughTheDiagnosticsToolset()
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable() }));

        await client.CallToolAsync("toolset_enable", new Dictionary<string, object?> { ["name"] = "diagnostics" });
        var addIns = await client.CallToolAsync("sw_addins");

        Assert.NotEqual(true, addIns.IsError);
        // Which add-ins are loaded depends on how SOLIDWORKS was started (automation skips some), so only the listing is checked.
        Assert.Matches(@"\[[x ]\] 3DEXPERIENCE Marketplace \{219180b0-7183-4fe2-b167-4e2bfe534004\}", TextOf(addIns));
    }

    private static string TextOf(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    // Tests run from tests/Datum.LiveTests/bin/<Configuration>/<tfm>/; use the server built in the same configuration.
    private static string DatumExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent!.Name;
        var root = output;
        while (!File.Exists(Path.Combine(root.FullName, "datum.slnx")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("Couldn't find datum.slnx above the test output.");
        }

        var exe = Path.Combine(root.FullName, "src", "Datum.Server", "bin", configuration, output.Name, "datum.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException($"Build Datum.Server ({configuration}) first.", exe);
    }
}
