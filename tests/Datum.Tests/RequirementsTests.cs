using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class RequirementsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-requirements-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private readonly RequirementStore _store;
    private string _part = "";

    public RequirementsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        _store = new RequirementStore(Path.Combine(_root, "requirements"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("mass", 100, null, RequirementStatus.Pass)]
    [InlineData("mass", 102, null, RequirementStatus.Fail)]
    [InlineData("volume", 37000, 200d, RequirementStatus.Pass)]
    [InlineData("box_x", 60, null, RequirementStatus.Pass)]
    [InlineData("box_x", 60.02, null, RequirementStatus.Fail)]
    [InlineData("box_y", 40, null, RequirementStatus.Pass)]
    [InlineData("box_z", 5, null, RequirementStatus.Pass)]
    [InlineData("bodies", 1, null, RequirementStatus.Pass)]
    [InlineData("bodies", 2, null, RequirementStatus.Fail)]
    public void NumbersAreComparedWithTheirTolerance(string check, double target, double? tolerance, RequirementStatus expected)
    {
        var item = new RequirementSpec { Id = "r1", Text = "size", Check = check, Target = target, Tolerance = tolerance };

        Assert.Equal(expected, RequirementChecks.Evaluate(item, Facts()).Status);
    }

    [Fact]
    public void ADimensionIsReadByName()
    {
        var item = new RequirementSpec { Id = "r1", Text = "hole", Check = "dimension", Name = "D1@Hole", Target = 8 };

        var result = RequirementChecks.Evaluate(item, Facts());

        Assert.Equal(RequirementStatus.Pass, result.Status);
        Assert.Equal("measured 8 mm, target 8 mm ±0.01", result.Detail);
    }

    [Fact]
    public void AMissingDimensionIsUnverifiedNotFailedOrPassed()
    {
        var item = new RequirementSpec { Id = "r1", Text = "hole", Check = "dimension", Name = "D9@Gone", Target = 8 };

        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(item, Facts()).Status);
    }

    [Fact]
    public void ManualItemsStayUnverifiedUntilMarked()
    {
        var item = new RequirementSpec { Id = "r1", Text = "looks right" };

        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(item, Facts()).Status);
        var marked = RequirementChecks.Evaluate(item with { Mark = "user looked at it" }, Facts());
        Assert.Equal(RequirementStatus.Marked, marked.Status);
        Assert.Equal("marked by hand, not measured: user looked at it", marked.Detail);
    }

    [Fact]
    public void StaleHandlesAreUnverified()
    {
        var item = new RequirementSpec { Id = "r1", Text = "gap", Check = "measure", First = "face#1", Quantity = "distance", Target = 5 };

        var result = RequirementChecks.Evaluate(item, Facts(), _ => throw new DatumException(ErrorCode.NotFound, "face#1 no longer exists"));

        Assert.Equal(RequirementStatus.Unverified, result.Status);
        Assert.Contains("set the requirement again", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildErrorsFailTheirRequirement()
    {
        var facts = Facts() with { Features = [new("feat#1", "Fillet1", "Fillet", 0, false, "error x")] };
        var item = new RequirementSpec { Id = "r1", Text = "rebuilds", Check = "no_rebuild_errors" };

        Assert.Equal("errors in Fillet1", RequirementChecks.Evaluate(item, facts).Detail);
    }

    [Theory]
    [InlineData("r1", "", "mass", "requirement r1 needs text")]
    [InlineData("r1", "x", "weight", "requirement r1 needs a known check, not 'weight'")]
    [InlineData("r1", "x", "mass", "requirement r1 needs a target")]
    public void BadItemsAreRefusedBeforeAnythingIsStored(string id, string text, string check, string expected)
    {
        var error = Assert.Throws<DatumException>(() => RequirementChecks.Validate([new RequirementSpec { Id = id, Text = text, Check = check }]));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task ReportsFailuresFirstAndAlwaysSaysWhatItDidNotProve()
    {
        await Part();
        Ok(await CheckTools.SetRequirements(_connection, _store,
        [
            new RequirementSpec { Id = "r1", Text = "Length", Source = "drawing p2", Check = "box_x", Target = 60 },
            new RequirementSpec { Id = "r2", Text = "Mass", Check = "mass", Target = 500 },
            new RequirementSpec { Id = "r3", Text = "Looks right" },
        ]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, _store));

        Assert.Equal(
            "Part1: 1 pass, 1 fail, 1 unverified, 0 marked by hand\n"
                + "FAIL r2 Mass: measured 100 g, target 500 g ±5\n"
                + "UNVERIFIED r3 Looks right: no automatic check; a person must confirm it\n"
                + "PASS r1 Length [drawing p2]: measured 60 mm, target 60 mm ±0.01\n"
                + "Only the listed items were checked, so passing doesn't prove the part is right.\n",
            report);
    }

    [Fact]
    public async Task OnlyManualItemsCanBeMarkedAndMarksPersist()
    {
        await Part();
        Ok(await CheckTools.SetRequirements(_connection, _store,
        [
            new RequirementSpec { Id = "r1", Text = "Mass", Check = "mass", Target = 500 },
            new RequirementSpec { Id = "r2", Text = "Looks right" },
        ]));

        Assert.StartsWith("INVALID_ARGUMENT: r1 isn't a manual requirement", Error(await CheckTools.CheckRequirements(_connection, _store, [new RequirementMark { Id = "r1", Note = "trust me" }])), StringComparison.Ordinal);
        Ok(await CheckTools.CheckRequirements(_connection, _store, [new RequirementMark { Id = "r2", Note = "user confirmed" }]));

        Assert.Contains("MARKED r2 Looks right: marked by hand, not measured: user confirmed", Ok(await CheckTools.CheckRequirements(_connection, _store)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckWithNoListSaysSo()
    {
        await Part();

        Assert.StartsWith("NOT_FOUND: no requirements stored", Error(await CheckTools.CheckRequirements(_connection, _store)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleHandleIsReportedThroughTheTool()
    {
        await Part();
        Ok(await CheckTools.SetRequirements(_connection, _store, [new RequirementSpec { Id = "r1", Text = "Gap", Check = "measure", First = "face#404", Quantity = "distance", Target = 5 }]));

        Assert.Contains("UNVERIFIED r1 Gap: handles no longer valid", Ok(await CheckTools.CheckRequirements(_connection, _store)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheListSurvivesANewStoreAndFollowsTheFirstSave()
    {
        var unsaved = new DocumentInfo("d1", "Part1", null, DocumentType.Part, false, true, false);
        var saved = unsaved with { Title = "Bracket.SLDPRT", Path = @"C:\Parts\Bracket.SLDPRT" };
        _store.Save(unsaved, [new RequirementSpec { Id = "r1", Text = "x" }]);

        Assert.Single(_store.Load(saved));
        Assert.Single(new RequirementStore(Path.Combine(_root, "requirements")).Load(saved));
        Assert.Empty(_store.Load(unsaved with { Id = "d2", Title = "Part2" }));
    }

    [Fact]
    public async Task FlexReportsProblemsAndPassesItsOptionsOn()
    {
        await Part();
        _backend.GeometryOf(_part).Flexed = new FlexResult(
            [new("D1@S", 10, 66, " mm", null), new("D1@S", -10, 54, " mm", "rebuild error in Fillet1 (FilletRadiusTooBig)"), new("D2@S", 10, 44, " mm", null), new("D2@S", -10, 36, " mm", null)],
            3,
            null);

        var report = Ok(await CheckTools.Flex(_connection, _workspace, percent: 10));

        Assert.Equal(
            "Flexed 2 dimensions by ±10%: 1 fine, 1 problems\n"
                + "D1@S -10% (54 mm): rebuild error in Fillet1 (FilletRadiusTooBig)\n"
                + "3 dimensions weren't tested; name them to test them\n"
                + "All values restored. This shows which sizes rebuild, not whether the design intent is right.\n",
            report);
        Assert.Equal((null, 10, FlexResult.DefaultLimit), _backend.LastFlex);
    }

    [Fact]
    public async Task AFlexThatDoesNotRestoreWarnsAndBlocksCheckpointRestore()
    {
        await Part();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        _backend.GeometryOf(_part).Flexed = new FlexResult([], 0, "the volume changed");

        Assert.Contains("WARNING: the part didn't return to its starting state: the volume changed", Ok(await CheckTools.Flex(_connection, _workspace)), StringComparison.Ordinal);
        Assert.StartsWith("CANNOT_RESTORE: ", Error(await EditTools.Restore(_connection, _workspace, checkpoint)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-5)]
    public async Task FlexRefusesAnImpossiblePercent(double percent)
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: percent", Error(await CheckTools.Flex(_connection, _workspace, percent: percent)), StringComparison.Ordinal);
    }

    private static RequirementFacts Facts() => new(
        new ModelInfo("Bracket", DocumentType.Part, "AISI 304", 100, 37000, 9000, default, new Box(default, new Point3(60, 40, 5)), 1),
        [new DimensionInfo("D1@Hole", 8, false, "feat#2")],
        [new CustomProperty("PartNo", "42", "42")],
        []);

    private async Task Part()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        _part = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(_part).Model = new ModelInfo("Part1", DocumentType.Part, null, 100, 37000, 9000, default, new Box(default, new Point3(60, 40, 5)), 1);
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
