namespace DP.VectorMapTileRenderer;

/// <summary>Vector-tile encodings understood by the library.</summary>
public enum VectorTileEncoding
{
    /// <summary>MapLibre Tile v1 (columnar).</summary>
    Mlt,

    /// <summary>Mapbox Vector Tile 2.x (protobuf).</summary>
    Mvt
}

/// <summary>
/// Single entry point for decoding a vector tile. Both encodings the library understands are read
/// here: MapLibre Tile (MLT v1), which our workers write, and Mapbox Vector Tile (MVT), which
/// third-party archives and older pipelines still carry.
/// </summary>
public static class VectorTileReader
{
    /// <summary>
    /// Reads a tile. With <paramref name="encoding"/> left as <see langword="null"/> the encoding is
    /// detected from the payload: an MLT tile starts with a varint frame size followed by the layer
    /// frame tag <c>0x01</c>, an MVT tile starts with the layer field tag <c>0x1A</c>. Detection
    /// prefers MLT, so an MLT frame size that happens to be <c>0x1A</c> is not read as MVT.
    /// </summary>
    public static VectorTile Read(ReadOnlySpan<byte> tile, VectorTileEncoding? encoding = null) =>
        (encoding ?? Detect(tile)) == VectorTileEncoding.Mvt
            ? Mvt.MvtTileReader.Read(tile)
            : Mlt.MltTileReader.Read(tile);

    /// <summary>Sniffs the encoding of a payload; see <see cref="Read"/> for the rule.</summary>
    public static VectorTileEncoding Detect(ReadOnlySpan<byte> tile)
    {
        if (tile.Length == 0 || tile[0] != 0x1A)
        {
            return VectorTileEncoding.Mlt;
        }

        // The first byte matches the MVT layer tag, but it is also the low byte of a 26-byte MLT
        // frame size. The byte after that varint tells them apart: MLT frames carry tag 0x01.
        var size = 0;
        var shift = 0;
        var index = 0;
        while (index < tile.Length && index < 5)
        {
            var current = tile[index++];
            size |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        return size > 0 && index < tile.Length && tile[index] == 0x01
            ? VectorTileEncoding.Mlt
            : VectorTileEncoding.Mvt;
    }
}
