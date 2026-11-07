using Datum.Core;

namespace Datum.Tests;

public sealed class TrialTests
{
    private static ModelInfo Model(double volume, double height) =>
        new("p", DocumentType.Part, null, volume / 1000, volume, 0, default, new Box(default, new Point3(10, height, 10)), 1);

    [Fact]
    public void TheDiffShowsWhatChanged()
    {
        var report = TrialDiff.Report(Model(1000, 10), Model(2000, 20));

        Assert.Contains("Volume 1000 → 2000 mm³", report, StringComparison.Ordinal);
        Assert.Contains("Box 10 x 10 x 10 → 10 x 20 x 10 mm", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2010, "PASS")]
    [InlineData(2500, "FAIL")]
    public void ATargetPassesWithinItsTolerance(double target, string expected)
    {
        Assert.StartsWith(expected, TrialDiff.Targets(Model(2000, 20), target, null, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void AGrowthLineSaysWhereThePartRuns()
    {
        Assert.Equal(" · part now runs 0,0,0 to 10,20,10 mm", Box.Growth(Model(1, 10).Box, Model(1, 20).Box));
    }

    [Fact]
    public void NoChangeSaysNothing()
    {
        Assert.Equal("", Box.Growth(Model(1, 10).Box, Model(1, 10).Box));
    }
}
