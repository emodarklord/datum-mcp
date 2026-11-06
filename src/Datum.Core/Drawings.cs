using System.Globalization;

namespace Datum.Core;

public sealed record DrawingSpec(string Sheet, string Scale, string Projection)
{
    // Numbered like swDwgPaperSizes_e.
    private static readonly string[] Sizes = ["a", "a-portrait", "b", "c", "d", "e", "a4", "a4-portrait", "a3", "a2", "a1", "a0"];

    public string SheetName => Sheet.Trim().ToLowerInvariant();

    public int PaperSize => Array.IndexOf(Sizes, SheetName);

    public bool FirstAngle => Projection.Trim().Equals("first", StringComparison.OrdinalIgnoreCase);

    public (double Numerator, double Denominator) ParsedScale => DrawingScale.Parse(Scale);

    public void Validate()
    {
        if (PaperSize < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown sheet size '{Sheet}'", $"Use {string.Join(", ", Sizes)}");
        }

        if (!FirstAngle && !Projection.Trim().Equals("third", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown projection '{Projection}'", "Use first or third");
        }

        _ = ParsedScale;
    }
}

public static class DrawingScale
{
    public static (double Numerator, double Denominator) Parse(string scale)
    {
        var parts = scale.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return (numerator, denominator);
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"unknown scale '{scale}'", "Write it like 1:2 or 2:1");
    }
}

public static class ViewDisplays
{
    // Numbered like swDisplayMode_e.
    public static int Parse(string display) => display.Trim().ToLowerInvariant() switch
    {
        "wireframe" => 0,
        "hidden" => 1,
        "removed" => 2,
        "shaded" => 7,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown display '{display}'", "Use hidden (hidden lines shown), removed (hidden lines removed), wireframe or shaded"),
    };

    public static string Name(int mode) => mode switch
    {
        0 or 4 => "wireframe",
        1 or 5 => "hidden",
        2 or 6 => "removed",
        3 or 7 => "shaded",
        _ => "default",
    };
}

public sealed record ViewInfo(
    string Handle, string Name, string Sheet, string Kind, string? Model, string Scale, double X, double Y, double Width, double Height, string Display, int Dimensions, int CentreMarks, int CentreLines)
{
    public override string ToString() =>
        $"{Handle} {Name} [{Kind}] {Scale} at {Numbers.Format(X)},{Numbers.Format(Y)} mm, {Numbers.Format(Width)} x {Numbers.Format(Height)} mm, {Display}, {Dimensions} dimensions, {CentreMarks} centre marks, {CentreLines} centre lines";
}

// A null format means the sheet has no title block.
public sealed record DrawingCreated(DocumentInfo Document, string? Format, double WidthMm, double HeightMm, bool FirstAngle);
