using System.IO.Compression;
using System.Text;
using DP.VectorMapTileRenderer.Pmtiles;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class PmtilesTests
{
    [Fact]
    public async Task ReadsMltTileFromArchive()
    {
        var mlt = ReadFixture("stations.mlt");
        var archive = BuildArchive(mlt, PmtilesTileType.Mlt);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(
            0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        var layer = Assert.Single(tile.Layers);
        Assert.Equal(12, layer.Features.Count);
        Assert.Equal(VectorGeometryType.Point, layer.Features[0].GeometryType);
    }

    [Fact]
    public async Task ReadsRasterPayloadAsBytes()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var archive = BuildArchive(png, PmtilesTileType.Png);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var bytes = await source.GetTileBytesAsync(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.Equal(png, bytes);
        Assert.Null(await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsGzippedTilePayload()
    {
        var mlt = ReadFixture("stations.mlt");
        var archive = BuildArchive(mlt, PmtilesTileType.Mlt, PmtilesCompression.Gzip);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task ReadsBrotliCompressedTilePayload()
    {
        var mlt = ReadFixture("stations.mlt");
        var archive = BuildArchive(mlt, PmtilesTileType.Mlt, PmtilesCompression.Brotli);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task SupportsConcurrentReadsFromFile()
    {
        var archive = BuildArchive(ReadFixture("stations.mlt"), PmtilesTileType.Mlt);
        var path = Path.Combine(Path.GetTempPath(), $"dp-pmtiles-{Guid.NewGuid():N}.pmtiles");
        await File.WriteAllBytesAsync(path, archive, TestContext.Current.CancellationToken);

        try
        {
            using var source = new PmtilesTileSource(path);
            var tiles = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken)));

            Assert.All(tiles, tile => Assert.NotNull(tile));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadingTruncatedTileThrowsFormatException()
    {
        var truncated = ReadFixture("stations.mlt")[..20];

        Assert.Throws<DP.VectorMapTileRenderer.Mlt.MltFormatException>(
            () => DP.VectorMapTileRenderer.Mlt.MltTileReader.Read(truncated));
    }

    [Fact]
    public async Task ReadsZstdCompressedTilePayload()
    {
        var mlt = ReadFixture("stations.mlt");
        var archive = BuildArchive(mlt, PmtilesTileType.Mlt, PmtilesCompression.Zstd);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task OverzoomsWhenRequestedZoomExceedsArchive()
    {
        var mlt = ReadFixture("stations.mlt");
        var archive = BuildArchive(mlt, PmtilesTileType.Mlt);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(1, 1, 1, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.True(tile!.IsOverZoomed);
        Assert.Equal(12, tile.Layers[0].Features.Count);
    }

    [Fact]
    public async Task ReturnsNullForMissingTile()
    {
        var archive = BuildArchive([1, 2, 3], PmtilesTileType.Mvt);
        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));

        Assert.Null(await source.GetVectorTile(1, 0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsTileAtOffsetBeyondTwoGigabytes()
    {
        var mlt = ReadFixture("stations.mlt");
        var directory = DirectoryBytes(mlt.Length);
        const long tileOffset = 3_000_000_000L;
        var header = BuildHeader(directory.Length, tileOffset, mlt.Length, PmtilesTileType.Mlt, PmtilesCompression.None);
        var prefix = header.Concat(directory).ToArray();

        using var source = new PmtilesTileSource(new VirtualRangeSource(prefix, tileOffset, mlt));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Equal(12, Assert.Single(tile!.Layers).Features.Count);
    }

    [Fact]
    public void RejectsCorruptDirectoryWithoutHugeAllocation()
    {
        // Declares more entries than the payload could ever hold.
        var corrupt = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };

        Assert.Throws<InvalidDataException>(() => PmtilesCodec.DeserializeDirectory(corrupt));
    }

    [Fact]
    public void ParsesHeaderAndTileIds()
    {
        var archive = BuildArchive([1, 2, 3], PmtilesTileType.Mlt);
        var header = PmtilesCodec.ParseHeader(archive);

        Assert.Equal(3, header.Version);
        Assert.Equal(PmtilesTileType.Mlt, header.TileType);
        Assert.Equal(PmtilesCompression.None, header.TileCompression);
        Assert.Equal(0UL, PmtilesCodec.ZxyToTileId(0, 0, 0));
        Assert.Single(PmtilesCodec.DeserializeDirectory(archive.AsSpan(127, (int)header.RootDirectoryLength)));
    }

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static byte[] BuildArchive(
        byte[] tile,
        PmtilesTileType tileType,
        PmtilesCompression compression = PmtilesCompression.None)
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
            compression);

        var result = new byte[header.Length + directory.Length + payload.Length];
        header.CopyTo(result, 0);
        directory.CopyTo(result, header.Length);
        payload.CopyTo(result, header.Length + directory.Length);
        return result;
    }

    private static byte[] DirectoryBytes(int tileLength)
    {
        var directory = new List<byte>();
        directory.AddRange(Varint(1));            // entry count
        directory.AddRange(Varint(0));            // tile id delta
        directory.AddRange(Varint(1));            // run length
        directory.AddRange(Varint((ulong)tileLength));
        directory.AddRange(Varint(0));            // first offset is implicit 0
        return directory.ToArray();
    }

    private static byte[] BuildHeader(
        int rootLength,
        long tileDataOffset,
        int tileLength,
        PmtilesTileType tileType,
        PmtilesCompression compression)
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
        header[101] = 0;                                            // max zoom
        return header;
    }

    private sealed class VirtualRangeSource(byte[] prefix, long tileOffset, byte[] tile) : IPmtilesRangeSource
    {
        public string Key => "virtual";

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            if (offset == tileOffset)
            {
                return Task.FromResult(tile[..Math.Min(length, tile.Length)]);
            }

            if (offset < prefix.Length)
            {
                var available = (int)Math.Min(length, prefix.Length - offset);
                return Task.FromResult(prefix.AsSpan((int)offset, available).ToArray());
            }

            return Task.FromResult(Array.Empty<byte>());
        }
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
