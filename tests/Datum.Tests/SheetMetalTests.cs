using Datum.Core;

namespace Datum.Tests;

public sealed class SheetMetalTests
{
    private const string Header = "0\nSECTION\n2\nENTITIES\n";
    private const string Footer = "0\nENDSEC\n0\nEOF\n";

    private static string Line(double x1, double y1, double x2, double y2) => $"0\nLINE\n8\n0\n10\n{x1}\n20\n{y1}\n11\n{x2}\n21\n{y2}\n";

    [Fact]
    public void AnOutlineIsMeasuredFromItsLines()
    {
        Assert.Equal((180d, 60d), Dxf.Extents(Header + Line(0, 0, 180, 0) + Line(180, 0, 180, 60) + Footer));
    }

    [Fact]
    public void AnInchFileIsReportedInMm()
    {
        var inches = "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n1\n0\nENDSEC\n" + Header + Line(0, 0, 10, 2) + Footer;

        Assert.Equal((254d, 50.8d), Dxf.Extents(inches));
    }

    [Fact]
    public void ACircleCountsToItsEdge()
    {
        Assert.Equal((20d, 20d), Dxf.Extents(Header + "0\nCIRCLE\n8\n0\n10\n50\n20\n50\n40\n10\n" + Footer));
    }

    [Fact]
    public void AnArcCountsOnlyTheQuadrantsItPasses()
    {
        var (width, height) = Dxf.Extents(Header + "0\nARC\n8\n0\n10\n0\n20\n0\n40\n10\n50\n0\n51\n90\n" + Footer)!.Value;

        Assert.Equal(10, width, 6);
        Assert.Equal(10, height, 6);
    }

    [Fact]
    public void AnEmptyFileHasNoOutline()
    {
        Assert.Null(Dxf.Extents(Header + Footer));
    }

    [Fact]
    public void ANegativeBendRadiusIsRefused()
    {
        Assert.Throws<DatumException>(() => new SheetBaseSpec("feat#1", 2, -1, null).Validate());
    }

    [Fact]
    public void ABendNeedsARealAngle()
    {
        Assert.Throws<DatumException>(() => new SheetBendSpec("feat#1", 180, 2, false).Validate());
    }

    [Fact]
    public void AHemNeedsEdges()
    {
        Assert.Throws<DatumException>(() => new SheetHemSpec([], 10, 0).Validate());
    }
}
