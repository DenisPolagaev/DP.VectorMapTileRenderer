using System.IO.Compression;
using DP.VectorMapTileRenderer.MbTiles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class MbTilesTests
{
    [Fact]
    public async Task ReadsMltTileFromTmsRow()
    {
        var path = CreateArchive(("mlt", ReadFixture("stations.mlt"), zoom: 0, column: 0, row: 0));

        try
        {
            var source = new MbTilesSource(path);
            var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

            Assert.NotNull(tile);
            Assert.Equal(12, Assert.Single(tile.Layers).Features.Count);
            Assert.Equal("mlt", source.Metadata.Format);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FlipsSlippyYForTmsArchives()
    {
        // slippy 1/0/0 is the north-west tile; MBTiles stores it in TMS row 1.
        var path = CreateArchive(("mlt", ReadFixture("stations.mlt"), zoom: 1, column: 0, row: 1));

        try
        {
            var source = new MbTilesSource(path);

            Assert.NotNull(await source.GetVectorTile(0, 0, 1, TestContext.Current.CancellationToken));
            Assert.Null(await source.GetVectorTile(0, 1, 1, TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsGzippedTileData()
    {
        var path = CreateArchive(("mlt", Gzip(ReadFixture("stations.mlt")), zoom: 0, column: 0, row: 0));

        try
        {
            var source = new MbTilesSource(path);
            Assert.NotNull(await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsesMetadataAndBounds()
    {
        var path = CreateArchive(("pbf", ReadFixture("stations.mlt"), zoom: 0, column: 0, row: 0));

        try
        {
            var source = new MbTilesSource(path);

            Assert.Equal(0, source.Metadata.MinZoom);
            Assert.Equal(5, source.Metadata.MaxZoom);
            Assert.True(source.HasTileIndex);
            Assert.NotNull(source.Metadata.Bounds);
            Assert.Equal(8.5, source.Metadata.Bounds!.East, 3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsNormalizedMapImagesSchema()
    {
        var path = CreateNormalizedArchive(ReadFixture("stations.mlt"));

        try
        {
            var source = new MbTilesSource(path);
            var tile = await source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken);

            Assert.NotNull(tile);
            Assert.Equal(12, Assert.Single(tile!.Layers).Features.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OverzoomsBeyondMaxZoom()
    {
        var path = CreateArchive(("mlt", ReadFixture("stations.mlt"), zoom: 0, column: 0, row: 0), maxZoom: 0);

        try
        {
            var source = new MbTilesSource(path);

            // 1/1/1 is the south-east quarter of the only tile in the archive.
            var tile = await source.GetVectorTile(1, 1, 1, TestContext.Current.CancellationToken);

            Assert.NotNull(tile);
            Assert.True(tile!.IsOverZoomed);
            var first = tile.Layers[0].Features[0].Geometry[0][0];
            Assert.Equal(-4096, first.X, 3);
            Assert.Equal(4096, first.Y, 3);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SupportsConcurrentReads()
    {
        var path = CreateArchive(("mlt", ReadFixture("stations.mlt"), zoom: 0, column: 0, row: 0));

        try
        {
            var source = new MbTilesSource(path);
            var tiles = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                source.GetVectorTile(0, 0, 0, TestContext.Current.CancellationToken)));

            Assert.All(tiles, tile => Assert.NotNull(tile));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string CreateArchive(
        (string Format, byte[] Data, int zoom, int column, int row) tile,
        int maxZoom = 5)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-mbtiles-{Guid.NewGuid():N}.mbtiles");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE metadata (name TEXT, value TEXT);
                CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
                CREATE UNIQUE INDEX tile_index ON tiles (zoom_level, tile_column, tile_row);
                """;
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO metadata (name, value) VALUES ($name, $value);";
            command.Parameters.AddWithValue("$name", "format");
            command.Parameters.AddWithValue("$value", tile.Format);
            command.ExecuteNonQuery();
        }

        foreach (var (name, value) in new[]
                 {
                     ("minzoom", "0"),
                     ("maxzoom", maxZoom.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                     ("bounds", "-10,-20,8.5,30"),
                     ("name", "test")
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO metadata (name, value) VALUES ($name, $value);";
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES ($z, $x, $y, $data);";
            command.Parameters.AddWithValue("$z", tile.zoom);
            command.Parameters.AddWithValue("$x", tile.column);
            command.Parameters.AddWithValue("$y", tile.row);
            command.Parameters.AddWithValue("$data", tile.Data);
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static string CreateNormalizedArchive(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-mbtiles-norm-{Guid.NewGuid():N}.mbtiles");

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE metadata (name TEXT, value TEXT);
                CREATE TABLE map (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_id TEXT);
                CREATE TABLE images (tile_id TEXT, tile_data BLOB);
                INSERT INTO metadata (name, value) VALUES ('format', 'mlt'), ('maxzoom', '0');
                INSERT INTO images (tile_id, tile_data) VALUES ('tile-0', $data);
                INSERT INTO map (zoom_level, tile_column, tile_row, tile_id) VALUES (0, 0, 0, 'tile-0');
                """;
            command.Parameters.AddWithValue("$data", data);
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return path;
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
}
