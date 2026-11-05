using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class ClippingTests
{
    [Fact]
    public void ClipsPolylineToBounds()
    {
        var line = new List<Point> { new(-10, 5), new(10, 5), new(30, 5) };
        var bounds = new Rect(0, 0, 20, 20);

        var clipped = LineClipper.ClipPolyline(line, bounds);

        Assert.NotNull(clipped);
        Assert.Equal(new Point(0, 5), clipped[0]);
        Assert.Equal(new Point(20, 5), clipped[^1]);
    }

    [Fact]
    public void ReturnsNullForLineOutsideBounds()
    {
        var line = new List<Point> { new(-10, -10), new(-5, -10) };

        Assert.Null(LineClipper.ClipPolyline(line, new Rect(0, 0, 20, 20)));
    }

    [Fact]
    public void ClipsPolygonToBounds()
    {
        var ring = new List<Point> { new(-10, -10), new(30, -10), new(30, 30), new(-10, 30), new(-10, -10) };

        var clipped = PolygonClipper.Clip(ring, new Rect(0, 0, 20, 20));

        Assert.NotNull(clipped);
        Assert.Equal(4, clipped.Count);
        Assert.All(clipped, point =>
        {
            Assert.InRange(point.X, 0, 20);
            Assert.InRange(point.Y, 0, 20);
        });
    }

    [Fact]
    public void ApplyExtentRewritesCoordinates()
    {
        var tile = new VectorTile();
        var layer = new VectorTileLayer { Name = "test", Extent = 8 };
        var feature = new VectorTileFeature { GeometryType = VectorGeometryType.Point, Extent = 8 };
        feature.Geometry.Add([new Point(2, 2)]);
        layer.Features.Add(feature);
        tile.Layers.Add(layer);

        var result = tile.ApplyExtent(new Rect(0, 0, 4, 4));

        Assert.Equal(new Point(4, 4), result.Layers[0].Features[0].Geometry[0][0]);
        Assert.Equal(new Point(2, 2), tile.Layers[0].Features[0].Geometry[0][0]);
    }

    [Fact]
    public void OffsetsLineToTheLeft()
    {
        var line = LineOffsetter.Offset([new Point(0, 0), new Point(10, 0)], 2);

        Assert.Equal(2, line.Count);
        Assert.Equal(0, line[0].X, 6);
        Assert.Equal(-2, line[0].Y, 6);
        Assert.Equal(10, line[1].X, 6);
        Assert.Equal(-2, line[1].Y, 6);
    }

    [Fact]
    public void MercatorRoundTripsTileCoordinates()
    {
        var mercator = new GlobalMercator();
        var tile = mercator.LatLonToTile(47.37, 8.54, 14);

        Assert.InRange(tile.X, 0, (1 << 14) - 1);
        Assert.InRange(tile.Y, 0, (1 << 14) - 1);
        Assert.Equal(tile, mercator.QuadTreeToTile(mercator.QuadTree(tile.X, tile.Y, 14), 14));
        Assert.Equal((1 << 14) - 1 - tile.Y, mercator.GoogleTile(tile.X, tile.Y, 14).Y);
    }
}
