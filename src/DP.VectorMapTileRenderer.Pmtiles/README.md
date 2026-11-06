# DP.VectorMapTileRenderer.Pmtiles

PMTiles v3 tile source for `DP.VectorMapTileRenderer`.

## Install

```bash
dotnet add package DP.VectorMapTileRenderer.Pmtiles
```

Depends on `DP.VectorMapTileRenderer` and `ZstdSharp.Port`.

## Usage

```csharp
using DP.VectorMapTileRenderer.Pmtiles;

using var source = new PmtilesTileSource("tiles/2026/11/05/12.pmtiles");
style.SetSourceProvider("weather", source);

// raw tile bytes (raster or vector)
var bytes = await source.GetTileBytesAsync(x, y, zoom);

// decoded vector tile (MVT or MLT, selected by the archive header)
var tile = await source.GetVectorTile(x, y, zoom);
```

An archive served over HTTP(S) is read with range requests: the client belongs to the caller (a
named `IHttpClientFactory` client with retries/timeouts), and the origin must answer `206` with a
matching `Content-Range` — a `200` fails fast instead of downloading the archive per slice.

```csharp
// client: AutomaticDecompression = None (ranges address the stored bytes)
using var http = new HttpClient();
var source = new PmtilesTileSource(http, new Uri("https://host/basemap.pmtiles"));

var bytes = await source.GetTileBytesAsync(x, y, zoom);

// or read ranges directly, with the archive identity the origin reported
var range = new PmtilesHttpRangeSource(http, uri);
var header = await range.ReadAsync(0, PmtilesCodec.HeaderSize);
Console.WriteLine($"{range.Key} ETag={range.ETag} length={range.TotalLength}");
```

## Features

- PMTiles v3 header, root/leaf directories, Hilbert tile ids.
- Vector tile types `mvt` and `mlt`; raster types `png`, `jpeg`, `webp`, `avif`.
- Compression: none, gzip, brotli, zstd.
- Byte-range access through `IPmtilesRangeSource`:
  - `PmtilesFileRangeSource` — local files via `RandomAccess`, thread-safe;
  - `PmtilesStreamRangeSource` — any seekable stream;
  - `PmtilesHttpRangeSource` — HTTP(S) `Range` requests (object storage, CDN, gateway). Requires
    `206` + matching `Content-Range`; captures `ETag` and total length. Caller owns the `HttpClient`.
- Overzoom past the archive's max zoom (stretches the deepest available tile).
- Bounded 256-entry LRU for leaf directories, `long` offsets, section-bound validation.

## Large files

The archive is never loaded into memory: one header read, one root-directory read, cached leaf
directories and one range read per tile. Tile data past 2 GB is covered by the test suite.

## License

MIT.
