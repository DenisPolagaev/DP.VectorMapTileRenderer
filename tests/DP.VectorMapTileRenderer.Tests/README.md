# DP.VectorMapTileRenderer.Tests

Test suite for the `DP.VectorMapTileRenderer` packages. Runs on `net8.0`, `net9.0`, `net10.0` and
`net11.0`.

## Run

```bash
dotnet test tests/DP.VectorMapTileRenderer.Tests
```

## Coverage

- golden MLT tiles produced by the Python workers (metadata, geometry, properties);
- MVT decoding (points, polygons, tags, packed commands);
- PMTiles v3: MLT/MVT/raster payloads, gzip/brotli/zstd, >2 GB offsets, corrupt directories,
  overzoom, concurrent reads;
- MBTiles: `tiles` and `map`/`images` schemas, TMS/Slippy, gzip, metadata, index detection,
  overzoom, concurrent reads;
- GeoJSON: coordinate spaces, clipping, STRtree filtering, `CreateAsync`, concurrent tile
  extraction;
- style engine: filters, zoom/property stops, MapLibre expressions, text anchors, colors;
- renderer: polygon holes, sprite icons, labels, tile rendering.

Fixtures live in `Fixtures/` and are copied to the output directory.
