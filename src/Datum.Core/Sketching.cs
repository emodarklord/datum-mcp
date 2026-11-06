using System.ComponentModel;
using System.Globalization;

namespace Datum.Core;

// One record for every entity type keeps the MCP schema flat; Validate checks which fields each type needs.
public sealed record SketchEntitySpec
{
    [Description("line, centerline, rect, center_rect, circle, arc, polygon, slot, point, polyline or spline")]
    public string Type { get; init; } = "";

    [Description("Start or first corner x (mm)")]
    public double? X1 { get; init; }

    [Description("Start or first corner y (mm)")]
    public double? Y1 { get; init; }

    [Description("End or opposite corner x (mm)")]
    public double? X2 { get; init; }

    [Description("End or opposite corner y (mm)")]
    public double? Y2 { get; init; }

    [Description("3D sketch only: start z (mm)")]
    public double? Z1 { get; init; }

    [Description("3D sketch only: end z (mm)")]
    public double? Z2 { get; init; }

    [Description("arc only: x of any point on the arc between start and end (mm)")]
    public double? X3 { get; init; }

    [Description("arc only: y of any point on the arc between start and end (mm)")]
    public double? Y3 { get; init; }

    [Description("Centre x (mm) for circle, polygon, center_rect")]
    public double? Cx { get; init; }

    [Description("Centre y (mm) for circle, polygon, center_rect")]
    public double? Cy { get; init; }

    [Description("circle radius, or polygon circumradius (mm)")]
    public double? Radius { get; init; }

    [Description("center_rect width, or slot width (mm)")]
    public double? Width { get; init; }

    [Description("center_rect height (mm)")]
    public double? Height { get; init; }

    [Description("polygon side count, 3 or more")]
    public int? Sides { get; init; }

    [Description("polyline or spline points as [x, y] pairs, or [x, y, z] in a 3D sketch (mm)")]
    public IReadOnlyList<IReadOnlyList<double>>? Points { get; init; }

    [Description("polyline only: join the last point back to the first")]
    public bool Closed { get; init; }

    [Description("Make it construction geometry")]
    public bool Construction { get; init; }

    [Description("Fully define it with dimensions from the sketch origin (the default); false leaves it free")]
    public bool? Dimension { get; init; }
}

public sealed record SketchConstraintSpec
{
    [Description("horizontal, vertical, coincident, parallel, perpendicular, tangent, equal, concentric, collinear, midpoint, fixed, "
        + "or a dimension: length, distance, diameter, radius, angle")]
    public string Kind { get; init; } = "";

    [Description("Sketch handles (seg#N, pt#N, from sketch_add or sketch_read) the constraint applies to, or 'origin' for the sketch origin")]
    public IReadOnlyList<string> Entities { get; init; } = [];

    [Description("Dimension value in mm, or degrees for angle")]
    public double? Value { get; init; }
}

public readonly record struct Point2(double X, double Y)
{
    public override string ToString() => $"{Numbers.Format(X)},{Numbers.Format(Y)}";
}

// Reopened: this sketch existed before the edit, so ending it must not record it as newly created.
public sealed record SketchInfo(string? Handle, string Name, string Status, int Segments, Point3 Origin, Point3 XAxis, Point3 YAxis, bool Reopened = false);

public sealed record SketchSegmentInfo(string Handle, string Kind, Point2 Start, Point2 End, Point2? Center, double? Radius, bool Construction);

public static class SketchSpecs
{
    public const string Origin = "origin";

    public const string ThreeD = "3D";

    public static bool Works3D(string type) => type is "line" or "polyline" or "spline" or "point";

    private static readonly Dictionary<string, (int Min, int Max, bool NeedsValue)> Constraints = new()
    {
        ["horizontal"] = (1, 2, false),
        ["vertical"] = (1, 2, false),
        ["coincident"] = (2, 2, false),
        ["parallel"] = (2, 2, false),
        ["perpendicular"] = (2, 2, false),
        ["tangent"] = (2, 2, false),
        ["equal"] = (2, 8, false),
        ["concentric"] = (2, 2, false),
        ["collinear"] = (2, 2, false),
        ["midpoint"] = (2, 2, false),
        ["fixed"] = (1, 8, false),
        ["length"] = (1, 1, true),
        ["distance"] = (2, 2, true),
        ["diameter"] = (1, 1, true),
        ["radius"] = (1, 1, true),
        ["angle"] = (2, 2, true),
    };

