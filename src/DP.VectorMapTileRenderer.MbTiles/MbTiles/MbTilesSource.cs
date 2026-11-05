using Microsoft.Data.Sqlite;

namespace DP.VectorMapTileRenderer.MbTiles;

/// <summary>How <c>tile_row</c> is interpreted in an MBTiles database.</summary>
public enum MbTilesTileRowOrder
{
    /// <summary>MBTiles spec: row 0 is the southern edge (the y axis of XYZ tiling is flipped).</summary>
    Tms,

    /// <summary>Non-standard archives that store slippy/XYZ rows as-is.</summary>
    Slippy
}

/// <summary>Optional contents of the MBTiles <c>metadata</c> table.</summary>
public sealed record MbTilesMetadata
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Tile payload format: <c>pbf</c>/<c>mvt</c>/<c>mlt</c>/<c>png</c>/<c>jpg</c>.</summary>
    public string? Format { get; init; }

    public string? Version { get; init; }

    public int? MinZoom { get; init; }

    public int? MaxZoom { get; init; }

    public GlobalMercator.GeoExtent? Bounds { get; init; }

    public GlobalMercator.CoordinatePair? Center { get; init; }

    public int? CenterZoom { get; init; }
}

/// <summary>
/// Reads tiles from an MBTiles (SQLite) archive. Supports both the classic <c>tiles</c> table and
/// the normalized <c>map</c> + <c>images</c> schema. Gzipped <c>tile_data</c> is unwrapped and
/// vector payloads are decoded with <see cref="VectorTileReader"/> (MapLibre Tile).
/// </summary>
public sealed class MbTilesSource : Sources.IVectorTileSource
{
    private readonly string _connectionString;
    private readonly MbTilesTileRowOrder _rowOrder;
    private readonly MbTilesSchema _schema;

