using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// Values are checked against a 50 x 30 x 10 block on the Front plane, extruded along +Z.
public sealed partial class CheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-check-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public CheckTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MeasuresDistanceAngleAndRadiusOnAKnownBlock() => await InNewPart(async () =>
    {
        await Block(new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true });
        var front = await Face("+z");
        var back = await Face("-z");
        var side = await Face("+x");
        var hole = FirstHandle(Ok(await InspectionTools.Faces(_connection, surface: "cylinder")));
        Ok(await InspectionTools.Select(_connection, [side]));

        var between = Ok(await CheckTools.Measure(_connection, front, back));
        var square = Ok(await CheckTools.Measure(_connection, front, side));
        var round = Ok(await CheckTools.Measure(_connection, hole));
        var area = Ok(await CheckTools.Measure(_connection, side));
        var selection = Ok(await InspectionTools.Selection(_connection));
        Console.WriteLine($"{between}{square}{round}{area}{selection}");

        Assert.Contains("normal distance 10 mm", between, StringComparison.Ordinal);
        Assert.Contains("clearance 10 mm", between, StringComparison.Ordinal);
        Assert.Contains("angle 90°", square, StringComparison.Ordinal);
        Assert.Contains("radius 5 mm", round, StringComparison.Ordinal);
        Assert.Contains("area 300 mm²", area, StringComparison.Ordinal);
        Assert.Contains(side, selection, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ReadsAFinishedSketchsEntitiesAndRelations() => await InNewPart(async () =>
    {
        var sketch = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true });

        var reply = Ok(await CheckTools.ReadSketch(_connection, sketch));
        Console.WriteLine(reply);

        Assert.Contains("fully defined", reply, StringComparison.Ordinal);
        Assert.Contains(" line ", reply, StringComparison.Ordinal);
        Assert.Contains("horizontal: seg#", reply, StringComparison.Ordinal);
        Assert.Contains(" 50 mm", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task SummaryFindsAnUnderDefinedSketchAndAMissingMaterial() => await InNewPart(async () =>
    {
        var sketch = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = false });
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));

        var reply = Ok(await CheckTools.Summary(_connection));
        Console.WriteLine(reply);

        Assert.DoesNotContain("Front Plane", reply, StringComparison.Ordinal);
        Assert.Matches(@"document units (mm|inches)", reply);
        Assert.Contains("box 50 x 30 x 10 mm", reply, StringComparison.Ordinal);
        Assert.Contains("is under defined", reply, StringComparison.Ordinal);
        Assert.Contains("No material set", reply, StringComparison.Ordinal);
        Assert.Contains("D1=10 mm", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task SummaryExplainsACutThatNoLongerTouchesThePart() => await InNewPart(async () =>
    {
        await Block();
        var circle = await Sketch(new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 0, Radius = 2, Dimension = true });
        var cut = await FeatureTools.Extrude(_connection, _workspace, circle, cut: true, end: "through_all");
        if (cut.IsError == true)
        {
            Ok(await FeatureTools.Extrude(_connection, _workspace, circle, cut: true, end: "through_all", reverse: true));
        }

        _backend.ForceDimension((await Active()).Id, "D1@Sketch1", 20);
        var reply = Ok(await CheckTools.Summary(_connection));
        Console.WriteLine(reply);

        Assert.Matches(@"Problems \(\d+\):\n- feat#\d+ Cut-Extrude\d+", reply);
        Assert.Contains("CutNotIntersectModel", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task MakesAnOffsetPlaneThatASketchCanUse() => await InNewPart(async () =>
    {
        await Block();

        var reply = Ok(await FeatureTools.Plane(_connection, _workspace, "Front", offset: 25));
        var plane = FeatHandle().Match(reply).Value;
        Console.WriteLine(reply);

        Assert.Contains("normal distance 15 mm", Ok(await CheckTools.Measure(_connection, plane, await Face("+z"))), StringComparison.Ordinal);
        Ok(await SketchTools.Begin(_connection, _workspace, plane));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true }]));
        Ok(await SketchTools.End(_connection, _workspace));
    });

    [Fact]
    public async Task MakesAnAngledPlaneAndAnAxisAndUndoesThem() => await InNewPart(async () =>
    {
        await Block(new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true });
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var edge = FirstHandle(Ok(await InspectionTools.Faces(_connection, kind: "edge", direction: "x")));
        var hole = FirstHandle(Ok(await InspectionTools.Faces(_connection, surface: "cylinder")));

        var angled = Ok(await FeatureTools.Plane(_connection, _workspace, "Top", angle: 30, through: edge));
        var axis = Ok(await FeatureTools.Axis(_connection, _workspace, hole));
        var tree = Ok(await InspectionTools.Tree(_connection));
        var measured = Ok(await CheckTools.Measure(_connection, FeatHandle().Match(angled).Value, await Face("+y")));
        Console.WriteLine($"{angled}{axis}{tree}{measured}");

        Assert.Contains("[RefAxis]", tree, StringComparison.Ordinal);
        Assert.Matches("angle (30|150)°", measured);
        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.DoesNotContain("[RefAxis]", Ok(await InspectionTools.Tree(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task SetsAMaterialAndMassFollowsItsDensity() => await InNewPart(async () =>
    {
        await Block();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        var reply = Ok(await EditTools.Material(_connection, _workspace, "aisi 304"));
        var info = Ok(await InspectionTools.Info(_connection));
        Console.WriteLine($"{reply}{info}");

        Assert.Contains("→ AISI 304", reply, StringComparison.Ordinal);
        Assert.Contains("Mass 120 g", info, StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND: ", Text(await EditTools.Material(_connection, _workspace, "unobtainium")), StringComparison.Ordinal);
        Assert.Contains("Closest names: ", Text(await EditTools.Material(_connection, _workspace, "stainless")), StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Contains("Material none", Ok(await CheckTools.Summary(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task SetsReadsAndRestoresACustomProperty() => await InNewPart(async () =>
    {
        await Block();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        Assert.Contains("(not set) → Bracket", Ok(await EditTools.Property(_connection, _workspace, "Description", "Bracket")), StringComparison.Ordinal);
        Assert.Contains("Bracket → Base bracket", Ok(await EditTools.Property(_connection, _workspace, "Description", "Base bracket")), StringComparison.Ordinal);
        Assert.Contains("Description = Base bracket", Ok(await EditTools.Properties(_connection)), StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.DoesNotContain("Description", Ok(await EditTools.Properties(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task FlexFindsADimensionThatBreaksTheRebuildAndRestoresEverything() => await InNewPart(async () =>
    {
        // Shrinking the block's width past the hole at x = 20 leaves the cut touching nothing.
        await Block();
        var circle = await Sketch(new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 0, Radius = 2, Dimension = true });
        var cut = await FeatureTools.Extrude(_connection, _workspace, circle, cut: true, end: "through_all");
        if (cut.IsError == true)
        {
            Ok(await FeatureTools.Extrude(_connection, _workspace, circle, cut: true, end: "through_all", reverse: true));
        }

        var before = Ok(await CheckTools.Summary(_connection));

        var report = Ok(await CheckTools.Flex(_connection, _workspace, ["D1@Sketch1"], percent: 60));
        var after = Ok(await CheckTools.Summary(_connection));
        Console.WriteLine($"{report}{after}");

        Assert.Contains("-60%", report, StringComparison.Ordinal);
        Assert.Contains("rebuild error in Cut-Extrude", report, StringComparison.Ordinal);
        Assert.Contains("All values restored", report, StringComparison.Ordinal);
        Assert.Equal(before, after);
    });

    [Fact]
    public async Task FlexPassesASimpleBlockAndCoversEveryDimension() => await InNewPart(async () =>
    {
        await Block();

        var report = Ok(await CheckTools.Flex(_connection, _workspace));
        Console.WriteLine(report);

        Assert.Contains("Flexed 3 dimensions by ±10%: 3 fine, 0 problems", report, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AFinishedSketchCanBeReopenedAndFullyDefined() => await InNewPart(async () =>
    {
        var sketch = await Sketch(new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = false });
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var read = Ok(await CheckTools.ReadSketch(_connection, sketch));
        Assert.Contains("under defined", read, StringComparison.Ordinal);
        var lines = SegHandle().Matches(read).Select(m => m.Value).Distinct().ToArray();

        Ok(await SketchTools.Edit(_connection, _workspace, sketch));
        Ok(await SketchTools.Constrain(_connection, _workspace, [.. lines.Select(line => new SketchConstraintSpec { Kind = "fixed", Entities = [line] })]));
        var ended = Ok(await SketchTools.End(_connection, _workspace));
        Console.WriteLine(ended);

        Assert.Contains("fully defined", ended, StringComparison.Ordinal);
        Assert.Contains("rebuild ok", ended, StringComparison.Ordinal);
        Assert.Contains("box 50 x 30 x 10 mm", Ok(await CheckTools.Summary(_connection)), StringComparison.Ordinal);
        Assert.StartsWith("CANNOT_RESTORE: ", Text(await EditTools.Restore(_connection, _workspace, checkpoint)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task RequirementsMeasureARealPart() => await InNewPart(async () =>
    {
        var store = new RequirementStore(Path.Combine(_root, "requirements"));
        await Block();
        var (front, back) = (await Face("+z"), await Face("-z"));
        Ok(await EditTools.Material(_connection, _workspace, "AISI 304"));
        Ok(await CheckTools.SetRequirements(_connection, store,
        [
            new RequirementSpec { Id = "r1", Text = "Width", Source = "sheet 1", Check = "box_x", Target = 50 },
            new RequirementSpec { Id = "r2", Text = "Thickness", Check = "measure", First = front, Second = back, Quantity = "normal_distance", Target = 10 },
            new RequirementSpec { Id = "r3", Text = "Mass", Check = "mass", Target = 120 },
            new RequirementSpec { Id = "r4", Text = "Material", Check = "material", Expected = "AISI 304" },
            new RequirementSpec { Id = "r5", Text = "Rebuilds", Check = "no_rebuild_errors" },
            new RequirementSpec { Id = "r6", Text = "Height", Check = "box_y", Target = 31 },
            new RequirementSpec { Id = "r7", Text = "Finish is acceptable" },
        ]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, store));
        Console.WriteLine(report);

        Assert.StartsWith("Part", report, StringComparison.Ordinal);
        Assert.Contains(": 5 pass, 1 fail, 1 unverified, 0 marked by hand\n", report, StringComparison.Ordinal);
        Assert.Contains("FAIL r6 Height: measured 30 mm", report, StringComparison.Ordinal);
    });

    private async Task<DocumentInfo> Active() =>
        (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active);

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

    private async Task Block(params SketchEntitySpec[] extra)
    {
        var sketch = await Sketch([new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true }, .. extra]);
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));
    }

    private async Task<string> Sketch(params SketchEntitySpec[] entities)
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, entities));
        return FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
    }

    private async Task<string> Face(string normal) => FirstHandle(Ok(await InspectionTools.Faces(_connection, normal: normal)));

    private static string FirstHandle(string reply) => reply.Split(' ')[0];

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    [GeneratedRegex(@"seg#\d+")]
    private static partial Regex SegHandle();
}