    public static IReadOnlyCollection<string> ConstraintKinds => Constraints.Keys;

    public static void Validate(IReadOnlyList<SketchEntitySpec> entities)
    {
        if (entities.Count == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "no entities to add");
        }

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            var problem = e.Type switch
            {
                "line" or "centerline" or "rect" => Need(e.X1, e.Y1, e.X2, e.Y2) ?? NotSame(e),
                "center_rect" => Need(e.Cx, e.Cy) ?? Positive(e.Width, "width") ?? Positive(e.Height, "height"),
                "circle" => Need(e.Cx, e.Cy) ?? Positive(e.Radius, "radius"),
                "arc" => Need(e.X1, e.Y1, e.X2, e.Y2, e.X3, e.Y3) ?? NotSame(e),
                "polygon" => Need(e.Cx, e.Cy) ?? Positive(e.Radius, "radius") ?? (e.Sides is >= 3 ? null : "sides of 3 or more"),
                "slot" => Need(e.X1, e.Y1, e.X2, e.Y2) ?? NotSame(e) ?? Positive(e.Width, "width"),
                "point" => Need(e.X1, e.Y1),
                "polyline" or "spline" => e.Points is { Count: >= 2 } points && points.All(p => p.Count is 2 or 3) ? null : "points: at least two [x, y] or [x, y, z] points",
                _ => "a type: line, centerline, rect, center_rect, circle, arc, polygon, slot, point, polyline or spline",
            };

            if (problem is not null)
            {
                throw new DatumException(
                    ErrorCode.InvalidArgument,
                    string.Create(CultureInfo.InvariantCulture, $"entity {i} ({(e.Type.Length == 0 ? "no type" : e.Type)}) needs {problem}"),
                    "Nothing was drawn; fix it and send the whole batch again");
            }
        }
    }

    public static void Validate(IReadOnlyList<SketchConstraintSpec> constraints)
    {
        if (constraints.Count == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "no constraints to add");
        }

        for (var i = 0; i < constraints.Count; i++)
        {
            var c = constraints[i];
            string? problem;
            if (!Constraints.TryGetValue(c.Kind, out var rule))
            {
                problem = $"a kind: {string.Join(", ", Constraints.Keys)}";
            }
            else if (c.Entities.Count < rule.Min || c.Entities.Count > rule.Max)
            {
                problem = rule.Min == rule.Max
                    ? string.Create(CultureInfo.InvariantCulture, $"exactly {rule.Min} entities")
                    : string.Create(CultureInfo.InvariantCulture, $"{rule.Min} to {rule.Max} entities");
            }
            else
            {
                problem = rule.NeedsValue ? Positive(c.Value, "value") : null;
            }

            if (problem is not null)
            {
                throw new DatumException(
                    ErrorCode.InvalidArgument,
                    string.Create(CultureInfo.InvariantCulture, $"constraint {i} ({(c.Kind.Length == 0 ? "no kind" : c.Kind)}) needs {problem}"),
                    "Nothing was applied; fix it and send the whole batch again");
            }
        }
    }

    public static string DescribeStatus(int solidWorksStatus) => solidWorksStatus switch
    {
        2 => "under defined",
        3 => "fully defined",
        4 => "over defined",
        5 => "no solution",
        6 => "invalid solution",
        _ => "status unknown",
    };

    private static string? Need(params double?[] values) => values.Any(v => v is null) ? "all of its coordinates" : null;

    private static string? Positive(double? value, string name) => value is > 0 ? null : $"{name} > 0";

    private static string? NotSame(SketchEntitySpec e) => e.X1 == e.X2 && e.Y1 == e.Y2 && e.Z1 == e.Z2 ? "different start and end points" : null;
}
