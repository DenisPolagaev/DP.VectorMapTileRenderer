namespace DP.VectorMapTileRenderer.Pmtiles;

/// <summary>
/// A tile source over a PMTiles v3 archive. Serves raw raster payloads through
/// <see cref="ITileSource.GetTile"/> and decodes vector payloads (MapLibre Tile, per the archive
/// header) through <see cref="IVectorTileSource.GetVectorTile"/>.
/// </summary>
public sealed class PmtilesTileSource : Sources.ITileSource, Sources.IVectorTileSource, IDisposable
{
    private readonly IPmtilesRangeSource _source;
    private readonly bool _ownsSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PmtilesArchive? _archive;
    private bool _loaded;
    private bool _disposed;

    public PmtilesTileSource(string path)
        : this(new PmtilesFileRangeSource(path), ownsSource: true)
    {
    }

    public PmtilesTileSource(IPmtilesRangeSource source, bool ownsSource = false)
    {
        _source = source;
        _ownsSource = ownsSource;
    }

    public async Task<PmtilesArchive?> GetArchiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_loaded)
        {
            return _archive;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                _archive = await PmtilesReader
                    .ReadArchiveAsync(_source, cancellationToken)
                    .ConfigureAwait(false);
                _loaded = true;
            }

            return _archive;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        var bytes = await GetTileBytesAsync(x, y, zoom, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(bytes ?? [], writable: false);
    }

    public async Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        var archive = await GetArchiveAsync(cancellationToken).ConfigureAwait(false);
        if (archive is null || !PmtilesCodec.IsVector(archive.Header.TileType))
        {
            return null;
        }

        var fetchZoom = zoom;
        var fetchX = x;
        var fetchY = y;

        if (zoom > archive.Header.MaxZoom)
        {
            if (!TileOverzoom.TryGetParent(x, y, zoom, archive.Header.MaxZoom, 4096, out fetchX, out fetchY, out _))
            {
                return null;
            }

            fetchZoom = archive.Header.MaxZoom;
        }

        var bytes = await PmtilesReader
            .ReadTileAsync(_source, archive, fetchZoom, fetchX, fetchY, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        var tile = VectorTileReader.Read(bytes);
        return fetchZoom == zoom ? tile : TileOverzoom.Apply(tile, x, y, zoom, fetchZoom);
    }

    public async Task<byte[]?> GetTileBytesAsync(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var archive = await GetArchiveAsync(cancellationToken).ConfigureAwait(false);
        return archive is null
            ? null
            : await PmtilesReader
                .ReadTileAsync(_source, archive, zoom, x, y, cancellationToken)
                .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_ownsSource && _source is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _gate.Dispose();
        _disposed = true;
    }
}
