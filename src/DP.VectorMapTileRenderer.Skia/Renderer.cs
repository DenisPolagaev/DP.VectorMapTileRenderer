using SkiaSharp;
using DP.VectorMapTileRenderer.Sources;

namespace DP.VectorMapTileRenderer;

/// <summary>Renders a single slippy-map tile from a <see cref="Style"/> onto an <see cref="ICanvas"/>.</summary>
public static class Renderer
{
    private enum VisualLayerType
    {
        Vector,
        Raster
    }

    private sealed class VisualLayer
    {
        public required VisualLayerType Type { get; init; }

        public Stream? RasterStream { get; init; }

        public VectorTileFeature? VectorTileFeature { get; init; }

        public List<List<Point>>? Geometry { get; init; }

        public required Brush Brush { get; init; }
    }

    /// <summary>Renders one tile and returns its bitmap, or <see langword="null"/> when a vector tile is missing.</summary>
    public static async Task<SKBitmap?> RenderAsync(
        Style style,
        ICanvas canvas,
        int x,
        int y,
        double zoom,
        double sizeX = 512,
        double sizeY = 512,
        double scale = 1,
        IReadOnlyCollection<string>? whiteListLayers = null,
        CancellationToken cancellationToken = default)
    {
        var vectorTileCache = new Dictionary<Source, VectorTile?>();
        var rasterTileCache = new Dictionary<Source, Stream>();
        var categorizedVectorLayers = new Dictionary<string, List<VectorTileLayer>>(StringComparer.Ordinal);
        var whiteList = whiteListLayers is null
            ? null
            : new HashSet<string>(whiteListLayers, StringComparer.Ordinal);

        double actualZoom = zoom;
        if (sizeX < 1024)
        {
            actualZoom = zoom - Math.Log(1024 / sizeX, 2);
        }

        sizeX *= scale;
        sizeY *= scale;

        canvas.ClipOverflow = false;
        canvas.StartDrawing(sizeX, sizeY);

        var visualLayers = new List<VisualLayer>();

        try
        {
            foreach (var layer in style.Layers)
            {
                if (whiteList is not null && layer.Type != "background" && layer.SourceLayer.Length > 0
                    && !whiteList.Contains(layer.SourceLayer))
                {
                    continue;
                }

                if (layer.Type == "background")
                {
                    foreach (var brush in style.GetStyleByType("background", actualZoom, scale))
                    {
                        canvas.DrawBackground(brush);
                    }

                    continue;
                }

                if (layer.Source is not { } source)
                {
                    continue;
                }

                if (source.Type == "vector")
                {
                    if (!vectorTileCache.TryGetValue(source, out var tile))
                    {
                        if (source.Provider is not IVectorTileSource vectorProvider)
                        {
                            continue;
                        }

                        tile = await vectorProvider
                            .GetVectorTile(x, y, (int)zoom, cancellationToken)
                            .ConfigureAwait(false);
                        if (tile is null)
                        {
                            return null;
                        }

                        if (tile.IsOverZoomed)
                        {
                            canvas.ClipOverflow = true;
                        }

                        Normalize(tile, sizeX, sizeY);

                        foreach (var tileLayer in tile.Layers)
                        {
                            if (!categorizedVectorLayers.TryGetValue(tileLayer.Name, out var list))
                            {
                                list = [];
                                categorizedVectorLayers[tileLayer.Name] = list;
                            }

                            list.Add(tileLayer);
                        }

                        vectorTileCache[source] = tile;
                    }

                    if (!categorizedVectorLayers.TryGetValue(layer.SourceLayer, out var tileLayers))
                    {
                        continue;
                    }

                    foreach (var tileLayer in tileLayers)
                    {
                        foreach (var feature in tileLayer.Features)
                        {
                            var attributes = new Dictionary<string, object?>(feature.Attributes, StringComparer.Ordinal)
                            {
                                ["$type"] = GeometryTypeName(feature.GeometryType),
                                ["$id"] = feature.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                                ["$zoom"] = actualZoom
                            };

                            if (!style.ValidateLayer(layer, actualZoom, attributes))
                            {
                                continue;
                            }

                            var brush = style.ParseStyle(layer, scale, attributes);
                            if (!brush.Paint.Visibility)
                            {
                                continue;
                            }

                            visualLayers.Add(new VisualLayer
                            {
                                Type = VisualLayerType.Vector,
                                VectorTileFeature = feature,
                                Geometry = feature.Geometry,
                                Brush = brush
                            });
                        }
                    }
                }
                else if (source.Type == "raster")
                {
                    if (source.Provider is not ITileSource rasterProvider)
                    {
                        continue;
                    }

                    if (!rasterTileCache.TryGetValue(source, out var stream))
                    {
                        stream = await rasterProvider
                            .GetTile(x, y, (int)zoom, cancellationToken)
                            .ConfigureAwait(false);
                        rasterTileCache[source] = stream;
                    }

                    if (!style.ValidateLayer(layer, actualZoom, null))
                    {
                        continue;
                    }

                    var brush = style.ParseStyle(layer, scale, []);
                    if (!brush.Paint.Visibility)
                    {
                        continue;
                    }

                    visualLayers.Add(new VisualLayer
                    {
                        Type = VisualLayerType.Raster,
                        RasterStream = stream,
                        Brush = brush
                    });
                }
            }

            // Sort once: fills/lines are painted bottom-up, labels top-down over the same order.
            var orderedLayers = visualLayers.OrderBy(item => item.Brush.ZIndex).ToArray();
            DrawGeometry(orderedLayers, canvas);
            DrawLabels(orderedLayers, canvas);

            return canvas.FinishDrawing();
        }
        finally
        {
            foreach (var stream in rasterTileCache.Values)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Renders a tile and caches the PNG in <paramref name="cachePath"/>; subsequent calls with the same
    /// style/size/scale return the cached bitmap.
    /// </summary>
    public static async Task<SKBitmap?> RenderCachedAsync(
        string cachePath,
        Style style,
        ICanvas canvas,
        int x,
        int y,
        double zoom,
        double sizeX = 512,
        double sizeY = 512,
        double scale = 1,
        IReadOnlyCollection<string>? whiteListLayers = null,
        CancellationToken cancellationToken = default)
    {
        var layerString = whiteListLayers is null ? string.Empty : string.Join(",-", whiteListLayers);
        var key = Utils.Sha256($"{style.Hash}|{sizeX}|{sizeY}|{scale}|{layerString}")[..12];
        var fileName = $"{x}x{y}-{zoom}-{key}.png";
        var path = Path.Combine(cachePath, fileName);

        Directory.CreateDirectory(cachePath);
        if (File.Exists(path))
        {
            try
            {
                var cached = SKBitmap.Decode(path);
                if (cached is not null)
                {
                    return cached;
                }
            }
            catch (Exception)
            {
                // A truncated cache entry must not break the render; fall through and repaint it.
            }
        }

        var bitmap = await RenderAsync(
                style, canvas, x, y, zoom, sizeX, sizeY, scale, whiteListLayers, cancellationToken)
            .ConfigureAwait(false);

        if (bitmap is null)
        {
            return null;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, data.ToArray(), cancellationToken).ConfigureAwait(false);
        return bitmap;
    }

    private static void DrawGeometry(IReadOnlyList<VisualLayer> visualLayers, ICanvas canvas)
    {
        foreach (var layer in visualLayers)
        {
            if (layer.Type == VisualLayerType.Raster)
            {
                if (layer.RasterStream is not null)
                {
                    canvas.DrawImage(layer.RasterStream, layer.Brush);
                }

                continue;
            }

            var feature = layer.VectorTileFeature!;
            var geometry = layer.Geometry!;
            var brush = layer.Brush;

            switch (feature.GeometryType)
            {
                case VectorGeometryType.Point:
                case VectorGeometryType.MultiPoint:
                    foreach (var ring in geometry)
                    {
                        foreach (var point in ring)
                        {
                            canvas.DrawPoint(point, brush);
                        }
                    }

                    break;

                case VectorGeometryType.LineString:
                case VectorGeometryType.MultiLineString:
                    foreach (var line in geometry)
                    {
                        canvas.DrawLineString(line, brush);
                    }

                    break;

                case VectorGeometryType.Polygon:
                case VectorGeometryType.MultiPolygon:
                    canvas.DrawPolygon(geometry, brush);
                    break;

                default:
                    canvas.DrawUnknown(geometry, brush);
                    break;
            }
        }
    }

    private static void DrawLabels(IReadOnlyList<VisualLayer> visualLayers, ICanvas canvas)
    {
        for (var index = visualLayers.Count - 1; index >= 0; index--)
        {
            var layer = visualLayers[index];
            if (layer.Type != VisualLayerType.Vector || string.IsNullOrEmpty(layer.Brush.Text))
            {
                continue;
            }

            var feature = layer.VectorTileFeature!;
            var geometry = layer.Geometry!;

            switch (feature.GeometryType)
            {
                case VectorGeometryType.Point:
                case VectorGeometryType.MultiPoint:
                    foreach (var ring in geometry)
                    {
                        foreach (var point in ring)
                        {
                            canvas.DrawText(point, layer.Brush);
                        }
                    }

                    break;

                case VectorGeometryType.LineString:
                case VectorGeometryType.MultiLineString:
                    foreach (var line in geometry)
                    {
                        canvas.DrawTextOnPath(line, layer.Brush);
                    }

                    break;
            }
        }
    }

    private static void Normalize(VectorTile tile, double sizeX, double sizeY)
    {
        foreach (var layer in tile.Layers)
        {
            foreach (var feature in layer.Features)
            {
                var extent = feature.Extent > 0 ? feature.Extent : layer.Extent;
                if (extent <= 0)
                {
                    extent = 4096;
                }

                foreach (var ring in feature.Geometry)
                {
                    for (var index = 0; index < ring.Count; index++)
                    {
                        var point = ring[index];
                        ring[index] = new Point(point.X / extent * sizeX, point.Y / extent * sizeY);
                    }
                }
            }
        }
    }

    private static string GeometryTypeName(VectorGeometryType type) => type switch
    {
        VectorGeometryType.Point or VectorGeometryType.MultiPoint => "Point",
        VectorGeometryType.LineString or VectorGeometryType.MultiLineString => "LineString",
        VectorGeometryType.Polygon or VectorGeometryType.MultiPolygon => "Polygon",
        _ => "Unknown"
    };
}
