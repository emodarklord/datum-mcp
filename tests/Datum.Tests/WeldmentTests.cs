using Datum.Core;

namespace Datum.Tests;

public sealed class WeldmentTests
{
    private static Point3 P(double x, double y) => new(x, y, 0);

    [Fact]
    public void ARectangleIsOneRun()
    {
        MemberSegment[] loop = [new(0, P(0, 0), P(1, 0)), new(1, P(1, 0), P(1, 1)), new(2, P(1, 1), P(0, 1)), new(3, P(0, 1), P(0, 0))];

        Assert.Equal([0, 1, 2, 3], Assert.Single(WeldmentPaths.Chains(loop)));
    }

    [Fact]
    public void AJunctionSplitsTheRuns()
    {
        MemberSegment[] tee = [new(0, P(0, 0), P(1, 0)), new(1, P(1, 0), P(2, 0)), new(2, P(1, 0), P(1, 1))];

        Assert.Equal(3, WeldmentPaths.Chains(tee).Count);
    }

    [Fact]
    public void SeparateLinesAreSeparateRuns()
    {
        MemberSegment[] rails = [new(0, P(0, 0), P(1, 0)), new(1, P(0, 1), P(1, 1))];

        Assert.Equal(2, WeldmentPaths.Chains(rails).Count);
    }

    [Theory]
    [InlineData("sketch1", "a.sldlfp", "40", "a path sketch handle")]
    [InlineData("feat#1", " ", "40", "a profile file")]
    [InlineData("feat#1", "a.sldlfp", "", "a size")]
    public void AMemberNeedsAPathProfileAndSize(string path, string profile, string size, string expected)
    {
        var refused = Assert.Throws<DatumException>(() => new StructuralMemberSpec(path, profile, size).Validate());

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyCantTrimItself()
    {
        Assert.Throws<DatumException>(() => new WeldTrimSpec(["a"], ["A"]).Validate());
    }
}