    public MbTilesSource(
        string path,
        MbTilesTileRowOrder rowOrder = MbTilesTileRowOrder.Tms)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MBTiles archive was not found.", path);
        }

        Path = System.IO.Path.GetFullPath(path);
        _rowOrder = rowOrder;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        Metadata = LoadMetadata();
        _schema = DetectSchema();
        HasTileIndex = _schema switch
        {
            MbTilesSchema.Tiles => DetectTileIndex("tiles"),
            MbTilesSchema.MapImages => DetectTileIndex("map"),
            _ => false
        };
    }

    public string Path { get; }

    public MbTilesMetadata Metadata { get; }

    public MbTilesSchema Schema => _schema;

    /// <summary>
    /// True when the archive has an index covering <c>zoom_level</c>/<c>tile_column</c>/<c>tile_row</c>.
    /// Standard MBTiles archives do; without it a tile lookup degrades to a full table scan.
    /// </summary>
    public bool HasTileIndex { get; }

    public MbTilesTileRowOrder RowOrder => _rowOrder;

    public async Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        var bytes = await GetTileBytesAsync(x, y, zoom, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes ?? [], writable: false);
    }

    public async Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        if (Metadata.Format is "png" or "jpg" or "jpeg" or "webp" or "avif")
        {
            return null;
        }

        var fetchZoom = zoom;
        var fetchX = x;
        var fetchY = y;

        // Serve the deepest available tile and stretch it.
        if (Metadata.MaxZoom is { } maxZoom && zoom > maxZoom)
        {
            if (!TileOverzoom.TryGetParent(x, y, zoom, maxZoom, 4096, out fetchX, out fetchY, out _))
            {
                return null;
            }

            fetchZoom = maxZoom;
        }

        var bytes = await GetTileBytesAsync(fetchX, fetchY, fetchZoom, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        if (Sources.FileVectorTileSource.IsGzip(bytes))
        {
            bytes = Sources.FileVectorTileSource.Decompress(bytes);
        }

        var tile = VectorTileReader.Read(bytes);
        return fetchZoom == zoom ? tile : TileOverzoom.Apply(tile, x, y, zoom, fetchZoom);
    }

    public async Task<byte[]?> GetTileBytesAsync(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        if (_schema == MbTilesSchema.Unknown)
        {
            throw new InvalidDataException(
                "The MBTiles archive has neither a 'tiles' table nor the normalized 'map' + 'images' schema.");
        }

        if (zoom is < 0 or > 30 || x < 0 || y < 0 || x >= (1 << zoom) || y >= (1 << zoom))
        {
            return null;
        }

        var row = _rowOrder == MbTilesTileRowOrder.Tms ? (1 << zoom) - 1 - y : y;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = _schema switch
        {
            MbTilesSchema.MapImages =>
                """
                SELECT images.tile_data
                FROM map
                JOIN images ON map.tile_id = images.tile_id
                WHERE map.zoom_level = $z AND map.tile_column = $x AND map.tile_row = $y
                LIMIT 1;
                """,
            _ => """
                SELECT tile_data
                FROM tiles
                WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y
                LIMIT 1;
                """
        };
        command.Parameters.AddWithValue("$z", zoom);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", row);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await reader
            .GetFieldValueAsync<byte[]>(0, cancellationToken)
            .ConfigureAwait(false);
    }

    private MbTilesSchema DetectSchema()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view');";

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    names.Add(reader.GetString(0));
                }
            }

            if (names.Contains("tiles"))
            {
                return MbTilesSchema.Tiles;
            }

            return names.Contains("map") && names.Contains("images")
                ? MbTilesSchema.MapImages
                : MbTilesSchema.Unknown;
        }
        catch (SqliteException)
        {
            return MbTilesSchema.Unknown;
        }
    }

    private bool DetectTileIndex(string table)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var indexNames = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list('{table}');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(1))
                    {
                        indexNames.Add(reader.GetString(1));
                    }
                }
            }

            foreach (var indexName in indexNames)
            {
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''", StringComparison.Ordinal)}');";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(2))
                    {
                        columns.Add(reader.GetString(2));
                    }
                }

                if (columns.Contains("zoom_level") && columns.Contains("tile_column") && columns.Contains("tile_row"))
                {
                    return true;
                }
            }

            return false;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private MbTilesMetadata LoadMetadata()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, value FROM metadata;";

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            return new MbTilesMetadata
            {
                Name = values.GetValueOrDefault("name"),
                Description = values.GetValueOrDefault("description"),
                Format = values.GetValueOrDefault("format"),
                Version = values.GetValueOrDefault("version"),
                MinZoom = ParseInt(values.GetValueOrDefault("minzoom")),
                MaxZoom = ParseInt(values.GetValueOrDefault("maxzoom")),
                Bounds = ParseBounds(values.GetValueOrDefault("bounds")),
                Center = ParseCenter(values.GetValueOrDefault("center")),
                CenterZoom = ParseInt(values.GetValueOrDefault("center")?.Split(',') is { Length: > 2 } center
                    ? center[2]
                    : null)
            };
        }
        catch (SqliteException)
        {
            // `metadata` is optional in practice; a missing/invalid table must not hide usable tiles.
            return new MbTilesMetadata();
        }
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static GlobalMercator.GeoExtent? ParseBounds(string? value)
    {
        var parts = value?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 })
        {
            return null;
        }

        var west = ParseDouble(parts[0]);
        var south = ParseDouble(parts[1]);
        var east = ParseDouble(parts[2]);
        var north = ParseDouble(parts[3]);
        return west is null || south is null || east is null || north is null
            ? null
            : new GlobalMercator.GeoExtent(north.Value, south.Value, east.Value, west.Value);
    }

    private static GlobalMercator.CoordinatePair? ParseCenter(string? value)
    {
        var parts = value?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        var x = ParseDouble(parts[0]);
        var y = ParseDouble(parts[1]);
        return x is null || y is null ? null : new GlobalMercator.CoordinatePair(x.Value, y.Value);
    }
}

/// <summary>MBTiles table layout detected in the archive.</summary>
public enum MbTilesSchema
{
    Unknown,
    Tiles,
    MapImages
}
