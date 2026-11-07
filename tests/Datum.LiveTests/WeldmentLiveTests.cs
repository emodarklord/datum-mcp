using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed partial class WeldmentLiveTests : IDisposable
{
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-weld-{Guid.NewGuid():N}");

    public WeldmentLiveTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(120));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ARectangularFrameGivesACorrectCutList()
    {
        var sizes = Ok(await WeldmentTools.Profiles(_connection, "iso/square tube")).Split('\n');
        var (file, size) = (sizes[0], sizes[1].Split(", ")[0]);
        Console.WriteLine($"{file} {sizes[1]}");
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 400, Height = 300 }]));
            var member = Ok(await WeldmentTools.Member(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), file, size));
            var list = Ok(await WeldmentTools.CutList(_connection));
            Console.WriteLine($"{member}{list}");

            Assert.Contains("4 bodies", member, StringComparison.Ordinal);
            Assert.Contains("2 x 420 mm", list, StringComparison.Ordinal);
            Assert.Contains("2 x 320 mm", list, StringComparison.Ordinal);
            Assert.Equal(2, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task ACrossMemberTrimsToTheFrame()
    {
        var sizes = Ok(await WeldmentTools.Profiles(_connection, "iso/square tube")).Split('\n');
        var (file, size) = (sizes[0], sizes[1].Split(", ")[0]);
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = -200, Y1 = 0, X2 = 200, Y2 = 0 }]));
            Ok(await WeldmentTools.Member(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), file, size));
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, X2 = 0, Y2 = 300 }]));
            Ok(await WeldmentTools.Member(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), file, size));
            var before = Ok(await WeldmentTools.CutList(_connection));
            var lines = before.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            static string Body(string line) => line[(line.IndexOf(" (", StringComparison.Ordinal) + 2)..^1];
            var upright = Body(lines.Single(l => l.Contains(" 300 mm", StringComparison.Ordinal)));
            var flat = Body(lines.Single(l => l.Contains(" 400 mm", StringComparison.Ordinal)));
            var trim = Ok(await WeldmentTools.Trim(_connection, _workspace, [upright], [flat]));
            var after = Ok(await WeldmentTools.CutList(_connection));
            Console.WriteLine($"{before}{trim}{after}");

            Assert.Contains("1 x 290 mm", after, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string Feat(string reply) => FeatHandle().Match(reply).Value;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
