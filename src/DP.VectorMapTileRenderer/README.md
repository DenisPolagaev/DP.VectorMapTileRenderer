# DP.VectorMapTileRenderer

Core package of the renderer family: vector tile model, MLT/MVT decoding, MapLibre style engine,
file/raster tile sources and overzoom. **No external NuGet dependencies.**

## Install

```bash
dotnet add package DP.VectorMapTileRenderer
```

## What is included

| Type | Purpose |
| --- | --- |
| `VectorTile`, `VectorTileLayer`, `VectorTileFeature` | encoding-neutral tile model |
| `Point`, `Rect`, `VectorGeometryType` | geometry primitives |
| `VectorTileReader` | reads MLT or MVT (auto-detected) |
| `Mlt.MltTileReader` | MapLibre Tile v1 decoder |
| `Mvt.MvtTileReader` | Mapbox Vector Tile 2.x decoder |
| `Style`, `Layer`, `Source`, `Brush`, `Paint`, `ColorParser` | MapLibre/Mapbox style engine |
| `FileVectorTileSource`, `RasterTileSource` | local tile sources |
| `TileOverzoom` | overzoom helper |
| `GlobalMercator` | slippy-map / Mercator math |

## Reading a tile

```csharp
using DP.VectorMapTileRenderer;
using DP.VectorMapTileRenderer.Sources;

var bytes = File.ReadAllBytes("tiles/0/0/0.mlt");
var tile = VectorTileReader.Read(bytes);

// or through a source (path template, stream, gzip)
using var source = new FileVectorTileSource("tiles/{z}/{x}/{y}.mlt");
var decoded = await source.GetVectorTile(0, 0, 0);
```

`VectorTileReader.Detect` distinguishes MLT from MVT by the first byte; gzip is unwrapped by
`FileVectorTileSource`.

## Style engine

```csharp
var style = Style.FromJson(styleJson);       // or new Style("style.json")
style.FontDirectory = "fonts";               // TTF/OTF files by family name
style.SetSourceProvider("vector", source);
```

Supported: layers/sources, filters (legacy and expression form), zoom/property `stops`, and the
MapLibre expression subset used by modern styles (`case`, `match`, `interpolate`, `step`, `get`,
`has`, `coalesce`, `concat`, comparisons, logical operators).

## Overzoom

`TileOverzoom.Apply` stretches the deepest available parent tile into the requested tile and sets
`VectorTile.IsOverZoomed`, so the renderer clips overflowing geometry.

## Related packages

- [`DP.VectorMapTileRenderer.Skia`](../DP.VectorMapTileRenderer.Skia/README.md) — rendering.
- [`DP.VectorMapTileRenderer.Pmtiles`](../DP.VectorMapTileRenderer.Pmtiles/README.md)
- [`DP.VectorMapTileRenderer.MbTiles`](../DP.VectorMapTileRenderer.MbTiles/README.md)
- [`DP.VectorMapTileRenderer.GeoJson`](../DP.VectorMapTileRenderer.GeoJson/README.md)

## License

MIT.
