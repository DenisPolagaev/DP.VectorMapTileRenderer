using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class StyleTests
{
    private const string Json = """
        {
          "version": 8,
          "sources": {
            "vector": { "type": "vector", "maxzoom": 14 },
            "satellite": { "type": "raster" }
          },
          "layers": [
            {
              "id": "background",
              "type": "background",
              "paint": { "background-color": "#102030", "background-opacity": 0.5 }
            },
            {
              "id": "stations",
              "type": "circle",
              "source": "vector",
              "source-layer": "weather_stations_mvt",
              "minzoom": 5,
              "filter": ["all", ["==", "kind", "station"], ["in", "country", "RU", "BY"]],
              "paint": {
                "circle-radius": { "stops": [[5, 2], [10, 6]] },
                "circle-color": "hsl(210, 50%, 40%)",
                "circle-opacity": 0.75
              },
              "layout": { "visibility": "visible" }
            },
            {
              "id": "labels",
              "type": "symbol",
              "source": "vector",
              "source-layer": "weather_stations_mvt",
              "layout": {
                "text-field": "{name}",
                "text-size": 12,
                "text-font": ["Open Sans Regular"],
                "visibility": "none"
              },
              "paint": { "text-color": "#ffffff", "text-halo-color": "black", "text-halo-width": 1 }
            }
          ]
        }
        """;

    [Fact]
    public void ParsesSourcesAndLayers()
    {
        var style = Style.FromJson(Json);

        Assert.Equal(2, style.Sources.Count);
        Assert.Equal("vector", style.Sources["vector"].Type);
        Assert.Equal(14d, style.Sources["vector"].MaxZoom);
        Assert.Equal(3, style.Layers.Count);
        Assert.Equal("weather_stations_mvt", style.Layers[1].SourceLayer);
        Assert.Equal(5d, style.Layers[1].MinZoom);
        Assert.False(string.IsNullOrEmpty(style.Hash));
    }

    [Fact]
    public void BackgroundColorUsesOpacity()
    {
        var style = Style.FromJson(Json);
        var color = style.GetBackgroundColor(10);

        Assert.Equal(new Color(128, 16, 32, 48), color);
    }

    [Fact]
    public void ValidatesZoomAndFilters()
    {
        var style = Style.FromJson(Json);
        var layer = style.Layers[1];

        Assert.True(style.ValidateLayer(layer, 7, new Dictionary<string, object?>
        {
            ["kind"] = "station",
            ["country"] = "RU"
        }));

        Assert.False(style.ValidateLayer(layer, 4, new Dictionary<string, object?>
        {
            ["kind"] = "station",
            ["country"] = "RU"
        }));

        Assert.False(style.ValidateLayer(layer, 7, new Dictionary<string, object?>
        {
            ["kind"] = "city",
            ["country"] = "RU"
        }));

        Assert.False(style.ValidateLayer(layer, 7, new Dictionary<string, object?>
        {
            ["kind"] = "station",
            ["country"] = "KZ"
        }));
    }

    [Fact]
    public void InterpolatesZoomStopsAndParsesText()
    {
        var style = Style.FromJson(Json);
        var attributes = new Dictionary<string, object?>
        {
            ["$zoom"] = 7d,
            ["$type"] = "Point",
            ["name"] = "Alpha"
        };

        var circle = style.ParseStyle(style.Layers[1], 1, attributes);
        Assert.Equal(3.6, circle.Paint.CircleRadius, 3);
        Assert.Equal(0.75, circle.Paint.CircleOpacity);

        var symbol = style.ParseStyle(style.Layers[2], 1, attributes);
        Assert.Equal("Alpha", symbol.Text);
        Assert.False(symbol.Paint.Visibility);
        Assert.Equal(12d, symbol.Paint.TextSize);
        Assert.Equal(Color.White, symbol.Paint.TextColor);
    }

    [Fact]
    public void EvaluatesMapLibreExpressions()
    {
        var style = Style.FromJson("""
            {
              "version": 8,
              "sources": { "vector": { "type": "vector" } },
              "layers": [
                {
                  "id": "expr",
                  "type": "circle",
                  "source": "vector",
                  "source-layer": "points",
                  "filter": ["all", ["==", ["get", "kind"], "station"]],
                  "paint": {
                    "circle-color": ["case", ["==", ["get", "kind"], "water"], "#0000ff", "#ff0000"],
                    "circle-radius": ["interpolate", ["linear"], ["zoom"], 5, 2, 10, 6]
                  },
                  "layout": {
                    "text-field": ["concat", "A ", ["get", "name"]],
                    "text-anchor": "top-left"
                  }
                }
              ]
            }
            """);

        var layer = style.Layers[0];
        Assert.True(style.ValidateLayer(layer, 7, new Dictionary<string, object?>
        {
            ["kind"] = "station",
            ["name"] = "X"
        }));
        Assert.False(style.ValidateLayer(layer, 7, new Dictionary<string, object?>
        {
            ["kind"] = "water",
            ["name"] = "X"
        }));

        var attributes = new Dictionary<string, object?>
        {
            ["$zoom"] = 7d,
            ["kind"] = "water",
            ["name"] = "X"
        };
        var brush = style.ParseStyle(layer, 1, attributes);

        Assert.Equal(3.6, brush.Paint.CircleRadius, 3);
        Assert.Equal(ColorParser.Parse("#0000ff"), brush.Paint.CircleColor);
        Assert.Equal("A X", brush.Text);
        Assert.Equal(TextAlignment.Left, brush.Paint.TextJustify);
        Assert.Equal(TextVerticalAnchor.Top, brush.Paint.TextVerticalAnchor);
    }

    [Fact]
    public void EvaluatesGeometryTypeFilters()
    {
        var style = Style.FromJson("""
            {
              "version": 8,
              "sources": { "vector": { "type": "vector" } },
              "layers": [
                {
                  "id": "polygons",
                  "type": "fill",
                  "source": "vector",
                  "source-layer": "shapes",
                  "filter": ["match", ["geometry-type"], ["MultiPolygon", "Polygon"], true, false]
                }
              ]
            }
            """);

        var layer = style.Layers[0];

        Assert.True(style.ValidateLayer(layer, 10, new Dictionary<string, object?> { ["$type"] = "Polygon" }));
        Assert.False(style.ValidateLayer(layer, 10, new Dictionary<string, object?> { ["$type"] = "LineString" }));
    }

    [Fact]
    public void SetSourceProviderByName()
    {
        var style = Style.FromJson(Json);
        var provider = new ThrowingSource();

        style.SetSourceProvider("vector", provider);

        Assert.Same(provider, style.Sources["vector"].Provider);
    }

    private sealed class ThrowingSource : Sources.IVectorTileSource
    {
        public Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
