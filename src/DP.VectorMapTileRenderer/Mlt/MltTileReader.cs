using System.Text;

namespace DP.VectorMapTileRenderer.Mlt;

/// <summary>Raised when a tile does not follow the MLT v1 byte layout.</summary>
public sealed class MltFormatException(string message) : Exception(message);

/// <summary>Reads MapLibre Tile (MLT) v1 archives into <see cref="VectorTile"/> instances.</summary>
/// <remarks>
/// Supported subset: frames with tag <c>0x01</c>; columns Id/LongId/Geometry/Bool/Int32/UInt32/
/// Int64/UInt64/Float/Double/String (+ nullable variants); logical encodings None/Delta/Delta-rle/
/// Componentwise-delta/Rle; physical FixedWidth/VarInt. SIMD-FastPFOR, FSST and tessellated
/// geometry are rejected on purpose: the tile workers pin the encoder to the plain subset
/// (see <c>workers/tiles/tile_format.py</c>), so a decoder never needs them.
/// </remarks>
public static class MltTileReader
{
    private const byte LayerFrameTag = 0x01;

    private enum ColumnType
    {
        Id = 0,
        OptId = 1,
        LongId = 2,
        OptLongId = 3,
        Geometry = 4,
        Bool = 10,
        OptBool = 11,
        Int32 = 16,
        OptInt32 = 17,
        UInt32 = 18,
        OptUInt32 = 19,
        Int64 = 20,
        OptInt64 = 21,
        UInt64 = 22,
        OptUInt64 = 23,
        Float = 24,
        OptFloat = 25,
        Double = 26,
        OptDouble = 27,
        String = 28,
        OptString = 29,
        SharedDict = 30
    }

    private sealed class ColumnMetadata
    {
        public required ColumnType Type { get; init; }

        public string? Name { get; init; }

        public List<(ColumnType Type, string Name)> Children { get; } = [];
    }

    public static VectorTile Read(ReadOnlySpan<byte> tile)
    {
        var cursor = new MltCursor(tile);
        var result = new VectorTile();

        while (!cursor.IsEmpty)
        {
            var size = cursor.ReadVarInt32();
            var tag = cursor.ReadByte();
            var body = cursor.Take(size - 1);

            if (tag == LayerFrameTag)
            {
                result.Layers.Add(ReadLayer(body));
            }
            // Unknown frame tags are skipped by design (the size field makes that safe).
        }

        return result;
    }

    private static VectorTileLayer ReadLayer(ReadOnlySpan<byte> body)
    {
        var cursor = new MltCursor(body);
        var name = cursor.ReadString();
        if (name.Length == 0)
        {
            throw new MltFormatException("MLT layer name is empty.");
        }

        var extent = cursor.ReadVarInt();
        if (extent == 0)
        {
            throw new MltFormatException($"MLT layer '{name}' declares extent 0.");
        }

        var columnCount = cursor.ReadVarInt32();
        var metadata = new List<ColumnMetadata>(columnCount);
        for (var index = 0; index < columnCount; index++)
        {
            metadata.Add(ReadColumnMetadata(ref cursor));
        }

        var geometryColumn = metadata.FindIndex(column => column.Type == ColumnType.Geometry);
        if (geometryColumn < 0)
        {
            throw new MltFormatException($"MLT layer '{name}' has no geometry column.");
        }

        var featureCount = -1;
        var columns = new List<ColumnData>(columnCount);
        for (var index = 0; index < columnCount; index++)
        {
            var column = ReadColumnData(ref cursor, metadata[index]);
            columns.Add(column);
            if (column.Count > featureCount)
            {
                featureCount = column.Count;
            }
        }

        if (featureCount < 0)
        {
            throw new MltFormatException($"MLT layer '{name}' declares no feature count.");
        }

        var layer = new VectorTileLayer { Name = name, Extent = extent };
        var geometry = columns[geometryColumn].Geometry!;
        var idColumn = columns.FindIndex(column => column.Ids is not null);

        for (var featureIndex = 0; featureIndex < featureCount; featureIndex++)
        {
            var feature = new VectorTileFeature
            {
                GeometryType = geometry.Types[featureIndex],
                Extent = extent,
                Id = idColumn >= 0 ? columns[idColumn].Ids![featureIndex] : null
            };

            foreach (var ring in geometry.Rings[featureIndex])
            {
                feature.Geometry.Add(ring);
            }

            for (var column = 0; column < columns.Count; column++)
            {
                var values = columns[column].Values;
                if (values is null || metadata[column].Name is null)
                {
                    continue;
                }

                feature.Attributes[metadata[column].Name!] = values[featureIndex];
            }

            layer.Features.Add(feature);
        }

        return layer;
    }

