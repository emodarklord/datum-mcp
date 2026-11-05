using System.Globalization;

namespace Datum.Core;

// Everything here is already in Datum's units: mm, mm², mm³ and grams.
public readonly record struct Point3(double X, double Y, double Z)
{
    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    public Point3 Normalized() => Length == 0 ? this : new(X / Length, Y / Length, Z / Length);

    public double Dot(Point3 other) => (X * other.X) + (Y * other.Y) + (Z * other.Z);

    public Point3 Minus(Point3 other) => new(X - other.X, Y - other.Y, Z - other.Z);

    public override string ToString() => $"{Numbers.Format(X)},{Numbers.Format(Y)},{Numbers.Format(Z)}";
}

public sealed record Box(Point3 Min, Point3 Max)
{
    public Point3 Size => new(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);
}

public enum SurfaceKind
{
    Plane,
    Cylinder,
    Cone,
    Sphere,
    Torus,
    Other,
}

public enum CurveKind
{
    Line,
    Circle,
    Other,
}

public sealed record FeatureInfo(string Handle, string Name, string TypeName, int Depth, bool Suppressed, string? Error);

public sealed record ModelInfo(
    string Title, DocumentType Type, string? Material, double MassGrams, double VolumeMm3, double AreaMm2, Point3 CenterOfMass, Box? Box, int Bodies);

public sealed record FaceInfo(string Handle, SurfaceKind Surface, double AreaMm2, Point3? Normal, Point3 Center, double? RadiusMm, string? Feature);

public sealed record EdgeInfo(string Handle, CurveKind Curve, double LengthMm, double? RadiusMm, IReadOnlyList<string> Faces, Point3? Direction = null);

public sealed record SelectionItem(string? Handle, string Kind);

public static class Numbers
{
    // For comparing against a target, where rounding to 4 digits would hide the difference being checked.
    public static string Precise(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    // About 4 significant digits keeps replies short without hiding real differences;
    // exponents only for values too large or small to read plainly.
    public static string Format(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude < 1e-9)
        {
            return "0";
        }

        if (magnitude is < 1e-3 or >= 1e9)
        {
            return value.ToString("G4", CultureInfo.InvariantCulture);
        }

        var step = Math.Pow(10, Math.Floor(Math.Log10(magnitude)) - 3);
        return (Math.Round(value / step) * step).ToString("0.######", CultureInfo.InvariantCulture);
    }
}
