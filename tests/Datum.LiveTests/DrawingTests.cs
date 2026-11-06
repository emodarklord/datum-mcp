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

    [Fact]
    public async Task ASectionThroughTheHoleShowsIt() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var section = Ok(await DrawingTools.Section(_connection, _workspace, front, "vertical", 20, 130, 60, label: "A"));
        var handle = Handle(section);
        var edges = Ok(await DrawingTools.Entities(_connection, handle));
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-section.png"), overwrite: true);
        var pdf = Path.Combine(CaptureFolder, "drawing-section.pdf");
        Ok(await DocumentTools.Export(_connection, _workspace, pdf, overwrite: true));
        Console.WriteLine($"{section}{edges}");

        Assert.Contains("[section]", section, StringComparison.Ordinal);
        Assert.True(picture.IsError != true);
        Assert.True(edges.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 4, edges);
    });

    [Fact]
    public async Task ADetailEnlargesThePartOfTheViewAskedFor() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var detail = Ok(await DrawingTools.Detail(_connection, _workspace, front, 20, 0, 12, "2:1", 120, 60, label: "B"));
        Console.WriteLine(detail);

        Assert.Contains("[detail] 2:1", detail, StringComparison.Ordinal);
    });

    [Fact]
    public async Task EntitiesAreListedInSheetCoordinates() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var entities = Ok(await DrawingTools.Entities(_connection, front));
        var view = Ok(await DrawingTools.ListViews(_connection)).Split('\n')[0];
        Console.WriteLine($"{view}\n{entities}");

        Assert.Contains(" circle ", entities, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Count(entities, " line "));
    });

    [Fact]
    public async Task AnAuxiliaryViewProjectsFromAnEdge() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));
        var edge = Ok(await DrawingTools.Entities(_connection, front)).Split('\n').First(line => line.Contains(" line ", StringComparison.Ordinal)).Split(' ')[0];

        var auxiliary = Ok(await DrawingTools.Auxiliary(_connection, _workspace, front, edge, 200, 60));
        Console.WriteLine(auxiliary);

        Assert.Contains("[auxiliary]", auxiliary, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ImportsDimensionsTolerancesThemAndMarksTheHole() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        var imported = Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        var list = Ok(await DrawingTools.ListDimensions(_connection));
        var name = Regex.Match(list, @"(D\d+@Sketch1) 80 mm").Groups[1].Value;
        var tolerance = Ok(await DrawingTools.Tolerance(_connection, _workspace, name, "bilateral", 0.2, -0.1));
        var side = Ok(await DrawingTools.Marks(_connection, _workspace, views[2]));
        var front = Ok(await DrawingTools.Marks(_connection, _workspace, views[0]));
        var note = Ok(await DrawingTools.Note(_connection, _workspace, "BREAK ALL SHARP EDGES", 30, 30));
        var after = Ok(await DrawingTools.ListDimensions(_connection));
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-dimensions.png"), overwrite: true);
        Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-dimensions.pdf"), overwrite: true));
        Console.WriteLine($"{imported}{list}{tolerance}{side}{front}{note}{after}");

        Assert.Contains("80 mm", list, StringComparison.Ordinal);
        Assert.Contains("50 mm", list, StringComparison.Ordinal);
        Assert.Contains("20 mm", list, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(list, Regex.Escape(name) + " "));
        Assert.Contains("+0.2 -0.1", after, StringComparison.Ordinal);
        Assert.Contains("nothing missing", side, StringComparison.Ordinal);
        Assert.Contains("1 centre lines", side, StringComparison.Ordinal);
        Assert.Contains("nothing missing", front, StringComparison.Ordinal);
        Assert.True(picture.IsError != true);
    });

    [Fact]
    public async Task AToleranceSurvivesSavingTheModelAndReopening() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "toleranced.slddrw");
        Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        var name = Regex.Match(Ok(await DrawingTools.ListDimensions(_connection)), @"(D\d+@Sketch1) 80 mm").Groups[1].Value;
        Ok(await DrawingTools.Tolerance(_connection, _workspace, name, "symmetric", 0.05));

        var refused = Text(await DocumentTools.Save(_connection, _workspace, path: file));
        Assert.StartsWith("INVALID_ARGUMENT: block", refused, StringComparison.OrdinalIgnoreCase);
        var drawing = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        var model = Ok(await DocumentTools.Open(_connection, _workspace, Block)).Split(' ')[1].TrimEnd('*');
        Ok(await DocumentTools.Save(_connection, _workspace, doc: model));
        Ok(await DocumentTools.Activate(_connection, _workspace, drawing));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        var reopened = Ok(await DrawingTools.ListDimensions(_connection));
        Console.WriteLine(reopened);
        Assert.Contains("±0.05", reopened, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheCheckFindsTheWeekTenMistakesAndPassesOnceFixed() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        var before = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(before);
        Assert.Contains("side-on hole", before, StringComparison.Ordinal);
        Assert.Contains("hidden lines are off", before, StringComparison.Ordinal);
        Assert.Contains("Not shown: D1@Boss-Extrude1 20 mm", before, StringComparison.Ordinal);

        Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        foreach (var view in views.Take(3))
        {
            Ok(await DrawingTools.Marks(_connection, _workspace, view));
            Ok(await DrawingTools.SetView(_connection, _workspace, view, display: "hidden"));
        }

        var after = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(after);
        Assert.DoesNotContain("side-on hole", after, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden lines are off", after, StringComparison.Ordinal);
        Assert.Contains("5 of 5 model dimensions shown", after, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheCheckSeesAViewOffTheSheetAndOverlappingViews() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        Ok(await DrawingTools.SetView(_connection, _workspace, views[3], x: 300, y: 100));
        Ok(await DrawingTools.SetView(_connection, _workspace, views[2], x: 62, y: 86));
        var report = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(report);

        Assert.Contains("Drawing View4 runs off the sheet", report, StringComparison.Ordinal);
        Assert.Contains("overlaps", report, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheTitleBlockShowsWhatIsSet() => await InDrawing(async () =>
    {
        var reply = Ok(await DrawingTools.Title(_connection, _workspace,
        [
            new TitleField { Name = "DrawnBy", Value = "JM" },
            new TitleField { Name = "Revision", Value = "B" },
            new TitleField { Name = "Description", Value = "CLAMP BASE" },
        ]));
        var missing = Text(await DrawingTools.Title(_connection, _workspace, [new TitleField { Name = "Nonsense", Value = "x" }]));
        Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-title.pdf"), overwrite: true));
        Console.WriteLine($"{reply}{missing}");

        Assert.DoesNotContain("NOT showing", reply, StringComparison.Ordinal);
        Assert.Contains("block.SLDPRT, which now needs saving", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DrawnBy", missing, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnAssemblyDrawingHasAMatchingBomBalloonsAndExplodeLines()
    {
        await MakeBlock();
        var pin = Path.Combine(_root, "Datum", "pin.sldprt");
        var assembly = Path.Combine(_root, "Datum", "pair.sldasm");
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 6, Dimension = true }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 40));
        Ok(await DocumentTools.Save(_connection, _workspace, path: pin));
        Ok(await DocumentTools.Close(_connection));

        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await AssemblyTools.Add(_connection, _workspace, Block));
        var pinA = Ok(await AssemblyTools.Add(_connection, _workspace, pin, x: 20, z: 40)).Split(' ')[1];
        var pinB = Ok(await AssemblyTools.Add(_connection, _workspace, pin, x: -20, z: 40)).Split(' ')[1];
        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded"));
        Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = pinA, Axis = "z", Distance = 30 }, new ExplodeStepSpec { Component = pinB, Axis = "z", Distance = 60 }]));
        var lines = Ok(await DrawingTools.ExplodeLines(_connection, _workspace));
        Ok(await DocumentTools.Save(_connection, _workspace, path: assembly));
        Ok(await DocumentTools.Close(_connection));
        Console.WriteLine(lines);

        Ok(await DrawingTools.New(_connection, _workspace, sheet: "b", scale: "1:2"));
        try
        {
            var view = Handle(Ok(await DrawingTools.AddView(_connection, _workspace, assembly, "iso", 120, 230)));
            Ok(await DrawingTools.SetView(_connection, _workspace, view, configuration: "Exploded", exploded: true));
            var noBom = Ok(await DrawingTools.Check(_connection));
            var rows = Ok(await DrawingTools.AddBom(_connection, _workspace, view, 230, 270));
            var balloons = Ok(await DrawingTools.Balloons(_connection, _workspace, view));
            var final = Ok(await DrawingTools.ReadBom(_connection));
            var report = Ok(await DrawingTools.Check(_connection));
            await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-assembly.png"), overwrite: true);
            Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-assembly.pdf"), overwrite: true));
            Console.WriteLine($"{noBom}{rows}{balloons}{final}{report}");

            Assert.Contains("no BOM table", noBom, StringComparison.Ordinal);
            Assert.Contains("2 x pin", final, StringComparison.Ordinal);
            Assert.Contains("1 x block", final, StringComparison.Ordinal);
            Assert.Contains("points at pin-", balloons, StringComparison.Ordinal);
            Assert.DoesNotContain("BOM", report, StringComparison.Ordinal);
            Assert.DoesNotContain("Balloon", report, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task MakeBlock()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 80, Height = 50, Dimension = true }, new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 0, Radius = 6, Dimension = true }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 20));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Block));
        Ok(await DocumentTools.Close(_connection));
    }

    private async Task InDrawing(Func<Task> test)
    {
        await MakeBlock();
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
