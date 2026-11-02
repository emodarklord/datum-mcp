using Datum.Core;

namespace Datum.Tests;

public class SolidWorksRevisionTests
{
    [Theory]
    [InlineData("34.4.1", 2026, 4, 1)]
    [InlineData("29.0.0", 2021, 0, 0)]
    [InlineData("32.5.0", 2024, 5, 0)]
    public void ParsesRevisionNumber(string revision, int year, int servicePack, int minor)
    {
        var parsed = SolidWorksRevision.Parse(revision);

        Assert.Equal(year, parsed.Year);
        Assert.Equal(servicePack, parsed.ServicePack);
        Assert.Equal(minor, parsed.Minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("34")]
    [InlineData("34.x.1")]
    [InlineData("SOLIDWORKS 2026")]
    public void RejectsMalformedRevision(string revision)
    {
        Assert.False(SolidWorksRevision.TryParse(revision, out _));
        Assert.Throws<FormatException>(() => SolidWorksRevision.Parse(revision));
    }

    [Theory]
    [InlineData("28.5.0", false)]
    [InlineData("29.0.0", true)]
    [InlineData("34.4.1", true)]
    public void SupportsSolidWorks2021AndLater(string revision, bool supported)
    {
        Assert.Equal(supported, SolidWorksRevision.Parse(revision).IsSupported);
    }

    [Fact]
    public void FormatsAsReleaseName()
    {
        Assert.Equal("SOLIDWORKS 2026 SP4.1", SolidWorksRevision.Parse("34.4.1").ToString());
    }
}
