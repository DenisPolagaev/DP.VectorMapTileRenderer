namespace DP.VectorMapTileRenderer;

/// <summary>The six geometry types of the vector-tile data model.</summary>
public enum VectorGeometryType
{
    Point = 0,
    LineString = 1,
    Polygon = 2,
    MultiPoint = 3,
    MultiLineString = 4,
    MultiPolygon = 5
}

/// <summary>
/// One decoded tile: an ordered set of layers. The model is shared by MLT, MVT, GeoJSON and any
/// custom source, so the style engine and renderer do not know the encoding.
/// </summary>
public sealed class VectorTile
{
    public bool IsOverZoomed { get; set; }

    public List<VectorTileLayer> Layers { get; } = [];

    public VectorTile ApplyExtent(Rect extent)
    {
        var result = new VectorTile { IsOverZoomed = IsOverZoomed };

        foreach (var layer in Layers)
        {
            var newLayer = new VectorTileLayer { Name = layer.Name, Extent = layer.Extent };
            foreach (var feature in layer.Features)
            {
                var newFeature = new VectorTileFeature
                {
                    Id = feature.Id,
                    Extent = feature.Extent,
                    GeometryType = feature.GeometryType
                };

                foreach (var (key, value) in feature.Attributes)
                {
                    newFeature.Attributes[key] = value;
                }

                foreach (var ring in feature.Geometry)
                {
                    var newRing = new List<Point>(ring.Count);
                    foreach (var point in ring)
                    {
                        var newX = Utils.ConvertRange(point.X, extent.Left, extent.Right, 0, feature.Extent);
                        var newY = Utils.ConvertRange(point.Y, extent.Top, extent.Bottom, 0, feature.Extent);
                        newRing.Add(new Point(newX, newY));
                    }

                    newFeature.Geometry.Add(newRing);
                }

                newLayer.Features.Add(newFeature);
            }

            result.Layers.Add(newLayer);
        }

        return result;
    }
}

public sealed class VectorTileLayer
{
    public required string Name { get; init; }

    /// <summary>Tile grid size the feature coordinates are quantized to (typically 4096).</summary>
    public double Extent { get; init; } = 4096;

    public List<VectorTileFeature> Features { get; } = [];
}

public sealed class VectorTileFeature
{
    public long? Id { get; init; }

    public required VectorGeometryType GeometryType { get; init; }

    /// <summary>Extent the <see cref="Geometry"/> coordinates are quantized to.</summary>
    public required double Extent { get; init; }

    public Dictionary<string, object?> Attributes { get; } = [];

    /// <summary>
    /// Rings in tile coordinates. A point feature is one ring of one point, a polygon is
    /// already closed (last point repeats the first), a multi geometry lists every part in order.
    /// </summary>
    public List<List<Point>> Geometry { get; } = [];
}
