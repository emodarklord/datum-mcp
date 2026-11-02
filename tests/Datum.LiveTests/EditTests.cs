using System.Globalization;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed partial class EditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-edit-").FullName;
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(60));
    private readonly Workspace _workspace;

    public EditTests() => _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ChangingTheDepthDoublesTheVolume() => await InNewPart(async () =>
    {
        await Block();

        var dimensions = Ok(await EditTools.Dimensions(_connection));
        var reply = Ok(await EditTools.SetDimension(_connection, _workspace, "D1@Boss-Extrude1", 20));
        Console.WriteLine($"{dimensions}\n{reply}");

        Assert.Matches(@"(?m)^D1@Boss-Extrude1 10 mm \(feat#\d+\)$", dimensions);
        Assert.Contains("D1@Boss-Extrude1 10 → 20 · volume 15000 → 30000 mm³", reply, StringComparison.Ordinal);
        Assert.EndsWith("rebuild ok\n", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnEditThatBreaksTheRebuildIsPutBack() => await InNewPart(async () =>
    {
        await Block();
        Ok(await FeatureTools.Shell(_connection, _workspace, 2, [await TopFace()]));
        var width = WidthDimension().Match(Ok(await EditTools.Dimensions(_connection))).Groups[1].Value;
        var infoBefore = Ok(await InspectionTools.Info(_connection));

        // Two 2 mm shell walls can't fit in a 3 mm wide block.
        var result = await EditTools.SetDimension(_connection, _workspace, width, 3);
        Console.WriteLine($"{width}: {Text(result)}");

        Assert.True(result.IsError);
        Assert.StartsWith($"REBUILD_FAILED: setting {width} to 3 breaks the rebuild: ", Text(result), StringComparison.Ordinal);
        Assert.Contains("Shell1", Text(result), StringComparison.Ordinal);
        Assert.Contains($"{width} 50 mm", Ok(await EditTools.Dimensions(_connection)), StringComparison.Ordinal);
        Assert.Equal(infoBefore, Ok(await InspectionTools.Info(_connection)));
    });

    [Fact]
    public async Task SuppressingAHoleFillsItBackIn() => await InNewPart(async () =>
    {
        await Block();
        var hole = Handle(Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,0,10", 6)));

        var suppressed = Ok(await EditTools.Suppress(_connection, _workspace, [hole]));
        var restored = Ok(await EditTools.Suppress(_connection, _workspace, [hole], suppress: false));

        AssertDelta(Math.PI * 9 * 10, suppressed);
        AssertDelta(-Math.PI * 9 * 10, restored);
    });

    [Fact]
    public async Task DeletesAHoleButNotWhatOthersDependOn() => await InNewPart(async () =>
    {
        var boss = await Block();
        var hole = Handle(Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,0,10", 6)));

        var deleted = Ok(await EditTools.Delete(_connection, _workspace, [hole]));
        var tree = Ok(await InspectionTools.Tree(_connection));
        Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,0,10", 6));
        var refused = await EditTools.Delete(_connection, _workspace, [boss]);
        Console.WriteLine($"{deleted}\n{tree}\n{Text(refused)}");

        AssertDelta(Math.PI * 9 * 10, deleted);
        Assert.DoesNotContain("Cut-Extrude1", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("Sketch2", tree, StringComparison.Ordinal);
        Assert.True(refused.IsError);
        Assert.StartsWith("INVALID_ARGUMENT: Boss-Extrude1 has dependent features", Text(refused), StringComparison.Ordinal);
    });

    [Fact]
    public async Task RestoringACheckpointPutsTheModelBackExactly() => await InNewPart(async () =>
    {
        await Block();
        var treeBefore = Ok(await InspectionTools.Tree(_connection));
        var infoBefore = Ok(await InspectionTools.Info(_connection));
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];

        Ok(await FeatureTools.Fillet(_connection, _workspace, await Edges(length: 10), 2));
        Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,0,10", 6));
        Ok(await EditTools.SetDimension(_connection, _workspace, "D1@Boss-Extrude1", 20));
        var list = Ok(await EditTools.List(_connection));
        var restored = Ok(await EditTools.Restore(_connection, _workspace, start));
        Console.WriteLine($"{list}\n{restored}");

        Assert.Equal(treeBefore, Ok(await InspectionTools.Tree(_connection)));
        Assert.Equal(infoBefore, Ok(await InspectionTools.Info(_connection)));
        Assert.DoesNotContain("skipped", restored, StringComparison.Ordinal);
    });

    [Fact]
    public async Task FeatureHandlesSurviveARebuild() => await InNewPart(async () =>
    {
        await Block();
        var before = Ok(await InspectionTools.Tree(_connection));

        Ok(await FeatureTools.Fillet(_connection, _workspace, await Edges(length: 10), 2));
        var after = Ok(await InspectionTools.Tree(_connection));
        Console.WriteLine($"{before}\n{after}");

        Assert.All(before.Split('\n').Skip(1).Where(line => line.Length > 0), line => Assert.Contains(line, after, StringComparison.Ordinal));
    });

    [Fact]
    public async Task FeatureHandlesReadDuringASketchStayTheSame() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        var during = Ok(await InspectionTools.Tree(_connection));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30 }]));
        Ok(await SketchTools.End(_connection, _workspace));
        var after = Ok(await InspectionTools.Tree(_connection));
        Console.WriteLine($"{during}\n{after}");

        Assert.All(during.Split('\n').Where(line => line.Contains("Plane", StringComparison.Ordinal)), line => Assert.Contains(line, after, StringComparison.Ordinal));
    });

    [Fact]
    public async Task ARestoreCannotCrossADelete() => await InNewPart(async () =>
    {
        await Block();
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];
        var hole = Handle(Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,0,10", 6)));
        Ok(await EditTools.Delete(_connection, _workspace, [hole]));
        var treeBefore = Ok(await InspectionTools.Tree(_connection));

        var result = await EditTools.Restore(_connection, _workspace, start);

        Assert.True(result.IsError);
        Assert.StartsWith("CANNOT_RESTORE: ", Text(result), StringComparison.Ordinal);
        Assert.Equal(treeBefore, Ok(await InspectionTools.Tree(_connection)));
    });

    [Fact]
    public async Task RebuildReportsACleanModel() => await InNewPart(async () =>
    {
        await Block();

        Assert.EndsWith("rebuild ok\n", Ok(await EditTools.Rebuild(_connection, _workspace)), StringComparison.Ordinal);
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

    private async Task<string> Block()
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true }]));
        var sketch = Handle(Ok(await SketchTools.End(_connection, _workspace)));
        return Handle(Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10)));
    }

    private async Task<string> TopFace() => Ok(await InspectionTools.Faces(_connection, normal: "+z")).Split(' ')[0];

    private async Task<string[]> Edges(double length)
    {
        var edges = Ok(await InspectionTools.Faces(_connection, kind: "edge", limit: 100));
        return [.. edges.Split('\n')
            .Select(line => EdgeLine().Match(line))
            .Where(m => m.Success && Math.Abs(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - length) < 1e-6)
            .Select(m => m.Groups[1].Value)];
    }

    private static string Handle(string reply) => FeatHandle().Match(reply).Value;

    private static void AssertDelta(double expected, string reply) =>
        Assert.True(
            Change().Match(reply) is { Success: true } m && Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - expected) <= Math.Max(1, Math.Abs(expected) * 0.01),
            $"Expected a change of about {expected:0.#} mm³: {reply}");

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    [GeneratedRegex(@"mm³ \(([-+]?[\d.E+-]+)\)")]
    private static partial Regex Change();

    [GeneratedRegex(@"^(edge#\d+) line ([\d.]+) mm")]
    private static partial Regex EdgeLine();

    [GeneratedRegex(@"(?m)^(D\d+@Sketch1) 50 mm")]
    private static partial Regex WidthDimension();
}
