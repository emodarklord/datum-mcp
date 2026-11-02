using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class InspectionToolsTests : IDisposable
{
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly string _part;

    public InspectionToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _part = _connection.UseAsync(c => c.RunAsync(b => b.NewDocument(DocumentType.Part)), CancellationToken.None).GetAwaiter().GetResult().Id;
        var geometry = _backend.GeometryOf(_part);
        geometry.Features.AddRange(
        [
            new FeatureInfo("feat#1", "Front Plane", "RefPlane", 0, false, null),
            new FeatureInfo("feat#2", "Boss-Extrude1", "Extrusion", 0, false, null),
            new FeatureInfo("feat#3", "Sketch1", "ProfileFeature", 1, false, null),
            new FeatureInfo("feat#4", "Fillet1", "Fillet", 0, true, "rebuild error 3"),
        ]);
        geometry.Faces.AddRange(
        [
            new FaceInfo("face#10", SurfaceKind.Plane, 5000, new(0, 0, 1), new(0, 0, 20), null, "Boss-Extrude1"),
            new FaceInfo("face#11", SurfaceKind.Cylinder, 1256.6, null, new(10, 0, 10), 5, "Cut-Extrude1"),
        ]);
        geometry.Edges.Add(new EdgeInfo("edge#20", CurveKind.Circle, 31.42, 5, ["face#10", "face#11"]));
        geometry.Model = new ModelInfo(
            "Part1", DocumentType.Part, "AISI 304", 784.5, 98000, 15400, new(0, 0, 10), new Box(new(-50, -25, 0), new(50, 25, 20)), 1);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task TreeIndentsSubFeaturesAndFlagsProblems()
    {
        Assert.Equal(
            """
            Part1: 4 features
            feat#1 Front Plane [RefPlane]
            feat#2 Boss-Extrude1 [Extrusion]
              feat#3 Sketch1 [ProfileFeature]
            feat#4 Fillet1 [Fillet] suppressed ERROR: rebuild error 3

            """.ReplaceLineEndings("\n"),
            Ok(await InspectionTools.Tree(_connection)));
    }

    [Fact]
    public async Task InfoReportsMassSizeAndMaterialInDatumUnits()
    {
        Assert.Equal(
            """
            Part1 (part), 1 body
            Mass 784.5 g, volume 98000 mm³, surface 15400 mm²
            Centre of mass 0,0,10 mm
            Bounding box 100 x 50 x 20 mm, from -50,-25,0 to 50,25,20
            Material AISI 304

            """.ReplaceLineEndings("\n"),
            Ok(await InspectionTools.Info(_connection)));
    }

    [Fact]
    public async Task InfoRefusesDrawings()
    {
        await _connection.UseAsync(c => c.RunAsync(b => b.NewDocument(DocumentType.Drawing)), CancellationToken.None);

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await InspectionTools.Info(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsFacesAndEdgesWithHandles()
    {
        Assert.Equal(
            "face#10 plane 5000 mm² normal +z centre 0,0,20 (Boss-Extrude1)\n"
                + "face#11 cylinder 1257 mm² radius 5 mm centre 10,0,10 (Cut-Extrude1)\n",
            Ok(await InspectionTools.Faces(_connection)));
        Assert.Equal(
            "edge#20 circle 31.42 mm radius 5 mm faces face#10,face#11\n",
            Ok(await InspectionTools.Faces(_connection, kind: "edge")));
    }

    [Fact]
    public async Task PagesWithAFooter()
    {
        Assert.Equal(
            "face#10 plane 5000 mm² normal +z centre 0,0,20 (Boss-Extrude1)\nshowing 1-1 of 2; pass offset 1 for more\n",
            Ok(await InspectionTools.Faces(_connection, limit: 1)));
        Assert.Equal("No more: 2 match in total\n", Ok(await InspectionTools.Faces(_connection, offset: 5)));
        Assert.Equal("Nothing matches\n", Ok(await InspectionTools.Faces(_connection, surface: "torus")));
    }

    [Fact]
    public async Task SelectsByHandleAndReadsTheSelectionBack()
    {
        Assert.Equal("Nothing is selected in Part1\n", Ok(await InspectionTools.Selection(_connection)));

        Ok(await InspectionTools.Select(_connection, ["face#11", "edge#20"]));

        Assert.Equal("face#11\nedge#20\n", Ok(await InspectionTools.Selection(_connection)));
    }

    [Fact]
    public async Task UnknownHandlesAreNotFound()
    {
        Assert.StartsWith("NOT_FOUND: no 'face#99'", Error(await InspectionTools.Select(_connection, ["face#99"])), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FacesRefusesNonParts()
    {
        await _connection.UseAsync(c => c.RunAsync(b => b.NewDocument(DocumentType.Assembly)), CancellationToken.None);

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await InspectionTools.Faces(_connection)), StringComparison.Ordinal);
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
