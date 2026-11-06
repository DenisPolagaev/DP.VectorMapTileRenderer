using System.Text.Json;
using DP.VectorMapTileRenderer.Mlt;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

/// <summary>
/// Decodes golden tiles produced by the Python workers (<c>workers/tiles/mlt_encoder.py</c>) and
/// compares them with the reference decoder's GeoJSON (<c>maplibre_tiles.decode_mlt_to_geojson</c>).
/// </summary>
public sealed class MltTileReaderTests
{
    public static TheoryData<string> Fixtures => new()
    {
        "weather_cells",
        "fire_incidents",
        "stations",
        "vegetation_cells",
        "mixed_geometry",
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesLayerMetadata(string fixture)
    {
        var (tile, expected) = Load(fixture);

        Assert.Single(tile.Layers);
        var layer = tile.Layers[0];
        Assert.False(string.IsNullOrEmpty(layer.Name));
        Assert.Equal(4096d, layer.Extent);
        Assert.Equal(expected.GetProperty("features").GetArrayLength(), layer.Features.Count);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesGeometry(string fixture)
    {
        var (tile, expected) = Load(fixture);
        var features = tile.Layers[0].Features;
        var expectedFeatures = expected.GetProperty("features");

        for (var index = 0; index < features.Count; index++)
        {
            var actual = features[index];
            var reference = expectedFeatures[index].GetProperty("geometry");
            var type = reference.GetProperty("type").GetString();
            var coordinates = reference.GetProperty("coordinates");

            switch (type)
            {
                case "Point":
                    Assert.Equal(VectorGeometryType.Point, actual.GeometryType);
                    Assert.Single(actual.Geometry);
                    Assert.Single(actual.Geometry[0]);
                    AssertCoordinate(coordinates, actual.Geometry[0][0]);
                    break;

                case "MultiPoint":
                    Assert.Equal(VectorGeometryType.MultiPoint, actual.GeometryType);
                    Assert.Equal(coordinates.GetArrayLength(), actual.Geometry.Count);
                    for (var point = 0; point < coordinates.GetArrayLength(); point++)
                    {
                        Assert.Single(actual.Geometry[point]);
                        AssertCoordinate(coordinates[point], actual.Geometry[point][0]);
                    }

                    break;

                case "LineString":
                    Assert.Equal(VectorGeometryType.LineString, actual.GeometryType);
                    Assert.Single(actual.Geometry);
                    AssertRing(coordinates, actual.Geometry[0], closed: false);
                    break;

                case "Polygon":
                    Assert.Equal(VectorGeometryType.Polygon, actual.GeometryType);
                    Assert.Equal(coordinates.GetArrayLength(), actual.Geometry.Count);
                    for (var ring = 0; ring < actual.Geometry.Count; ring++)
                    {
                        AssertRing(coordinates[ring], actual.Geometry[ring], closed: true);
                    }

                    break;

                case "MultiPolygon":
                {
                    Assert.Equal(VectorGeometryType.MultiPolygon, actual.GeometryType);
                    var ringIndex = 0;
                    foreach (var polygon in coordinates.EnumerateArray())
                    {
                        foreach (var ring in polygon.EnumerateArray())
                        {
                            Assert.True(ringIndex < actual.Geometry.Count, "Tile lost a MultiPolygon ring.");
                            AssertRing(ring, actual.Geometry[ringIndex], closed: true);
                            ringIndex++;
                        }
                    }

                    Assert.Equal(ringIndex, actual.Geometry.Count);
                    break;
                }

                default:
                    Assert.Fail($"Fixture {fixture} uses an unexpected geometry type {type}.");
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesProperties(string fixture)
    {
        var (tile, expected) = Load(fixture);
        var features = tile.Layers[0].Features;
        var expectedFeatures = expected.GetProperty("features");

        for (var index = 0; index < features.Count; index++)
        {
            var reference = expectedFeatures[index].GetProperty("properties");
            var actual = features[index].Attributes;

            foreach (var property in reference.EnumerateObject())
            {
                // The reference GeoJSON adds bookkeeping keys that are not tile attributes.
                if (property.Name is "_extent" or "_layer")
                {
                    continue;
                }

                Assert.True(actual.ContainsKey(property.Name), $"Fixture {fixture} lost property {property.Name}.");
                AssertEquivalent(property.Value, actual[property.Name]);
            }
        }
    }

    /// <summary>
    /// A nullable string column is the only column whose stream count precedes its Present
    /// stream; the tester used to misread it and lose the whole tile. The reference GeoJSON
    /// drops nulls, so the gaps are asserted here explicitly.
    /// </summary>
    [Fact]
    public void DecodesNullableStringColumn()
    {
        var tile = MltTileReader.Read(Fixture("vegetation_cells.mlt"));
        var features = tile.Layers[0].Features;

        Assert.Equal(6, features.Count);
        Assert.Equal("RU-1", features[1].Attributes["region_id"]);
        Assert.Equal("RU-0", features[2].Attributes["region_id"]);
        Assert.Null(features[0].Attributes["region_id"]);
        Assert.Null(features[3].Attributes["region_id"]);

        // ... next to a nullable numeric column in the same tile.
        Assert.Null(features[0].Attributes["confidence"]);
        Assert.Equal(61L, features[1].Attributes["confidence"]);
        Assert.Null(features[4].Attributes["confidence"]);
    }

    /// <summary>
    /// A layer mixing polygons and lines shares the ring/part cursors of the geometry column,
    /// so one fixture locks that layout for both the C# and the browser decoder.
    /// </summary>
    [Fact]
    public void DecodesMixedGeometryLayer()
    {
        var tile = MltTileReader.Read(Fixture("mixed_geometry.mlt"));
        var features = tile.Layers[0].Features;

        Assert.Equal(
            new[]
            {
                VectorGeometryType.Polygon,
                VectorGeometryType.LineString,
                VectorGeometryType.MultiPoint,
                VectorGeometryType.MultiPolygon,
            },
            features.Select(feature => feature.GeometryType));
        Assert.Equal(5, features[0].Geometry[0].Count);
        Assert.Equal(3, features[1].Geometry[0].Count);
        Assert.Equal(2, features[2].Geometry.Count);
        Assert.Equal(2, features[3].Geometry.Count);
    }

    [Fact]
    public void DetectsMltEncoding()
    {
        var bytes = Fixture("stations.mlt");

        Assert.NotEmpty(VectorTileReader.Read(bytes).Layers);
        Assert.Single(VectorTileReader.Read(bytes).Layers);
    }

    private static void AssertCoordinate(JsonElement expected, Point actual)
    {
        Assert.Equal(expected[0].GetDouble(), actual.X, 6);
        Assert.Equal(expected[1].GetDouble(), actual.Y, 6);
    }

    private static void AssertRing(JsonElement expected, List<Point> actual, bool closed)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        for (var point = 0; point < actual.Count; point++)
        {
            AssertCoordinate(expected[point], actual[point]);
        }

        if (closed)
        {
            // Polygon rings come back closed.
            Assert.Equal(actual[0], actual[^1]);
        }
    }

    private static void AssertEquivalent(JsonElement expected, object? actual)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), Assert.IsType<string>(actual));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), Assert.IsType<bool>(actual));
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetDouble(), Convert.ToDouble(actual, System.Globalization.CultureInfo.InvariantCulture), 5);
                break;
            case JsonValueKind.Null:
                Assert.Null(actual);
                break;
            default:
                Assert.Fail($"Unexpected property kind {expected.ValueKind}.");
                break;
        }
    }

    private static (VectorTile Tile, JsonElement Expected) Load(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".json"));

        using var document = JsonDocument.Parse(json);
        return (MltTileReader.Read(Fixture(fixture + ".mlt")), document.RootElement.Clone());
    }

    private static byte[] Fixture(string file) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
}
