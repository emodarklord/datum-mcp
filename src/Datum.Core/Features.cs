using System.Globalization;

namespace Datum.Core;

// Each feature kind validates its own inputs before anything reaches SOLIDWORKS, so a bad call changes nothing.
public abstract record FeatureSpec
{
    public abstract string Kind { get; }

    // Shown with FEATURE_FAILED; says what usually fixes this kind of failure.
    public abstract string FailureHint { get; }

    public abstract void Validate();

    protected void Require(bool condition, string problem)
    {
        if (!condition)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{Kind} needs {problem}", "Nothing was changed");
        }
    }

    protected void RequireHandles(IReadOnlyList<string> handles, string prefix, string what)
    {
        Require(handles.Count > 0, $"at least one {what}");
        Require(handles.All(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), $"{what} handles ({prefix}N)");
    }
}

public sealed record ExtrudeSpec(string Sketch, double? Depth, bool Cut, string End, bool Reverse) : FeatureSpec
{
    public static readonly string[] Ends = ["blind", "through_all", "mid_plane"];

    public override string Kind => Cut ? "extruded cut" : "extrusion";

    public override string FailureHint => "Check the sketch is a closed profile; for a cut, that it overlaps the part, or try reverse";

    public override void Validate()
    {
        Require(Sketch.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a sketch handle (feat#N from sketch_end)");
        Require(Ends.Contains(End), $"end to be one of {string.Join(", ", Ends)}");
        Require(End == "through_all" || Depth is > 0, "depth > 0 (mm)");
    }
}

public sealed record RevolveSpec(string Sketch, double Angle, bool Cut) : FeatureSpec
{
    public override string Kind => Cut ? "revolved cut" : "revolve";

    public override string FailureHint => "The sketch needs one centerline as the axis, with the profile entirely on one side of it";

    public override void Validate()
    {
        Require(Sketch.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a sketch handle (feat#N from sketch_end)");
        Require(Angle is > 0 and <= 360, "angle between 0 and 360 degrees");
    }
}

public sealed record FilletSpec(IReadOnlyList<string> Edges, double Radius) : FeatureSpec
{
    public override string Kind => "fillet";

    public override string FailureHint => "Try a smaller radius; it must be less than the faces around the edge allow";

    public override void Validate()
    {
        RequireHandles(Edges, "edge#", "edge");
        Require(Radius > 0, "radius > 0 (mm)");
    }
}

public sealed record ChamferSpec(IReadOnlyList<string> Edges, double Distance, double Angle) : FeatureSpec
{
    public override string Kind => "chamfer";

    public override string FailureHint => "Try a smaller distance";

    public override void Validate()
    {
        RequireHandles(Edges, "edge#", "edge");
        Require(Distance > 0, "distance > 0 (mm)");
        Require(Angle is > 0 and < 90, "angle between 0 and 90 degrees");
    }
}

public sealed record HoleSpec(string Face, string At, double Diameter, double? Depth) : FeatureSpec
{
    public override string Kind => "hole";

    public override string FailureHint => "Put the hole fully inside the face, and use a depth the part can take";

    public Point3 Point { get; private init; }

    public override void Validate()
    {
        Require(Face.StartsWith("face#", StringComparison.OrdinalIgnoreCase), "a flat face handle (face#N)");
        Require(Diameter > 0, "diameter > 0 (mm)");
        Require(Depth is null or > 0, "depth > 0 (mm), or no depth for through all");
        var parts = At.Split(',');
        Require(
            parts.Length == 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)),
            "at as \"x,y,z\" in model mm");
    }

    public HoleSpec Parsed()
    {
        Validate();
        var parts = At.Split(',').Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        return this with { Point = new Point3(parts[0], parts[1], parts[2]) };
    }
}

public sealed record PatternSpec(string Pattern, IReadOnlyList<string> Features, string Along, int Count, double? Spacing) : FeatureSpec
{
    public override string Kind => $"{Pattern} pattern";

    public override string FailureHint => Pattern == "linear"
        ? "along must be a straight edge; check the copies stay on the part"
        : "along must be a circular edge or cylindrical face to rotate about";

    public override void Validate()
    {
        Require(Pattern is "linear" or "circular", "kind linear or circular");
        RequireHandles(Features, "feat#", "feature");
        Require(Along.StartsWith("edge#", StringComparison.OrdinalIgnoreCase) || Along.StartsWith("face#", StringComparison.OrdinalIgnoreCase), "along as an edge# or face# handle");
        Require(Count >= 2, "count of 2 or more, including the original");
        Require(Pattern == "circular" || Spacing is > 0, "spacing > 0 (mm)");
        Require(Spacing is null or > 0, "spacing > 0");
    }
}

public sealed record MirrorSpec(IReadOnlyList<string> Features, string Plane) : FeatureSpec
{
    public override string Kind => "mirror";

    public override string FailureHint => "Mirror about Front, Top, Right, a plane feat# or a flat face#";

    public override void Validate()
    {
        RequireHandles(Features, "feat#", "feature");
        Require(Plane.Length > 0, "a plane");
    }
}

public sealed record ShellSpec(double Thickness, IReadOnlyList<string> Remove, bool Outward) : FeatureSpec
{
    public override string Kind => "shell";

    public override string FailureHint => "Try a thinner wall; it must be less than half the part's smallest thickness";

    public override void Validate()
    {
        Require(Thickness > 0, "thickness > 0 (mm)");
        Require(Remove.All(h => h.StartsWith("face#", StringComparison.OrdinalIgnoreCase)), "remove as face handles (face#N)");
    }
}

// Created lists every feature the call added, newest last; a hole is a sketch and a cut.
public sealed record FeatureResult(string Handle, string Name, ChangeSummary Change, IReadOnlyList<string> Created);
