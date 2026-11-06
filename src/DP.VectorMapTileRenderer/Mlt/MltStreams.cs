using System.Buffers.Binary;

namespace DP.VectorMapTileRenderer.Mlt;

internal enum StreamCategory
{
    Present = 0,
    Data = 1,
    Offset = 2,
    Length = 3
}

internal enum DataSubtype
{
    None = 0,
    Single = 1,
    Shared = 2,
    Vertex = 3,
    Morton = 4,
    Fsst = 5
}

internal enum OffsetSubtype
{
    Vertex = 0,
    Index = 1,
    String = 2,
    Key = 3
}

internal enum LengthSubtype
{
    VarBinary = 0,
    Geometries = 1,
    Parts = 2,
    Rings = 3,
    Triangles = 4,
    Symbol = 5,
    Dictionary = 6
}

internal enum LogicalEncoding
{
    None,
    Delta,
    DeltaRle,
    ComponentwiseDelta,
    Rle,
    Morton
}

internal enum PhysicalEncoding
{
    FixedWidth,
    FastPfor,
    VarInt
}

/// <summary>One physical sub-column of an MLT column.</summary>
internal sealed class MltStream
{
    public required byte StreamType { get; init; }

    public required LogicalEncoding Logical { get; init; }

    public required PhysicalEncoding Physical { get; init; }

    public required int NumValues { get; init; }

    public int Runs { get; init; }

    public int NumRleValues { get; init; }

    public required ReadOnlyMemory<byte> Payload { get; init; }

    public StreamCategory Category => (StreamCategory)(StreamType >> 4);

    public int Subtype => StreamType & 0xF;

    public static MltStream Read(ref MltCursor cursor)
    {
        var streamType = cursor.ReadByte();
        var encoding = cursor.ReadByte();
        var logical = ReadLogical(encoding);
        var physical = (PhysicalEncoding)(encoding & 0x3);
        var numValues = cursor.ReadVarInt32();
        var byteLength = cursor.ReadVarInt32();

        // RLE parameters live in the header only for physically encoded runs; a byte-RLE
        // boolean stream (physical None) carries them in the payload instead.
        var runs = 0;
        var numRleValues = 0;
        if (logical is LogicalEncoding.Rle or LogicalEncoding.DeltaRle && physical != PhysicalEncoding.FixedWidth)
        {
            runs = cursor.ReadVarInt32();
            numRleValues = cursor.ReadVarInt32();
        }

        var category = (StreamCategory)(streamType >> 4);
        var subtype = streamType & 0xF;
        if (category == StreamCategory.Data && subtype == (int)DataSubtype.Morton)
        {
            _ = cursor.ReadVarInt32(); // bits
            _ = cursor.ReadVarInt32(); // shift
        }

        if (physical == PhysicalEncoding.FastPfor)
        {
            throw new NotSupportedException(
                "MLT tile uses SIMD-FastPFOR, which the worker encoder never writes; " +
                "the tile is corrupt or was produced by another encoder.");
        }

        return new MltStream
        {
            StreamType = streamType,
            Logical = logical,
            Physical = physical,
            NumValues = numValues,
            Runs = runs,
            NumRleValues = numRleValues,
            Payload = cursor.Take(byteLength).ToArray()
        };
    }

    private static LogicalEncoding ReadLogical(byte encoding)
    {
        var logical1 = (encoding >> 5) & 0x7;
        var logical2 = (encoding >> 2) & 0x7;
        return (logical1, logical2) switch
        {
            (0, 0) => LogicalEncoding.None,
            (1, 0) => LogicalEncoding.Delta,
            (1, 3) => LogicalEncoding.DeltaRle,
            (2, 0) => LogicalEncoding.ComponentwiseDelta,
            (3, 0) => LogicalEncoding.Rle,
            (4, 0) or (4, 1) => LogicalEncoding.Morton,
            _ => throw new MltFormatException($"Unsupported MLT logical encoding {logical1}/{logical2}.")
        };
    }
}

