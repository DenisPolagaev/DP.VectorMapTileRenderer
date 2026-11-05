# DP.VectorMapTileRenderer.MbTiles

MBTiles (SQLite) tile source for `DP.VectorMapTileRenderer`.

## Install

```bash
dotnet add package DP.VectorMapTileRenderer.MbTiles
```

Depends on `DP.VectorMapTileRenderer` and `Microsoft.Data.Sqlite`.

## Usage

```csharp
using DP.VectorMapTileRenderer.MbTiles;

var source = new MbTilesSource("zurich.mbtiles");
style.SetSourceProvider("vector", source);

var tile = await source.GetVectorTile(x, y, zoom);
var bytes = await source.GetTileBytesAsync(x, y, zoom);
```

## Features

- Classic `tiles` table and normalized `map` + `images` schema (auto-detected, `Schema` property).
- Standard TMS rows by default; `MbTilesTileRowOrder.Slippy` for non-standard archives.
- Gzipped `tile_data` is unwrapped.
- `metadata` (format, min/max zoom, bounds, center) exposed as `Metadata`.
- `HasTileIndex` reports whether the archive has the standard tile index.
- Overzoom past `metadata.maxzoom`.
- Vector decoding via `VectorTileReader` (MapLibre Tile).
- Read-only, pooled SQLite connections; safe for concurrent reads.

## Large files

Each tile is one indexed point query. On a 1,000,000-tile archive a warm lookup is ~70 µs
(p99 ~110 µs). Without the standard index SQLite falls back to a table scan — check
`HasTileIndex` and add the index when generating the archive.

## License

MIT.
