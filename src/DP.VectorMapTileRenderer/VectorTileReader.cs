namespace DP.VectorMapTileRenderer;

/// <summary>Vector-tile encodings understood by the library.</summary>
public enum VectorTileEncoding
{
    /// <summary>MapLibre Tile v1 (columnar).</summary>
    Mlt,

    /// <summary>Mapbox Vector Tile 2.x (protobuf).</summary>
    Mvt
}

/// <summary>Single entry point for decoding either supported vector-tile encoding.</summary>
public static class VectorTileReader
{
    /// <summary>
    /// Reads a tile. With <paramref name="encoding"/> left as <see langword="null"/> the encoding is
    /// detected from the first byte: MVT tiles always start with the layer field tag <c>0x1A</c>,
    /// while MLT tiles start with a varint frame size.
    /// </summary>
    public static VectorTile Read(ReadOnlySpan<byte> tile, VectorTileEncoding? encoding = null) =>
        (encoding ?? Detect(tile)) == VectorTileEncoding.Mvt
            ? Mvt.MvtTileReader.Read(tile)
            : Mlt.MltTileReader.Read(tile);

    public static VectorTileEncoding Detect(ReadOnlySpan<byte> tile) =>
        tile.Length > 0 && tile[0] == 0x1A ? VectorTileEncoding.Mvt : VectorTileEncoding.Mlt;
}
