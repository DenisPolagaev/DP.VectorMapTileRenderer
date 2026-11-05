using System.IO.Compression;
using SkiaSharp;
using Xunit;

namespace DP.VectorMapTileRenderer.Tests;

public sealed class RendererTests
{
    private const string StyleJson = """
        {
          "version": 8,
          "sources": { "vector": { "type": "vector" } },
          "layers": [
            {
              "id": "background",
              "type": "background",
              "paint": { "background-color": "#ffffff" }
            },
            {
              "id": "stations",
              "type": "circle",
              "source": "vector",
              "source-layer": "weather_stations_mvt",
              "paint": { "circle-radius": 4, "circle-color": "#ff0000" }
            },
            {
              "id": "labels",
              "type": "symbol",
              "source": "vector",
              "source-layer": "weather_stations_mvt",
              "layout": { "text-field": "{name}", "text-size": 9 },
              "paint": { "text-color": "#000000" }
            }
          ]
        }
        """;

    [Fact]
    public async Task RendersMltTileAsBitmap()
    {
        var bytes = ReadFixture("stations.mlt");
        var source = new Sources.FileVectorTileSource(new MemoryStream(bytes));
        var style = Style.FromJson(StyleJson);
        style.SetSourceProvider("vector", source);

        using var canvas = new SkiaCanvas();
        using var bitmap = await Renderer.RenderAsync(
            style, canvas, 0, 0, zoom: 5, sizeX: 256, sizeY: 256,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(bitmap);
        Assert.Equal(256, bitmap.Width);
        Assert.Equal(256, bitmap.Height);

        var redPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green < 100 && pixel.Blue < 100)
                {
                    redPixels++;
                }
            }
        }

        Assert.True(redPixels > 50, $"Expected station circles, found {redPixels} red pixels.");
    }

    [Fact]
    public async Task RendersGzippedMltTile()
    {
        var bytes = ReadFixture("stations.mlt");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        compressed.Position = 0;
        var source = new Sources.FileVectorTileSource(compressed);
        var style = Style.FromJson(StyleJson);
        style.SetSourceProvider("vector", source);

        using var canvas = new SkiaCanvas();
        using var bitmap = await Renderer.RenderAsync(
            style, canvas, 0, 0, zoom: 5, sizeX: 256, sizeY: 256,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(bitmap);
    }

    [Fact]
    public async Task MissingVectorTileReturnsNull()
    {
        var style = Style.FromJson(StyleJson);
        style.SetSourceProvider("vector", new EmptySource());

        using var canvas = new SkiaCanvas();
        var bitmap = await Renderer.RenderAsync(
            style, canvas, 0, 0, zoom: 5, sizeX: 64, sizeY: 64,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(bitmap);
    }

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task RendersPolygonWithHole()
    {
        var tile = new VectorTile();
        var layer = new VectorTileLayer { Name = "shapes", Extent = 4096 };
        var feature = new VectorTileFeature
        {
            GeometryType = VectorGeometryType.Polygon,
            Extent = 4096
        };
        feature.Geometry.Add(
        [
            new Point(800, 800), new Point(3200, 800), new Point(3200, 3200), new Point(800, 3200),
            new Point(800, 800)
        ]);
        feature.Geometry.Add(
        [
            new Point(1600, 1600), new Point(2400, 1600), new Point(2400, 2400), new Point(1600, 2400),
            new Point(1600, 1600)
        ]);
        layer.Features.Add(feature);
        tile.Layers.Add(layer);

        var style = Style.FromJson("""
            {
              "version": 8,
              "sources": { "vector": { "type": "vector" } },
              "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#ffffff" } },
                {
                  "id": "fill", "type": "fill", "source": "vector", "source-layer": "shapes",
                  "paint": { "fill-color": "#ff0000" }
                }
              ]
            }
            """);
        style.SetSourceProvider("vector", new FixedVectorSource(tile));

        using var canvas = new SkiaCanvas();
        using var bitmap = await Renderer.RenderAsync(
            style, canvas, 0, 0, zoom: 5, sizeX: 512, sizeY: 512,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(bitmap);
        Assert.True(bitmap.GetPixel(100, 100).Red > 200, "outer ring should be filled");
        Assert.True(bitmap.GetPixel(256, 256).Red > 200, "hole should be transparent");
    }

    [Fact]
    public async Task RendersSpriteIcon()
    {
        using var iconBitmap = new SKBitmap(10, 10);
        using (var iconCanvas = new SKCanvas(iconBitmap))
        {
            iconCanvas.Clear(SKColors.Red);
        }

        using var png = SKImage.FromBitmap(iconBitmap).Encode(SKEncodedImageFormat.Png, 100);
        using var atlasBitmap = SKBitmap.Decode(png.ToArray())
            ?? throw new InvalidOperationException("Sprite PNG could not be decoded.");
        using var atlas = SpriteAtlas.FromJson(
            """{"poi":{"x":0,"y":0,"width":10,"height":10,"pixelRatio":1}}""",
            atlasBitmap);

        var tile = new VectorTile();
        var layer = new VectorTileLayer { Name = "pois", Extent = 4096 };
        var feature = new VectorTileFeature { GeometryType = VectorGeometryType.Point, Extent = 4096 };
        feature.Geometry.Add([new Point(2048, 2048)]);
        layer.Features.Add(feature);
        tile.Layers.Add(layer);

        var style = Style.FromJson("""
            {
              "version": 8,
              "sources": { "vector": { "type": "vector" } },
              "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#ffffff" } },
                {
                  "id": "poi", "type": "symbol", "source": "vector", "source-layer": "pois",
                  "layout": { "icon-image": "poi" }
                }
              ]
            }
            """);
        style.SetSourceProvider("vector", new FixedVectorSource(tile));

        using var canvas = new SkiaCanvas { Sprite = atlas };
        using var bitmap = await Renderer.RenderAsync(
            style, canvas, 0, 0, zoom: 5, sizeX: 512, sizeY: 512,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(bitmap);
        var pixel = bitmap.GetPixel(256, 256);
        Assert.True(pixel.Red > 200 && pixel.Green < 100, $"expected red icon, got {pixel}");
    }

    private sealed class FixedVectorSource(VectorTile tile) : Sources.IVectorTileSource
    {
        public Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            Task.FromResult<VectorTile?>(tile);
    }

    private sealed class EmptySource : Sources.IVectorTileSource
    {
        public Task<Stream> GetTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<VectorTile?> GetVectorTile(int x, int y, int zoom, CancellationToken cancellationToken = default) =>
            Task.FromResult<VectorTile?>(null);
    }
}
