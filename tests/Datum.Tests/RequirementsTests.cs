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
        _store = _workspace.Requirements;
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

    [Fact]
    public void AssemblyChecksReadTheAssembly()
    {
        var fixedBase = new ComponentInfo("comp#1", "base-1", null, "Default", true, false, "fully defined", default);
        var free = new ComponentInfo("comp#2", "pin-1", null, "Default", false, false, "under defined", default);
        var facts = Facts() with { Components = [fixedBase, free], Interferences = [new InterferencePair(["a", "b"], 5)], Bom = [new BomLine("pin.SLDPRT", "Default", 3, null, null)] };
        RequirementResult Run(RequirementSpec item) => RequirementChecks.Evaluate(item, facts);

        Assert.Equal("a and b overlap by 5 mm³", Run(new RequirementSpec { Id = "r", Text = "x", Check = "no_interference" }).Detail);
        Assert.Equal("pin-1 is under defined", Run(new RequirementSpec { Id = "r", Text = "x", Check = "fully_mated" }).Detail);
        Assert.Equal(RequirementStatus.Pass, Run(new RequirementSpec { Id = "r", Text = "x", Check = "bom_quantity", Name = "pin.sldprt", Target = 3 }).Status);
        Assert.Equal(RequirementStatus.Fail, Run(new RequirementSpec { Id = "r", Text = "x", Check = "bom_quantity", Name = "pin", Target = 4 }).Status);
    }

    [Fact]
    public void AssemblyChecksOnAPartAreUnverified()
    {
        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(new RequirementSpec { Id = "r", Text = "x", Check = "no_interference" }, Facts()).Status);
        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(new RequirementSpec { Id = "r", Text = "x", Check = "fully_mated" }, Facts()).Status);
    }

    [Fact]
    public void AnAssemblyWithNothingFixedIsNotFullyMated()
    {
        var free = new ComponentInfo("comp#2", "pin-1", null, "Default", false, false, "fully defined", default);

        var result = RequirementChecks.Evaluate(new RequirementSpec { Id = "r", Text = "x", Check = "fully_mated" }, Facts() with { Components = [free] });

        Assert.Equal("no component is fixed", result.Detail);
    }

    [Fact]
    public void TargetsAreComparedAndShownWithoutRounding()
    {
        var item = new RequirementSpec { Id = "r1", Text = "Volume", Check = "volume", Target = 36995.5, Tolerance = 0.1 };

        var result = RequirementChecks.Evaluate(item, Facts());

        Assert.Equal(RequirementStatus.Fail, result.Status);
        Assert.Equal("measured 37000 mm³, target 36995.5 mm³ ±0.1", result.Detail);
    }

    [Fact]
    public void DrawingChecksReadTheSheet()
    {
        var sheet = new DrawingFacts(279.4, 215.9, [], [], [new SheetDimension("view#1", "D1@S", 80, " mm", false, null)], [], false)
        {
            BomRows = [new BomRow("1", "pin", "", 2)],
            Balloons = [new BalloonInfo("1", "pin-1", false)],
        };
        var facts = Facts() with { Drawing = sheet };
        RequirementResult Run(RequirementSpec item) => RequirementChecks.Evaluate(item, facts);

        Assert.Equal(RequirementStatus.Pass, Run(new RequirementSpec { Id = "r", Text = "x", Check = "shown_dimension", Target = 80 }).Status);
        Assert.Equal(RequirementStatus.Fail, Run(new RequirementSpec { Id = "r", Text = "x", Check = "shown_dimension", Target = 81 }).Status);
        Assert.Equal(RequirementStatus.Fail, Run(new RequirementSpec { Id = "r", Text = "x", Check = "view_count", Target = 1 }).Status);
        Assert.Equal(RequirementStatus.Pass, Run(new RequirementSpec { Id = "r", Text = "x", Check = "balloons_complete" }).Status);
        Assert.Equal(RequirementStatus.Fail, Run(new RequirementSpec { Id = "r", Text = "x", Check = "drawing_clean" }).Status);
        Assert.Equal(RequirementStatus.Unverified, Run(new RequirementSpec { Id = "r", Text = "x", Check = "mass", Target = 5 }).Status);
    }

    [Fact]
    public void DrawingChecksOnAPartAreUnverified()
    {
        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(new RequirementSpec { Id = "r", Text = "x", Check = "drawing_clean" }, Facts()).Status);
    }

    [Fact]
    public void SketchDefinitionIsChecked()
    {
        var item = new RequirementSpec { Id = "r1", Text = "defined", Check = "sketches_defined" };
        SketchInfo Sketch(string name, string status) => new("feat#1", name, status, 1, default, default, default);

        Assert.Equal(RequirementStatus.Pass, RequirementChecks.Evaluate(item, Facts() with { Sketches = [Sketch("Sketch1", "fully defined")] }).Status);
        var loose = RequirementChecks.Evaluate(item, Facts() with { Sketches = [Sketch("Sketch1", "fully defined"), Sketch("Sketch2", "under defined")] });
        Assert.Equal(RequirementStatus.Fail, loose.Status);
        Assert.Equal("Sketch2 is under defined", loose.Detail);
    }

    [Fact]
    public void FlexPassesOnlyWhenEveryDimensionWasTestedAndFine()
    {
        var item = new RequirementSpec { Id = "r1", Text = "robust", Check = "flex" };
        FlexStep[] fine = [new("D1@S", 10, 11, " mm", null), new("D1@S", -10, 9, " mm", null)];

        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(item, Facts()).Status);
        Assert.Equal(RequirementStatus.Pass, RequirementChecks.Evaluate(item, Facts() with { Flex = new FlexResult(fine, 0, null) }).Status);
        Assert.Equal(RequirementStatus.Unverified, RequirementChecks.Evaluate(item, Facts() with { Flex = new FlexResult(fine, 2, null) }).Status);
        var broken = RequirementChecks.Evaluate(item, Facts() with { Flex = new FlexResult([new("D1@S", -10, 9, " mm", "faces 19 → 18")], 0, null) });
        Assert.Equal(RequirementStatus.Fail, broken.Status);
        Assert.Equal("D1@S -10%: faces 19 → 18", broken.Detail);
        Assert.Equal(RequirementStatus.Fail, RequirementChecks.Evaluate(item, Facts() with { Flex = new FlexResult(fine, 0, "the volume changed") }).Status);
    }

    [Fact]
    public async Task AFlexRequirementOnAnUneditablePartIsUnverifiedAndNeverRuns()
    {
        var file = Path.Combine(_root, "Datum", "theirs.sldprt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "part");
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        Ok(await CheckTools.SetRequirements(_connection, _workspace, [new RequirementSpec { Id = "r1", Text = "Robust", Check = "flex" }]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));

        Assert.Contains("UNVERIFIED r1 Robust: the flex test needs an editable part", report, StringComparison.Ordinal);
        Assert.Null(_backend.LastFlex);
    }

    [Fact]
    public async Task AFlexRequirementRunsAtItsOwnPercent()
    {
        await Part();
        Ok(await CheckTools.SetRequirements(_connection, _workspace, [new RequirementSpec { Id = "r1", Text = "Robust", Check = "flex", Target = 5 }]));

        Ok(await CheckTools.CheckRequirements(_connection, _workspace));

        Assert.Equal((null, 5, FlexResult.DefaultLimit), _backend.LastFlex);
    }

    [Fact]
    public async Task MeasureItemsKeepAReferenceThatOutlivesTheirHandle()
    {
        await Part();
        var geometry = _backend.GeometryOf(_part);
        geometry.Faces.Add(new FaceInfo("face#1", SurfaceKind.Plane, 100, null, default, null, null));
        geometry.Measured = new Measurement(null, null, null, null, null, null, null, null, null, 15);
        Ok(await CheckTools.SetRequirements(_connection, _workspace, [new RequirementSpec { Id = "r1", Text = "Offset", Check = "measure", First = "face#1", Second = "Right", Quantity = "plane_offset", Target = 15 }]));

        var active = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active);
        var stored = Assert.Single(_store.Load(active));

        Assert.Equal("ref:face#1", stored.FirstReference);
        Assert.Null(stored.SecondReference);
        Assert.Contains("PASS r1 Offset: measured 15 mm", Ok(await CheckTools.CheckRequirements(_connection, _workspace)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencesSentByACallerAreIgnored()
    {
        await Part();

        Ok(await CheckTools.SetRequirements(_connection, _workspace, [new RequirementSpec { Id = "r1", Text = "x", Mark = "trust me", FirstReference = "ref:face#9" }]));

        var active = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active);
        var stored = Assert.Single(_store.Load(active));
        Assert.Null(stored.Mark);
        Assert.Null(stored.FirstReference);
    }

    [Fact]
    public void FlexSaysWhenSolidWorksMarksThePartAsChanged()
    {
        var report = new FlexResult([], 0, null, MarkedChanged: true).Report();

        Assert.Contains("SOLIDWORKS now marks the part as changed", report, StringComparison.Ordinal);
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
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Length", Source = "drawing p2", Check = "box_x", Target = 60 },
            new RequirementSpec { Id = "r2", Text = "Mass", Check = "mass", Target = 500 },
            new RequirementSpec { Id = "r3", Text = "Looks right" },
        ]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));

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
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Mass", Check = "mass", Target = 500 },
            new RequirementSpec { Id = "r2", Text = "Looks right" },
        ]));

        Assert.StartsWith("INVALID_ARGUMENT: r1 isn't a manual requirement", Error(await CheckTools.CheckRequirements(_connection, _workspace, [new RequirementMark { Id = "r1", Note = "trust me" }])), StringComparison.Ordinal);
        Ok(await CheckTools.CheckRequirements(_connection, _workspace, [new RequirementMark { Id = "r2", Note = "user confirmed" }]));

        Assert.Contains("MARKED r2 Looks right: marked by hand, not measured: user confirmed", Ok(await CheckTools.CheckRequirements(_connection, _workspace)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACheckWithNoListSaysSo()
    {
        await Part();

        Assert.StartsWith("NOT_FOUND: no requirements stored", Error(await CheckTools.CheckRequirements(_connection, _workspace)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleHandleIsReportedThroughTheTool()
    {
        await Part();
        Ok(await CheckTools.SetRequirements(_connection, _workspace, [new RequirementSpec { Id = "r1", Text = "Gap", Check = "measure", First = "face#404", Quantity = "distance", Target = 5 }]));

        Assert.Contains("UNVERIFIED r1 Gap: handles no longer valid", Ok(await CheckTools.CheckRequirements(_connection, _workspace)), StringComparison.Ordinal);
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
        [],
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
