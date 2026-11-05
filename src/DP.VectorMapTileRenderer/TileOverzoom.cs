namespace DP.VectorMapTileRenderer;

/// <summary>
/// Computes which already-present parent tile covers a higher-zoom request and the sub-rectangle
/// that must be stretched to the requested tile (the "overzoom" behaviour).
/// </summary>
public static class TileOverzoom
{
    /// <summary>
    /// Returns the parent tile coordinates and the requested tile's region inside that parent,
    /// expressed in the parent's tile-coordinate units (<c>0..extent</c>).
    /// </summary>
    public static bool TryGetParent(
        int x,
        int y,
        int zoom,
        int parentZoom,
        double extent,
        out int parentX,
        out int parentY,
        out Rect region)
    {
        parentX = 0;
        parentY = 0;
        region = default;

        if (parentZoom < 0 || parentZoom >= zoom || extent <= 0)
        {
            return false;
        }

        var delta = zoom - parentZoom;
        if (delta > 30)
        {
            return false;
        }

        var factor = 1 << delta;
        parentX = x / factor;
        parentY = y / factor;
        var localX = x - (parentX * factor);
        var localY = y - (parentY * factor);
        var size = extent / factor;
        region = new Rect(localX * size, localY * size, size, size);
        return true;
    }

    /// <summary>Stretches a parent tile into the requested tile and marks it as overzoomed.</summary>
    public static VectorTile Apply(VectorTile parentTile, int x, int y, int zoom, int parentZoom)
    {
        if (parentZoom < 0 || parentZoom >= zoom)
        {
            return parentTile;
        }

        var delta = zoom - parentZoom;
        if (delta > 30)
        {
            return parentTile;
        }

        var factor = 1 << delta;
        var localX = x - ((x / factor) * factor);
        var localY = y - ((y / factor) * factor);

        var result = new VectorTile { IsOverZoomed = true };

        foreach (var layer in parentTile.Layers)
        {
            var newLayer = new VectorTileLayer { Name = layer.Name, Extent = layer.Extent };

            foreach (var feature in layer.Features)
            {
                var extent = feature.Extent > 0
                    ? feature.Extent
                    : layer.Extent > 0
                        ? layer.Extent
                        : 4096;

                var left = (double)localX * extent / factor;
                var top = (double)localY * extent / factor;
                var size = extent / factor;

                var newFeature = new VectorTileFeature
                {
                    Id = feature.Id,
                    GeometryType = feature.GeometryType,
                    Extent = extent
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
                        newRing.Add(new Point(
                            Utils.ConvertRange(point.X, left, left + size, 0, extent),
                            Utils.ConvertRange(point.Y, top, top + size, 0, extent)));
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
