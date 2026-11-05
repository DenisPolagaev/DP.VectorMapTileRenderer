using System.IO.Compression;

namespace DP.VectorMapTileRenderer.Sources;

/// <summary>Provides a raw tile payload (MVT/MLT bytes, PNG/JPEG raster, ...).</summary>
public interface ITileSource
{
    Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default);
}

/// <summary>Provides a decoded vector tile.</summary>
public interface IVectorTileSource : ITileSource
{
    Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads vector tiles from a file path template (<c>{x}</c>/<c>{y}</c>/<c>{z}</c>) or from a stream.
/// Gzip-compressed payloads are detected by magic bytes and decompressed automatically.
/// </summary>
public sealed class FileVectorTileSource : IVectorTileSource
{
    private readonly string? _path;
    private readonly Stream? _stream;
    private readonly VectorTileEncoding? _encoding;
    private readonly SemaphoreSlim _streamLock = new(1, 1);

    public FileVectorTileSource(string path, VectorTileEncoding? encoding = null)
    {
        _path = path;
        _encoding = encoding;
    }

    public FileVectorTileSource(Stream stream, VectorTileEncoding? encoding = null)
    {
        _stream = stream;
        _encoding = encoding;
    }

    public async Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        if (_stream is not null)
        {
            await _streamLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stream.CanSeek)
                {
                    _stream.Position = 0;
                }

                var buffer = new MemoryStream();
                await _stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                buffer.Position = 0;
                return buffer;
            }
            finally
            {
                _streamLock.Release();
            }
        }

        var path = ResolvePath(x, y, zoom);
        return new MemoryStream(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            writable: false);
    }

    public async Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        await using var tile = await GetTile(x, y, zoom, cancellationToken);
        var bytes = await ReadAllAsync(tile, cancellationToken).ConfigureAwait(false);

        if (IsGzip(bytes))
        {
            bytes = Decompress(bytes);
        }

        return bytes.Length == 0 ? null : VectorTileReader.Read(bytes, _encoding);
    }

    private string ResolvePath(int x, int y, int zoom)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("This source reads from a stream, not from a file path.");
        }

        return _path
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static bool IsGzip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    public static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream memory)
        {
            return memory.ToArray();
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

/// <summary>Reads raster tiles from a file path template.</summary>
public sealed class RasterTileSource : ITileSource
{
    public RasterTileSource(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default)
    {
        var path = Path
            .Replace("{x}", x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{z}", zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Task.FromResult<Stream>(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
    }
}
