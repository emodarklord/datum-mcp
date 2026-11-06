using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed partial class ShapeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-shape-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public ShapeTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A3dPathSweepsIntoATubeOfTheRightVolume() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, SketchSpecs.ThreeD));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, Z1 = 0, X2 = 200, Y2 = 100, Z2 = 200 }]));
        var ended = Ok(await SketchTools.End(_connection, _workspace));
        var reply = Ok(await FeatureTools.Sweep(_connection, _workspace, Feat(ended), diameter: 25.4, wall: 2));
        Console.WriteLine($"{ended}{reply}");

        // A 300 mm long tube: pi / 4 * (25.4^2 - 21.4^2) * 300 = 44,108 mm3.
        Assert.Contains("→ 44108 ", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AProfileSweepsAlongAFlatPath() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, X2 = 0, Y2 = 100 }]));
        var path = Feat(Ok(await SketchTools.End(_connection, _workspace)));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 20, Height = 10 }]));
        var profile = Feat(Ok(await SketchTools.End(_connection, _workspace)));

        var reply = Ok(await FeatureTools.Sweep(_connection, _workspace, path, profile: profile));
        Console.WriteLine(reply);

        Assert.Matches("→ 20000 ", reply);
    });

    [Fact]
    public async Task ALoftBlendsBetweenAFootprintAndASmallerSpline() => await InNewPart(async () =>
    {
        var plane = Feat(Ok(await FeatureTools.Plane(_connection, _workspace, "Front", offset: 100)));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 100, Height = 60 }]));
        var first = Feat(Ok(await SketchTools.End(_connection, _workspace)));
        Ok(await SketchTools.Begin(_connection, _workspace, plane));
        Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "spline", Points = [[-30, 0], [0, 15], [30, 0]] },
            new SketchEntitySpec { Type = "line", X1 = -30, Y1 = 0, X2 = 30, Y2 = 0 },
        ]));
        var ended = Ok(await SketchTools.End(_connection, _workspace));
        var reply = Ok(await FeatureTools.Loft(_connection, _workspace, [first, Feat(ended)]));
        Console.WriteLine($"{ended}{reply}");

        Assert.Contains("1 body", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AFlatShapeIsRefusedInA3dSketch() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, SketchSpecs.ThreeD));
        try
        {
            var reply = Text(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5 }]));

            Assert.StartsWith("INVALID_ARGUMENT: a circle can't go in a 3D sketch", reply, StringComparison.Ordinal);
        }
        finally
        {
            Ok(await SketchTools.End(_connection, _workspace));
        }
    });

    [Fact]
    public async Task APartAndAFaceTakeColours() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 40, Height = 40 }]));
        var sketch = Feat(Ok(await SketchTools.End(_connection, _workspace)));
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));
        var face = Ok(await InspectionTools.Faces(_connection, normal: "+z", limit: 1)).Split(' ')[0];

        Console.WriteLine(Ok(await EditTools.Appearance(_connection, _workspace, "red")));
        Console.WriteLine(Ok(await EditTools.Appearance(_connection, _workspace, "#F7C600", face)));
    });

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

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    private static string Feat(string reply) => FeatHandle().Match(reply).Value;

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError != true, text);
        return text;
    }
}
