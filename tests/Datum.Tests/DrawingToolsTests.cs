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
