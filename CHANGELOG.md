# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- `PmtilesHttpRangeSource`: reads PMTiles v3 archives over HTTP(S) with `Range` requests. A partial
  response must be `206` with a matching `Content-Range` (a `200` fails fast instead of downloading
  the whole archive per slice), `416` reads as "no data", other statuses surface as
  `HttpRequestException` with the status code. The origin's `ETag` and total length are captured;
  the `HttpClient` stays caller-owned (timeouts, retries, connection pooling) and automatic
  decompression must be disabled on it.
- `PmtilesTileSource(HttpClient, Uri)` convenience constructor over `PmtilesHttpRangeSource`.
- Golden fixtures `vegetation_cells` (nullable string + nullable numeric column) and
  `mixed_geometry` (Polygon + LineString + MultiPoint + MultiPolygon in one layer), plus explicit
  nullable-value and mixed-geometry assertions in `MltTileReaderTests`.

### Fixed

- `MltTileReader` could not read a **nullable string column**: the column's stream count precedes
  the `Present` stream and includes it (`hasStreamCount` → STRING, same as `@maplibre/mlt`), but the
  reader consumed `Present` first and then read the count as if it were a stream header. Every tile
  with a partially-null string property (e.g. vegetation `region_id`, fire `satellite_id`) failed
  with a misleading `SIMD-FastPFOR`/boolean-stream error. A null value in such a column is now
  returned as `null`.
- Boolean streams validate their payload length and byte-RLE coverage instead of throwing
  `ArgumentOutOfRangeException` from a slice (a parse desync now reports an `MltFormatException`).

## [1.0.0] - 2026-11-05

### Added

- Vector tile model and decoders: MapLibre Tile v1 (`Mlt.MltTileReader`) and
  Mapbox Vector Tile 2.x (`Mvt.MvtTileReader`), MLT/MVT auto-detection and gzip support.
- MapLibre/Mapbox style engine: sources, layers, filters, zoom/property `stops`,
  expressions (`case`, `match`, `interpolate`, `step`, `get`, `has`, `coalesce`,
  comparisons, logical operators, `concat`, `to-string`, `to-number`, `geometry-type`).
- SkiaSharp renderer: fill/line/circle/background/symbol/raster layers, polygon holes
  and multipolygons, dashes, line joins/offsets, text with wrapping/halo/anchor/rotation,
  text on paths and collision detection.
- Sprite atlas support: `icon-image` (size/rotate/offset/opacity) and sprite-based
  `fill-pattern`, `line-pattern`, `background-pattern`.
- Tile sources: local files, raster tiles, PMTiles v3 (range reads, gzip/brotli/zstd,
  overzoom), MBTiles (SQLite, `tiles` and `map`/`images` schemas), GeoJSON (WGS84,
  Web Mercator and tile-local coordinates, STRtree extraction).
- Overzoom for PMTiles/MBTiles, large-archive safeguards (long offsets, bounded LRU,
  section validation), cancellation tokens and parallel-safe sources.
- Packages target `net8.0`, `net9.0`, `net10.0` and `net11.0`.
- Test suite covering golden MLT/MVT tiles, archives, style expressions, rendering,
  sprite icons, polygon holes, overzoom, large offsets and concurrency.

[1.0.0]: https://github.com/DenisPolagaev/DP.VectorMapTileRenderer/releases/tag/v1.0.0
