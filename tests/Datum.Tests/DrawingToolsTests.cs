using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class DrawingToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-drawing-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public DrawingToolsTests()
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
    public async Task ANewDrawingReportsItsSheetAndIsEditable()
    {
        var reply = Ok(await DrawingTools.New(_connection, _workspace, sheet: "a", scale: "1:2"));

        Assert.EndsWith(": 279.4 x 215.9 mm, 1:2, third angle, with title block\n", reply, StringComparison.Ordinal);
        Assert.Matches(@"^view#\d+ ", Ok(await DrawingTools.AddView(_connection, _workspace, "plate.sldprt", "front")));
    }

    [Fact]
    public async Task AnnotationsCanBeListedMovedAndDeleted()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(id).Annotations.Add(new SheetAnnotation("NOTE", 10, 10, false, null, "DetailItem1", null));

        Assert.Contains("DetailItem1 sheet 'NOTE' at 10,10", Ok(await DrawingTools.ListAnnotations(_connection)), StringComparison.Ordinal);
        Assert.Contains("at 50,60", Ok(await DrawingTools.MoveAnnotation(_connection, _workspace, "DetailItem1", 50, 60)), StringComparison.Ordinal);
        Ok(await DrawingTools.DeleteAnnotation(_connection, _workspace, "DetailItem1"));

        Assert.Equal("No annotations\n", Ok(await DrawingTools.ListAnnotations(_connection)));
        Assert.StartsWith("NOT_FOUND: ", Error(await DrawingTools.MoveAnnotation(_connection, _workspace, "DetailItem1", 1, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AViewCanBeDeletedAndTheSheetRescaled()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var view = Ok(await DrawingTools.AddView(_connection, _workspace, "plate.sldprt", "front")).Split(' ')[0];

        Assert.StartsWith("Sheet scale 1:5\n", Ok(await DrawingTools.SetSheet(_connection, _workspace, "1:5")), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await DrawingTools.SetSheet(_connection, _workspace, "half")), StringComparison.Ordinal);
        Assert.StartsWith("Deleted ", Ok(await DrawingTools.DeleteView(_connection, _workspace, view)), StringComparison.Ordinal);
        Assert.Equal("No views\n", Ok(await DrawingTools.ListViews(_connection)));
    }

    [Fact]
    public async Task ADrawingWithAnUnsavedModelBehindItWontCloseQuietly()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(id).UnsavedReferences.Add("plate.SLDPRT");

        Assert.StartsWith("UNSAVED_CHANGES: plate.SLDPRT would close with", Error(await DocumentTools.Close(_connection)), StringComparison.Ordinal);
        Ok(await DocumentTools.Close(_connection, discardChanges: true));
    }

    [Fact]
    public async Task ADrawingAnswersToItsNameWithoutTheSheet()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var document = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active);

        Ok(await DrawingTools.ListViews(_connection, $"{document.Title.Split(" - ")[0]} - Sheet9"));
    }

    [Fact]
    public async Task ASheetWithNoFormatSaysSo()
    {
        Assert.Contains("no title block found", Ok(await DrawingTools.New(_connection, _workspace, sheet: "a0")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivingAModelAddsTheStandardViews()
    {
        var reply = Ok(await DrawingTools.New(_connection, _workspace, model: "plate.sldprt"));

        Assert.Equal(5, reply.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData("z", "1:1", "third", "INVALID_ARGUMENT: unknown sheet size 'z'")]
    [InlineData("a", "half", "third", "INVALID_ARGUMENT: unknown scale 'half'")]
    [InlineData("a", "0:2", "third", "INVALID_ARGUMENT: unknown scale '0:2'")]
    [InlineData("a", "1:1", "second", "INVALID_ARGUMENT: unknown projection 'second'")]
    public async Task BadSheetsAreRefused(string sheet, string scale, string projection, string expected)
    {
        var reply = Error(await DrawingTools.New(_connection, _workspace, sheet: sheet, scale: scale, projection: projection));

        Assert.StartsWith(expected, reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewsCanBeListedAndChanged()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var view = Ok(await DrawingTools.AddView(_connection, _workspace, "plate.sldprt", "front", 80, 90)).Split(' ')[0];

        var changed = Ok(await DrawingTools.SetView(_connection, _workspace, view, x: 120, scale: "1:2", display: "hidden"));

        Assert.Contains("[named] 1:2 at 120,90 mm", changed, StringComparison.Ordinal);
        Assert.Contains(", hidden, ", changed, StringComparison.Ordinal);
        Assert.Equal(changed, Ok(await DrawingTools.ListViews(_connection)));
    }

    [Theory]
    [InlineData("half", null, "INVALID_ARGUMENT: unknown scale")]
    [InlineData(null, "xray", "INVALID_ARGUMENT: unknown display")]
    public async Task BadViewChangesAreRefused(string? scale, string? display, string expected)
    {
        Ok(await DrawingTools.New(_connection, _workspace));

        Assert.StartsWith(expected, Error(await DrawingTools.SetView(_connection, _workspace, "view#1", scale: scale, display: display)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SectionDetailAndAuxiliaryViewsReportTheirKind()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var front = Ok(await DrawingTools.AddView(_connection, _workspace, "plate.sldprt", "front")).Split(' ')[0];

        Assert.Contains("[section]", Ok(await DrawingTools.Section(_connection, _workspace, front, "vertical", 5, 200, 50)), StringComparison.Ordinal);
        Assert.Contains("[detail] 2:1", Ok(await DrawingTools.Detail(_connection, _workspace, front, 0, 0, 10, "2:1", 200, 150)), StringComparison.Ordinal);
        Assert.Contains("[auxiliary]", Ok(await DrawingTools.Auxiliary(_connection, _workspace, front, "edge#500", 100, 180)), StringComparison.Ordinal);
        Assert.Equal("edge#500 line 10,10 to 60,10\n", Ok(await DrawingTools.Entities(_connection, front)));
    }

    [Fact]
    public async Task BadSectionsAndDetailsAreRefused()
    {
        Ok(await DrawingTools.New(_connection, _workspace));

        Assert.StartsWith("INVALID_ARGUMENT: unknown direction", Error(await DrawingTools.Section(_connection, _workspace, "view#1", "diagonal", 0, 10, 10)), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: a detail circle", Error(await DrawingTools.Detail(_connection, _workspace, "view#1", 0, 0, 0, "2:1", 10, 10)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnnotationToolsReportWhatTheyDid()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var front = Ok(await DrawingTools.AddView(_connection, _workspace, "plate.sldprt", "front")).Split(' ')[0];

        Assert.EndsWith(": 2 dimensions\n", Ok(await DrawingTools.ImportDimensions(_connection, _workspace)), StringComparison.Ordinal);
        Assert.Equal($"{front} RD1@Drawing View1 25 mm\n", Ok(await DrawingTools.AddDimension(_connection, _workspace, front, "edge#1", 50, 50)));
        Assert.Equal($"{front} RD1@Drawing View1 25 mm\n", Ok(await DrawingTools.ListDimensions(_connection)));
        Assert.Equal("D1@Sketch1 bilateral +0.1 -0.2 mm. Stored in plate.SLDPRT, which now needs saving\n", Ok(await DrawingTools.Tolerance(_connection, _workspace, "D1@Sketch1", "bilateral", 0.1, -0.2)));
        Assert.Equal($"{front}: 1 centre marks, 2 centre lines, nothing missing\n", Ok(await DrawingTools.Marks(_connection, _workspace, front)));
        Assert.Equal("Note: BREAK EDGES\n", Ok(await DrawingTools.Note(_connection, _workspace, "BREAK EDGES", 20, 20)));
    }

    [Fact]
    public async Task BadAnnotationsAreRefused()
    {
        Ok(await DrawingTools.New(_connection, _workspace));

        Assert.StartsWith("INVALID_ARGUMENT: unknown tolerance", Error(await DrawingTools.Tolerance(_connection, _workspace, "D1@Sketch1", "tight")), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: a note", Error(await DrawingTools.Note(_connection, _workspace, " ", 1, 1)), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: unknown units", Error(await DrawingTools.New(_connection, _workspace, units: "cubits")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrawingWontSaveWhileAToleranceWaitsInItsModel()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        Ok(await DrawingTools.Tolerance(_connection, _workspace, "D1@Sketch1", "symmetric", 0.1));

        Assert.StartsWith("INVALID_ARGUMENT: plate.SLDPRT has unsaved changes", Error(await DocumentTools.Save(_connection, _workspace, path: "a.slddrw")), StringComparison.Ordinal);

        _workspace.MarkSaved(@"C:\Parts\plate.SLDPRT");
        Assert.StartsWith("Saved ", Ok(await DocumentTools.Save(_connection, _workspace, path: "a.slddrw")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TitleFieldsSayWhereTheyWentAndWhetherTheyShow()
    {
        Ok(await DrawingTools.New(_connection, _workspace));

        var reply = Ok(await DrawingTools.Title(_connection, _workspace, [new TitleField { Name = "DrawnBy", Value = "JM" }, new TitleField { Name = "Description", Value = "Clamp" }]));

        Assert.Equal("DrawnBy = JM, showing\nDescription = Clamp, showing; stored in plate.SLDPRT, which now needs saving\n", reply);
        Assert.StartsWith("INVALID_ARGUMENT: plate.SLDPRT has unsaved", Error(await DocumentTools.Save(_connection, _workspace, path: "t.slddrw")), StringComparison.Ordinal);
        Assert.Equal("NOT_FOUND: the title block has no field 'Nope'. Hint: Fields: DrawnBy, Description", Error(await DrawingTools.Title(_connection, _workspace, [new TitleField { Name = "Nope", Value = "x" }])));
    }

    [Fact]
    public async Task BomAndBalloonsReportWhatIsOnTheSheet()
    {
        Ok(await DrawingTools.New(_connection, _workspace));
        var view = Ok(await DrawingTools.AddView(_connection, _workspace, "clamp.sldasm", "iso")).Split(' ')[0];

        Assert.StartsWith("NOT_FOUND: ", Error(await DrawingTools.ReadBom(_connection)), StringComparison.Ordinal);
        Assert.Equal("1: 1 x plate\n2: 2 x pin Pin\n", Ok(await DrawingTools.AddBom(_connection, _workspace, view, 20, 200)));
        Assert.Equal("Balloon 1 points at plate-1\nBalloon 2 points at pin-1\n", Ok(await DrawingTools.Balloons(_connection, _workspace, view)));
        Assert.Equal("1: 1 x plate\n2: 2 x pin Pin\n", Ok(await DrawingTools.ReadBom(_connection)));
    }

    [Fact]
    public async Task DrawingToolsNeedADrawing()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await DrawingTools.ListViews(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrawingCapturesAsItsSheet()
    {
        Ok(await DrawingTools.New(_connection, _workspace));

        var picture = await ViewTools.Capture(_connection, _workspace);

        Assert.NotEqual(true, picture.IsError);
        Assert.Contains(" sheet, 800×600", Assert.IsType<TextContentBlock>(picture.Content[0]).Text, StringComparison.Ordinal);
        Assert.Equal(true, (await ViewTools.Capture(_connection, _workspace, mode: "hidden")).IsError);
    }

    [Theory]
    [InlineData(0, "wireframe")]
    [InlineData(1, "hidden")]
    [InlineData(2, "removed")]
    [InlineData(7, "shaded")]
    public void DisplayModesRoundTrip(int mode, string name)
    {
        Assert.Equal(name, ViewDisplays.Name(mode));
        Assert.Equal(mode, ViewDisplays.Parse(name));
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
