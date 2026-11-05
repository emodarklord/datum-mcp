using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Datum.Core;

// Numbered like swStandardViews_e so the COM layer can pass them straight to ShowNamedView2.
public enum StandardView
{
    Front = 1,
    Back = 2,
    Left = 3,
    Right = 4,
    Top = 5,
    Bottom = 6,
    Isometric = 7,
    Trimetric = 8,
    Dimetric = 9,
}

public static class Views
{
    public static StandardView Parse(string text)
    {
        var name = text.Trim();
        if (string.Equals(name, "iso", StringComparison.OrdinalIgnoreCase))
        {
            return StandardView.Isometric;
        }

        return Enum.TryParse<StandardView>(name, ignoreCase: true, out var view) && !int.TryParse(name, out _)
            ? view
            : throw new DatumException(ErrorCode.InvalidArgument, $"unknown view '{text}'", "Use front, back, left, right, top, bottom, iso, trimetric, dimetric or sheet");
    }

    public static string Name(StandardView view) => view.ToString().ToLowerInvariant();
}

public sealed record Raster(int Width, int Height, byte[] Rgb)
{
    private const int ContentThreshold = 24;
    private const int Gutter = 2;
    private const byte GutterGrey = 160;
    private static readonly (byte R, byte G, byte B) Background = (255, 255, 255);

    public static Raster Filled(int width, int height, byte r, byte g, byte b)
    {
        var raster = new Raster(width, height, new byte[width * height * 3]);
        for (var i = 0; i < raster.Rgb.Length; i += 3)
        {
            (raster.Rgb[i], raster.Rgb[i + 1], raster.Rgb[i + 2]) = (r, g, b);
        }

        return raster;
    }

