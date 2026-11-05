using System.Text.Json;
using DP.VectorMapTileRenderer.Mlt;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

/// <summary>
/// Decodes golden tiles produced by the Python workers (<c>workers/tiles/tile_format.py</c>) and
/// compares them with the reference decoder's GeoJSON (<c>maplibre_tiles.decode_mlt_to_geojson</c>).
/// </summary>
public sealed class MltTileReaderTests
{
    public static TheoryData<string> Fixtures => new() { "weather_cells", "fire_incidents", "stations" };

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
                    Assert.Equal(coordinates[0].GetDouble(), actual.Geometry[0][0].X, 6);
                    Assert.Equal(coordinates[1].GetDouble(), actual.Geometry[0][0].Y, 6);
                    break;

                case "Polygon":
                    Assert.Equal(VectorGeometryType.Polygon, actual.GeometryType);
                    Assert.Equal(coordinates.GetArrayLength(), actual.Geometry.Count);
                    for (var ring = 0; ring < actual.Geometry.Count; ring++)
                    {
                        var expectedRing = coordinates[ring];
                        Assert.Equal(expectedRing.GetArrayLength(), actual.Geometry[ring].Count);
                        for (var point = 0; point < actual.Geometry[ring].Count; point++)
                        {
                            Assert.Equal(expectedRing[point][0].GetDouble(), actual.Geometry[ring][point].X, 6);
                            Assert.Equal(expectedRing[point][1].GetDouble(), actual.Geometry[ring][point].Y, 6);
                        }

                        // Polygon rings come back closed.
                        Assert.Equal(actual.Geometry[ring][0], actual.Geometry[ring][^1]);
                    }

                    break;

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

    [Fact]
    public void DetectsMltEncoding()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stations.mlt"));

        Assert.Equal(VectorTileEncoding.Mlt, VectorTileReader.Detect(bytes));
        Assert.Single(VectorTileReader.Read(bytes).Layers);
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
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var tile = File.ReadAllBytes(Path.Combine(directory, fixture + ".mlt"));
        var json = File.ReadAllText(Path.Combine(directory, fixture + ".json"));

        using var document = JsonDocument.Parse(json);
        return (MltTileReader.Read(tile), document.RootElement.Clone());
    }
}
