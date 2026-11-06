using DP.VectorMapTileRenderer.Pmtiles;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class PmtilesTests
{
    [Fact]
    public async Task ReadsMltTileFromArchive()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var archive = TestArchives.Build(mlt, PmtilesTileType.Mlt);

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
        var archive = TestArchives.Build(png, PmtilesTileType.Png);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var bytes = await source.GetTileBytesAsync(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.Equal(png, bytes);
        Assert.Null(await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsGzippedTilePayload()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var archive = TestArchives.Build(mlt, PmtilesTileType.Mlt, PmtilesCompression.Gzip);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task ReadsBrotliCompressedTilePayload()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var archive = TestArchives.Build(mlt, PmtilesTileType.Mlt, PmtilesCompression.Brotli);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task SupportsConcurrentReadsFromFile()
    {
        var archive = TestArchives.Build(TestArchives.ReadFixture("stations.mlt"), PmtilesTileType.Mlt);
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
        var truncated = TestArchives.ReadFixture("stations.mlt")[..20];

        Assert.Throws<DP.VectorMapTileRenderer.Mlt.MltFormatException>(
            () => DP.VectorMapTileRenderer.Mlt.MltTileReader.Read(truncated));
    }

    [Fact]
    public async Task ReadsZstdCompressedTilePayload()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var archive = TestArchives.Build(mlt, PmtilesTileType.Mlt, PmtilesCompression.Zstd);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.Single(tile!.Layers);
    }

    [Fact]
    public async Task OverzoomsWhenRequestedZoomExceedsArchive()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var archive = TestArchives.Build(mlt, PmtilesTileType.Mlt);

        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));
        var tile = await source.GetVectorTile(1, 1, 1, TestContext.Current.CancellationToken);

        Assert.NotNull(tile);
        Assert.True(tile!.IsOverZoomed);
        Assert.Equal(12, tile.Layers[0].Features.Count);
    }

    [Fact]
    public async Task ReturnsNullForMissingTile()
    {
        var archive = TestArchives.Build([1, 2, 3], PmtilesTileType.Mlt);
        using var source = new PmtilesTileSource(new PmtilesStreamRangeSource(new MemoryStream(archive)));

        Assert.Null(await source.GetVectorTile(1, 0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsTileAtOffsetBeyondTwoGigabytes()
    {
        var mlt = TestArchives.ReadFixture("stations.mlt");
        var directory = TestArchives.DirectoryBytes(mlt.Length);
        const long tileOffset = 3_000_000_000L;
        var header = TestArchives.BuildHeader(directory.Length, tileOffset, mlt.Length, PmtilesTileType.Mlt, PmtilesCompression.None);
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
        var archive = TestArchives.Build([1, 2, 3], PmtilesTileType.Mlt);
        var header = PmtilesCodec.ParseHeader(archive);

        Assert.Equal(3, header.Version);
        Assert.Equal(PmtilesTileType.Mlt, header.TileType);
        Assert.Equal(PmtilesCompression.None, header.TileCompression);
        Assert.Equal(0UL, PmtilesCodec.ZxyToTileId(0, 0, 0));
        Assert.Single(PmtilesCodec.DeserializeDirectory(archive.AsSpan(127, (int)header.RootDirectoryLength)));
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
}