    // Front | top on the first row, right | isometric on the second, like a drawing's standard views.
    public static Raster Sheet(IReadOnlyList<Raster> views)
    {
        var (width, height) = (views[0].Width, views[0].Height);
        var sheet = Filled(width * 2 + Gutter, height * 2 + Gutter, GutterGrey, GutterGrey, GutterGrey);
        for (var i = 0; i < 4; i++)
        {
            var (left, top) = (i % 2 * (width + Gutter), i / 2 * (height + Gutter));
            for (var y = 0; y < height; y++)
            {
                Array.Copy(views[i].Rgb, y * width * 3, sheet.Rgb, ((top + y) * sheet.Width + left) * 3, width * 3);
            }
        }

        return sheet;
    }

    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        var at = (y * Width + x) * 3;
        return (Rgb[at], Rgb[at + 1], Rgb[at + 2]);
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        var at = (y * Width + x) * 3;
        (Rgb[at], Rgb[at + 1], Rgb[at + 2]) = (r, g, b);
    }

    public Raster FitContent(int width, int height) => FitTogether([this], width, height)[0];

    // Crops each render to whatever isn't background, centred, with a margin and the target's aspect
    // ratio, then scales down. Zoom-to-fit leaves the model off-centre, so this is what centres every
    // capture. All renders get the same crop size, so renders made at one zoom keep one scale, like
    // the views on a drawing. A render with nothing on it is scaled down whole.
    public static IReadOnlyList<Raster> FitTogether(IReadOnlyList<Raster> renders, int width, int height)
    {
        var crops = CropsFor(renders, width, height);
        return [.. renders.Select((render, i) => render.Resample(crops[i].Left, crops[i].Top, crops[i].StepX, crops[i].StepY, width, height))];
    }

    public static IReadOnlyList<ImageCrop> CropsFor(IReadOnlyList<Raster> renders, int width, int height)
    {
        var boxes = renders.Select(r => r.ContentBox() ?? (0, 0, r.Width, r.Height)).ToList();
        var margin = Math.Max(1, boxes.Max(b => Math.Max(b.Width, b.Height)) * 0.06);
        var (cropWidth, cropHeight) = (boxes.Max(b => b.Width) + 2 * margin, boxes.Max(b => b.Height) + 2 * margin);
        var aspect = (double)width / height;
        (cropWidth, cropHeight) = cropWidth / cropHeight < aspect ? (cropHeight * aspect, cropHeight) : (cropWidth, cropWidth / aspect);

        return [.. boxes.Select(box => new ImageCrop(
            box.Left + box.Width / 2 - cropWidth / 2,
            box.Top + box.Height / 2 - cropHeight / 2,
            cropWidth / width,
            cropHeight / height,
            box.Left,
            box.Top,
            box.Width,
            box.Height))];
    }

    public bool HasContent() => ContentBox() is not null;

    // True when the model runs into the border, which means the render cut part of it off.
    public bool ContentTouchesEdge() =>
        ContentBox() is var (left, top, width, height) && (left <= 0 || top <= 0 || left + width >= Width || top + height >= Height);

    // Datum renders on the plain white background it sets, so white is the background. Reading it from a
    // corner pixel failed when something covered that corner.
    private (double Left, double Top, double Width, double Height)? ContentBox()
    {
        var (minX, minY, maxX, maxY) = (Width, Height, -1, -1);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (Differs(Pixel(x, y), Background))
                {
                    (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                }
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private Raster Resample(double left, double top, double stepX, double stepY, int width, int height)
    {
        var result = new Raster(width, height, new byte[width * height * 3]);
        for (var ty = 0; ty < height; ty++)
        {
            var (y0, y1) = Span(top + ty * stepY, stepY);
            for (var tx = 0; tx < width; tx++)
            {
                var (x0, x1) = Span(left + tx * stepX, stepX);
                int r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var (pr, pg, pb) = x >= 0 && y >= 0 && x < Width && y < Height ? Pixel(x, y) : Background;
                        (r, g, b) = (r + pr, g + pg, b + pb);
                    }
                }

                var count = (x1 - x0) * (y1 - y0);
                result.Set(tx, ty, (byte)(r / count), (byte)(g / count), (byte)(b / count));
            }
        }

        return result;
    }

    private static (int From, int To) Span(double start, double step)
    {
        var from = (int)Math.Floor(start);
        return (from, Math.Max(from + 1, (int)Math.Ceiling(start + step)));
    }

    private static bool Differs((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Abs(a.R - b.R) > ContentThreshold || Math.Abs(a.G - b.G) > ContentThreshold || Math.Abs(a.B - b.B) > ContentThreshold;
}

public static class Bmp
{
    public static Raster Decode(byte[] file)
    {
        var bits = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(30));
        // 32-bit bitmaps may say BI_BITFIELDS (3) while still being plain BGRA.
        if (file.Length < 54 || file[0] != 'B' || file[1] != 'M' || bits is not (24 or 32) || !(compression == 0 || (compression == 3 && bits == 32)))
        {
            throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS saved an image Datum can't read", "Report this with the SOLIDWORKS version");
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(10));
        var width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(18));
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(22));
        var height = Math.Abs(storedHeight);
        var bytesPerPixel = bits / 8;
        var stride = (width * bits + 31) / 32 * 4;
        var raster = new Raster(width, height, new byte[width * height * 3]);
        for (var y = 0; y < height; y++)
        {
            var row = offset + (storedHeight > 0 ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                var at = row + x * bytesPerPixel;
                raster.Set(x, y, file[at + 2], file[at + 1], file[at]);
            }
        }

        return raster;
    }
}

public static class Png
{
    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    })];

    public static byte[] Encode(Raster raster)
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, raster.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), raster.Height);
        (header[8], header[9]) = (8, 2);
        WriteChunk(png, "IHDR", header);

        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < raster.Height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(raster.Rgb, y * raster.Width * 3, raster.Width * 3);
            }
        }

        WriteChunk(png, "IDAT", pixels.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(typeBytes);
        png.Write(data);

        var crc = 0xFFFFFFFF;
        foreach (var value in typeBytes.Concat(data))
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
        png.Write(number);
    }
}

// SketchOpen: SaveBMP doesn't draw the entities of a sketch being edited, so callers can say so.
public sealed record ViewCapture(IReadOnlyList<Raster> Views, bool SketchOpen, IReadOnlyList<ImageLabel> Labels);
