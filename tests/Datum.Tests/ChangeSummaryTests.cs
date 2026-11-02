using Datum.Core;

namespace Datum.Tests;

public class ChangeSummaryTests
{
    [Theory]
    [InlineData(24000, 23922.74, "volume 24000 → 23922.74 mm³ (-77.26)")]
    [InlineData(15000, 30000, "volume 15000 → 30000 mm³ (+15000)")]
    [InlineData(123456.789, 123457.1, "volume 123456.789 → 123457.1 mm³ (+0.311)")]
    [InlineData(0, 1000, "volume 0 → 1000 mm³ (+1000)")]
    [InlineData(24000, 24000, "volume 24000 → 24000 mm³ (+0)")]
    public void BeforeAndAfterAreShownToTheChangesPrecision(double before, double after, string expected) =>
        Assert.StartsWith(expected + " · ", new ChangeSummary(before, after, 1, []).ToString(), StringComparison.Ordinal);
}
