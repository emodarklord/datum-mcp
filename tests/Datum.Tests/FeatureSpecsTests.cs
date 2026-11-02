using Datum.Core;

namespace Datum.Tests;

public class FeatureSpecsTests
{
    public static TheoryData<FeatureSpec> Valid => new()
    {
        new ExtrudeSpec("feat#3", 10, Cut: false, "blind", Reverse: false),
        new ExtrudeSpec("feat#3", null, Cut: true, "through_all", Reverse: false),
        new RevolveSpec("feat#3", 360, Cut: false),
        new FilletSpec(["edge#1", "edge#2"], 2),
        new ChamferSpec(["edge#1"], 1, 45),
        new HoleSpec("face#7", "25,15,10", 6, null),
        new PatternSpec("linear", ["feat#9"], "edge#4", 3, 12),
        new PatternSpec("circular", ["feat#9"], "face#5", 6, null),
        new MirrorSpec(["feat#9"], "Right"),
        new ShellSpec(2, ["face#7"], Outward: false),
    };

    public static TheoryData<FeatureSpec, string> Invalid => new()
    {
        { new ExtrudeSpec("Sketch1", 10, false, "blind", false), "extrusion needs a sketch handle" },
        { new ExtrudeSpec("feat#3", null, false, "blind", false), "extrusion needs depth > 0" },
        { new ExtrudeSpec("feat#3", 5, false, "sideways", false), "extrusion needs end to be one of" },
        { new RevolveSpec("feat#3", 400, false), "revolve needs angle between 0 and 360" },
        { new FilletSpec([], 2), "fillet needs at least one edge" },
        { new FilletSpec(["face#1"], 2), "fillet needs edge handles" },
        { new FilletSpec(["edge#1"], 0), "fillet needs radius > 0" },
        { new ChamferSpec(["edge#1"], 1, 90), "chamfer needs angle between 0 and 90" },
        { new HoleSpec("edge#1", "0,0,0", 5, null), "hole needs a flat face handle" },
        { new HoleSpec("face#1", "0,0", 5, null), "hole needs at as \"x,y,z\"" },
        { new PatternSpec("linear", ["feat#1"], "edge#1", 1, 5), "linear pattern needs count of 2 or more" },
        { new PatternSpec("linear", ["feat#1"], "edge#1", 3, null), "linear pattern needs spacing > 0" },
        { new PatternSpec("spiral", ["feat#1"], "edge#1", 3, 5), "spiral pattern needs kind linear or circular" },
        { new ShellSpec(0, [], false), "shell needs thickness > 0" },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void AcceptsWellFormedFeatures(FeatureSpec spec)
    {
        spec.Validate();
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void ExplainsWhatIsMissing(FeatureSpec spec, string expected)
    {
        var error = Assert.Throws<DatumException>(spec.Validate);

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.StartsWith(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal("Nothing was changed", error.Hint);
    }

    [Fact]
    public void ParsesTheHolePoint()
    {
        Assert.Equal(new Point3(25, 15.5, -3), new HoleSpec("face#1", "25, 15.5, -3", 6, null).Parsed().Point);
    }
}
