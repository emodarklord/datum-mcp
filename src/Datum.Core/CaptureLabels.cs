namespace Datum.Core;

// Mode values match swViewDisplayMode_e.
public enum CaptureMode
{
    Wireframe = 1,
    Hidden = 3,
    Shaded = 5,
}

public enum CaptureLabels
{
    None,
    Faces,
    Edges,
}

public sealed record SectionCut(StandardPlane Plane, double OffsetMm);

public enum StandardPlane
{
    Front,
    Top,
    Right,
}

public sealed record CaptureOptions(CaptureMode Mode = CaptureMode.Shaded, SectionCut? Section = null, CaptureLabels Labels = CaptureLabels.None)
{
    public static CaptureOptions Parse(string mode, string? section, double sectionOffset, string? labels, bool sheet)
    {
        var parsedMode = mode.Trim().ToUpperInvariant() switch
        {
            "SHADED" => CaptureMode.Shaded,
            "HIDDEN" => CaptureMode.Hidden,
            "WIREFRAME" => CaptureMode.Wireframe,
            _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown mode '{mode}'", "Use shaded, hidden or wireframe"),
        };
        var cut = section is null
            ? null
            : new SectionCut(
                Enum.TryParse<StandardPlane>(section.Trim(), ignoreCase: true, out var plane) && !int.TryParse(section, out _)
                    ? plane
                    : throw new DatumException(ErrorCode.InvalidArgument, $"unknown section plane '{section}'", "Use front, top or right"),
                sectionOffset);
        var parsedLabels = labels?.Trim().ToUpperInvariant() switch
        {
            null => CaptureLabels.None,
            "FACES" => CaptureLabels.Faces,
            "EDGES" => CaptureLabels.Edges,
            _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown labels '{labels}'", "Use faces or edges"),
        };
        if (parsedLabels != CaptureLabels.None && (sheet || cut is not null))
        {
            throw new DatumException(ErrorCode.InvalidArgument, "labels work on a single uncut view", "Capture one view without a section");
        }

        return new CaptureOptions(parsedMode, cut, parsedLabels);
    }
}

// Where a render's content ended up in the final image, in render pixels.
public sealed record ImageCrop(double Left, double Top, double StepX, double StepY, double ContentLeft, double ContentTop, double ContentWidth, double ContentHeight);

public readonly record struct ViewExtents(double MinX, double MaxX, double MinY, double MaxY);

public sealed record ImageLabel(string Handle, double X, double Y);

public static class LabelPlacement
{
    private const int DigitWidth = 5;
    private const int DigitHeight = 7;
    private const int Scale = 2;
    private const int Pad = 2;
    private const int MaxNudges = 6;

    // Rows of a 5×7 font, one array per digit, a set bit being ink.
    private static readonly byte[][] Digits =
    [
        [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        [0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F],
        [0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E],
        [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
    ];

    // The model's projected extents fill the render's content box, so matching one to the other gives the
    // mapping without knowing how SOLIDWORKS defines its zoom. View Y points up; image Y points down.
    public static (double X, double Y) Project(ViewExtents extents, ImageCrop crop, double viewX, double viewY)
    {
        var (spanX, spanY) = (extents.MaxX - extents.MinX, extents.MaxY - extents.MinY);
        var pixelsPerUnit = spanX >= spanY ? crop.ContentWidth / spanX : crop.ContentHeight / spanY;
        var renderX = crop.ContentLeft + (crop.ContentWidth / 2) + ((viewX - ((extents.MinX + extents.MaxX) / 2)) * pixelsPerUnit);
        var renderY = crop.ContentTop + (crop.ContentHeight / 2) - ((viewY - ((extents.MinY + extents.MaxY) / 2)) * pixelsPerUnit);
        return ((renderX - crop.Left) / crop.StepX, (renderY - crop.Top) / crop.StepY);
    }

    public static void Draw(Raster image, IReadOnlyList<ImageLabel> labels)
    {
        var placed = new List<(int Left, int Top, int Right, int Bottom)>();
        foreach (var label in labels)
        {
            var text = label.Handle[(label.Handle.IndexOf('#', StringComparison.Ordinal) + 1)..];
            var boxWidth = (text.Length * (DigitWidth + 1) * Scale) + (Pad * 2);
            var boxHeight = (DigitHeight * Scale) + (Pad * 2);
            var (x, y) = ((int)Math.Round(label.X), (int)Math.Round(label.Y));
            var box = Free(placed, Clamp(x + 4, image.Width - boxWidth), Clamp(y - boxHeight - 4, image.Height - boxHeight), boxWidth, boxHeight, image.Height);
            placed.Add((box.Left, box.Top, box.Left + boxWidth, box.Top + boxHeight));

            Line(image, x, y, box.Left, box.Top + boxHeight - 1);
            Box(image, box.Left, box.Top, boxWidth, boxHeight);
            for (var i = 0; i < text.Length; i++)
            {
                Digit(image, text[i] - '0', box.Left + Pad + (i * (DigitWidth + 1) * Scale), box.Top + Pad);
            }

            Dot(image, x, y);
        }
    }

    private static int Clamp(int value, int max) => Math.Max(0, Math.Min(value, max));

    // Steps down past labels already drawn, so numbers stay readable where small faces crowd together.
    private static (int Left, int Top) Free(List<(int Left, int Top, int Right, int Bottom)> placed, int left, int top, int width, int height, int imageHeight)
    {
        for (var nudge = 0; nudge < MaxNudges; nudge++)
        {
            var clash = placed.Any(p => left < p.Right && left + width > p.Left && top < p.Bottom && top + height > p.Top);
            if (!clash)
            {
                break;
            }

            top = Clamp(top + height + 1, imageHeight - height);
        }

        return (left, top);
    }

    private static void Box(Raster image, int left, int top, int width, int height)
    {
        for (var y = top; y < top + height && y < image.Height; y++)
        {
            for (var x = left; x < left + width && x < image.Width; x++)
            {
                var border = y == top || y == top + height - 1 || x == left || x == left + width - 1;
                image.Set(x, y, border ? (byte)200 : (byte)255, border ? (byte)0 : (byte)255, border ? (byte)0 : (byte)255);
            }
        }
    }

    private static void Digit(Raster image, int digit, int left, int top)
    {
        for (var row = 0; row < DigitHeight; row++)
        {
            for (var column = 0; column < DigitWidth; column++)
            {
                if ((Digits[digit][row] >> (DigitWidth - 1 - column) & 1) == 0)
                {
                    continue;
                }

                for (var y = 0; y < Scale; y++)
                {
                    for (var x = 0; x < Scale; x++)
                    {
                        Ink(image, left + (column * Scale) + x, top + (row * Scale) + y, 0, 0, 0);
                    }
                }
            }
        }
    }

    private static void Dot(Raster image, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                Ink(image, x + dx, y + dy, 200, 0, 0);
            }
        }
    }

    private static void Line(Raster image, int fromX, int fromY, int toX, int toY)
    {
        var steps = Math.Max(Math.Abs(toX - fromX), Math.Abs(toY - fromY));
        for (var i = 0; i <= steps; i++)
        {
            var t = steps == 0 ? 0 : (double)i / steps;
            Ink(image, (int)Math.Round(fromX + ((toX - fromX) * t)), (int)Math.Round(fromY + ((toY - fromY) * t)), 200, 0, 0);
        }
    }

    private static void Ink(Raster image, int x, int y, byte r, byte g, byte b)
    {
        if (x >= 0 && y >= 0 && x < image.Width && y < image.Height)
        {
            image.Set(x, y, r, g, b);
        }
    }
}
