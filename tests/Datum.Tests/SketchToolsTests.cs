using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class SketchToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-sketch-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public SketchToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task DrawsAFullyDefinedProfileInOneCall()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var begun = Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        var added = Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "rect", X1 = 0, Y1 = 0, X2 = 50, Y2 = 30, Dimension = true },
            new SketchEntitySpec { Type = "circle", Cx = 25, Cy = 15, Radius = 5, Dimension = true },
        ]));
        var ended = Ok(await SketchTools.End(_connection, _workspace));

        Assert.Equal("Editing Sketch1 on Front: x → +x, y → +y, origin 0,0,0 mm\n", begun);
        var lines = added.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        Assert.Matches(@"^seg#\d+ line 0,0 → 50,0$", lines[0]);
        Assert.Matches(@"^seg#\d+ circle centre 25,15 radius 5$", lines[4]);
        Assert.Equal("Sketch1: 5 segments, fully defined", lines[5]);
        Assert.Matches(@"^Sketch1 is feat#\d+, fully defined, 5 segments\n$", ended);
    }

    [Fact]
    public async Task ConstraintsCanFinishDefiningASketch()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
        var added = Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, X2 = 10, Y2 = 3 }]));
        var segment = added.Split(' ')[0];

        var constrained = Ok(await SketchTools.Constrain(_connection, _workspace, [new SketchConstraintSpec { Kind = "horizontal", Entities = [segment] }]));

        Assert.EndsWith("under defined\n", added, StringComparison.Ordinal);
        Assert.Equal("Sketch1: 1 segments, fully defined\n", constrained);
    }

    [Fact]
    public async Task ABadBatchDrawsNothing()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));

        var result = await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "rect", X1 = 0, Y1 = 0, X2 = 50, Y2 = 30 },
            new SketchEntitySpec { Type = "circle", Cx = 1, Cy = 1 },
        ]);

        Assert.StartsWith("INVALID_ARGUMENT: entity 1 (circle) needs radius > 0", Error(result), StringComparison.Ordinal);
        Assert.Matches(@"^Sketch1 is feat#\d+, under defined, 0 segments\n$", Ok(await SketchTools.End(_connection, _workspace)));
    }

    [Fact]
    public async Task NeedsAnActiveSketch()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        var result = await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "point", X1 = 1, Y1 = 1 }]);

        Assert.Equal("INVALID_ARGUMENT: no sketch is being edited. Hint: Call sketch_begin first", Error(result));
    }

    [Fact]
    public async Task RefusesDocumentsDatumMayNotEdit()
    {
        var file = Path.Combine(_root, "Datum", "theirs.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "user's part");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        Assert.StartsWith("NOT_OWNED: ", Error(await SketchTools.Begin(_connection, _workspace, "Front")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesToStartASecondSketch()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));

        Assert.Equal(
            "INVALID_ARGUMENT: Sketch1 is still being edited. Hint: Call sketch_end first",
            Error(await SketchTools.Begin(_connection, _workspace, "Top")));
    }

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, StatusToolsTests.TextOf(result));
        return StatusToolsTests.TextOf(result);
    }

    private static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return StatusToolsTests.TextOf(result);
    }
}
