using System.Buffers.Binary;
using System.Text;

namespace DP.VectorMapTileRenderer.Mvt;

/// <summary>
/// Dependency-free Mapbox Vector Tile (MVT 2.x) reader.
/// </summary>
public static class MvtTileReader
{
    private const int MoveTo = 1;
    private const int LineTo = 2;
    private const int ClosePath = 7;
    private const int DefaultExtent = 4096;

    public static VectorTile Read(ReadOnlySpan<byte> tile)
    {
        var result = new VectorTile();
        var cursor = tile;
        while (!cursor.IsEmpty)
        {
            var tag = ReadVarint(ref cursor);
            var field = tag >> 3;
            var wire = (int)(tag & 0x7);
            if (field == 3 && wire == 2)
            {
                result.Layers.Add(ReadLayer(ReadLengthPrefixed(ref cursor)));
                continue;
            }

            Skip(ref cursor, wire);
        }

        return result;
    }

    private static VectorTileLayer ReadLayer(ReadOnlySpan<byte> layer)
    {
        var name = string.Empty;
        var extent = DefaultExtent;
        var keys = new List<string>();
        var values = new List<object?>();
        var rawFeatures = new List<(int Type, ulong? Id, uint[] Tags, uint[] Geometry)>();
        var cursor = layer;

        while (!cursor.IsEmpty)
        {
            var tag = ReadVarint(ref cursor);
            var field = tag >> 3;
            var wire = (int)(tag & 0x7);
            switch (field)
            {
                case 1 when wire == 2:
                    name = ReadString(ref cursor);
                    break;
                case 2 when wire == 2:
                    rawFeatures.Add(ReadFeature(ReadLengthPrefixed(ref cursor)));
                    break;
                case 3 when wire == 2:
                    keys.Add(ReadString(ref cursor));
                    break;
                case 4 when wire == 2:
                    values.Add(ReadValue(ReadLengthPrefixed(ref cursor)));
                    break;
                case 5 when wire == 0:
                    extent = (int)ReadVarint(ref cursor);
                    break;
                default:
                    Skip(ref cursor, wire);
                    break;
            }
        }

        if (extent <= 0)
        {
            extent = DefaultExtent;
        }

        var result = new VectorTileLayer { Name = name, Extent = extent };

        foreach (var (type, id, tags, geometry) in rawFeatures)
        {
            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index + 1 < tags.Length; index += 2)
            {
                var keyIndex = (int)tags[index];
                var valueIndex = (int)tags[index + 1];
                if (keyIndex < keys.Count && valueIndex < values.Count)
                {
                    attributes[keys[keyIndex]] = values[valueIndex];
                }
            }

            var geometryType = type switch
            {
                1 => VectorGeometryType.Point,
                2 => VectorGeometryType.LineString,
                3 => VectorGeometryType.Polygon,
                _ => VectorGeometryType.Point
            };

            var feature = new VectorTileFeature
            {
                Id = id is null ? null : unchecked((long)id.Value),
                GeometryType = geometryType,
                Extent = extent
            };

            foreach (var (key, value) in attributes)
            {
                feature.Attributes[key] = value;
            }

            foreach (var ring in DecodeGeometry(type, geometry))
            {
                feature.Geometry.Add(ring);
            }

            if (feature.Geometry.Count > 0)
            {
                result.Features.Add(feature);
            }
        }

