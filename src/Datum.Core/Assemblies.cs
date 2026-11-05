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

    public double[] Apply(double[] pointMetres)
    {
        var t = ToTransform();
        return
        [
            (pointMetres[0] * t[0]) + (pointMetres[1] * t[3]) + (pointMetres[2] * t[6]) + t[9],
            (pointMetres[0] * t[1]) + (pointMetres[1] * t[4]) + (pointMetres[2] * t[7]) + t[10],
            (pointMetres[0] * t[2]) + (pointMetres[1] * t[5]) + (pointMetres[2] * t[8]) + t[11],
        ];
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

// Held means mates or a fix kept the component from reaching the requested pose.
public sealed record ComponentMove(ComponentPose Before, ComponentPose After, bool Held);
