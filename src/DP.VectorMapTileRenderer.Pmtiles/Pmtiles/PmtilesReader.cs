namespace DP.VectorMapTileRenderer.Pmtiles;

/// <summary>A parsed PMTiles v3 archive: header plus the root directory.</summary>
public sealed class PmtilesArchive
{
    public PmtilesArchive(string key, PmtilesHeader header, IReadOnlyList<PmtilesEntry> root, string? etag = null)
    {
        Key = key;
        Header = header;
        Root = root;
        ETag = etag;
    }

    public string Key { get; }

    public PmtilesHeader Header { get; }

    public IReadOnlyList<PmtilesEntry> Root { get; }

    public string? ETag { get; }

    internal PmtilesDirectoryCache LeafCache { get; } = new();
}

/// <summary>
/// A tiny bounded LRU cache for leaf directories. A large archive can have thousands of leaves;
/// keeping all of them would grow without bound over a long-lived process.
/// </summary>
internal sealed class PmtilesDirectoryCache
{
    private const int Capacity = 256;

    private readonly Dictionary<(ulong Offset, ulong Length), IReadOnlyList<PmtilesEntry>> _values = [];
    private readonly Dictionary<(ulong Offset, ulong Length), LinkedListNode<(ulong Offset, ulong Length)>> _nodes = [];
    private readonly LinkedList<(ulong Offset, ulong Length)> _order = new();
    private readonly object _gate = new();

    public bool TryGet((ulong Offset, ulong Length) key, out IReadOnlyList<PmtilesEntry> entries)
    {
        lock (_gate)
        {
            if (!_values.TryGetValue(key, out var found))
            {
                entries = [];
                return false;
            }

            // Move to the most-recently-used end.
            if (_nodes.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }

            entries = found;
            return true;
        }
    }

    public void Set((ulong Offset, ulong Length) key, IReadOnlyList<PmtilesEntry> entries)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out _))
            {
                return;
            }

            _values[key] = entries;
            _nodes[key] = _order.AddFirst(key);

            while (_values.Count > Capacity)
            {
                var oldest = _order.Last;
                if (oldest is null)
                {
                    break;
                }

                _order.RemoveLast();
                _values.Remove(oldest.Value);
                _nodes.Remove(oldest.Value);
            }
        }
    }
}

/// <summary>Reads PMTiles v3 archives and individual tiles over an <see cref="IPmtilesRangeSource"/>.</summary>
public static class PmtilesReader
{
    public static async Task<PmtilesArchive?> ReadArchiveAsync(
        IPmtilesRangeSource source,
        CancellationToken cancellationToken = default)
    {
        var headerBytes = await source
            .ReadAsync(0, PmtilesCodec.HeaderSize, cancellationToken)
            .ConfigureAwait(false);
        if (headerBytes.Length < PmtilesCodec.HeaderSize)
        {
            return null;
        }

        var header = PmtilesCodec.ParseHeader(headerBytes);
        if (header.Version != 3)
        {
            throw new NotSupportedException($"PMTiles version {header.Version} is not supported; only v3 is.");
        }

        if (header.RootDirectoryOffset > long.MaxValue
            || header.LeafDirectoryOffset > long.MaxValue
            || header.TileDataOffset > long.MaxValue)
        {
            throw new InvalidDataException("PMTiles section offset exceeds Int64.");
        }

        if (header.RootDirectoryLength > int.MaxValue)
        {
            throw new InvalidDataException("PMTiles root directory is too large.");
        }

        var rootBytes = await source
            .ReadAsync(
                checked((long)header.RootDirectoryOffset),
                (int)header.RootDirectoryLength,
                cancellationToken)
            .ConfigureAwait(false);

        var root = PmtilesCodec.DeserializeDirectory(
            PmtilesCodec.Decompress(rootBytes, header.InternalCompression));

        return new PmtilesArchive(source.Key, header, root);
    }

    /// <summary>Returns the decompressed tile payload, or <see langword="null"/> when the tile is absent.</summary>
    public static async Task<byte[]?> ReadTileAsync(
        IPmtilesRangeSource source,
        PmtilesArchive archive,
        int z,
        int x,
        int y,
        CancellationToken cancellationToken = default)
    {
        var header = archive.Header;
        if (z < header.MinZoom || z > header.MaxZoom)
        {
            return null;
        }

        var max = (1 << z) - 1;
        if ((uint)x > (uint)max || (uint)y > (uint)max)
        {
            return null;
        }

        var tileId = PmtilesCodec.ZxyToTileId(z, x, y);
        var entry = PmtilesCodec.FindTile(archive.Root, tileId);
        if (entry is null)
        {
            return null;
        }

        if (entry.Value.RunLength == 0)
        {
            if (!IsInsideSection(entry.Value, header.LeafDirectoryOffset, header.LeafDirectoryLength))
            {
                return null;
            }

            var leaf = await ReadLeafAsync(source, archive, entry.Value, cancellationToken).ConfigureAwait(false);
            if (leaf is null)
            {
                return null;
            }

            entry = PmtilesCodec.FindTile(leaf, tileId);
            if (entry is null || entry.Value.RunLength == 0)
            {
                return null;
            }
        }

        if (entry.Value.Length == 0
            || entry.Value.Length > int.MaxValue
            || !IsInsideSection(entry.Value, header.TileDataOffset, header.TileDataLength))
        {
            return null;
        }

        var offset = checked((long)(header.TileDataOffset + entry.Value.Offset));
        var data = await source
            .ReadAsync(offset, (int)entry.Value.Length, cancellationToken)
            .ConfigureAwait(false);
        return PmtilesCodec.Decompress(data, header.TileCompression);
    }

    private static bool IsInsideSection(PmtilesEntry entry, ulong sectionOffset, ulong sectionLength) =>
        entry.Offset <= sectionLength
        && entry.Length <= sectionLength - entry.Offset
        && sectionOffset <= ulong.MaxValue - entry.Offset
        && sectionOffset + entry.Offset <= (ulong)long.MaxValue;

    private static async Task<IReadOnlyList<PmtilesEntry>?> ReadLeafAsync(
        IPmtilesRangeSource source,
        PmtilesArchive archive,
        PmtilesEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length == 0 || entry.Length > int.MaxValue)
        {
            return null;
        }

        if (archive.LeafCache.TryGet((entry.Offset, entry.Length), out var cached))
        {
            return cached;
        }

        var offset = checked((long)(archive.Header.LeafDirectoryOffset + entry.Offset));
        var bytes = await source
            .ReadAsync(offset, (int)entry.Length, cancellationToken)
            .ConfigureAwait(false);
        var directory = PmtilesCodec.DeserializeDirectory(
            PmtilesCodec.Decompress(bytes, archive.Header.InternalCompression));

        archive.LeafCache.Set((entry.Offset, entry.Length), directory);
        return directory;
    }
}
