using Datum.Core;

namespace Datum.Tests;

public class EntityQueryTests
{
    private static readonly FaceInfo Top = new("face#1", SurfaceKind.Plane, 5000, new(0, 0, 1), new(0, 0, 20), null, "Boss-Extrude1");
    private static readonly FaceInfo Bottom = new("face#2", SurfaceKind.Plane, 5000.5, new(0, 0, -1), new(0, 0, 0), null, "Boss-Extrude1");
    private static readonly FaceInfo Tilted = new("face#3", SurfaceKind.Plane, 300, new(0, 0.7071, 0.7071), new(0, 30, 10), null, "Chamfer1");
    private static readonly FaceInfo Hole = new("face#4", SurfaceKind.Cylinder, 1200, null, new(10, 0, 10), 5, "Cut-Extrude1");
    private static readonly FaceInfo[] Faces = [Hole, Top, Tilted, Bottom];

    private static readonly EdgeInfo[] Edges =
    [
        new("edge#1", CurveKind.Line, 100, null, ["face#1", "face#5"], new(1, 0, 0)),
        new("edge#2", CurveKind.Circle, 31.4, 5, ["face#1", "face#4"]),
        new("edge#3", CurveKind.Line, 50, null, ["face#2", "face#5"], new(0, 0, -1)),
    ];

    [Fact]
    public void SortsFacesLargestFirst()
    {
        Assert.Equal(["face#2", "face#1", "face#4", "face#3"], Handles(EntityQuery.Faces(Faces, null, null, null, 0, 50)));
    }

    [Theory]
    [InlineData("plane", new[] { "face#2", "face#1", "face#3" })]
    [InlineData("CYLINDER", new[] { "face#4" })]
    [InlineData("sphere", new string[0])]
    public void FiltersFacesBySurface(string surface, string[] expected)
    {
        Assert.Equal(expected, Handles(EntityQuery.Faces(Faces, surface, null, null, 0, 50)));
    }

    [Theory]
    [InlineData("+z", "face#1")]
    [InlineData("z", "face#1")]
    [InlineData("-Z", "face#2")]
    [InlineData("0,1,1", "face#3")]
    [InlineData("0, 2, 2", "face#3")]
    public void FiltersFacesByNormal(string normal, string expected)
    {
        Assert.Equal([expected], Handles(EntityQuery.Faces(Faces, null, normal, null, 0, 50)));
    }

    [Fact]
    public void NormalToleranceIsAboutOneDegree()
    {
        var almost = Top with { Handle = "face#9", Normal = new(0, Math.Sin(0.5 * Math.PI / 180), Math.Cos(0.5 * Math.PI / 180)) };
        var off = Top with { Handle = "face#10", Normal = new(0, Math.Sin(2 * Math.PI / 180), Math.Cos(2 * Math.PI / 180)) };

        Assert.Equal(["face#9"], Handles(EntityQuery.Faces([almost, off], null, "+z", null, 0, 50)));
    }

    [Fact]
    public void FiltersFacesByFeature()
    {
        Assert.Equal(["face#4"], Handles(EntityQuery.Faces(Faces, null, null, "cut-extrude1", 0, 50)));
    }

    [Fact]
    public void FiltersEdgesByCurveAndFace()
    {
        Assert.Equal(["edge#1", "edge#3"], EntityQuery.Edges(Edges, "line", null, null, 0, 50).Items.Select(e => e.Handle));
        Assert.Equal(["edge#1", "edge#2"], EntityQuery.Edges(Edges, null, "FACE#1", null, 0, 50).Items.Select(e => e.Handle));
    }

    [Fact]
    public void FiltersStraightEdgesByDirectionEitherWay()
    {
        Assert.Equal(["edge#3"], EntityQuery.Edges(Edges, null, null, "z", 0, 50).Items.Select(e => e.Handle));
        Assert.Equal(["edge#3"], EntityQuery.Edges(Edges, null, null, "+z", 0, 50).Items.Select(e => e.Handle));
        Assert.Equal(["edge#1"], EntityQuery.Edges(Edges, null, null, "-x", 0, 50).Items.Select(e => e.Handle));
    }

    [Fact]
    public void PagesResults()
    {
        var page = EntityQuery.Faces(Faces, null, null, null, 1, 2);

        Assert.Equal(["face#1", "face#4"], Handles(page));
        Assert.Equal(4, page.Total);
        Assert.Empty(EntityQuery.Faces(Faces, null, null, null, 10, 2).Items);
    }

    [Theory]
    [InlineData("blob", null)]
    [InlineData(null, "up")]
    [InlineData(null, "0,0,0")]
    public void RejectsUnknownFilters(string? surface, string? normal)
    {
        var error = Assert.Throws<DatumException>(() => EntityQuery.Faces(Faces, surface, normal, null, 0, 50));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public void DescribesAxisDirectionsBriefly()
    {
        Assert.Equal("+z", EntityQuery.DescribeDirection(new(0, 0, 3)));
        Assert.Equal("-x", EntityQuery.DescribeDirection(new(-1, 0, 0)));
        Assert.Equal("0,0.7071,0.7071", EntityQuery.DescribeDirection(new(0, 1, 1)));
    }

    private static string[] Handles(EntityPage<FaceInfo> page) => [.. page.Items.Select(f => f.Handle)];
}
