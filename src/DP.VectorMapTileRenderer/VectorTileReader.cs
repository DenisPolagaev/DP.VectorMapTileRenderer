namespace DP.VectorMapTileRenderer;

/// <summary>
/// Single entry point for decoding a vector tile. The library reads **MapLibre Tile
/// (MLT v1)** only: it is the encoding our workers write, and there is deliberately no
/// MVT decoder or MVT/MVT auto-detection to keep a legacy path alive.
/// </summary>
public static class VectorTileReader
{
    /// <summary>Reads one MLT tile. Gzip payloads are unwrapped by the sources, not here.</summary>
    public static VectorTile Read(ReadOnlySpan<byte> tile) => Mlt.MltTileReader.Read(tile);
}
