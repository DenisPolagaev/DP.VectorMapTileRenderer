using System.Text;
using DP.VectorMapTileRenderer.GeoJson;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class GeoJsonTests
{
    private const string Json = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "kind": "point", "name": "origin" },
              "geometry": { "type": "Point", "coordinates": [0, 0] }
            },
            {
              "type": "Feature",
              "properties": { "kind": "line" },
              "geometry": { "type": "LineString", "coordinates": [[-20, -10], [20, 10]] }
            },
            {
              "type": "Feature",
              "properties": { "kind": "area" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[-180, -90], [180, -90], [180, 90], [-180, 90], [-180, -90]]]
              }
            }
          ]
        }
        """;

    [Fact]
    public void MapsLonLatToTileCoordinates()
    {
        var tile = GeoJsonTileReader.Read(Json, 0, 0, 0, extent: 4096);

        var layer = Assert.Single(tile.Layers);
        Assert.Equal("geojson", layer.Name);
        Assert.Equal(3, layer.Features.Count);

        var point = layer.Features[0];
        Assert.Equal(VectorGeometryType.Point, point.GeometryType);
        Assert.Equal("origin", point.Attributes["name"]);
        Assert.Equal(2048, point.Geometry[0][0].X, 3);
        Assert.Equal(2048, point.Geometry[0][0].Y, 3);
    }

    [Fact]
    public void ClipsGeometryToTileBounds()
    {
        var tile = GeoJsonTileReader.Read(Json, 0, 0, 0, extent: 4096);
        var area = tile.Layers[0].Features[2];

        Assert.Equal(VectorGeometryType.Polygon, area.GeometryType);
        Assert.All(area.Geometry, ring => Assert.All(ring, point =>
        {
            Assert.InRange(point.X, 0, 4096);
            Assert.InRange(point.Y, 0, 4096);
        }));

        // A world-sized polygon becomes the tile rectangle.
        Assert.Contains(area.Geometry[0], point => Math.Abs(point.X) < 0.001 || Math.Abs(point.X - 4096) < 0.001);
    }

    [Fact]
    public async Task SourceReturnsTileAndRawDocument()
    {
        var bytes = Encoding.UTF8.GetBytes(Json);
        var source = new GeoJsonTileSource(bytes) { Extent = 512, LayerName = "custom" };

        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);
        Assert.NotNull(tile);
        Assert.Equal("custom", Assert.Single(tile!.Layers).Name);

        await using var raw = await source.GetTile(0, 0, 0, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(raw);
        Assert.Contains("FeatureCollection", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParsesSingleFeatureAndBareGeometry()
    {
        var feature = GeoJsonTileReader.Read(
            """{"type":"Feature","properties":{"id":7},"geometry":{"type":"Point","coordinates":[10,20]}}""",
            0, 0, 0);

        Assert.Equal(7d, Convert.ToDouble(feature.Layers[0].Features[0].Attributes["id"]));

        var geometry = GeoJsonTileReader.Read(
            """{"type":"LineString","coordinates":[[-10,0],[10,0]]}""",
            0, 0, 0);

        Assert.Equal(VectorGeometryType.LineString, geometry.Layers[0].Features[0].GeometryType);
    }

    [Fact]
    public void SkipsPointsOutsideTile()
    {
        var tile = GeoJsonTileReader.Read(
            """{"type":"Point","coordinates":[-100, 0]}""",
            1, 0, 1);

        Assert.Empty(tile.Layers);
    }

    [Fact]
    public void HandlesMultiGeometries()
    {
        var tile = GeoJsonTileReader.Read(
            """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{},"geometry":{"type":"MultiPoint","coordinates":[[-10,0],[10,0]]}},
              {"type":"Feature","properties":{},"geometry":{"type":"MultiLineString","coordinates":[[[-20,0],[0,0]],[[0,0],[20,0]]]}}
            ]}
            """,
            0, 0, 0);

        var features = tile.Layers[0].Features;
        Assert.Equal(VectorGeometryType.MultiPoint, features[0].GeometryType);
        Assert.Equal(2, features[0].Geometry.Count);
        Assert.Equal(VectorGeometryType.MultiLineString, features[1].GeometryType);
        Assert.Equal(2, features[1].Geometry.Count);
    }

    [Fact]
    public void FiltersLargeDocumentByTileEnvelope()
    {
        var builder = new StringBuilder("""{"type":"FeatureCollection","features":[""");
        for (var index = 0; index < 5000; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(
                """{"type":"Feature","properties":{"i":""" + index +
                """},"geometry":{"type":"Point","coordinates":[100,""" +
                (index % 80) + "]}}");
        }

        builder.Append(
            """,{"type":"Feature","properties":{"i":-1},"geometry":{"type":"Point","coordinates":[0.1,-0.1]}}]}""");

        // z10/512/512 covers a small box around 0..0.35 lon and 0..-0.34 lat.
        var tile = GeoJsonTileReader.Read(builder.ToString(), 512, 512, 10);

        var feature = Assert.Single(Assert.Single(tile.Layers).Features);
        Assert.Equal(-1L, Convert.ToInt64(feature.Attributes["i"]));
    }

    [Fact]
    public async Task ReadsFromFilePathAndReopensTheDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-geojson-{Guid.NewGuid():N}.geojson");
        await File.WriteAllTextAsync(path, Json, TestContext.Current.CancellationToken);

        try
        {
            var source = new GeoJsonTileSource(path);
            var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

            Assert.NotNull(tile);
            Assert.Equal(3, Assert.Single(tile!.Layers).Features.Count);

            await using var raw = await source.GetTile(0, 0, 0, TestContext.Current.CancellationToken);
            using var reader = new StreamReader(raw);
            Assert.Contains(
                "FeatureCollection",
                await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SupportsConcurrentTileExtraction()
    {
        var set = GeoJsonTileSet.Parse(Json);

        var tiles = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(
            () => set.CreateTile(index % 2, index % 2, 1),
            TestContext.Current.CancellationToken)));

        Assert.All(tiles, tile => Assert.NotNull(tile));
    }

    [Fact]
    public async Task CreateAsyncLoadsDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-geojson-async-{Guid.NewGuid():N}.geojson");
        await File.WriteAllTextAsync(path, Json, TestContext.Current.CancellationToken);

        try
        {
            var source = await GeoJsonTileSource.CreateAsync(path, TestContext.Current.CancellationToken);
            var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

            Assert.NotNull(tile);
            Assert.Equal(3, Assert.Single(tile!.Layers).Features.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsWebMercatorCoordinates()
    {
        var tile = GeoJsonTileReader.Read(
            """{"type":"Point","coordinates":[0, 0]}""",
            0, 0, 0,
            coordinateSpace: GeoJsonCoordinateSpace.WebMercator);

        var point = tile.Layers[0].Features[0].Geometry[0][0];
        Assert.Equal(2048, point.X, 3);
        Assert.Equal(2048, point.Y, 3);
    }

    [Fact]
    public void ReadsTileLocalCoordinates()
    {
        var tile = GeoJsonTileReader.Read(
            """{"type":"Point","coordinates":[100, 200]}""",
            0, 0, 0,
            extent: 4096,
            coordinateSpace: GeoJsonCoordinateSpace.TileLocal);

        var point = tile.Layers[0].Features[0].Geometry[0][0];
        Assert.Equal(100, point.X, 3);
        Assert.Equal(200, point.Y, 3);
    }
}
