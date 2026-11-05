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

        Assert.Matches(@"Problems \(\d+\):\n- feat#\d+ Cut-Extrude1", reply);
        Assert.Contains("CutNotIntersectModel", reply, StringComparison.Ordinal);
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

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
