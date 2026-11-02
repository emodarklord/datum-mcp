using System.Globalization;

namespace Datum.Core;

public sealed record EntityPage<T>(IReadOnlyList<T> Items, int Total, int Offset);

public static class EntityQuery
{
    private static readonly double SameDirectionCosine = Math.Cos(Math.PI / 180);

    private static readonly (string Name, Point3 Direction)[] Axes =
    [
        ("+x", new(1, 0, 0)), ("-x", new(-1, 0, 0)),
        ("+y", new(0, 1, 0)), ("-y", new(0, -1, 0)),
        ("+z", new(0, 0, 1)), ("-z", new(0, 0, -1)),
    ];

    public static EntityPage<FaceInfo> Faces(
        IEnumerable<FaceInfo> faces, string? surface, string? normal, string? feature, int offset, int limit)
    {
        var kind = surface is null ? (SurfaceKind?)null : ParseKind<SurfaceKind>(surface, "surface");
        var direction = normal is null ? (Point3?)null : ParseDirection(normal);
        var matches = faces
            .Where(f => kind is null || f.Surface == kind)
            .Where(f => direction is null || (f.Normal is { } n && n.Normalized().Dot(direction.Value) >= SameDirectionCosine))
            .Where(f => feature is null || string.Equals(f.Feature, feature, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.AreaMm2)
            .ToList();
        return Page(matches, offset, limit);
    }

    public static EntityPage<EdgeInfo> Edges(IEnumerable<EdgeInfo> edges, string? curve, string? face, int offset, int limit)
    {
        var kind = curve is null ? (CurveKind?)null : ParseKind<CurveKind>(curve, "curve");
        var matches = edges
            .Where(e => kind is null || e.Curve == kind)
            .Where(e => face is null || e.Faces.Contains(face, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(e => e.LengthMm)
            .ToList();
        return Page(matches, offset, limit);
    }

    public static Point3 ParseDirection(string text)
    {
        var trimmed = text.Trim();
        var signed = trimmed.StartsWith('+') || trimmed.StartsWith('-') ? trimmed : "+" + trimmed;
        var axis = Axes.FirstOrDefault(a => string.Equals(a.Name, signed, StringComparison.OrdinalIgnoreCase));
        if (axis.Name is not null)
        {
            return axis.Direction;
        }

        var parts = trimmed.Split(',');
        if (parts.Length == 3
            && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var y)
            && double.TryParse(parts[2], CultureInfo.InvariantCulture, out var z)
            && new Point3(x, y, z).Length > 0)
        {
            return new Point3(x, y, z).Normalized();
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"'{text}' is not a direction", "Use +x, -x, +y, -y, +z, -z or x,y,z");
    }

    public static string DescribeDirection(Point3 direction)
    {
        var unit = direction.Normalized();
        return Axes.FirstOrDefault(a => a.Direction.Dot(unit) >= SameDirectionCosine).Name ?? unit.ToString();
    }

    private static T ParseKind<T>(string text, string what)
        where T : struct, Enum =>
        Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var kind)
            ? kind
            : throw new DatumException(ErrorCode.InvalidArgument, $"unknown {what} '{text}'", $"Use one of {string.Join(", ", Enum.GetNames<T>())}");

    private static EntityPage<T> Page<T>(List<T> matches, int offset, int limit)
    {
        if (offset < 0 || limit <= 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "offset must be 0 or more and limit more than 0");
        }

        return new EntityPage<T>([.. matches.Skip(offset).Take(limit)], matches.Count, offset);
    }
}