    private static ColumnMetadata ReadColumnMetadata(ref MltCursor cursor)
    {
        var typeByte = cursor.ReadByte();
        if (!Enum.IsDefined((ColumnType)typeByte))
        {
            throw new MltFormatException($"Unknown MLT column type {typeByte}.");
        }

        var type = (ColumnType)typeByte;
        if (type is ColumnType.Id or ColumnType.OptId or ColumnType.LongId or ColumnType.OptLongId or ColumnType.Geometry)
        {
            return new ColumnMetadata { Type = type };
        }

        var name = cursor.ReadString();
        var metadata = new ColumnMetadata { Type = type, Name = name };

        if (type == ColumnType.SharedDict)
        {
            var childCount = cursor.ReadVarInt32();
            for (var index = 0; index < childCount; index++)
            {
                var childType = (ColumnType)cursor.ReadByte();
                metadata.Children.Add((childType, cursor.ReadString()));
            }
        }

        return metadata;
    }

    private static ColumnData ReadColumnData(ref MltCursor cursor, ColumnMetadata metadata) =>
        metadata.Type switch
        {
            ColumnType.Id or ColumnType.LongId => ColumnData.ForIds(ReadIdColumn(ref cursor, metadata.Type)),
            ColumnType.OptId or ColumnType.OptLongId => ColumnData.ForIds(ReadIdColumn(ref cursor, metadata.Type)),
            ColumnType.Geometry => ColumnData.ForGeometry(ReadGeometryColumn(ref cursor)),
            ColumnType.SharedDict => throw new NotSupportedException(
                "MLT shared dictionaries are disabled by the worker encoder (shared_dict=False)."),
            _ => ColumnData.ForValues(ReadValueColumn(ref cursor, metadata.Type))
        };

    private static long[] ReadIdColumn(ref MltCursor cursor, ColumnType type)
    {
        var nullable = type is ColumnType.OptId or ColumnType.OptLongId;
        var wordSize = type is ColumnType.Id or ColumnType.OptId ? 4 : 8;
        bool[]? present = null;
        if (nullable)
        {
            present = MltStreamDecoder.DecodeBooleans(MltStream.Read(ref cursor));
        }

        var stream = MltStream.Read(ref cursor);
        var ids = MltStreamDecoder.DecodeIntegers(stream, wordSize, signed: false);
        return present is null ? ids : ApplyNulls(ids, present, 0);
    }

    private static object?[] ReadValueColumn(ref MltCursor cursor, ColumnType type)
    {
        var nullable = IsNullable(type);
        bool[]? present = null;
        if (nullable)
        {
            present = MltStreamDecoder.DecodeBooleans(MltStream.Read(ref cursor));
        }

