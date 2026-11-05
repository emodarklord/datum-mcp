using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class AssemblyToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-assembly-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public AssemblyToolsTests()
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
    public async Task AddsAndListsAComponent()
    {
        await Assembly();

        var added = Ok(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt", x: 10));

        Assert.Matches(@"^Added comp#\d+ plate-1 \[Default\] under defined, at 10,0,0 mm\n$", added);
        Assert.Equal(added["Added ".Length..], Ok(await AssemblyTools.List(_connection)));
    }

    [Fact]
    public async Task ComponentToolsNeedAnAssembly()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssemblyOpenedFromDiskNeedsPermission()
    {
        var file = Path.Combine(_root, "Datum", "theirs.sldasm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "assembly");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        Assert.StartsWith("NOT_OWNED: ", Error(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingReportsThePoseAndCanBeUndone()
    {
        await Assembly();
        var plate = await Component();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        Assert.Equal($"{plate} moved at 5,6,7 mm, rotated 0,0,90°\n", Ok(await AssemblyTools.Move(_connection, _workspace, plate, 5, 6, 7, rz: 90)));

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Contains("at 0,0,0 mm\n", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldComponentSaysSo()
    {
        await Assembly();
        var plate = await Component();
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(id).HeldComponents.Add(plate);

        Assert.Equal($"{plate} didn't reach the requested pose; mates hold it at 0,0,0 mm\n", Ok(await AssemblyTools.Move(_connection, _workspace, plate, 5, 0, 0)));
    }

    [Fact]
    public async Task AFixedComponentIsNotMoved()
    {
        await Assembly();
        var plate = await Component();
        Ok(await AssemblyTools.Fix(_connection, _workspace, plate));

        Assert.StartsWith($"INVALID_ARGUMENT: {plate} is fixed", Error(await AssemblyTools.Move(_connection, _workspace, plate, 5, 0, 0)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixingAndAddingCanBeUndone()
    {
        await Assembly();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var plate = await Component();
        Assert.Contains(" fixed, ", Ok(await AssemblyTools.Fix(_connection, _workspace, plate)), StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));

        Assert.Equal("No components\n", Ok(await AssemblyTools.List(_connection)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 0, 0)]
    [InlineData(0, 90, 0)]
    [InlineData(0, 0, 90)]
    [InlineData(30, -45, 120)]
    public void PosesSurviveARoundTripThroughATransform(double rx, double ry, double rz)
    {
        var pose = new ComponentPose(new Point3(5, -6, 7), new Point3(rx, ry, rz));

        var back = ComponentPose.FromTransform(pose.ToTransform());

        Assert.True(pose.Matches(back), $"{pose} became {back}");
    }

    [Fact]
    public void RotatingAboutZTurnsXIntoY()
    {
        var pose = new ComponentPose(new Point3(1000, 0, 0), new Point3(0, 0, 90));

        var moved = pose.Place(new Point3(10, 0, 0));

        Assert.Equal(1000, moved.X, 6);
        Assert.Equal(10, moved.Y, 6);
        Assert.Equal(new Point3(0, 1, 0), pose.Turn(new Point3(1, 0, 0)) with { X = 0 });
    }

    [Fact]
    public void PosesThatDifferDoNotMatch()
    {
        var pose = new ComponentPose(new Point3(5, 0, 0), default);

        Assert.False(pose.Matches(new ComponentPose(new Point3(5.01, 0, 0), default)));
        Assert.False(pose.Matches(new ComponentPose(new Point3(5, 0, 0), new Point3(0, 0, 1))));
    }

    [Fact]
    public async Task AMateReportsWhatMovedAndWhatIsFree()
    {
        await Assembly();
        var plate = await Component();
        Geometry().NextMateMoves = [new ComponentMoved(plate, "plate-1", default, new ComponentPose(new Point3(0, 10, 0), default))];

        var reply = Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", "face#1", "face#2"));

        Assert.Matches(@"^Coincident1 is feat#\d+\n", reply);
        Assert.Contains($"{plate} plate-1 moved at 0,10,0 mm\n", reply, StringComparison.Ordinal);
        Assert.EndsWith("Still free to move: plate-1\n", reply, StringComparison.Ordinal);
        Assert.Contains("[coincident] face#1 to face#2", Ok(await AssemblyTools.Mates(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMateCanBeUndoneAndItsComponentsGoBack()
    {
        await Assembly();
        var plate = await Component();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        Geometry().NextMateMoves = [new ComponentMoved(plate, "plate-1", new ComponentPose(new Point3(1, 2, 3), default), default)];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "lock", "face#1", "face#2"));

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));

        Assert.Contains("at 1,2,3 mm", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glue", null, "closest", "INVALID_ARGUMENT: unknown mate 'glue'")]
    [InlineData("distance", null, "closest", "INVALID_ARGUMENT: a distance mate needs a value")]
    [InlineData("distance", -1d, "closest", "INVALID_ARGUMENT: a distance mate needs a value")]
    [InlineData("coincident", 5d, "closest", "INVALID_ARGUMENT: a coincident mate takes no value")]
    [InlineData("coincident", null, "sideways", "INVALID_ARGUMENT: unknown alignment 'sideways'")]
    public async Task BadMatesAreRefusedBeforeReachingSolidWorks(string kind, double? value, string alignment, string expected)
    {
        await Assembly();

        var reply = Error(await AssemblyTools.Mate(_connection, _workspace, kind, "face#1", "face#2", value, alignment));

        Assert.StartsWith(expected, reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedMateIsReported()
    {
        await Assembly();
        _backend.FailNextFeature();

        Assert.StartsWith("FEATURE_FAILED: ", Error(await AssemblyTools.Mate(_connection, _workspace, "coincident", "face#1", "face#2")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FacesNeedAComponentInAnAssembly()
    {
        await Assembly();
        var plate = await Component();
        Geometry().Faces.Add(new FaceInfo("face#7", SurfaceKind.Plane, 100, null, default, null, null));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await InspectionTools.Faces(_connection)), StringComparison.Ordinal);
        Assert.StartsWith("face#7 ", Ok(await InspectionTools.Faces(_connection, component: plate)), StringComparison.Ordinal);
    }

    private FakeGeometry Geometry() =>
        _backend.GeometryOf(_connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None).GetAwaiter().GetResult().Single(d => d.Active).Id);

    private async Task Assembly() => Ok(await DocumentTools.New(_connection, _workspace, "assembly"));

    private async Task<string> Component() => Ok(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")).Split(' ')[1];

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
