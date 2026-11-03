using Datum.Core;

namespace Datum.Tests;

public class SketchSpecsTests
{
    public static TheoryData<SketchEntitySpec> ValidEntities => new()
    {
        new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, X2 = 10, Y2 = 0, Dimension = true },
        new SketchEntitySpec { Type = "centerline", X1 = 0, Y1 = 0, X2 = 0, Y2 = 10 },
        new SketchEntitySpec { Type = "rect", X1 = 0, Y1 = 0, X2 = 50, Y2 = 30, Dimension = true },
        new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 20, Height = 10 },
        new SketchEntitySpec { Type = "circle", Cx = 25, Cy = 15, Radius = 5, Dimension = true },
        new SketchEntitySpec { Type = "arc", X1 = 0, Y1 = 0, X2 = 10, Y2 = 0, X3 = 5, Y3 = 5 },
        new SketchEntitySpec { Type = "polygon", Cx = 0, Cy = 0, Radius = 8, Sides = 6, Dimension = true },
        new SketchEntitySpec { Type = "slot", X1 = 0, Y1 = 0, X2 = 30, Y2 = 0, Width = 6 },
        new SketchEntitySpec { Type = "point", X1 = 3, Y1 = 4, Dimension = true },
        new SketchEntitySpec { Type = "polyline", Points = [[0, 0], [40, 0], [40, 10]], Closed = true },
    };

    [Theory]
    [MemberData(nameof(ValidEntities))]
    public void AcceptsWellFormedEntities(SketchEntitySpec entity)
    {
        SketchSpecs.Validate([entity]);
    }

    [Theory]
    [InlineData("circle", "needs radius > 0")]
    [InlineData("polygon", "needs all of its coordinates")]
    [InlineData("blob", "needs a type: ")]
    [InlineData("", "(no type) needs a type")]
    public void NamesTheBadEntityAndWhatItNeeds(string type, string expected)
    {
        var good = new SketchEntitySpec { Type = "line", X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 };
        var bad = new SketchEntitySpec { Type = type, Cx = type == "circle" ? 1 : null, Cy = type == "circle" ? 1 : null, Radius = 0 };

        var error = Assert.Throws<DatumException>(() => SketchSpecs.Validate([good, bad]));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        Assert.StartsWith("entity 1 (", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("Nothing was drawn", error.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void PolygonsNeedThreeSides(int sides, bool valid)
    {
        var polygon = new SketchEntitySpec { Type = "polygon", Cx = 0, Cy = 0, Radius = 5, Sides = sides };

        var error = Record.Exception(() => SketchSpecs.Validate([polygon]));

        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void RejectsDegenerateAndMalformedShapes()
    {
        Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchEntitySpec { Type = "line", X1 = 1, Y1 = 1, X2 = 1, Y2 = 1 }]));
        Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchEntitySpec { Type = "polyline", Points = [[0, 0]] }]));
        Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchEntitySpec { Type = "polyline", Points = [[0, 0], [1, 2, 3]] }]));
        Assert.Throws<DatumException>(() => SketchSpecs.Validate(Array.Empty<SketchEntitySpec>()));
    }

    [Theory]
    [InlineData("arc", null)]
    [InlineData("slot", true)]
    [InlineData("polyline", false)]
    [InlineData("centerline", null)]
    public void EveryTypeCanBeDimensionedOrLeftFree(string type, bool? dimension)
    {
        var entity = new SketchEntitySpec
        {
            Type = type,
            X1 = 0,
            Y1 = 0,
            X2 = 10,
            Y2 = 0,
            X3 = 5,
            Y3 = 5,
            Width = 2,
            Points = [[0, 0], [5, 5]],
            Dimension = dimension,
        };

        SketchSpecs.Validate([entity]);
    }

    [Fact]
    public void ChecksConstraintKindsCountsAndValues()
    {
        SketchSpecs.Validate([new SketchConstraintSpec { Kind = "horizontal", Entities = ["seg#1"] }]);
        SketchSpecs.Validate([new SketchConstraintSpec { Kind = "distance", Entities = ["pt#1", "pt#2"], Value = 12.5 }]);

        Assert.Contains(
            "exactly 2 entities",
            Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchConstraintSpec { Kind = "parallel", Entities = ["seg#1"] }])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "value > 0",
            Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchConstraintSpec { Kind = "length", Entities = ["seg#1"] }])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "needs a kind",
            Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchConstraintSpec { Kind = "glue", Entities = ["seg#1"] }])).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "under defined")]
    [InlineData(3, "fully defined")]
    [InlineData(4, "over defined")]
    [InlineData(99, "status unknown")]
    public void DescribesSolidWorksSketchStatus(int status, string text)
    {
        Assert.Equal(text, SketchSpecs.DescribeStatus(status));
    }
}
