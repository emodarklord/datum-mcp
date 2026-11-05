using Datum.Core;

namespace Datum.Tests;

public sealed class LabelPlacementTests
{
    [Fact]
    public void TheModelsCornersLandOnTheContentBox()
    {
        var extents = new ViewExtents(-0.025, 0.025, -0.015, 0.015);
        var crop = new ImageCrop(Left: 100, Top: 50, StepX: 2, StepY: 2, ContentLeft: 200, ContentTop: 100, ContentWidth: 500, ContentHeight: 300);

        var topLeft = LabelPlacement.Project(extents, crop, -0.025, 0.015);
        var bottomRight = LabelPlacement.Project(extents, crop, 0.025, -0.015);

        Assert.Equal((50, 25), (Math.Round(topLeft.X), Math.Round(topLeft.Y)));
        Assert.Equal((300, 175), (Math.Round(bottomRight.X), Math.Round(bottomRight.Y)));
    }

    [Fact]
    public void AFlatEdgeOnViewStillPlacesPoints()
    {
        var extents = new ViewExtents(-0.025, 0.025, 0, 0);
        var crop = new ImageCrop(0, 0, 1, 1, 100, 200, 500, 2);

        var middle = LabelPlacement.Project(extents, crop, 0, 0);

        Assert.Equal((350, 201), (Math.Round(middle.X), Math.Round(middle.Y)));
    }

    [Fact]
    public void CropsReportWhereTheContentWas()
    {
        var render = Raster.Filled(100, 100, 255, 255, 255);
        for (var y = 20; y < 40; y++)
        {
            for (var x = 30; x < 70; x++)
            {
                render.Set(x, y, 0, 0, 0);
            }
        }

        var crop = Raster.CropsFor([render], 40, 40)[0];

        Assert.Equal((30, 20, 40, 20), (crop.ContentLeft, crop.ContentTop, crop.ContentWidth, crop.ContentHeight));
    }

    [Fact]
    public void DrawsADotAtThePointAndABoxBesideIt()
    {
        var image = Raster.Filled(100, 100, 120, 120, 120);

        LabelPlacement.Draw(image, [new ImageLabel("face#8", 40, 60)]);

        Assert.Equal((200, 0, 0), image.Pixel(40, 60));
        Assert.Equal((120, 120, 120), image.Pixel(90, 90));
        Assert.Equal((255, 255, 255), Whitest(image));
    }

    [Fact]
    public void EachDigitDrawsDifferently()
    {
        var drawings = Enumerable.Range(0, 10).Select(digit =>
        {
            var image = Raster.Filled(60, 60, 120, 120, 120);
            LabelPlacement.Draw(image, [new ImageLabel($"edge#{digit}", 20, 40)]);
            return Convert.ToHexString(image.Rgb);
        });

        Assert.Equal(10, drawings.Distinct().Count());
    }

    [Fact]
    public void LabelsAtOnePointDoNotCoverEachOther()
    {
        var image = Raster.Filled(200, 200, 120, 120, 120);

        LabelPlacement.Draw(image, [new ImageLabel("face#1", 100, 100), new ImageLabel("face#2", 100, 100)]);

        var red = Enumerable.Range(0, 200).SelectMany(y => Enumerable.Range(0, 200).Select(x => (x, y))).Where(p => image.Pixel(p.x, p.y) == (200, 0, 0)).ToList();
        Assert.True(red.Max(p => p.y) - red.Min(p => p.y) > 30, "the second box should sit below the first");
    }

    [Fact]
    public void ALabelAtTheImageEdgeStaysInside()
    {
        var image = Raster.Filled(50, 50, 120, 120, 120);

        LabelPlacement.Draw(image, [new ImageLabel("face#123456", 49, 1)]);

        Assert.Equal((200, 0, 0), image.Pixel(49, 1));
    }

    private static (byte R, byte G, byte B) Whitest(Raster image) =>
        Enumerable.Range(0, image.Height).SelectMany(y => Enumerable.Range(0, image.Width).Select(x => image.Pixel(x, y))).MaxBy(p => p.R + p.G + p.B);
}
