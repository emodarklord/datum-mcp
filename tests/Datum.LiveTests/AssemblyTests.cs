using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// A 60 x 40 x 10 plate with a 10 mm hole at its centre, and a 10 mm diameter pin 30 long.
public sealed partial class AssemblyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-assembly-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public AssemblyTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    private string Plate => Path.Combine(_root, "Datum", "plate.sldprt");

    private string Pin => Path.Combine(_root, "Datum", "pin.sldprt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddsMovesAndRotatesComponentsWithoutTouchingTheFiles() => await InNewAssembly(async () =>
    {
        var stamp = File.GetLastWriteTimeUtc(Plate);

        var added = Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 10, y: 20, z: 30));
        var handle = added.Split(' ')[1];
        Assert.Contains("at 10,20,30 mm", added, StringComparison.Ordinal);
        Ok(await AssemblyTools.Fix(_connection, _workspace, handle, isFixed: false));
        var moved = Ok(await AssemblyTools.Move(_connection, _workspace, handle, 100, 50, 0, rz: 90));
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{added}{moved}{list}");

        Assert.Contains("moved at 100,50,0 mm, rotated 0,0,90°", moved, StringComparison.Ordinal);
        Assert.Contains("plate-1", list, StringComparison.Ordinal);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Plate));
        Assert.DoesNotContain("plate", await OpenTitles(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public async Task AddingTheSameFileTwiceGivesTwoComponents() => await InNewAssembly(async () =>
    {
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 100));

        var list = Ok(await AssemblyTools.List(_connection));

        Assert.Contains("plate-1", list, StringComparison.Ordinal);
        Assert.Contains("plate-2", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AFixedComponentDoesNotMoveAndUndoWorks() => await InNewAssembly(async () =>
    {
        var checkpoint = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];
        var handle = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];

        var moved = Text(await AssemblyTools.Move(_connection, _workspace, handle, 50, 0, 0));

        Assert.StartsWith($"INVALID_ARGUMENT: {handle} is fixed", moved, StringComparison.Ordinal);
        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Equal("No components\n", Ok(await AssemblyTools.List(_connection)));
    });

    [Fact]
    public async Task AMissingFileIsRefused() => await InNewAssembly(async () =>
    {
        var reply = Text(await AssemblyTools.Add(_connection, _workspace, Path.Combine(_root, "Datum", "nothing.sldprt")));

        Assert.StartsWith("NOT_FOUND: ", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task APinSeatsInAHoleAndAMeasureConfirmsIt() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var hole = await FaceOf(plate, surface: "cylinder");
        var shaft = await FaceOf(pin, surface: "cylinder");

        var concentric = Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", hole, shaft));
        var seated = Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        var turned = Ok(await AssemblyTools.Mate(_connection, _workspace, "parallel", $"{plate}:front", $"{pin}:front"));
        var pinTop = await FaceOf(pin, normal: "+y");
        var height = Ok(await CheckTools.Measure(_connection, pinTop, await FaceOf(plate, normal: "+y")));
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{concentric}{seated}{turned}{height}{list}{Ok(await AssemblyTools.Mates(_connection))}");

        Assert.Contains("normal distance 30 mm", height, StringComparison.Ordinal);
        Assert.Contains("All components are fixed or fully defined", turned, StringComparison.Ordinal);
        Assert.Contains("at 0,10,0 mm", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TwoInstancesKeepSeparateFaces() => await InNewAssembly(async () =>
    {
        var first = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var second = Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 100, y: 5)).Split(' ')[1];

        var a = await FaceOf(first, normal: "+y");
        var b = await FaceOf(second, normal: "+y");
        var between = Ok(await CheckTools.Measure(_connection, a, b));
        var centre = Ok(await InspectionTools.Faces(_connection, component: second, normal: "+y"));
        Console.WriteLine($"{between}{centre}");

        Assert.NotEqual(a, b);
        Assert.Contains("normal distance 5 mm", between, StringComparison.Ordinal);
        Assert.Contains("100,15,0", centre, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnImpossibleMateRollsBackAndLeavesEverythingInPlace() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        var before = Ok(await AssemblyTools.List(_connection));

        var reply = Text(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "-y"), await FaceOf(pin, normal: "-y")));
        Console.WriteLine(reply);

        Assert.StartsWith("FEATURE_FAILED: ", reply, StringComparison.Ordinal);
        Assert.Equal(before, Ok(await AssemblyTools.List(_connection)));
        Assert.Single(Ok(await AssemblyTools.Mates(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    });

    [Fact]
    public async Task AMatedComponentIsHeldAndUndoLetsItGo() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var mated = Ok(await AssemblyTools.Mate(_connection, _workspace, "lock", plate, pin));

        var held = Ok(await AssemblyTools.Move(_connection, _workspace, pin, 200, 0, 0));
        Console.WriteLine($"{mated}{held}");
        Assert.Contains("mates hold it", held, StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Equal("No mates\n", Ok(await AssemblyTools.Mates(_connection)));
        Assert.Contains("at 100,0,0 mm", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    });

    private async Task<string> FaceOf(string component, string? surface = null, string? normal = null) =>
        Ok(await InspectionTools.Faces(_connection, component: component, surface: surface, normal: normal)).Split(' ')[0];

    private async Task<string> OpenTitles() => Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None));

    private async Task InNewAssembly(Func<Task> test)
    {
        await MakePart(Plate, "Top", [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 60, Height = 40, Dimension = true }, new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true }], 10);
        await MakePart(Pin, "Top", [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true }], 30);
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task MakePart(string file, string plane, SketchEntitySpec[] profile, double depth)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, plane));
        Ok(await SketchTools.Add(_connection, _workspace, profile));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: depth));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
