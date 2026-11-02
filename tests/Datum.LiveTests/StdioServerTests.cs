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

        var result = await client.CallToolAsync("sw_status");

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith("SOLIDWORKS 20", text, StringComparison.Ordinal);
        Assert.Contains("License: ", text, StringComparison.Ordinal);
    }

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