        return result;
    }

    private static (int Type, ulong? Id, uint[] Tags, uint[] Geometry) ReadFeature(ReadOnlySpan<byte> feature)
    {
        var type = 0;
        ulong? id = null;
        uint[] tags = [];
        uint[] geometry = [];
        var cursor = feature;
        while (!cursor.IsEmpty)
        {
            var tag = ReadVarint(ref cursor);
            var field = tag >> 3;
            var wire = (int)(tag & 0x7);
            switch (field)
            {
                case 1 when wire == 0:
                    id = ReadVarint(ref cursor);
                    break;
                case 2:
                    tags = ReadPackedUInt32(ref cursor, wire);
                    break;
                case 3 when wire == 0:
                    type = (int)ReadVarint(ref cursor);
                    break;
                case 4:
                    geometry = ReadPackedUInt32(ref cursor, wire);
                    break;
                default:
                    Skip(ref cursor, wire);
                    break;
            }
        }

        return (type, id, tags, geometry);
    }

    private static List<List<Point>> DecodeGeometry(int type, uint[] commands)
    {
        var rings = new List<List<Point>>();
        var current = new List<Point>();
        var index = 0;
        var x = 0;
        var y = 0;

        while (index < commands.Length)
        {
            var parameter = commands[index++];
            var command = (int)(parameter & 7);
            var count = (int)(parameter >> 3);

            if (command == ClosePath)
            {
                PushRing(rings, ref current, closeRing: type == 3);
                continue;
            }

            if (command is not (MoveTo or LineTo))
            {
                break;
            }

            for (var item = 0; item < count && index + 1 < commands.Length; item++)
            {
                x += ZigZag((int)commands[index++]);
                y += ZigZag((int)commands[index++]);

                if (command == MoveTo && type != 1)
                {
                    PushRing(rings, ref current, closeRing: type == 3);
                }

                current.Add(new Point(x, y));
            }
        }

        PushRing(rings, ref current, closeRing: type == 3);
        return rings;
    }

    private static void PushRing(List<List<Point>> rings, ref List<Point> current, bool closeRing)
    {
        if (current.Count == 0)
        {
            return;
        }

        if (closeRing && current[0] != current[^1])
        {
            current.Add(current[0]);
        }

        rings.Add(current);
        current = [];
    }

    private static uint[] ReadPackedUInt32(ref ReadOnlySpan<byte> cursor, int wire)
    {
        if (wire == 0)
        {
            return [(uint)ReadVarint(ref cursor)];
        }

        if (wire != 2)
        {
            Skip(ref cursor, wire);
            return [];
        }

        var payload = ReadLengthPrefixed(ref cursor);
        var values = new List<uint>();
        while (!payload.IsEmpty)
        {
            values.Add((uint)ReadVarint(ref payload));
        }

        return [.. values];
    }

    private static object? ReadValue(ReadOnlySpan<byte> value)
    {
        var cursor = value;
        while (!cursor.IsEmpty)
        {
            var tag = ReadVarint(ref cursor);
            var field = tag >> 3;
            var wire = (int)(tag & 0x7);
            switch (field)
            {
                case 1 when wire == 2:
                    return ReadString(ref cursor);
                case 2 when wire == 5:
                    return BitConverter.Int32BitsToSingle((int)ReadFixed32(ref cursor));
                case 3 when wire == 1:
                    return BitConverter.Int64BitsToDouble((long)ReadFixed64(ref cursor));
                case 4 when wire == 0:
                    return (long)ReadVarint(ref cursor);
                case 5 when wire == 0:
                    return ReadVarint(ref cursor);
                case 6 when wire == 0:
                    return ZigZag((int)ReadVarint(ref cursor));
                case 7 when wire == 0:
                    return ReadVarint(ref cursor) != 0;
                default:
                    Skip(ref cursor, wire);
                    break;
            }
        }

        return null;
    }

    private static string ReadString(ref ReadOnlySpan<byte> cursor)
    {
        var payload = ReadLengthPrefixed(ref cursor);
        return Encoding.UTF8.GetString(payload);
    }

    private static ReadOnlySpan<byte> ReadLengthPrefixed(ref ReadOnlySpan<byte> cursor)
    {
        var length = (int)ReadVarint(ref cursor);
        if (length < 0 || length > cursor.Length)
        {
            throw new InvalidDataException("Invalid protobuf length.");
        }

        var payload = cursor[..length];
        cursor = cursor[length..];
        return payload;
    }

    private static ulong ReadVarint(ref ReadOnlySpan<byte> cursor)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (cursor.IsEmpty)
            {
                throw new InvalidDataException("Truncated protobuf varint.");
            }

            var current = cursor[0];
            cursor = cursor[1..];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("Protobuf varint is longer than 64 bits.");
            }
        }
    }

    private static void Skip(ref ReadOnlySpan<byte> cursor, int wire)
    {
        switch (wire)
        {
            case 0:
                _ = ReadVarint(ref cursor);
                break;
            case 1:
                cursor = cursor.Length >= 8
                    ? cursor[8..]
                    : throw new InvalidDataException("Truncated fixed64.");
                break;
            case 2:
                _ = ReadLengthPrefixed(ref cursor);
                break;
            case 5:
                cursor = cursor.Length >= 4
                    ? cursor[4..]
                    : throw new InvalidDataException("Truncated fixed32.");
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
        }
    }

    private static uint ReadFixed32(ref ReadOnlySpan<byte> cursor)
    {
        if (cursor.Length < 4)
        {
            throw new InvalidDataException("Truncated fixed32.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(cursor);
        cursor = cursor[4..];
        return value;
    }

    private static ulong ReadFixed64(ref ReadOnlySpan<byte> cursor)
    {
        if (cursor.Length < 8)
        {
            throw new InvalidDataException("Truncated fixed64.");
        }

        var value = BinaryPrimitives.ReadUInt64LittleEndian(cursor);
        cursor = cursor[8..];
        return value;
    }

    private static int ZigZag(int value) => (value >> 1) ^ -(value & 1);
}
