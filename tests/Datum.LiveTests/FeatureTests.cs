using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// Volumes are checked against hand calculations for a 50 x 30 x 10 block centred on the origin.
public sealed partial class FeatureTests : IDisposable
{
    private const double BlockVolume = 50 * 30 * 10;

    private readonly string _root = Directory.CreateTempSubdirectory("datum-feature-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(60));
    private readonly Workspace _workspace;

    public FeatureTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExtrudesAProfileWithAHole() => await InNewPart(async () =>
    {
        var sketch = await Sketch(
            new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true },
            new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true });

        var reply = Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));

        AssertVolume(BlockVolume - (Math.PI * 25 * 10), reply);
        Assert.Contains("· 1 body", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task CutsThroughAll() => await InNewPart(async () =>
    {
        await Block();
        var square = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 10, Height = 10 });

        var reply = await FeatureTools.Extrude(_connection, _workspace, square, cut: true, end: "through_all");
        if (Delta(Text(reply)) == 0 || reply.IsError == true)
        {
            reply = await FeatureTools.Extrude(_connection, _workspace, square, cut: true, end: "through_all", reverse: true);
        }

        AssertDelta(-1000, Ok(reply));
    });

    [Fact]
    public async Task RevolvesARing() => await InNewPart(async () =>
    {
        var sketch = await Sketch(
            new SketchEntitySpec { Type = "centerline", X1 = 0, Y1 = -20, X2 = 0, Y2 = 20 },
            new SketchEntitySpec { Type = "rect", X1 = 20, Y1 = 0, X2 = 30, Y2 = 10 });

        var reply = Ok(await FeatureTools.Revolve(_connection, _workspace, sketch));

        AssertVolume(Math.PI * ((30 * 30) - (20 * 20)) * 10, reply);
    });

    [Fact]
    public async Task FilletsTheVerticalEdges() => await InNewPart(async () =>
    {
        await Block();
        var vertical = await Edges(length: 10);

        var reply = Ok(await FeatureTools.Fillet(_connection, _workspace, vertical, 2));

        Assert.Equal(4, vertical.Length);
        AssertDelta(-4 * (4 - Math.PI) * 10, reply);
    });

    [Fact]
    public async Task ChamfersATopEdge() => await InNewPart(async () =>
    {
        await Block();
        var top = await TopFace();
        var edge = (await Edges(length: 50, face: top))[0];

        var reply = Ok(await FeatureTools.Chamfer(_connection, _workspace, [edge], 1));

        AssertDelta(-0.5 * 1 * 1 * 50, reply);
    });

    [Fact]
    public async Task DrillsAndPatternsHoles() => await InNewPart(async () =>
    {
        await Block();
        var top = await TopFace();
        var hole = Ok(await FeatureTools.Hole(_connection, _workspace, top, "0,0,10", 6));
        var holeFeature = Handle(hole);
        var longEdge = (await Edges(length: 50, face: top))[0];

        var pattern = Ok(await FeatureTools.Pattern(_connection, _workspace, "linear", [holeFeature], longEdge, 3, 8));

        AssertDelta(-Math.PI * 9 * 10, hole);
        AssertDelta(-2 * Math.PI * 9 * 10, pattern);
    });

    [Fact]
    public async Task PatternsAHoleAroundADisc() => await InNewPart(async () =>
    {
        var disc = await Sketch(new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 30, Dimension = true });
        Ok(await FeatureTools.Extrude(_connection, _workspace, disc, depth: 10));
        var hole = Handle(Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "20,0,10", 4)));
        var rim = FirstHandle(Ok(await InspectionTools.Faces(_connection, surface: "cylinder")));

        var reply = Ok(await FeatureTools.Pattern(_connection, _workspace, "circular", [hole], rim, 6));

        AssertDelta(-5 * Math.PI * 4 * 10, reply);
    });

    [Fact]
    public async Task MirrorsAHoleAboutTheRightPlane() => await InNewPart(async () =>
    {
        await Block();
        var hole = Handle(Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "12,0,10", 6)));

        var reply = Ok(await FeatureTools.Mirror(_connection, _workspace, [hole], "Right"));

        AssertDelta(-Math.PI * 9 * 10, reply);
    });

    [Fact]
    public async Task ShellsTheBlockOpenAtTheTop() => await InNewPart(async () =>
    {
        await Block();

        var reply = Ok(await FeatureTools.Shell(_connection, _workspace, 2, [await TopFace()]));

        AssertVolume(BlockVolume - (46 * 26 * 8), reply);
    });

    [Fact]
    public async Task RollsBackAFeatureThatChangesNothing() => await InNewPart(async () =>
    {
        await Block();
        var away = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 200, Cy = 200, Width = 10, Height = 10 });
        var treeBefore = Ok(await InspectionTools.Tree(_connection));
        var infoBefore = Ok(await InspectionTools.Info(_connection));

        var result = await FeatureTools.Extrude(_connection, _workspace, away, cut: true, end: "through_all");
        Console.WriteLine(Text(result));

        Assert.True(result.IsError);
        Assert.StartsWith("FEATURE_FAILED: SOLIDWORKS couldn't make the extruded cut", Text(result), StringComparison.Ordinal);
        Assert.Equal(treeBefore, Ok(await InspectionTools.Tree(_connection)));
        Assert.Equal(infoBefore, Ok(await InspectionTools.Info(_connection)));
    });

    // Records what feature creation does while a modal dialog is up; either outcome is fine if it returns.
    [Fact]
    public async Task ExtrudeBehindAModalDialogReturns() => await InNewPart(async () =>
    {
        var sketch = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 20, Height = 20 });
        using var helper = new StaDispatcher();
        var helperBackend = new ComBackend();
        var alreadyOpen = helperBackend.FindBlockingDialogs().Select(d => d.Handle).ToHashSet();
        var messageBox = helper.InvokeAsync(
            () =>
            {
                helperBackend.Connect(launch: false);
                return helperBackend.ShowMessageBox("Datum live test: this closes by itself");
            },
            TimeSpan.FromMinutes(1));
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            BlockingDialog? dialog = null;
            while (dialog is null && DateTime.UtcNow < deadline)
            {
                dialog = helperBackend.FindBlockingDialogs().FirstOrDefault(d => !alreadyOpen.Contains(d.Handle));
                await Task.Delay(100);
            }

            Assert.NotNull(dialog);
            var watch = Stopwatch.StartNew();
            var result = await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 5);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
            Assert.True(result.IsError != true || Text(result).StartsWith("DIALOG_BLOCKING", StringComparison.Ordinal), Text(result));
            Console.WriteLine($"Extrude behind dialog: {Text(result).Trim()}");
            DialogFinder.Close(dialog.Handle);
            await messageBox.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var open in helperBackend.FindBlockingDialogs().Where(d => !alreadyOpen.Contains(d.Handle)))
            {
                DialogFinder.Close(open.Handle);
            }

            await helper.InvokeAsync(() => { helperBackend.Dispose(); return true; }, TimeSpan.FromSeconds(10));
        }
    });

    private async Task InNewPart(Func<Task> test)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task Block()
    {
        var sketch = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true });
        AssertVolume(BlockVolume, Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10)));
    }

    private async Task<string> Sketch(params SketchEntitySpec[] entities)
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, entities));
        return Handle(Ok(await SketchTools.End(_connection, _workspace)));
    }

    private async Task<string> TopFace() => FirstHandle(Ok(await InspectionTools.Faces(_connection, normal: "+z")));

    private async Task<string[]> Edges(double length, string? face = null)
    {
        var edges = Ok(await InspectionTools.Faces(_connection, kind: "edge", face: face, limit: 100));
        return [.. edges.Split('\n')
            .Select(line => EdgeLine().Match(line))
            .Where(m => m.Success && Math.Abs(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - length) < 1e-6)
            .Select(m => m.Groups[1].Value)];
    }

    private static string Handle(string reply) => FeatHandle().Match(reply).Value;

    private static string FirstHandle(string reply) => reply.Split(' ')[0];

    private static double After(string reply) => double.Parse(Volumes().Match(reply).Groups[2].Value, CultureInfo.InvariantCulture);

    // The printed before and after are rounded, so read the exact change from the parentheses.
    private static double Delta(string reply) =>
        Change().Match(reply) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    private static void AssertVolume(double expected, string reply) =>
        Assert.True(Math.Abs(After(reply) - expected) <= expected * 0.005, $"Expected about {expected:0.#} mm³: {reply}");

    private static void AssertDelta(double expected, string reply) =>
        Assert.True(Math.Abs(Delta(reply) - expected) <= Math.Max(1, Math.Abs(expected) * 0.01), $"Expected a change of about {expected:0.#} mm³: {reply}");

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    [GeneratedRegex(@"volume (-?[\d.E+]+) → (-?[\d.E+]+) mm³")]
    private static partial Regex Volumes();

    [GeneratedRegex(@"mm³ \(([-+]?[\d.E+-]+)\)")]
    private static partial Regex Change();

    [GeneratedRegex(@"^(edge#\d+) line ([\d.]+) mm")]
    private static partial Regex EdgeLine();
}
