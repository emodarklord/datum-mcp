using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed partial class SheetMetalLiveTests : IDisposable
{
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-sheet-{Guid.NewGuid():N}");

    public SheetMetalLiveTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(120));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ABentBracketFlattensToTheSizeOfItsDxf()
    {
        var file = Path.Combine(_root, "Datum", "bracket.sldprt");
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "polyline", Points = [[0, 60], [0, 0], [80, 0]] }]));
            var made = Ok(await SheetMetalTools.Base(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), thickness: 2, radius: 3, depth: 50));
            Ok(await DocumentTools.Save(_connection, _workspace, path: file));
            var flat = Ok(await SheetMetalTools.Flatten(_connection, _workspace));
            var formed = Ok(await SheetMetalTools.Flatten(_connection, _workspace, flat: false));
            var exported = Ok(await SheetMetalTools.ExportFlat(_connection, _workspace, Path.Combine(_root, "Datum", "bracket.dxf")));
            Console.WriteLine($"{made}{flat}{formed}{exported}");

            Assert.Contains("box 140.3 x 2 x 50 mm", flat, StringComparison.Ordinal);
            Assert.Contains("outline 140.3 x 50 mm", exported, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task AFlatBlankTakesASketchedBendAndAHem()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 100, Height = 60 }]));
            Ok(await SheetMetalTools.Base(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), thickness: 2, radius: 2));
            var face = Ok(await InspectionTools.Faces(_connection, normal: "+y", limit: 1)).Split(' ')[0];
            Ok(await SketchTools.Begin(_connection, _workspace, face));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "line", X1 = 20, Y1 = -30, X2 = 20, Y2 = 30 }]));
            var bend = Ok(await SheetMetalTools.Bend(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), 90, 2));
            var info = Ok(await InspectionTools.Info(_connection));
            Console.WriteLine($"{bend}{info}");

            Assert.Contains("rebuild ok", bend, StringComparison.Ordinal);
            Assert.DoesNotContain(" x 2 mm", info, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task AnEdgeTakesAHem()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 100, Height = 60 }]));
            Ok(await SheetMetalTools.Base(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), thickness: 2, radius: 2));
            var edge = Ok(await InspectionTools.Faces(_connection, kind: "edge", direction: "x", limit: 1)).Split(' ')[0];
            var hem = Ok(await SheetMetalTools.Hem(_connection, _workspace, [edge], 10));
            Console.WriteLine(hem);

            Assert.Contains("rebuild ok", hem, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string Feat(string reply) => FeatHandle().Match(reply).Value;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
