using System.Collections.Concurrent;
using System.Text.Json;
using SkiaSharp;

namespace DP.VectorMapTileRenderer;

/// <summary>One sprite region inside a MapLibre sprite atlas.</summary>
public readonly record struct SpriteIcon(string Name, SKRect Source, double PixelRatio);

/// <summary>
/// A MapLibre sprite atlas: a PNG/WebP image plus the sprite JSON index. Assign it to
/// <see cref="SkiaCanvas.Sprite"/> to enable <c>icon-image</c> and <c>*-pattern</c>.
/// The atlas owns the bitmap and the shader cache; dispose it after the canvas.
/// </summary>
public sealed class SpriteAtlas : IDisposable
{
    private static readonly HttpClient SharedHttpClient = new();

    private readonly SKBitmap _image;
    private readonly Dictionary<string, SpriteIcon> _icons;
    private readonly ConcurrentDictionary<string, SKShader> _shaders = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<SKBitmap> _shaderBitmaps = [];
    private bool _disposed;

    private SpriteAtlas(SKBitmap image, Dictionary<string, SpriteIcon> icons)
    {
        _image = image;
        _icons = icons;
    }

    internal SKBitmap Image => _image;

    public int Count => _icons.Count;

    public static SpriteAtlas FromJson(string json, SKBitmap image)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(image);

        using var document = JsonDocument.Parse(json);
        var icons = new Dictionary<string, SpriteIcon>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("x", out var x)
                || !value.TryGetProperty("y", out var y)
                || !value.TryGetProperty("width", out var width)
                || !value.TryGetProperty("height", out var height))
            {
                continue;
            }

            var pixelRatio = value.TryGetProperty("pixelRatio", out var ratio) && ratio.TryGetDouble(out var parsedRatio)
                ? parsedRatio
                : 1;

            icons[property.Name] = new SpriteIcon(
                property.Name,
                SKRect.Create((float)x.GetDouble(), (float)y.GetDouble(), (float)width.GetDouble(), (float)height.GetDouble()),
                pixelRatio <= 0 ? 1 : pixelRatio);
        }

        return new SpriteAtlas(image, icons);
    }

    public static SpriteAtlas FromFiles(string jsonPath, string imagePath)
    {
        var json = File.ReadAllText(jsonPath);
        var image = SKBitmap.Decode(imagePath)
            ?? throw new InvalidDataException($"Sprite image '{imagePath}' could not be decoded.");
        try
        {
            return FromJson(json, image);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>Loads <c>url + ".json"</c> and <c>url + ".png"</c> from a local path or HTTP(S).</summary>
    public static async Task<SpriteAtlas> LoadAsync(
        string url,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var client = httpClient ?? SharedHttpClient;
            var json = await client.GetStringAsync(url + ".json", cancellationToken).ConfigureAwait(false);
            var bytes = await client.GetByteArrayAsync(url + ".png", cancellationToken).ConfigureAwait(false);
            var image = SKBitmap.Decode(bytes)
                ?? throw new InvalidDataException($"Sprite image '{url}.png' could not be decoded.");
            try
            {
                return FromJson(json, image);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        return FromFiles(url + ".json", url + ".png");
    }

    public bool TryGet(string name, out SpriteIcon icon) => _icons.TryGetValue(name, out icon);

    /// <summary>
    /// Returns a repeat shader for the sprite region; used by <c>fill-pattern</c> and friends.
    /// The shader is cached and owned by the atlas.
    /// </summary>
    public bool TryGetShader(string name, out SKShader? shader)
    {
        if (_shaders.TryGetValue(name, out shader))
        {
            return true;
        }

        if (!_icons.TryGetValue(name, out var icon))
        {
            shader = null;
            return false;
        }

        var width = Math.Max(1, (int)Math.Round(icon.Source.Width));
        var height = Math.Max(1, (int)Math.Round(icon.Source.Height));
        var crop = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(
                _image,
                icon.Source,
                new SKRect(0, 0, width, height),
                new SKPaint { IsAntialias = true });
        }

        _shaderBitmaps.Add(crop);
        shader = _shaders.GetOrAdd(
            name,
            _ => SKShader.CreateBitmap(crop, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var shader in _shaders.Values)
        {
            shader.Dispose();
        }

        _shaders.Clear();

        foreach (var bitmap in _shaderBitmaps)
        {
            bitmap.Dispose();
        }

        _shaderBitmaps.Clear();
        _image.Dispose();
        _disposed = true;
    }
}
