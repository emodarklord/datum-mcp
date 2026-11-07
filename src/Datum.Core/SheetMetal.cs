using System.Globalization;

namespace Datum.Core;

// Depth is for an open profile, which bends at its corners. A closed profile makes a flat blank and takes none.
public sealed record SheetBaseSpec(string Sketch, double Thickness, double Radius, double? Depth) : FeatureSpec
{
    public override string Kind => "base flange";

    public override string FailureHint => "An open profile needs a depth, and the bend radius must fit its shortest line.";

    public override void Validate()
    {
        Require(Sketch.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a sketch handle (feat#N from sketch_end)");
        Require(Thickness > 0, "thickness > 0 (mm)");
        Require(Radius > 0, "bend radius > 0 (mm)");
        Require(Depth is null or > 0, "depth > 0 (mm)");
    }
}

public sealed record SheetBendSpec(string Sketch, double Angle, double Radius, bool Flip) : FeatureSpec
{
    public override string Kind => "sketched bend";

    // Folding a sheet leaves its volume alone.
    public override bool ChangesVolume => false;

    public override string FailureHint => "Sketch one straight line right across a flat face. Keep it clear of other bends.";

    public override void Validate()
    {
        Require(Sketch.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a sketch handle (feat#N from sketch_end)");
        Require(Angle is > 0 and < 180, "an angle between 0 and 180 degrees");
        Require(Radius > 0, "bend radius > 0 (mm)");
    }
}

public sealed record SheetHemSpec(IReadOnlyList<string> Edges, double Length, double Gap) : FeatureSpec
{
    public override string Kind => "hem";

    public override string FailureHint => "Pick a straight outer edge of a flat, on a face that is long enough for the hem.";

    public override void Validate()
    {
        RequireHandles(Edges, "edge#", "edge");
        Require(Length > 0, "length > 0 (mm)");
        Require(Gap >= 0, "gap of 0 or more (mm)");
    }
}

public static class Dxf
{
    private const string EntityMarker = "0";
    private const string InchUnits = "1";
    private const double MmPerInch = 25.4;

    // The size of the geometry in a DXF in mm, largest first, from the end points of its lines and the extremes of its
    // arcs and circles. SOLIDWORKS writes the document's units and says so in $INSUNITS.
    public static (double Width, double Height)? Extents(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).ToList();
        var xs = new List<double>();
        var ys = new List<double>();
        var entities = lines.IndexOf("ENTITIES");
        for (var i = entities + 1; entities >= 0 && i + 1 < lines.Count; i += 2)
        {
            if (lines[i] == EntityMarker && lines[i + 1] is "LINE" or "ARC" or "CIRCLE")
            {
                var entity = lines[i + 1];
                var codes = new Dictionary<string, double>();
                for (i += 2; i + 1 < lines.Count && lines[i] != EntityMarker; i += 2)
                {
                    if (double.TryParse(lines[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        codes[lines[i]] = value;
                    }
                }

                i -= 2;
                if (entity == "LINE")
                {
                    xs.AddRange([codes["10"], codes["11"]]);
                    ys.AddRange([codes["20"], codes["21"]]);
                }
                else if (entity == "CIRCLE")
                {
                    xs.AddRange([codes["10"] - codes["40"], codes["10"] + codes["40"]]);
                    ys.AddRange([codes["20"] - codes["40"], codes["20"] + codes["40"]]);
                }
                else
                {
                    var (start, end) = (codes["50"], codes["51"] < codes["50"] ? codes["51"] + 360 : codes["51"]);
                    var angles = new[] { start, end }.Concat(Enumerable.Range(0, 9).Select(q => q * 90d).Where(a => a > start && a < end));
                    foreach (var angle in angles)
                    {
                        xs.Add(codes["10"] + (codes["40"] * Math.Cos(angle * Math.PI / 180)));
                        ys.Add(codes["20"] + (codes["40"] * Math.Sin(angle * Math.PI / 180)));
                    }
                }
            }
        }

        if (xs.Count == 0)
        {
            return null;
        }

        var unitsAt = lines.IndexOf("$INSUNITS");
        var scale = unitsAt >= 0 && unitsAt + 2 < lines.Count && lines[unitsAt + 2] == InchUnits ? MmPerInch : 1;
        var (first, second) = ((xs.Max() - xs.Min()) * scale, (ys.Max() - ys.Min()) * scale);
        return (Math.Max(first, second), Math.Min(first, second));
    }
}
