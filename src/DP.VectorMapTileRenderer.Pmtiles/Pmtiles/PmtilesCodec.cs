using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DP.VectorMapTileRenderer.Pmtiles;

/// <summary>PMTiles v3 tile payload types.</summary>
public enum PmtilesTileType : byte
{
    Unknown = 0,
    Mvt = 1,
    Png = 2,
    Jpeg = 3,
    Webp = 4,
    Avif = 5,

    /// <summary>MapLibre Tile v1 (the value used by MapLibre's PMTiles implementation).</summary>
    Mlt = 6
}

/// <summary>PMTiles v3 compression ids.</summary>
public enum PmtilesCompression : byte
{
    Unknown = 0,
    None = 1,
    Gzip = 2,
    Brotli = 3,
    Zstd = 4
}

/// <summary>Low-level PMTiles v3 codec: header, directories, tile ids and decompression.</summary>
public static class PmtilesCodec
{
    public const int HeaderSize = 127;

    public static PmtilesHeader ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new InvalidDataException("PMTiles header is truncated.");
        }

        if (!Encoding.ASCII.GetString(bytes[..7]).Equals("PMTiles", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Not a PMTiles archive.");
        }

        return new PmtilesHeader(
            Version: bytes[7],
            RootDirectoryOffset: BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            RootDirectoryLength: BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            MetadataOffset: BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
            MetadataLength: BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
            LeafDirectoryOffset: BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
            LeafDirectoryLength: BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]),
            TileDataOffset: BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]),
            TileDataLength: BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]),
            AddressedTilesCount: BinaryPrimitives.ReadUInt64LittleEndian(bytes[72..]),
            TileEntriesCount: BinaryPrimitives.ReadUInt64LittleEndian(bytes[80..]),
            TileContentsCount: BinaryPrimitives.ReadUInt64LittleEndian(bytes[88..]),
            Clustered: bytes[96],
            InternalCompression: (PmtilesCompression)bytes[97],
            TileCompression: (PmtilesCompression)bytes[98],
            TileType: (PmtilesTileType)bytes[99],
            MinZoom: bytes[100],
            MaxZoom: bytes[101],
            MinLonE7: BinaryPrimitives.ReadInt32LittleEndian(bytes[102..]),
            MinLatE7: BinaryPrimitives.ReadInt32LittleEndian(bytes[106..]),
            MaxLonE7: BinaryPrimitives.ReadInt32LittleEndian(bytes[110..]),
            MaxLatE7: BinaryPrimitives.ReadInt32LittleEndian(bytes[114..]));
    }

    public static bool IsVector(PmtilesTileType tileType) =>
        tileType is PmtilesTileType.Mvt or PmtilesTileType.Mlt;

    public static bool IsRaster(PmtilesTileType tileType) =>
        tileType is PmtilesTileType.Png or PmtilesTileType.Jpeg or PmtilesTileType.Webp or PmtilesTileType.Avif;

    public static ulong ZxyToTileId(int z, int x, int y)
    {
        if (z is < 0 or > 26)
        {
            throw new ArgumentOutOfRangeException(nameof(z), z, "PMTiles supports zoom 0..26.");
        }

        var max = (1 << z) - 1;
        if ((uint)x > (uint)max || (uint)y > (uint)max)
        {
            throw new ArgumentOutOfRangeException($"Tile {z}/{x}/{y} is outside zoom bounds.");
        }

        ulong acc = ((1UL << (z * 2)) - 1) / 3;
        var tx = x;
        var ty = y;
        for (var a = z - 1; a >= 0; a--)
        {
            var s = 1 << a;
            var rx = (tx & s) != 0 ? 1 : 0;
            var ry = (ty & s) != 0 ? 1 : 0;
            acc += (ulong)(s * s * ((3 * rx) ^ ry));
            Rotate(s, ref tx, ref ty, rx, ry);
        }

        return acc;
    }

    public static IReadOnlyList<PmtilesEntry> DeserializeDirectory(ReadOnlySpan<byte> decoded)
    {
        var cursor = decoded;
        var count = ReadVarint(ref cursor);

        // Each entry needs at least one byte in each of the four column sections, so a count
        // larger than the payload is corrupt. The extra cap keeps a hostile header from
        // allocating gigabytes before the first read fails.
        if (count > (ulong)decoded.Length || count > 10_000_000)
        {
            throw new InvalidDataException($"PMTiles directory declares {count} entries in {decoded.Length} bytes.");
        }

        var entryCount = (int)count;
        var entries = new PmtilesEntry[entryCount];
        ulong lastId = 0;
        for (var index = 0; index < entryCount; index++)
        {
            lastId += ReadVarint(ref cursor);
            entries[index].TileId = lastId;
        }

        for (var index = 0; index < entryCount; index++)
        {
            entries[index].RunLength = ReadVarint(ref cursor);
        }

        for (var index = 0; index < entryCount; index++)
        {
            entries[index].Length = ReadVarint(ref cursor);
        }

        for (var index = 0; index < entryCount; index++)
        {
            var value = ReadVarint(ref cursor);
            entries[index].Offset = value == 0 && index > 0
                ? entries[index - 1].Offset + entries[index - 1].Length
                : value == 0
                    ? 0
                    : value - 1;
        }

        return entries;
    }

    public static PmtilesEntry? FindTile(IReadOnlyList<PmtilesEntry> entries, ulong tileId)
    {
        var low = 0;
        var high = entries.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            var id = entries[mid].TileId;
            if (tileId > id)
            {
                low = mid + 1;
            }
            else if (tileId < id)
            {
                high = mid - 1;
            }
            else
            {
                return entries[mid];
            }
        }

        if (high < 0)
        {
            return null;
        }

        var candidate = entries[high];
        if (candidate.RunLength == 0)
        {
            return candidate;
        }

        return tileId - candidate.TileId < candidate.RunLength ? candidate : null;
    }

    public static byte[] Decompress(byte[] bytes, PmtilesCompression compression)
    {
        switch (compression)
        {
            case PmtilesCompression.None:
                return bytes;
            case PmtilesCompression.Gzip:
                return DecompressWith(bytes, static stream => new GZipStream(stream, CompressionMode.Decompress));
            case PmtilesCompression.Brotli:
                return DecompressWith(bytes, static stream => new BrotliStream(stream, CompressionMode.Decompress));
            case PmtilesCompression.Zstd:
                return DecompressWith(bytes, static stream => new ZstdSharp.DecompressionStream(stream));
            default:
                throw new InvalidDataException($"Unknown PMTiles compression id {(byte)compression}.");
        }
    }

    public static ulong ReadVarint(ref ReadOnlySpan<byte> span)
    {
        ulong result = 0;
        var shift = 0;
        while (span.Length > 0)
        {
            var current = span[0];
            span = span[1..];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("Varint is too long.");
            }
        }

        throw new InvalidDataException("Truncated varint.");
    }

    private static byte[] DecompressWith(byte[] bytes, Func<Stream, Stream> createDecoder)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var decoder = createDecoder(input);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static void Rotate(int n, ref int x, ref int y, int rx, int ry)
    {
        if (ry != 0)
        {
            return;
        }

        if (rx == 1)
        {
            x = n - 1 - x;
            y = n - 1 - y;
        }

        (x, y) = (y, x);
    }
}

