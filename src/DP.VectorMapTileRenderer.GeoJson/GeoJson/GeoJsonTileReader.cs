using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO.Converters;
using NtsPoint = NetTopologySuite.Geometries.Point;

namespace DP.VectorMapTileRenderer.GeoJson;

/// <summary>Coordinate space of a GeoJSON document.</summary>
public enum GeoJsonCoordinateSpace
{
    /// <summary>WGS84 longitude/latitude degrees (standard GeoJSON, RFC 7946).</summary>
    Wgs84,

    /// <summary>Web Mercator metres (EPSG:3857).</summary>
    WebMercator,

    /// <summary>Coordinates already expressed in this tile's <c>0..Extent</c> grid.</summary>
    TileLocal
}

/// <summary>
/// A parsed GeoJSON document that can be cut into slippy-map tiles. Features are fully
/// materialized (NetTopologySuite geometries), so multi-gigabyte GeoJSON should be pre-tiled into
/// PMTiles/MBTiles; tile extraction itself is a cheap envelope pre-filter plus a clip.
/// </summary>
public sealed class GeoJsonTileSet
{
    private static readonly GeometryFactory GeometryFactory = new();
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly IReadOnlyList<IFeature> _features;
    private readonly STRtree<IFeature>? _spatialIndex;

    private GeoJsonTileSet(IReadOnlyList<IFeature> features)
    {
        _features = features;

        if (features.Count == 0)
        {
            return;
        }

        var index = new STRtree<IFeature>();
        foreach (var feature in features)
        {
            var geometry = feature.Geometry;
            if (geometry is not null && !geometry.IsEmpty)
            {
                index.Insert(geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        _spatialIndex = index;
    }

    public IReadOnlyList<IFeature> Features => _features;

    public static GeoJsonTileSet Parse(string json) =>
        Parse(System.Text.Encoding.UTF8.GetBytes(json));

    public static GeoJsonTileSet Parse(byte[] utf8Json)
    {
        var type = PeekType(utf8Json);

        var features = type switch
        {
            "FeatureCollection" => JsonSerializer
                .Deserialize<FeatureCollection>(utf8Json, SerializerOptions)?
                .Cast<IFeature>()
                .ToList() ?? [],
            "Feature" => JsonSerializer.Deserialize<IFeature>(utf8Json, SerializerOptions) is { } feature
                ? [feature]
                : [],
            _ => JsonSerializer.Deserialize<Geometry>(utf8Json, SerializerOptions) is { } geometry
                ? [new Feature(geometry, new AttributesTable())]
                : []
        };

        return new GeoJsonTileSet(features);
    }

    /// <summary>
    /// Parses a seekable stream without copying the whole document into a byte array. Features are
    /// still materialized; this only removes the duplicate raw-JSON buffer.
    /// </summary>
    public static GeoJsonTileSet Parse(Stream stream)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("The GeoJSON stream must be readable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Parse(buffer.ToArray());
        }

        var type = PeekType(stream);
        stream.Position = 0;

        var features = type switch
        {
            "FeatureCollection" => JsonSerializer
                .Deserialize<FeatureCollection>(stream, SerializerOptions)?
                .Cast<IFeature>()
                .ToList() ?? [],
            "Feature" => JsonSerializer.Deserialize<IFeature>(stream, SerializerOptions) is { } feature
                ? [feature]
                : [],
            _ => JsonSerializer.Deserialize<Geometry>(stream, SerializerOptions) is { } geometry
                ? [new Feature(geometry, new AttributesTable())]
                : []
        };

        return new GeoJsonTileSet(features);
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="Parse(Stream)"/>; useful at startup for large
    /// documents so the caller does not block a request thread on file I/O.
    /// </summary>
    public static async Task<GeoJsonTileSet> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("The GeoJSON stream must be readable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Parse(buffer.ToArray());
        }

        var type = PeekType(stream);
        stream.Position = 0;

        var features = type switch
        {
            "FeatureCollection" => (await JsonSerializer
                    .DeserializeAsync<FeatureCollection>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false))?
                .Cast<IFeature>()
                .ToList() ?? [],
            "Feature" => await JsonSerializer
                .DeserializeAsync<IFeature>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) is { } feature
                ? [feature]
                : [],
            _ => await JsonSerializer
                .DeserializeAsync<Geometry>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) is { } geometry
                ? [new Feature(geometry, new AttributesTable())]
                : []
        };

