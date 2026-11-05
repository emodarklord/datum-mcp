using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class ViewTests : IDisposable
{
    private const string Sample = @"C:\Users\Public\Documents\SOLIDWORKS\SOLIDWORKS 2026\samples\introsw\box.sldprt";

    // Kept after the run so the captures can be looked at by hand.
    private static readonly string CaptureFolder = Path.Combine(Path.GetTempPath(), "datum-live-captures");

    private readonly string _root = Directory.CreateTempSubdirectory("datum-view-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public ViewTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum"), CaptureFolder], Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CapturesAnIsometricViewOfABlockWithAHole() => await InNewPart(async () =>
    {
        await Block(60, 40);
        Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "20,10,10", 8));

        var raster = Image(await Capture("iso", "block-iso.png"));

        Assert.Equal((800, 600), (raster.Width, raster.Height));
        Assert.Equal((255, 255, 255), raster.Pixel(0, 0));
        Assert.NotEqual(raster.Pixel(0, 0), raster.Pixel(400, 300));
    });

    [Fact]
    public async Task CapturesASheetOfFourDifferentViews() => await InNewPart(async () =>
    {
        await Block(120, 20);

        var sheet = Image(await Capture("sheet", "bar-sheet.png"));
        var quadrants = new[] { (0, 0), (401, 0), (0, 301), (401, 301) }.Select(q => Quadrant(sheet, q.Item1, q.Item2)).ToList();

        Assert.Equal((800, 600), (sheet.Width, sheet.Height));
        Assert.Equal(4, quadrants.Distinct().Count());
    });

    [Fact]
    public async Task PutsTheUsersViewBackAndLeavesTheFileClean()
    {
        Ok(await DocumentTools.Open(_connection, _workspace, Sample, readOnly: true));
        try
        {
            var id = (await Documents()).Single(d => d.Active).Id;
            var before = await _connection.UseAsync(c => c.RunAsync(_ => _backend.ViewState(id)), CancellationToken.None);

            await Capture("sheet", "sample-sheet.png");
            var after = await _connection.UseAsync(c => c.RunAsync(_ => _backend.ViewState(id)), CancellationToken.None);

            Assert.Equal(before, after);
            Assert.False((await Documents()).Single(d => d.Id == id).Modified);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task CapturesWhileASketchIsOpenAndAfter() => await InNewPart(async () =>
    {
        await Block(60, 40);
        Ok(await SketchTools.Begin(_connection, _workspace, await TopFace()));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5 }]));

        var open = Assert.IsType<TextContentBlock>((await Capture("top", "sketch-open.png")).Content[0]).Text;
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 15, Cy = 0, Radius = 3 }]));
        var ended = Ok(await SketchTools.End(_connection, _workspace));
        var closed = Assert.IsType<TextContentBlock>((await Capture("top", "sketch-closed.png")).Content[0]).Text;

        Assert.Contains("the open sketch isn't drawn", open, StringComparison.Ordinal);
        Assert.Contains("2 segments", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("isn't drawn", closed, StringComparison.Ordinal);
    });

    [Fact]
    public async Task HiddenModeShowsWhatShadedHides() => await InNewPart(async () =>
    {
        await Block(60, 40);
        Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "20,10,10", 8));

        var shaded = await ViewTools.Capture(_connection, _workspace, "front", path: Path.Combine(CaptureFolder, "mode-shaded.png"), overwrite: true);
        var hidden = await ViewTools.Capture(_connection, _workspace, "front", path: Path.Combine(CaptureFolder, "mode-hidden.png"), overwrite: true, mode: "hidden");

        Assert.NotEqual(Quadrant(Image(shaded), 0, 0), Quadrant(Image(hidden), 0, 0));
    });

    [Fact]
    public async Task ASectionShowsTheInsideAndLeavesNothingBehind() => await InNewPart(async () =>
    {
        await Block(60, 40);
        Ok(await FeatureTools.Hole(_connection, _workspace, await TopFace(), "0,10,0", 8, depth: 6));
        var id = (await Documents()).Single(d => d.Active).Id;
        var before = await Capture("iso", "section-before.png");

        var cut = await ViewTools.Capture(_connection, _workspace, "iso", path: Path.Combine(CaptureFolder, "section-cut.png"), overwrite: true, section: "right", sectionOffset: 0);
        var after = await Capture("iso", "section-after.png");

        Assert.True(cut.IsError != true, Assert.IsType<TextContentBlock>(cut.Content[0]).Text);
        Assert.NotEqual(Quadrant(Image(before), 0, 0), Quadrant(Image(cut), 0, 0));
        Assert.Equal(Quadrant(Image(before), 0, 0), Quadrant(Image(after), 0, 0));
        Assert.Contains("1 body", Ok(await InspectionTools.Info(_connection)), StringComparison.Ordinal);
        Assert.Equal(id, (await Documents()).Single(d => d.Active).Id);
    });

    [Fact]
    public async Task LabelsOnlyWhatTheViewCanSee() => await InNewPart(async () =>
    {
        await Block(60, 40);
        var top = await TopFace();
        var bottom = Ok(await InspectionTools.Faces(_connection, normal: "-y")).Split(' ')[0];

        var result = await ViewTools.Capture(_connection, _workspace, "iso", path: Path.Combine(CaptureFolder, "labels-iso.png"), overwrite: true, labels: "faces");
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Console.WriteLine(text);

        Assert.Contains(top + ",", text + ",", StringComparison.Ordinal);
        Assert.DoesNotContain(bottom + ",", text + ",", StringComparison.Ordinal);
    });

    [Fact]
    public async Task ALabelLandsOnTheFaceItNames() => await InNewPart(async () =>
    {
        // A 50 x 30 block on Front, with a 10 mm diameter boss at x = 15 standing proud of its +Z face.
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 50, Height = 30, Dimension = true }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, FirstFeature(Ok(await SketchTools.End(_connection, _workspace))), depth: 10));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 15, Cy = 0, Radius = 5, Dimension = true }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, FirstFeature(Ok(await SketchTools.End(_connection, _workspace))), depth: 20));
        var bossEnd = Ok(await InspectionTools.Faces(_connection, normal: "+z")).Split('\n', StringSplitOptions.RemoveEmptyEntries).MinBy(line => Area(line))!.Split(' ')[0];
        var id = (await Documents()).Single(d => d.Active).Id;

        var capture = await _connection.UseAsync(
            c => c.RunAsync(_ => _backend.CaptureViews(id, [StandardView.Front], 800, 600, new CaptureOptions(Labels: CaptureLabels.Faces))),
            CancellationToken.None);
        var label = Assert.Single(capture.Labels, l => l.Handle == bossEnd);

        // The block's 50 mm fills the width less FitTogether's 6% margin each side, and is centred.
        var pixelsPerMm = 800 / 1.12 / 50;
        var distance = Math.Sqrt(Math.Pow(label.X - (400 + (15 * pixelsPerMm)), 2) + Math.Pow(label.Y - 300, 2));
        Console.WriteLine($"{label} is {distance:0.0} px from the boss centre");
        Assert.True(distance <= (5 * pixelsPerMm) + 4, $"{label} is {distance} px from the boss centre");
    });

    private static double Area(string faceLine) =>
        double.Parse(Regex.Match(faceLine, @"([\d.]+) ?mm²").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private async Task<CallToolResult> Capture(string view, string file)
    {
        var result = await ViewTools.Capture(_connection, _workspace, view, path: Path.Combine(CaptureFolder, file), overwrite: true);
        Assert.True(result.IsError != true, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Console.WriteLine(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        return result;
    }

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

    private async Task Block(double width, double height)
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = width, Height = height }]));
        var sketch = FirstFeature(Ok(await SketchTools.End(_connection, _workspace)));
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));
    }

    private async Task<string> TopFace() => Ok(await InspectionTools.Faces(_connection, normal: "+y")).Split(' ')[0];

    private async Task<IReadOnlyList<DocumentInfo>> Documents() =>
        await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None);

    private static string FirstFeature(string reply) => reply.Split(' ').First(word => word.StartsWith("feat#", StringComparison.Ordinal)).TrimEnd(',');

    private static string Quadrant(Raster sheet, int left, int top)
    {
        var sum = 0L;
        for (var y = top; y < top + 299; y += 7)
        {
            for (var x = left; x < left + 399; x += 7)
            {
                var (r, g, b) = sheet.Pixel(x, y);
                sum = sum * 31 + r + g + b;
            }
        }

        return sum.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Reads back Datum's own PNGs: one IDAT chunk, filter 0 on every row.
    private static Raster Image(CallToolResult result)
    {
        var png = Assert.IsType<ImageContentBlock>(result.Content[1]).DecodedData.ToArray();
        var (width, height) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        var idat = 8 + 25;
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat));
        using var rows = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(png, idat + 8, length), CompressionMode.Decompress))
        {
            zlib.CopyTo(rows);
        }

        var filtered = rows.ToArray();
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(filtered, y * (width * 3 + 1) + 1, rgb, y * width * 3, width * 3);
        }

        return new Raster(width, height, rgb);
    }

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