/// <summary>Payload decoders for MLT streams, limited to the encodings the workers emit.</summary>
internal static class MltStreamDecoder
{
    /// <summary>Decodes an integer stream. <paramref name="wordSize"/> is 4 or 8 bytes for fixed-width payloads.</summary>
    public static long[] DecodeIntegers(MltStream stream, int wordSize, bool signed)
    {
        // Delta values are always zigzag-encoded, whatever the column's signedness.
        var zigzag = signed || stream.Logical is LogicalEncoding.Delta or LogicalEncoding.DeltaRle;
        var values = stream.Logical is LogicalEncoding.Rle or LogicalEncoding.DeltaRle
            ? ExpandRuns(stream, wordSize, zigzag)
            : ReadWords(stream, stream.NumValues, wordSize, zigzag);

        return stream.Logical switch
        {
            LogicalEncoding.None or LogicalEncoding.Rle => values,
            // Deltas arrive zigzag-encoded before the prefix sum, so undo it here.
            LogicalEncoding.Delta or LogicalEncoding.DeltaRle => PrefixSum(values),
            _ => throw new NotSupportedException(
                $"MLT stream {stream.Category}/{stream.Subtype} uses {stream.Logical}, which this reader does not support.")
        };
    }

    /// <summary>Decodes the interleaved (x, y) words of a vertex stream into absolute coordinates.</summary>
    public static (double X, double Y)[] DecodeVertices(MltStream stream)
    {
        if (stream.Logical != LogicalEncoding.ComponentwiseDelta)
        {
            throw new MltFormatException($"Vertex stream must use ComponentwiseDelta, got {stream.Logical}.");
        }

        var words = ReadWords(stream, stream.NumValues, 4, signed: true);
        if (words.Length % 2 != 0)
        {
            throw new MltFormatException("Vertex stream holds an odd number of words.");
        }

        var vertices = new (double X, double Y)[words.Length / 2];
        long x = 0;
        long y = 0;
        for (var index = 0; index < vertices.Length; index++)
        {
            x += words[index * 2];
            y += words[(index * 2) + 1];
            vertices[index] = (x, y);
        }

        return vertices;
    }

    /// <summary>
    /// Decodes a boolean stream (<c>Present</c> or a bool column). Booleans are stored as a bitmap
    /// of <c>ceil(count / 8)</c> bytes, optionally byte-run-length encoded (header byte: <c>? 0x7F</c>
    /// means "one value repeated header + 3 times", otherwise <c>256 - header</c> literal bytes).
    /// </summary>
    public static bool[] DecodeBooleans(MltStream stream)
    {
        var count = stream.NumValues;
        var byteCount = (count + 7) / 8;
        if (stream.Logical == LogicalEncoding.Rle)
        {
            var bitmap = DecodeByteRle(stream.Payload.Span, byteCount);
            return ExpandBitmap(bitmap, count);
        }

        if (stream.Payload.Length < byteCount)
        {
            throw new MltFormatException(
                $"Boolean stream holds {stream.Payload.Length} bytes, needs {byteCount} for {count} values.");
        }

        return ExpandBitmap(stream.Payload.Span[..byteCount], count);
    }

    private static bool[] ExpandBitmap(ReadOnlySpan<byte> bitmap, int count)
    {
        var result = new bool[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = (bitmap[index / 8] & (1 << (index % 8))) != 0;
        }

        return result;
    }

    private static byte[] DecodeByteRle(ReadOnlySpan<byte> payload, int byteCount)
    {
        var values = new byte[byteCount];
        var offset = 0;
        var position = 0;
        while (offset < byteCount && position < payload.Length)
        {
            var header = payload[position++];
            if (header <= 0x7F)
            {
                var runLength = header + 3;
                if (position >= payload.Length)
                {
                    throw new MltFormatException("Byte-RLE payload ends inside a repeat run.");
                }

                var value = payload[position++];
                var end = Math.Min(offset + runLength, byteCount);
                values.AsSpan(offset, end - offset).Fill(value);
                offset = end;
                continue;
            }

            var literals = 256 - header;
            for (var index = 0; index < literals && offset < byteCount && position < payload.Length; index++)
            {
                values[offset++] = payload[position++];
            }
        }

        if (offset != byteCount)
        {
            throw new MltFormatException(
                $"Byte-RLE payload covers {offset} of {byteCount} bytes.");
        }

        return values;
    }

