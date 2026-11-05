namespace Datum.Core;

// Where a component sits in its assembly, in mm and degrees. Rotations apply about the assembly's X, then Y, then Z.
public readonly record struct ComponentPose(Point3 PositionMm, Point3 RotationDegrees)
{
    private const double Mm = 1000;
    private const double GimbalLimit = 1 - 1e-9;

    public bool IsRotated => RotationDegrees.Length > 1e-6;

    // SOLIDWORKS's math transforms hold each axis's image as a row, then the translation in metres, then a scale of 1.
    public double[] ToTransform()
    {
        var (sx, cx) = SinCos(RotationDegrees.X);
        var (sy, cy) = SinCos(RotationDegrees.Y);
        var (sz, cz) = SinCos(RotationDegrees.Z);
        return
        [
            cz * cy, sz * cy, -sy,
            (cz * sy * sx) - (sz * cx), (sz * sy * sx) + (cz * cx), cy * sx,
            (cz * sy * cx) + (sz * sx), (sz * sy * cx) - (cz * sx), cy * cx,
            PositionMm.X / Mm, PositionMm.Y / Mm, PositionMm.Z / Mm,
            1, 0, 0, 0,
        ];
    }

    public static ComponentPose FromTransform(double[] transform)
    {
        var position = new Point3(transform[9] * Mm, transform[10] * Mm, transform[11] * Mm);
        var sinY = -transform[2];
        double x, z;
        if (Math.Abs(sinY) < GimbalLimit)
        {
            x = Math.Atan2(transform[5], transform[8]);
            z = Math.Atan2(transform[1], transform[0]);
        }
        else
        {
            x = Math.Atan2(-transform[7], transform[4]);
            z = 0;
        }

        return new ComponentPose(position, new Point3(Degrees(x), Degrees(Math.Asin(Math.Clamp(sinY, -1, 1))), Degrees(z)));
    }

    // In the assembly's coordinates, from the component's own. Mm in, mm out.
    public Point3 Place(Point3 mm)
    {
        var t = ToTransform();
        return new Point3(
            (mm.X * t[0]) + (mm.Y * t[3]) + (mm.Z * t[6]) + PositionMm.X,
            (mm.X * t[1]) + (mm.Y * t[4]) + (mm.Z * t[7]) + PositionMm.Y,
            (mm.X * t[2]) + (mm.Y * t[5]) + (mm.Z * t[8]) + PositionMm.Z);
    }

    public Point3 Turn(Point3 direction)
    {
        var t = ToTransform();
        return new Point3(
            (direction.X * t[0]) + (direction.Y * t[3]) + (direction.Z * t[6]),
            (direction.X * t[1]) + (direction.Y * t[4]) + (direction.Z * t[7]),
            (direction.X * t[2]) + (direction.Y * t[5]) + (direction.Z * t[8]));
    }

    public bool Matches(ComponentPose other) =>
        new Point3(PositionMm.X - other.PositionMm.X, PositionMm.Y - other.PositionMm.Y, PositionMm.Z - other.PositionMm.Z).Length < 1e-4
        && ToTransform().Take(9).Zip(other.ToTransform().Take(9)).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);

    public override string ToString() => IsRotated ? $"at {PositionMm} mm, rotated {RotationDegrees}°" : $"at {PositionMm} mm";

    private static (double Sin, double Cos) SinCos(double degrees) => Math.SinCos(degrees * Math.PI / 180);

    private static double Degrees(double radians)
    {
        var degrees = radians * 180 / Math.PI;
        return Math.Abs(degrees) < 1e-9 ? 0 : degrees;
    }
}

public sealed record ComponentInfo(string Handle, string Name, string? Path, string Configuration, bool Fixed, bool Suppressed, string Status, ComponentPose Pose)
{
    public override string ToString()
    {
        var state = Suppressed ? "suppressed" : Fixed ? "fixed" : Status;
        return $"{Handle} {Name} [{Configuration}] {state}, {Pose}";
    }
}

// Held means mates kept the component from reaching the requested pose.
public sealed record ComponentMove(ComponentPose Before, ComponentPose After, bool Held);

public sealed record MateSpec(string Kind, string First, string Second, double? Value, string Alignment, bool Flip)
{
    private static readonly string[] Kinds = ["coincident", "concentric", "parallel", "perpendicular", "tangent", "distance", "angle", "lock"];

    public string NormalizedKind => Kind.Trim().ToLowerInvariant();

    public string NormalizedAlignment => Alignment.Trim().ToLowerInvariant();

    public void Validate()
    {
        if (!Kinds.Contains(NormalizedKind))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown mate '{Kind}'", $"Use {string.Join(", ", Kinds)}");
        }

        if (NormalizedAlignment is not ("closest" or "aligned" or "anti_aligned"))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown alignment '{Alignment}'", "Use closest, aligned or anti_aligned");
        }

        var needsValue = NormalizedKind is "distance" or "angle";
        if (needsValue && Value is not >= 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {NormalizedKind} mate needs a value of 0 or more", "Nothing was changed");
        }

        if (!needsValue && Value is not null)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {NormalizedKind} mate takes no value", "Nothing was changed");
        }
    }
}

public sealed record ComponentMoved(string Handle, string Name, ComponentPose Before, ComponentPose After);

// Unconstrained lists the components still free to move.
public sealed record MateResult(string Handle, string Name, IReadOnlyList<ComponentMoved> Moved, IReadOnlyList<string> Unconstrained);

public sealed record MateInfo(string Handle, string Name, string Kind, IReadOnlyList<string> Components, bool Suppressed, string? Error);
