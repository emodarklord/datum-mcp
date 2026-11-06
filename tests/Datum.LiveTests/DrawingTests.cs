using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// An 80 x 50 x 20 block with a 12 mm through hole at x = 20, drawn at 1:1 unless a test says otherwise.
public sealed partial class DrawingTests : IDisposable
{
    private static readonly string CaptureFolder = Path.Combine(Path.GetTempPath(), "datum-live-captures");

    private readonly string _root = Directory.CreateTempSubdirectory("datum-drawing-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public DrawingTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum"), CaptureFolder], Path.Combine(_root, "backups"));
    }

    private string Block => Path.Combine(_root, "Datum", "block.sldprt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MakesASheetWithStandardViewsAndATitleBlock() => await InDrawing(async () =>
    {
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1200, path: Path.Combine(CaptureFolder, "drawing-standard.png"), overwrite: true);
        var list = Ok(await DrawingTools.ListViews(_connection));
        Console.WriteLine(list);

        Assert.True(picture.IsError != true);
        Assert.Equal(4, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("1:2", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AViewCanBeMovedRescaledAndKeepsItAfterAReopen() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "moved.slddrw");
        var view = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var changed = Ok(await DrawingTools.SetView(_connection, _workspace, view, x: 90, y: 70, scale: "1:4", display: "hidden"));
        Console.WriteLine(changed);
        Assert.Contains("1:4 at 90,70 mm", changed, StringComparison.Ordinal);
        Assert.Contains(", hidden, ", changed, StringComparison.Ordinal);

        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        var reopened = Ok(await DrawingTools.ListViews(_connection));
        Console.WriteLine(reopened);

        Assert.Contains("1:4 at 90,70 mm", reopened, StringComparison.Ordinal);
        Assert.Contains(", hidden, ", reopened, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ClosingADrawingReallyClosesItAndReleasesItsModel() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "closing.slddrw");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));

        Assert.DoesNotContain("closing", Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None)), StringComparison.OrdinalIgnoreCase);
        using var stream = new FileStream(Block, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Ok(await DrawingTools.New(_connection, _workspace));
    });

    private async Task InDrawing(Func<Task> test)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 80, Height = 50, Dimension = true }, new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 0, Radius = 6, Dimension = true }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 20));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Block));
        Ok(await DocumentTools.Close(_connection));
        Console.WriteLine(Ok(await DrawingTools.New(_connection, _workspace, model: Block, scale: "1:2")));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string Handle(string reply) => reply.Split(' ')[0];

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
