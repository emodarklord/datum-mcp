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
}