public readonly record struct PmtilesHeader(
    byte Version,
    ulong RootDirectoryOffset,
    ulong RootDirectoryLength,
    ulong MetadataOffset,
    ulong MetadataLength,
    ulong LeafDirectoryOffset,
    ulong LeafDirectoryLength,
    ulong TileDataOffset,
    ulong TileDataLength,
    ulong AddressedTilesCount,
    ulong TileEntriesCount,
    ulong TileContentsCount,
    byte Clustered,
    PmtilesCompression InternalCompression,
    PmtilesCompression TileCompression,
    PmtilesTileType TileType,
    byte MinZoom,
    byte MaxZoom,
    int MinLonE7,
    int MinLatE7,
    int MaxLonE7,
    int MaxLatE7)
{
    /// <summary>
    /// True when the archive's declared WGS84 bounds contain the point. A dateline-crossing
    /// archive declares a wrapped longitude range.
    /// </summary>
    public bool Contains(double lon, double lat)
    {
        if (MinLatE7 == 0 && MaxLatE7 == 0 && MinLonE7 == 0 && MaxLonE7 == 0)
        {
            return true;
        }

        var minLat = MinLatE7 / 1e7;
        var maxLat = MaxLatE7 / 1e7;
        if (lat < minLat || lat > maxLat)
        {
            return false;
        }

        var minLon = MinLonE7 / 1e7;
        var maxLon = MaxLonE7 / 1e7;
        return minLon <= maxLon
            ? lon >= minLon && lon <= maxLon
            : lon >= minLon || lon <= maxLon;
    }
}

public struct PmtilesEntry
{
    public ulong TileId;
    public ulong RunLength;
    public ulong Length;
    public ulong Offset;
}