        var valueCount = present?.Count(value => value) ?? 0;
        object?[] values = type switch
        {
            ColumnType.Float or ColumnType.OptFloat => Wrap(MltStreamDecoder.DecodeFloats(MltStream.Read(ref cursor))),
            ColumnType.Double or ColumnType.OptDouble => Wrap(MltStreamDecoder.DecodeDoubles(MltStream.Read(ref cursor))),
            ColumnType.Bool or ColumnType.OptBool => Wrap(MltStreamDecoder.DecodeBooleans(MltStream.Read(ref cursor))),
            ColumnType.Int32 or ColumnType.OptInt32 => Wrap(MltStreamDecoder.DecodeIntegers(MltStream.Read(ref cursor), 4, signed: true)),
            ColumnType.Int64 or ColumnType.OptInt64 => Wrap(MltStreamDecoder.DecodeIntegers(MltStream.Read(ref cursor), 8, signed: true)),
            ColumnType.UInt32 or ColumnType.OptUInt32 => Wrap(MltStreamDecoder.DecodeIntegers(MltStream.Read(ref cursor), 4, signed: false)),
            ColumnType.UInt64 or ColumnType.OptUInt64 => Wrap(MltStreamDecoder.DecodeIntegers(MltStream.Read(ref cursor), 8, signed: false)),
            ColumnType.String or ColumnType.OptString => ReadStringColumn(ref cursor, valueCount),
            _ => throw new NotSupportedException($"MLT column type {type} is not supported.")
        };

        return present is null ? values : ExpandNulls(values, present);
    }

    private static object?[] ReadStringColumn(ref MltCursor cursor, int _)
    {
        var streamCount = cursor.ReadVarInt32();
        MltStream? present = null;
        MltStream? lengths = null;
        MltStream? offsets = null;
        MltStream? data = null;
        for (var index = 0; index < streamCount; index++)
        {
            var stream = MltStream.Read(ref cursor);
            switch (stream.Category, stream.Subtype)
            {
                case (StreamCategory.Present, _):
                    present = stream;
                    break;
                case (StreamCategory.Length, _):
                    lengths = stream;
                    break;
                case (StreamCategory.Offset, _):
                    offsets = stream;
                    break;
                case (StreamCategory.Data, _):
                    data = stream;
                    break;
                default:
                    throw new MltFormatException($"Unexpected MLT string stream {stream.Category}/{stream.Subtype}.");
            }
        }

        if (lengths is null || data is null)
        {
            throw new MltFormatException("MLT string column is missing its length or data stream.");
        }

        var byteLengths = MltStreamDecoder.DecodeIntegers(lengths, 4, signed: false);
        var bytes = data.Payload.Span;
        var dictionary = new List<string>(byteLengths.Length);
        var position = 0;
        foreach (var length in byteLengths)
        {
            var valueLength = checked((int)length);
            if (position + valueLength > bytes.Length)
            {
                throw new MltFormatException("MLT string data is shorter than its length stream declares.");
            }

            dictionary.Add(Encoding.UTF8.GetString(bytes.Slice(position, valueLength)));
            position += valueLength;
        }

        if (offsets is null)
        {
            return [.. dictionary.Cast<object?>()];
        }

        var codes = MltStreamDecoder.DecodeIntegers(offsets, 4, signed: false);
        var values = new object?[codes.Length];
        for (var index = 0; index < codes.Length; index++)
        {
            if (codes[index] < 0 || codes[index] >= dictionary.Count)
            {
                throw new MltFormatException($"MLT dictionary code {codes[index]} is out of range.");
            }

            values[index] = dictionary[(int)codes[index]];
        }

        return present is null ? values : ExpandNulls(values, MltStreamDecoder.DecodeBooleans(present));
    }

