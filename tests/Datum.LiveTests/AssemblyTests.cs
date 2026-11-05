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