    public static float[] DecodeFloats(MltStream stream)
    {
        ValidateFixedWidth(stream, sizeof(float));
        var payload = stream.Payload.Span;
        var result = new float[stream.NumValues];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(index * sizeof(float), sizeof(float)));
        }

        return result;
    }

    public static double[] DecodeDoubles(MltStream stream)
    {
        ValidateFixedWidth(stream, sizeof(double));
        var payload = stream.Payload.Span;
        var result = new double[stream.NumValues];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadDoubleLittleEndian(payload.Slice(index * sizeof(double), sizeof(double)));
        }

        return result;
    }

    private static void ValidateFixedWidth(MltStream stream, int width)
    {
        if (stream.Logical != LogicalEncoding.None || stream.Physical != PhysicalEncoding.FixedWidth)
        {
            throw new NotSupportedException(
                $"Floating-point stream {stream.Category}/{stream.Subtype} uses {stream.Logical}/{stream.Physical}.");
        }

        var expected = stream.NumValues * width;
        if (stream.Payload.Length < expected)
        {
            throw new MltFormatException($"Stream holds {stream.Payload.Length} bytes, needs {expected}.");
        }
    }

    private static long[] ReadWords(MltStream stream, int count, int wordSize, bool signed)
    {
        var payload = stream.Payload.Span;
        var result = new long[count];

        if (stream.Physical == PhysicalEncoding.VarInt)
        {
            var cursor = new MltCursor(payload);
            for (var index = 0; index < count; index++)
            {
                var raw = cursor.ReadVarInt();
                result[index] = signed ? Unzigzag(raw) : checked((long)raw);
            }

            return result;
        }

        if (payload.Length < count * wordSize)
        {
            throw new MltFormatException($"Stream holds {payload.Length} bytes, needs {count * wordSize}.");
        }

        for (var index = 0; index < count; index++)
        {
            var slice = payload.Slice(index * wordSize, wordSize);
            result[index] = wordSize == 4
                ? signed
                    ? BinaryPrimitives.ReadInt32LittleEndian(slice)
                    : BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : signed
                    ? BinaryPrimitives.ReadInt64LittleEndian(slice)
                    : checked((long)BinaryPrimitives.ReadUInt64LittleEndian(slice));
        }

        return result;
    }

    private static long[] ExpandRuns(MltStream stream, int wordSize, bool signed)
    {
        var payload = stream.Payload.Span;
        var raw = new long[stream.Runs * 2];

        if (stream.Physical == PhysicalEncoding.VarInt)
        {
            var cursor = new MltCursor(payload);
            for (var index = 0; index < raw.Length; index++)
            {
                var value = cursor.ReadVarInt();
                // Run lengths are never zigzag-encoded; only the values of a signed stream are.
                raw[index] = index < stream.Runs
                    ? checked((long)value)
                    : signed ? Unzigzag(value) : checked((long)value);
            }
        }
        else
        {
            var bytes = stream.Runs * wordSize * 2;
            if (payload.Length < bytes)
            {
                throw new MltFormatException($"RLE stream holds {payload.Length} bytes, needs {bytes}.");
            }

            for (var index = 0; index < raw.Length; index++)
            {
                var slice = payload.Slice(index * wordSize, wordSize);
                var value = wordSize == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                    : BinaryPrimitives.ReadInt64LittleEndian(slice);
                raw[index] = index < stream.Runs ? value : signed ? Unzigzag((ulong)value) : value;
            }
        }

        var result = new long[stream.NumRleValues];
        var position = 0;
        for (var run = 0; run < stream.Runs; run++)
        {
            var runLength = raw[run];
            var value = raw[stream.Runs + run];
            for (var offset = 0; offset < runLength; offset++)
            {
                if (position >= result.Length)
                {
                    throw new MltFormatException("RLE runs overflow the declared value count.");
                }

                result[position++] = value;
            }
        }

        if (position != result.Length)
        {
            throw new MltFormatException($"RLE runs cover {position} values, header declares {result.Length}.");
        }

        return result;
    }

    private static long[] PrefixSum(long[] deltas)
    {
        long running = 0;
        for (var index = 0; index < deltas.Length; index++)
        {
            running += deltas[index];
            deltas[index] = running;
        }

        return deltas;
    }

    private static long Unzigzag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