    private static GeometryColumn ReadGeometryColumn(ref MltCursor cursor)
    {
        var streamCount = cursor.ReadVarInt32();
        MltStream? types = null;
        MltStream? geometries = null;
        MltStream? parts = null;
        MltStream? rings = null;
        MltStream? vertexOffsets = null;
        MltStream? vertices = null;

        for (var index = 0; index < streamCount; index++)
        {
            var stream = MltStream.Read(ref cursor);
            if (index == 0)
            {
                // The types stream is positional (spec: [types stream] right after the count).
                types = stream;
                continue;
            }

            switch (stream.Category, stream.Subtype)
            {
                case (StreamCategory.Length, (int)LengthSubtype.Geometries):
                    geometries = stream;
                    break;
                case (StreamCategory.Length, (int)LengthSubtype.Parts):
                    parts = stream;
                    break;
                case (StreamCategory.Length, (int)LengthSubtype.Rings):
                    rings = stream;
                    break;
                case (StreamCategory.Length, (int)LengthSubtype.Triangles):
                    throw new NotSupportedException("Tessellated MLT geometry is not produced by the workers.");
                case (StreamCategory.Offset, (int)OffsetSubtype.Vertex):
                    vertexOffsets = stream;
                    break;
                case (StreamCategory.Offset, (int)OffsetSubtype.Index):
                    throw new NotSupportedException("Tessellated MLT geometry is not produced by the workers.");
                case (StreamCategory.Data, (int)DataSubtype.Vertex):
                    vertices = stream;
                    break;
                case (StreamCategory.Data, (int)DataSubtype.Morton):
                    throw new NotSupportedException("Morton-encoded MLT vertices are not supported by this reader.");
                case (StreamCategory.Present, _):
                    break;
                default:
                    throw new MltFormatException($"Unexpected MLT geometry stream {stream.Category}/{stream.Subtype}.");
            }
        }

        if (types is null || vertices is null)
        {
            throw new MltFormatException("MLT geometry column is missing its types or vertex stream.");
        }

        var featureTypes = MltStreamDecoder.DecodeIntegers(types, 4, signed: false);
        var geometryCounts = geometries is null ? null : MltStreamDecoder.DecodeIntegers(geometries, 4, signed: false);
        var partCounts = parts is null ? null : MltStreamDecoder.DecodeIntegers(parts, 4, signed: false);
        var ringCounts = rings is null ? null : MltStreamDecoder.DecodeIntegers(rings, 4, signed: false);

        var vertexBuffer = MltStreamDecoder.DecodeVertices(vertices);
        if (vertexOffsets is not null)
        {
            throw new NotSupportedException("Dictionary-encoded MLT vertices are not supported by this reader.");
        }

        var result = new GeometryColumn(featureTypes.Length);
        var vertexCursor = 0;
        var partCursor = 0;
        var ringCursor = 0;
        var geometryCursor = 0;

        for (var featureIndex = 0; featureIndex < featureTypes.Length; featureIndex++)
        {
            var type = (VectorGeometryType)featureTypes[featureIndex];
            var feature = result.Rings[featureIndex];

            switch (type)
            {
                case VectorGeometryType.Point:
                {
                    var vertex = vertexBuffer[vertexCursor++];
                    feature.Add([new Point(vertex.X, vertex.Y)]);
                    break;
                }

                case VectorGeometryType.MultiPoint:
                {
                    var count = geometryCounts?[geometryCursor++] ?? 1;
                    for (var point = 0; point < count; point++)
                    {
                        var vertex = vertexBuffer[vertexCursor++];
                        feature.Add([new Point(vertex.X, vertex.Y)]);
                    }

                    break;
                }

                case VectorGeometryType.LineString:
                {
                    var length = TakeCount(ringCounts, ref ringCursor, partCounts, ref partCursor);
                    feature.Add(Slice(vertexBuffer, ref vertexCursor, length));
                    break;
                }

                case VectorGeometryType.MultiLineString:
                {
                    var count = geometryCounts?[geometryCursor++] ?? 1;
                    for (var line = 0; line < count; line++)
                    {
                        var length = TakeCount(ringCounts, ref ringCursor, partCounts, ref partCursor);
                        feature.Add(Slice(vertexBuffer, ref vertexCursor, length));
                    }

                    break;
                }

                case VectorGeometryType.Polygon:
                {
                    var ringCount = PartCount(partCounts, ref partCursor);
                    for (var ring = 0; ring < ringCount; ring++)
                    {
                        var length = ringCounts is null ? 0 : checked((int)ringCounts[ringCursor++]);
                        feature.Add(CloseRing(Slice(vertexBuffer, ref vertexCursor, length)));
                    }

                    break;
                }

                case VectorGeometryType.MultiPolygon:
                {
                    var polygonCount = geometryCounts?[geometryCursor++] ?? 1;
                    for (var polygon = 0; polygon < polygonCount; polygon++)
                    {
                        var ringCount = partCounts is null ? 1 : checked((int)partCounts[partCursor++]);
                        for (var ring = 0; ring < ringCount; ring++)
                        {
                            var length = ringCounts is null ? 0 : checked((int)ringCounts[ringCursor++]);
                            feature.Add(CloseRing(Slice(vertexBuffer, ref vertexCursor, length)));
                        }
                    }

                    break;
                }

                default:
                    throw new MltFormatException($"Unknown geometry type {featureTypes[featureIndex]}.");
            }

            result.Types[featureIndex] = type;
        }

        return result;
    }

