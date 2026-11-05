namespace DP.VectorMapTileRenderer.GeoJson;

/// <summary>
/// A vector tile source backed by one GeoJSON document. The document is parsed once and cut into
/// the requested tile on every call; coordinates are WGS84 lon/lat.
/// </summary>
public sealed class GeoJsonTileSource : Sources.IVectorTileSource
{
    private readonly string? _path;
    private readonly byte[]? _utf8Json;
    private readonly GeoJsonTileSet _set;

    public GeoJsonTileSource(string path)
    {
        _path = System.IO.Path.GetFullPath(path);
        using var stream = File.OpenRead(_path);
        _set = GeoJsonTileSet.Parse(stream);
    }

    public GeoJsonTileSource(Stream stream)
        : this(ReadAll(stream))
    {
    }

    public GeoJsonTileSource(byte[] utf8Json)
    {
        _utf8Json = utf8Json;
        _set = GeoJsonTileSet.Parse(utf8Json);
    }

    private GeoJsonTileSource(string path, GeoJsonTileSet set)
    {
        _path = path;
        _set = set;
    }

    /// <summary>Loads the document asynchronously; preferable for large files at startup.</summary>
    public static async Task<GeoJsonTileSource> CreateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        await using var stream = File.OpenRead(fullPath);
        var set = await GeoJsonTileSet.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
        return new GeoJsonTileSource(fullPath, set);
    }

    /// <summary>Tile grid size the geometry is quantized to (typically 4096).</summary>
    public double Extent { get; init; } = 4096;

    /// <summary>Layer name the features are exposed under, for the style's <c>source-layer</c>.</summary>
    public string LayerName { get; init; } = "geojson";

    /// <summary>Coordinate space of the source document.</summary>
    public GeoJsonCoordinateSpace CoordinateSpace { get; init; } = GeoJsonCoordinateSpace.Wgs84;

    /// <summary>Returns the raw GeoJSON document. Use <see cref="GetVectorTile"/> for tile content.</summary>
    public Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(_path is not null
            ? File.OpenRead(_path)
            : new MemoryStream(_utf8Json!, writable: false));

    public Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
        Task.FromResult<VectorTile?>(_set.CreateTile(x, y, zoom, Extent, LayerName, CoordinateSpace));

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
