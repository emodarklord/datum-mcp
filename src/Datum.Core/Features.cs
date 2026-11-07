using System.Globalization;

namespace Datum.Core;

// Each feature kind validates its own inputs before anything reaches SOLIDWORKS, so a bad call changes nothing.
public abstract record FeatureSpec
{
    public abstract string Kind { get; }

    // Shown with FEATURE_FAILED; says what usually fixes this kind of failure.
    public abstract string FailureHint { get; }

    // Planes and axes add no material, so an unchanged volume isn't a failure for them.
    public virtual bool ChangesVolume => true;

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

// A round section needs no profile sketch: Diameter alone gives a bar, and Wall makes it a tube.
public sealed record SweepSpec(string Path, string? Profile, double? Diameter, double? Wall, bool Merge) : FeatureSpec
{
    public override string Kind => "sweep";

    public override string FailureHint => "A profile must start on the path and be square to it. The path can't turn tighter than the section's radius.";

    public override void Validate()
    {
        Require(Path.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a path sketch handle (feat#N from sketch_end)");
        Require(Profile is null != Diameter is null, "either a profile sketch or a diameter, not both");
        Require(Profile is null || Profile.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a profile sketch handle (feat#N from sketch_end)");
        Require(Diameter is null or > 0, "diameter > 0 (mm)");
        Require(Wall is null || (Diameter is { } outside && Wall > 0 && Wall < outside / 2), "wall between 0 and half the diameter (mm), with a diameter");
    }
}

public sealed record LoftSpec(IReadOnlyList<string> Profiles, IReadOnlyList<string> Guides, bool Merge) : FeatureSpec
{
    public override string Kind => "loft";

    public override string FailureHint => "Profiles must each be closed, on separate planes, in order along the loft. Each guide must touch every profile.";

    public override void Validate()
    {
        Require(Profiles.Count >= 2, "at least two profile sketches, in order");
        Require(Profiles.Concat(Guides).All(handle => handle.StartsWith("feat#", StringComparison.OrdinalIgnoreCase)), "sketch handles (feat#N from sketch_end)");
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

public sealed record RibSpec(string Sketch, double Thickness, bool BothSides, bool Flip) : FeatureSpec
{
    public override string Kind => "rib";

    public override string FailureHint => "A rib sketch is an open line whose ends reach towards the walls, on a plane through the part. Try flip.";

    public override void Validate()
    {
        Require(Sketch.StartsWith("feat#", StringComparison.OrdinalIgnoreCase), "a sketch handle (feat#N from sketch_end)");
        Require(Thickness > 0, "thickness > 0 (mm)");
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

public sealed record PlaneSpec(string From, double? Offset, double? Angle, string? Through) : FeatureSpec
{
    public override string Kind => "plane";

    public override bool ChangesVolume => false;

    public override string FailureHint => "An angled plane needs a straight edge that lies in or crosses the reference plane";

    public override void Validate()
    {
        Require(From.Length > 0, "a plane or flat face to start from");
        Require(Offset is null != Angle is null, "either offset (mm) or angle (degrees), not both");
        Require(Angle is null || (Through?.StartsWith("edge#", StringComparison.OrdinalIgnoreCase) ?? false), "through as an edge# handle for an angled plane");
        Require(Angle is null or (> 0 and < 180), "an angle between 0 and 180 degrees");
    }
}

public sealed record AxisSpec(string First, string? Second) : FeatureSpec
{
    public override string Kind => "axis";

    public override bool ChangesVolume => false;

    public override string FailureHint => "Use one cylindrical face, or two planes that aren't parallel";

    public override void Validate()
    {
        Require(First.Length > 0, "a cylindrical face or a plane");
        Require(Second is not null || First.StartsWith("face#", StringComparison.OrdinalIgnoreCase), "a second plane unless first is a cylindrical face");
    }
}

// Created lists every feature the call added, newest last; a hole is a sketch and a cut.
public sealed record FeatureResult(string Handle, string Name, ChangeSummary Change, IReadOnlyList<string> Created);
