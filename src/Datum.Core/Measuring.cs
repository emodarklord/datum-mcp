using System.Globalization;

namespace Datum.Core;

// A null value means SOLIDWORKS has no such measure for these entities.
public sealed record Measurement(
    double? DistanceMm,
    double? NormalDistanceMm,
    double? CenterDistanceMm,
    double? AngleDegrees,
    Point3? DeltaMm,
    double? ClearanceMm,
    double? LengthMm,
    double? RadiusMm,
    double? AreaMm2,
    double? PlaneOffsetMm = null)
{
    public override string ToString()
    {
        var parts = new List<string>();
        void Add(string label, double? value, string unit)
        {
            if (value is { } known)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {Numbers.Format(known)}{unit}"));
            }
        }

        Add("distance", DistanceMm, " mm");
        Add("normal distance", NormalDistanceMm, " mm");
        Add("centre distance", CenterDistanceMm, " mm");
        Add("plane offset", PlaneOffsetMm, " mm");
        Add("clearance", ClearanceMm, " mm");
        Add("angle", AngleDegrees, "°");
        if (DeltaMm is { } delta)
        {
            parts.Add($"delta {delta} mm");
        }

        Add("length", LengthMm, " mm");
        Add("radius", RadiusMm, " mm");
        Add("area", AreaMm2, " mm²");
        return parts.Count == 0 ? "nothing measurable" : string.Join(" · ", parts);
    }
}
