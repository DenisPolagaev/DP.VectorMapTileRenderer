# DP.VectorMapTileRenderer.GeoJson

GeoJSON tile source for `DP.VectorMapTileRenderer`.

## Install

```bash
dotnet add package DP.VectorMapTileRenderer.GeoJson
```

Depends on `DP.VectorMapTileRenderer`, `NetTopologySuite` and
`NetTopologySuite.IO.GeoJSON4STJ`.

## Usage

```csharp
using DP.VectorMapTileRenderer.GeoJson;

var source = new GeoJsonTileSource("incidents.geojson")
{
    LayerName = "incidents",
    Extent = 4096,
    CoordinateSpace = GeoJsonCoordinateSpace.Wgs84
};
style.SetSourceProvider("incidents", source);

var tile = await source.GetVectorTile(x, y, zoom);

// large documents: parse asynchronously
var asyncSource = await GeoJsonTileSource.CreateAsync("world.geojson");
```

## Features

- `FeatureCollection`, single `Feature` and bare geometries.
- Coordinate spaces: WGS84 lon/lat, Web Mercator metres (EPSG:3857), tile-local grid.
- Clip to tile bounds and quantize to `Extent`.
- Spatial index (STRtree) built once; per-tile extraction is an envelope lookup, not a full scan.
- Properties preserved for style filters and `text-field`.
- Invalid geometries are rendered unclipped instead of failing the tile.

## Large files

Features are materialized in memory (NetTopologySuite). The source avoids storing the raw JSON
buffer for file-based input and builds an STRtree for fast tile extraction, but multi-gigabyte
GeoJSON should be pre-tiled into PMTiles or MBTiles.

## License

MIT.
