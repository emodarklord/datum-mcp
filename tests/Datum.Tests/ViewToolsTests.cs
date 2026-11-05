using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class ViewToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-view-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public ViewToolsTests()
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
    public async Task ReturnsADescriptionAndAPng()
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, view: "top");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Part1 top, 800×600\n", Text(result));
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, image.DecodedData.ToArray()[..4]);
        Assert.Equal([(StandardView.Top, 800, 600)], _backend.Captures);
    }

    [Fact]
    public async Task ASheetAsksForFourViewsAtHalfSize()
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, view: "sheet");

        Assert.Equal("Part1 sheet 800×600: front | top / right | isometric, all at one scale\n", Text(result));
        Assert.Equal(
            [(StandardView.Front, 399, 299), (StandardView.Top, 399, 299), (StandardView.Right, 399, 299), (StandardView.Isometric, 399, 299)],
            _backend.Captures);
    }

    [Theory]
    [InlineData("upside", 800, "INVALID_ARGUMENT: unknown view 'upside'")]
    [InlineData("iso", 100, "INVALID_ARGUMENT: size 100 is out of range")]
    [InlineData("iso", 4000, "INVALID_ARGUMENT: size 4000 is out of range")]
    public async Task RejectsBadArguments(string view, int size, string expected)
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, view, size);

        Assert.True(result.IsError);
        Assert.StartsWith(expected, StatusToolsTests.TextOf(result), StringComparison.Ordinal);
        Assert.Empty(_backend.Captures);
    }

    [Fact]
    public async Task SavesThePngOnlyWithConsentToReplace()
    {
        await Part();

        var saved = Text(await ViewTools.Capture(_connection, _workspace, path: "look.png"));
        var refused = await ViewTools.Capture(_connection, _workspace, path: "look.png");
        var replaced = Text(await ViewTools.Capture(_connection, _workspace, path: "look.png", overwrite: true));

        var file = Path.Combine(_root, "Datum", "look.png");
        Assert.Equal($"Part1 isometric, 800×600 · saved to {file}\n", saved);
        Assert.StartsWith("FILE_EXISTS: ", StatusToolsTests.TextOf(refused), StringComparison.Ordinal);
        Assert.Contains("previous file backed up to", replaced, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, File.ReadAllBytes(file)[..4]);
    }

    [Theory]
    [InlineData("look.jpg", "INVALID_ARGUMENT: ")]
    [InlineData(@"C:\Windows\look.png", "PATH_NOT_ALLOWED: ")]
    public async Task RefusesOtherFormatsAndFolders(string path, string expected)
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, path: path);

        Assert.StartsWith(expected, StatusToolsTests.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaysAnOpenSketchIsNotDrawn()
    {
        await Part();
        Assert.NotEqual(true, (await SketchTools.Begin(_connection, _workspace, "Front")).IsError);

        var result = await ViewTools.Capture(_connection, _workspace);

        Assert.EndsWith(" · the open sketch isn't drawn; it shows after sketch_end\n", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyCapturesParts()
    {
        Assert.NotEqual(true, (await DocumentTools.New(_connection, _workspace, "drawing")).IsError);

        var result = await ViewTools.Capture(_connection, _workspace);

        Assert.StartsWith("INVALID_ARGUMENT: ", StatusToolsTests.TextOf(result), StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    [Fact]
    public async Task PassesModeAndSectionToTheBackendAndSaysSo()
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, mode: "hidden", section: "right", sectionOffset: 5);

        Assert.Equal("Part1 isometric, 800×600 · hidden · cut along right at 5 mm\n", Text(result));
        Assert.Equal(new CaptureOptions(CaptureMode.Hidden, new SectionCut(StandardPlane.Right, 5)), _backend.LastCapture);
    }

    [Fact]
    public async Task SaysWhichHandlesTheNumbersBelongTo()
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, labels: "faces");

        Assert.Equal("Part1 isometric, 800×600 · numbers are face#7\n", Text(result));
    }

    [Theory]
    [InlineData("iso", "faces", "front", "shaded", "INVALID_ARGUMENT: labels work on a single uncut view")]
    [InlineData("sheet", "faces", null, "shaded", "INVALID_ARGUMENT: labels work on a single uncut view")]
    [InlineData("iso", "holes", null, "shaded", "INVALID_ARGUMENT: unknown labels 'holes'")]
    [InlineData("iso", null, "diagonal", "shaded", "INVALID_ARGUMENT: unknown section plane 'diagonal'")]
    [InlineData("iso", null, null, "xray", "INVALID_ARGUMENT: unknown mode 'xray'")]
    public async Task RefusesBadCaptureOptions(string view, string? labels, string? section, string mode, string expected)
    {
        await Part();

        var result = await ViewTools.Capture(_connection, _workspace, view, labels: labels, section: section, mode: mode);

        Assert.True(result.IsError);
        Assert.StartsWith(expected, Text(result), StringComparison.Ordinal);
    }

    private async Task Part() => Assert.NotEqual(true, (await DocumentTools.New(_connection, _workspace, "part")).IsError);
}
