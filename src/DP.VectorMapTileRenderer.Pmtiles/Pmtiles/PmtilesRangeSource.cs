namespace DP.VectorMapTileRenderer.Pmtiles;

/// <summary>Byte-range access to a PMTiles archive (local file, HTTP range request, MinIO, ...).</summary>
public interface IPmtilesRangeSource
{
    /// <summary>Stable identity used for diagnostics and archive caching.</summary>
    string Key { get; }

    Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken = default);
}

/// <summary>Reads ranges from a local file with <see cref="RandomAccess"/> (thread-safe, no seek state).</summary>
public sealed class PmtilesFileRangeSource : IPmtilesRangeSource, IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;
    private bool _disposed;

    public PmtilesFileRangeSource(string path)
    {
        Key = Path.GetFullPath(path);
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string Key { get; }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await RandomAccess
                .ReadAsync(_handle, buffer.AsMemory(read, length - read), offset + read, cancellationToken)
                .ConfigureAwait(false);

            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        return read == length ? buffer : buffer[..read];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _handle.Dispose();
        _disposed = true;
    }
}

/// <summary>Ranges over a seekable <see cref="Stream"/> (tests, already-open files, memory).</summary>
public sealed class PmtilesStreamRangeSource : IPmtilesRangeSource
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PmtilesStreamRangeSource(Stream stream, string? key = null)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("A PMTiles range stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        Key = key ?? $"stream:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(stream)}";
    }

    public string Key { get; }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken = default)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream.Position = offset;
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var chunk = await _stream
                    .ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                    .ConfigureAwait(false);
                if (chunk == 0)
                {
                    break;
                }

                read += chunk;
            }

            return read == length ? buffer : buffer[..read];
        }
        finally
        {
            _lock.Release();
        }
    }
}