    private static int PartCount(long[]? partCounts, ref int partCursor) =>
        partCounts is null ? 1 : checked((int)partCounts[partCursor++]);

    private static int TakeCount(long[]? ringCounts, ref int ringCursor, long[]? partCounts, ref int partCursor) =>
        ringCounts is not null
            ? checked((int)ringCounts[ringCursor++])
            : partCounts is not null
                ? checked((int)partCounts[partCursor++])
                : 0;

    private static List<Point> Slice((double X, double Y)[] vertices, ref int cursor, int length)
    {
        if (length < 0 || cursor + length > vertices.Length)
        {
            throw new MltFormatException($"Vertex stream holds {vertices.Length} vertices, need {cursor + length}.");
        }

        var ring = new List<Point>(length + 1);
        for (var index = 0; index < length; index++)
        {
            var vertex = vertices[cursor++];
            ring.Add(new Point(vertex.X, vertex.Y));
        }

        return ring;
    }

    private static List<Point> CloseRing(List<Point> ring)
    {
        if (ring.Count > 0 && ring[0] != ring[^1])
        {
            ring.Add(ring[0]);
        }

        return ring;
    }

    private static bool IsNullable(ColumnType type) =>
        type is ColumnType.OptId or ColumnType.OptLongId or ColumnType.OptBool or ColumnType.OptInt32
            or ColumnType.OptUInt32 or ColumnType.OptInt64 or ColumnType.OptUInt64 or ColumnType.OptFloat
            or ColumnType.OptDouble or ColumnType.OptString;

    private static long[] ApplyNulls(long[] values, bool[] present, long fallback) =>
        [.. ExpandNulls([.. values.Cast<object?>()], present).Select(value => value is long number ? number : fallback)];

    private static object?[] ExpandNulls(object?[] present, bool[] mask)
    {
        var result = new object?[mask.Length];
        var cursor = 0;
        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index])
            {
                continue;
            }

            if (cursor >= present.Length)
            {
                throw new MltFormatException("MLT present stream marks more values than the column holds.");
            }

            result[index] = present[cursor++];
        }

        return result;
    }

    private static object?[] Wrap<T>(T[] values)
    {
        var result = new object?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            result[index] = values[index];
        }

        return result;
    }

    private sealed class ColumnData
    {
        public object?[]? Values { get; private init; }

        public long[]? Ids { get; private init; }

        public GeometryColumn? Geometry { get; private init; }

        public int Count => Values?.Length ?? Ids?.Length ?? Geometry?.Types.Length ?? 0;

        public static ColumnData ForValues(object?[] values) => new() { Values = values };

        public static ColumnData ForIds(long[] ids) => new() { Ids = ids };

        public static ColumnData ForGeometry(GeometryColumn geometry) => new() { Geometry = geometry };
    }

    private sealed class GeometryColumn(int featureCount)
    {
        public VectorGeometryType[] Types { get; } = new VectorGeometryType[featureCount];

        public List<List<Point>>[] Rings { get; } =
            [.. Enumerable.Range(0, featureCount).Select(_ => new List<List<Point>>())];
    }
}
