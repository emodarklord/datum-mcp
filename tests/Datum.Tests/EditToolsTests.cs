using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class EditToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-edit-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private string _part = "";

    public EditToolsTests()
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
    public async Task ListsAndSetsDimensions()
    {
        var boss = await Part();

        var listed = Ok(await EditTools.Dimensions(_connection));
        var set = Ok(await EditTools.SetDimension(_connection, _workspace, "D1@extrusion1", 20));

        Assert.Equal($"D1@extrusion1 10 mm ({boss})\nA1@extrusion1 3°  ({boss})\n".Replace("°  ", "° ", StringComparison.Ordinal), listed);
        Assert.Equal("D1@extrusion1 10 → 20 · volume 1000 → 2000 mm³ (+1000) · 1 body · rebuild ok\n", set);
    }

    [Fact]
    public async Task ABreakingDimensionIsRefusedAndKept()
    {
        await Part();
        _backend.FailNextRebuild();

        var error = Error(await EditTools.SetDimension(_connection, _workspace, "D1@extrusion1", 2));

        Assert.StartsWith("REBUILD_FAILED: setting D1@extrusion1 to 2 breaks the rebuild", error, StringComparison.Ordinal);
        Assert.Contains("D1@extrusion1 10 mm", Ok(await EditTools.Dimensions(_connection)), StringComparison.Ordinal);
        Assert.Equal(ErrorCode.NotFound, Assert.Throws<DatumException>(() => _connection.Checkpoints.StepsToUndo(_part, "cp0")).Code);
    }

    [Fact]
    public async Task RestoresDimensionsSuppressionAndNewFeatures()
    {
        var boss = await Part();
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];
        Ok(await EditTools.SetDimension(_connection, _workspace, "D1@extrusion1", 20));
        Ok(await EditTools.Suppress(_connection, _workspace, [boss]));
        var fillet = Ok(await FeatureTools.Fillet(_connection, _workspace, ["edge#1"], 1));

        var restored = Ok(await EditTools.Restore(_connection, _workspace, start));

        Assert.Contains("fillet2", fillet, StringComparison.Ordinal);
        Assert.StartsWith($"Restored {start}: undid fillet2, suppressing {boss}, setting D1@extrusion1 to 20", restored, StringComparison.Ordinal);
        Assert.Contains("D1@extrusion1 10 mm", Ok(await EditTools.Dimensions(_connection)), StringComparison.Ordinal);
        Assert.Matches($@"^Part1: 1 features\n{boss} extrusion1 \[ExtrudeSpec\]\n$".Replace("ExtrudeSpec", "Extrude", StringComparison.Ordinal), Ok(await InspectionTools.Tree(_connection)));
        Assert.Equal($"{start} \"start\"\n", Ok(await EditTools.List(_connection)).Split('\n')[0] + "\n");
    }

    [Fact]
    public async Task ADeleteIsABarrier()
    {
        var boss = await Part();
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];

        Ok(await EditTools.Delete(_connection, _workspace, [boss]));

        Assert.StartsWith("CANNOT_RESTORE: ", Error(await EditTools.Restore(_connection, _workspace, start)), StringComparison.Ordinal);
        Assert.Contains("(can't restore past this)", Ok(await EditTools.List(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesToDeleteFeaturesOthersDependOn()
    {
        var boss = await Part();
        _backend.GeometryOf(_part).Dependants[boss] = ["feat#99"];

        Assert.StartsWith($"INVALID_ARGUMENT: {boss} has dependent features feat#99", Error(await EditTools.Delete(_connection, _workspace, [boss])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipsStepsAlreadyUndoneByHand()
    {
        await Part();
        var start = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];
        var fillet = Ok(await FeatureTools.Fillet(_connection, _workspace, ["edge#1"], 1)).Split(' ')[2];
        await _connection.UseAsync(c => c.RunAsync(b => b.DeleteFeatures(_part, [fillet], withAbsorbed: false)), CancellationToken.None);

        var restored = Ok(await EditTools.Restore(_connection, _workspace, start));

        Assert.Contains("skipped fillet2 (already changed in SOLIDWORKS)", restored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditsNeedAnEditableDocument()
    {
        var file = Path.Combine(_root, "Datum", "theirs.SLDPRT");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "user's part");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        Assert.StartsWith("NOT_OWNED: ", Error(await EditTools.Rebuild(_connection, _workspace)), StringComparison.Ordinal);
    }

    private async Task<string> Part()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        _part = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        var boss = Ok(await FeatureTools.Extrude(_connection, _workspace, "feat#1", depth: 10)).Split(' ')[2];
        _backend.GeometryOf(_part).Dimensions.AddRange([new DimensionInfo("D1@extrusion1", 10, false, boss), new DimensionInfo("A1@extrusion1", 3, true, boss)]);
        return boss;
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