        return new GeoJsonTileSet(features);
    }

    public VectorTile CreateTile(
        int x,
        int y,
        int zoom,
        double extent = 4096,
        string layerName = "geojson",
        GeoJsonCoordinateSpace coordinateSpace = GeoJsonCoordinateSpace.Wgs84)
    {
        var clip = GeometryFactory.ToGeometry(new Envelope(0, extent, 0, extent));
        var tileEnvelope = GetTileEnvelope(x, y, zoom, extent, coordinateSpace);
        IEnumerable<IFeature> candidates = _spatialIndex is null || tileEnvelope is null
            ? _features
            : _spatialIndex.Query(tileEnvelope);
        var tile = new VectorTile();
        var layer = new VectorTileLayer { Name = layerName, Extent = extent };
        tile.Layers.Add(layer);

        foreach (var feature in candidates)
        {
            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                continue;
            }

            if (tileEnvelope is not null && !geometry.EnvelopeInternal.Intersects(tileEnvelope))
            {
                continue;
            }

            var transformed = geometry.Copy();
            transformed.Apply(new TileCoordinateFilter(x, y, zoom, extent, coordinateSpace));

            Geometry clipped;
            try
            {
                clipped = transformed.Intersection(clip);
            }
            catch (Exception exception) when (exception is TopologyException or ArgumentException)
            {
                // Invalid source geometry: render what we have instead of failing the tile.
                clipped = transformed;
            }

            if (clipped.IsEmpty)
            {
                continue;
            }

            Emit(layer, clipped, ConvertAttributes(feature.Attributes), extent);
        }

        if (layer.Features.Count == 0)
        {
            tile.Layers.Remove(layer);
        }

        return tile;
    }

    private static void Emit(VectorTileLayer layer, Geometry geometry, Dictionary<string, object?> attributes, double extent)
    {
        switch (geometry)
        {
            case NtsPoint point when IsFinite(point.Coordinate):
                AddFeature(layer, VectorGeometryType.Point, [[new Point(point.X, point.Y)]], attributes, extent);
                break;

            case MultiPoint multiPoint:
            {
                var rings = multiPoint.Geometries
                    .OfType<NtsPoint>()
                    .Where(item => IsFinite(item.Coordinate))
                    .Select(item => new List<Point> { new(item.X, item.Y) })
                    .ToList();
                if (rings.Count > 0)
                {
                    AddFeature(layer, VectorGeometryType.MultiPoint, rings, attributes, extent);
                }

                break;
            }

            case LineString line:
            {
                var ring = ToRing(line.CoordinateSequence);
                if (ring.Count > 1)
                {
                    AddFeature(layer, VectorGeometryType.LineString, [ring], attributes, extent);
                }

                break;
            }

            case MultiLineString multiLine:
            {
                var rings = multiLine.Geometries
                    .OfType<LineString>()
                    .Select(item => ToRing(item.CoordinateSequence))
                    .Where(ring => ring.Count > 1)
                    .ToList();
                if (rings.Count > 0)
                {
                    AddFeature(layer, VectorGeometryType.MultiLineString, rings, attributes, extent);
                }

                break;
            }

            case Polygon polygon:
            {
                var rings = ToRings(polygon);
                if (rings.Count > 0)
                {
                    AddFeature(layer, VectorGeometryType.Polygon, rings, attributes, extent);
                }

                break;
            }

            case MultiPolygon multiPolygon:
            {
                var rings = multiPolygon.Geometries
                    .OfType<Polygon>()
                    .SelectMany(ToRings)
                    .ToList();
                if (rings.Count > 0)
                {
                    AddFeature(layer, VectorGeometryType.MultiPolygon, rings, attributes, extent);
                }

                break;
            }

            // MultiPoint/MultiLineString/MultiPolygon derive from GeometryCollection, so the
            // generic collection case must stay last.
            case GeometryCollection collection:
                foreach (var part in collection.Geometries)
                {
                    Emit(layer, part, attributes, extent);
                }

                break;
        }
    }

    private static void AddFeature(
        VectorTileLayer layer,
        VectorGeometryType geometryType,
        List<List<Point>> rings,
        Dictionary<string, object?> attributes,
        double extent)
    {
        var feature = new VectorTileFeature { GeometryType = geometryType, Extent = extent };
        foreach (var (key, value) in attributes)
        {
            feature.Attributes[key] = value;
        }

        foreach (var ring in rings)
        {
            feature.Geometry.Add(ring);
        }

        layer.Features.Add(feature);
    }

    private static List<List<Point>> ToRings(Polygon polygon)
    {
        var rings = new List<List<Point>>(polygon.NumInteriorRings + 1);
        AddRing(rings, polygon.ExteriorRing);
        for (var index = 0; index < polygon.NumInteriorRings; index++)
        {
            AddRing(rings, polygon.GetInteriorRingN(index));
        }

        return rings;
    }

    private static void AddRing(List<List<Point>> rings, LineString ring)
    {
        var points = ToRing(ring.CoordinateSequence);
        if (points.Count < 3)
        {
            return;
        }

        if (points[0] != points[^1])
        {
            points.Add(points[0]);
        }

        rings.Add(points);
    }

    private static List<Point> ToRing(CoordinateSequence sequence)
    {
        var ring = new List<Point>(sequence.Count);
        for (var index = 0; index < sequence.Count; index++)
        {
            var coordinate = sequence.GetCoordinate(index);
            if (IsFinite(coordinate))
            {
                ring.Add(new Point(coordinate.X, coordinate.Y));
            }
        }

        return ring;
    }

    private static Dictionary<string, object?> ConvertAttributes(IAttributesTable attributes)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in attributes.GetNames())
        {
            result[name] = ConvertValue(attributes[name]);
        }

        return result;
    }

    private static object? ConvertValue(object? value) => value switch
    {
        null => null,
        JsonElement element => ConvertJsonElement(element),
        _ => value
    };

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(property => property.Name, property => ConvertJsonElement(property.Value), StringComparer.Ordinal),
        _ => element.ToString()
    };

    private static bool IsFinite(Coordinate? coordinate) =>
        coordinate is not null && double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new GeoJsonConverterFactory());
        return options;
    }

    /// <summary>Reads only the root <c>type</c> property without materializing the document.</summary>
    private static string? PeekType(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("type"u8))
                {
                    return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // Truncated/streamed preview: fall back to the default root type.
        }

        return null;
    }

    private static string? PeekType(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        var type = PeekType(buffer.AsSpan(0, read));
        stream.Position = 0;

        // Standard GeoJSON puts "type" first; if a huge properties block precedes it, assume the
        // most common root instead of buffering the whole file just to inspect it.
        return type ?? "FeatureCollection";
    }

    private static Envelope? GetTileEnvelope(
        int x,
        int y,
        int zoom,
        double extent,
        GeoJsonCoordinateSpace coordinateSpace)
    {
        if (coordinateSpace == GeoJsonCoordinateSpace.TileLocal)
        {
            return new Envelope(0, extent, 0, extent);
        }

        var tiles = 1 << zoom;
        if (coordinateSpace == GeoJsonCoordinateSpace.WebMercator)
        {
            const double originShift = Math.PI * 6378137;
            var size = 2 * originShift;
            return new Envelope(
                (x / (double)tiles * size) - originShift,
                ((x + 1) / (double)tiles * size) - originShift,
                originShift - ((y + 1) / (double)tiles * size),
                originShift - (y / (double)tiles * size));
        }

        // Slippy (XYZ) bounds, matching the tile coordinate conversion in TileCoordinateFilter.
        return new Envelope(
            (x / (double)tiles * 360) - 180,
            ((x + 1) / (double)tiles * 360) - 180,
            TileYToLatitude(y + 1, tiles),
            TileYToLatitude(y, tiles));
    }

    private static double TileYToLatitude(int tileY, int tiles)
    {
        var mercator = Math.PI * (1 - (2.0 * tileY / tiles));
        return Math.Atan(Math.Sinh(mercator)) * 180 / Math.PI;
    }

    private sealed class TileCoordinateFilter(
        int tileX,
        int tileY,
        int zoom,
        double extent,
        GeoJsonCoordinateSpace coordinateSpace) : ICoordinateFilter
    {
        private const double MaxLatitude = 85.05112878;
        private const double OriginShift = Math.PI * 6378137;

        public void Filter(Coordinate coordinate)
        {
            if (coordinateSpace == GeoJsonCoordinateSpace.TileLocal)
            {
                return;
            }

            coordinate.X = LongitudeToTileX(coordinate.X);
            coordinate.Y = LatitudeToTileY(coordinate.Y);
        }

        private double LongitudeToTileX(double longitude)
        {
            var worldX = coordinateSpace == GeoJsonCoordinateSpace.WebMercator
                ? (longitude + OriginShift) / (2 * OriginShift)
                : (longitude + 180.0) / 360.0;

            return ((worldX * (1L << zoom)) - tileX) * extent;
        }

        private double LatitudeToTileY(double latitude)
        {
            double worldY;
            if (coordinateSpace == GeoJsonCoordinateSpace.WebMercator)
            {
                worldY = (OriginShift - latitude) / (2 * OriginShift);
            }
            else
            {
                var clamped = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
                var radians = clamped * Math.PI / 180.0;
                var mercator = Math.Log(Math.Tan((Math.PI / 4) + (radians / 2)));
                worldY = (1 - (mercator / Math.PI)) / 2;
            }

            return ((worldY * (1L << zoom)) - tileY) * extent;
        }
    }
}

/// <summary>Convenience one-shot GeoJSON → tile decoder.</summary>
public static class GeoJsonTileReader
{
    public static VectorTile Read(
        string json,
        int x,
        int y,
        int zoom,
        double extent = 4096,
        GeoJsonCoordinateSpace coordinateSpace = GeoJsonCoordinateSpace.Wgs84) =>
        GeoJsonTileSet.Parse(json).CreateTile(x, y, zoom, extent, coordinateSpace: coordinateSpace);

    public static VectorTile Read(
        byte[] utf8Json,
        int x,
        int y,
        int zoom,
        double extent = 4096,
        GeoJsonCoordinateSpace coordinateSpace = GeoJsonCoordinateSpace.Wgs84) =>
        GeoJsonTileSet.Parse(utf8Json).CreateTile(x, y, zoom, extent, coordinateSpace: coordinateSpace);

    public static VectorTile Read(
        Stream stream,
        int x,
        int y,
        int zoom,
        double extent = 4096,
        GeoJsonCoordinateSpace coordinateSpace = GeoJsonCoordinateSpace.Wgs84) =>
        GeoJsonTileSet.Parse(stream).CreateTile(x, y, zoom, extent, coordinateSpace: coordinateSpace);
}
