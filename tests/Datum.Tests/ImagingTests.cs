using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Datum.Core;

namespace Datum.Tests;

public class ImagingTests
{
    [Fact]
    public void EncodesAPngThatInflatesBackToTheRows()
    {
        var raster = new Raster(2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9]);

        var png = Png.Encode(raster);
        var chunks = Chunks(png);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(chunks[0].Data.AsSpan(4)));
        Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 9, 9, 9 }, Inflate(chunks[1].Data));
    }

    [Fact]
    public void WritesChunkChecksums()
    {
        var iend = Png.Encode(new Raster(1, 1, [0, 0, 0]))[^12..];

        // IEND has no data, so its CRC is the well-known constant for the bytes "IEND".
        Assert.Equal(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 0xAE, 0x42, 0x60, 0x82 }, iend);
    }

    [Fact]
    public void Decodes24BitBottomUpBitmapsWithRowPadding()
    {
        // 2 × 3 pixels: each 6-byte row is padded to 8. Rows are stored bottom first, as BGR.
        var bmp = Bitmap(24, 2, 3, [
            [1, 2, 3, 4, 5, 6, 0, 0],
            [7, 8, 9, 10, 11, 12, 0, 0],
            [13, 14, 15, 16, 17, 18, 0, 0],
        ]);

        var raster = Bmp.Decode(bmp);

        Assert.Equal((2, 3), (raster.Width, raster.Height));
        Assert.Equal(new byte[] { 15, 14, 13, 18, 17, 16, 9, 8, 7, 12, 11, 10, 3, 2, 1, 6, 5, 4 }, raster.Rgb);
    }

    [Fact]
    public void Decodes32BitBitmaps()
    {
        var raster = Bmp.Decode(Bitmap(32, 1, 1, [[30, 20, 10, 255]]));

        Assert.Equal(new byte[] { 10, 20, 30 }, raster.Rgb);
    }

    [Fact]
    public void RejectsCompressedBitmaps()
    {
        var bmp = Bitmap(24, 1, 1, [[0, 0, 0, 0]]);
        bmp[30] = 1;

        Assert.Equal(ErrorCode.SolidworksError, Assert.Throws<DatumException>(() => Bmp.Decode(bmp)).Code);
    }

    [Fact]
    public void FitsTheContentCentredWithAMargin()
    {
        // A white 40 × 20 image with a black 10 × 10 square at the right edge.
        var source = Raster.Filled(40, 20, 255, 255, 255);
        for (var y = 5; y < 15; y++)
        {
            for (var x = 30; x < 40; x++)
            {
                source.Set(x, y, 0, 0, 0);
            }
        }

        var fitted = source.FitContent(20, 20);

        Assert.Equal((20, 20), (fitted.Width, fitted.Height));
        Assert.Equal((0, 0, 0), fitted.Pixel(10, 10));
        Assert.Equal((255, 255, 255), fitted.Pixel(0, 10));
        Assert.Equal((255, 255, 255), fitted.Pixel(19, 10));
        Assert.Equal(fitted.Pixel(2, 10), fitted.Pixel(17, 10));
    }

    [Fact]
    public void FitsRendersTogetherAtOneScale()
    {
        var square = Raster.Filled(40, 20, 255, 255, 255);
        var wide = Raster.Filled(40, 20, 255, 255, 255);
        for (var y = 5; y < 15; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                wide.Set(x + 10, y, 0, 0, 0);
                if (x < 10)
                {
                    square.Set(x + 2, y, 0, 0, 0);
                }
            }
        }

        var fitted = Raster.FitTogether([square, wide], 40, 40);

        Assert.Equal(2 * Dark(fitted[0]), Dark(fitted[1]), tolerance: 2);
        Assert.Equal((0, 0, 0), fitted[0].Pixel(20, 20));
    }

    [Fact]
    public void TellsWhenTheContentRunsIntoTheBorder()
    {
        var inside = Raster.Filled(10, 10, 255, 255, 255);
        inside.Set(5, 5, 0, 0, 0);
        var cut = Raster.Filled(10, 10, 255, 255, 255);
        cut.Set(9, 5, 0, 0, 0);

        Assert.False(inside.ContentTouchesEdge());
        Assert.True(cut.ContentTouchesEdge());
        Assert.False(Raster.Filled(10, 10, 255, 255, 255).ContentTouchesEdge());
        Assert.True(inside.HasContent());
        Assert.False(Raster.Filled(10, 10, 255, 255, 255).HasContent());
    }

    [Fact]
    public void FitsABlankImageByScalingIt()
    {
        var fitted = Raster.Filled(40, 30, 255, 255, 255).FitContent(4, 3);

        Assert.Equal((4, 3), (fitted.Width, fitted.Height));
        Assert.Equal((255, 255, 255), fitted.Pixel(3, 2));
    }

    [Fact]
    public void PutsFourViewsInASheet()
    {
        var views = new[] { Raster.Filled(4, 3, 1, 1, 1), Raster.Filled(4, 3, 2, 2, 2), Raster.Filled(4, 3, 3, 3, 3), Raster.Filled(4, 3, 4, 4, 4) };

        var sheet = Raster.Sheet(views);

        Assert.Equal((10, 8), (sheet.Width, sheet.Height));
        Assert.Equal((1, 1, 1), sheet.Pixel(0, 0));
        Assert.Equal((2, 2, 2), sheet.Pixel(9, 0));
        Assert.Equal((3, 3, 3), sheet.Pixel(0, 7));
        Assert.Equal((4, 4, 4), sheet.Pixel(9, 7));
        Assert.Equal((160, 160, 160), sheet.Pixel(4, 0));
    }

    [Theory]
    [InlineData("iso", StandardView.Isometric)]
    [InlineData("Isometric", StandardView.Isometric)]
    [InlineData(" TOP ", StandardView.Top)]
    [InlineData("dimetric", StandardView.Dimetric)]
    public void ParsesViewNames(string text, StandardView expected) => Assert.Equal(expected, Views.Parse(text));

    [Fact]
    public void RejectsUnknownViews()
    {
        var error = Assert.Throws<DatumException>(() => Views.Parse("upside"));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.Contains("front, back, left, right, top, bottom, iso, trimetric, dimetric", error.Hint, StringComparison.Ordinal);
    }

    private static double Dark(Raster raster) => Enumerable.Range(0, raster.Width).Count(x => raster.Pixel(x, raster.Height / 2).R < 128);

    private static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var at = 8; at < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            chunks.Add((Encoding.ASCII.GetString(png, at + 4, 4), png[(at + 8)..(at + 8 + length)]));
            at += 12 + length;
        }

        return chunks;
    }

    private static byte[] Inflate(byte[] data)
    {
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        return inflated.ToArray();
    }

    private static byte[] Bitmap(short bitsPerPixel, int width, int height, byte[][] rows)
    {
        var pixels = rows.SelectMany(r => r).ToArray();
        var file = new byte[54 + pixels.Length];
        "BM"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(28), bitsPerPixel);
        pixels.CopyTo(file, 54);
        return file;
    }
}
