using System.IO.Compression;
using System.Text;
using DP.VectorMapTileRenderer.Pmtiles;

namespace DP.VectorMapTileRenderer.Tests;

/// <summary>
/// Builds minimal but valid PMTiles v3 archives for tests: one root directory with a single
/// z0 entry and the payload right after it. Shared by the stream, file and HTTP source suites.
/// </summary>
internal static class TestArchives
{
    public static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Build(
        byte[] tile,
        PmtilesTileType tileType,
        PmtilesCompression compression = PmtilesCompression.None,
        byte maxZoom = 0)
    {
        var payload = compression switch
        {
            PmtilesCompression.Gzip => Gzip(tile),
            PmtilesCompression.Brotli => Brotli(tile),
            PmtilesCompression.Zstd => new ZstdSharp.Compressor().Wrap(tile).ToArray(),
            _ => tile
        };

        var directory = DirectoryBytes(payload.Length);
        var header = BuildHeader(
            directory.Length,
            PmtilesCodec.HeaderSize + (long)directory.Length,
            payload.Length,
            tileType,
            compression,
            maxZoom);

        var result = new byte[header.Length + directory.Length + payload.Length];
        header.CopyTo(result, 0);
        directory.CopyTo(result, header.Length);
        payload.CopyTo(result, header.Length + directory.Length);
        return result;
    }

    public static byte[] DirectoryBytes(int tileLength)
    {
        var directory = new List<byte>();
        directory.AddRange(Varint(1));            // entry count
        directory.AddRange(Varint(0));            // tile id delta
        directory.AddRange(Varint(1));            // run length
        directory.AddRange(Varint((ulong)tileLength));
        directory.AddRange(Varint(0));            // first offset is implicit 0
        return directory.ToArray();
    }

    public static byte[] BuildHeader(
        int rootLength,
        long tileDataOffset,
        int tileLength,
        PmtilesTileType tileType,
        PmtilesCompression compression,
        byte maxZoom = 0)
    {
        var header = new byte[PmtilesCodec.HeaderSize];
        Encoding.ASCII.GetBytes("PMTiles").CopyTo(header, 0);
        header[7] = 3;
        WriteUInt64(header, 8, PmtilesCodec.HeaderSize);            // root offset
        WriteUInt64(header, 16, (ulong)rootLength);                 // root length
        WriteUInt64(header, 56, (ulong)tileDataOffset);             // tile data offset (may exceed 2 GB)
        WriteUInt64(header, 64, (ulong)tileLength);                 // tile data length
        WriteUInt64(header, 72, 1);                                 // addressed tiles
        WriteUInt64(header, 80, 1);                                 // tile entries
        WriteUInt64(header, 88, 1);                                 // tile contents
        header[96] = 1;                                             // clustered
        header[97] = (byte)PmtilesCompression.None;                 // internal compression
        header[98] = (byte)compression;
        header[99] = (byte)tileType;
        header[100] = 0;                                            // min zoom
        header[101] = maxZoom;
        return header;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }

    private static byte[] Brotli(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(bytes);
        }

        return output.ToArray();
    }

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), value);

    private static byte[] Varint(ulong value)
    {
        var result = new List<byte>();
        do
        {
            var current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            result.Add(current);
        }
        while (value != 0);

        return result.ToArray();
    }
}
