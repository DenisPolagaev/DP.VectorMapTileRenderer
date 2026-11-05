using System.Collections.Concurrent;
using SkiaSharp;

namespace DP.VectorMapTileRenderer;

/// <summary>Draws tiles onto an <see cref="SKBitmap"/> with SkiaSharp. Not thread-safe: one instance per render.</summary>
public sealed class SkiaCanvas : ICanvas, IDisposable
{
    private const int TextCellSize = 64;

    private static readonly ConcurrentDictionary<string, SKTypeface> TypefaceCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(int X, int Y), List<Rect>> _textGrid = [];
    private int _width;
    private int _height;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private Rect _clipRectangle;
    private bool _disposed;

    public bool ClipOverflow { get; set; }

    /// <summary>Optional sprite atlas used for <c>icon-image</c> and <c>*-pattern</c>.</summary>
    public SpriteAtlas? Sprite { get; set; }

    public void StartDrawing(double width, double height)
    {
        _width = Math.Max(1, (int)width);
        _height = Math.Max(1, (int)height);

        _canvas?.Dispose();
        _bitmap?.Dispose();

        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _canvas = new SKCanvas(_bitmap);
        _textGrid.Clear();

        const double padding = -5;
        _clipRectangle = new Rect(padding, padding, _width - (padding * 2), _height - (padding * 2));
    }

    public void DrawBackground(Brush style)
    {
        if (_canvas is null)
        {
            return;
        }

        if (style.Paint.BackgroundPattern is { Length: > 0 } pattern
            && Sprite is { } sprite
            && sprite.TryGetShader(pattern, out var patternShader)
            && patternShader is not null)
        {
            using var patternPaint = new SKPaint { Shader = patternShader };
            _canvas.DrawRect(new SKRect(0, 0, _width, _height), patternPaint);
            return;
        }

        var color = style.Paint.BackgroundColor.WithOpacity(style.Paint.BackgroundOpacity);
        _canvas.Clear(ToSKColor(color));
    }

    public void DrawLineString(List<Point> geometry, Brush style)
    {
        if (_canvas is null || geometry.Count < 2)
        {
            return;
        }

        if (style.Paint.LineOffset != 0)
        {
            geometry = LineOffsetter.Offset(geometry, style.Paint.LineOffset);
        }

        if (ClipOverflow)
        {
            var clipped = LineClipper.ClipPolyline(geometry, _clipRectangle);
            if (clipped is null)
            {
                return;
            }

            geometry = clipped;
        }
        if (geometry.Count < 2)
        {
            return;
        }

        using var path = BuildPath(geometry);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = ConvertCap(style.Paint.LineCap),
            StrokeJoin = ConvertJoin(style.Paint.LineJoin),
            StrokeWidth = (float)Math.Max(0.1, style.Paint.LineWidth),
            Color = ToSKColor(style.Paint.LineColor.WithOpacity(style.Paint.LineOpacity)),
            IsAntialias = true
        };

        if (style.Paint.LineDashArray.Length > 0)
        {
            paint.PathEffect = SKPathEffect.CreateDash(
                [.. style.Paint.LineDashArray.Select(value => (float)value)],
                0);
        }

        if (style.Paint.LinePattern is { Length: > 0 } linePattern
            && Sprite is { } lineSprite
            && lineSprite.TryGetShader(linePattern, out var lineShader)
            && lineShader is not null)
        {
            paint.Shader = lineShader;
            paint.Color = SKColors.White;
        }

        _canvas.DrawPath(path, paint);
    }

    public void DrawPolygon(List<Point> geometry, Brush style) => DrawPolygon([geometry], style);

    public void DrawPolygon(IReadOnlyList<List<Point>> rings, Brush style)
    {
        if (_canvas is null || rings.Count == 0)
        {
            return;
        }

        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        var hasGeometry = false;

        foreach (var ring in rings)
        {
            if (ring.Count < 3)
            {
                continue;
            }

            var clipped = ClipOverflow ? PolygonClipper.Clip(ring, _clipRectangle) : ring;
            if (clipped is null)
            {
                continue;
            }

            AppendRing(path, clipped);
            hasGeometry = true;
        }

        if (!hasGeometry)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = ToSKColor(style.Paint.FillColor.WithOpacity(style.Paint.FillOpacity)),
            IsAntialias = true
        };

        if (style.Paint.FillPattern is { Length: > 0 } fillPattern
            && Sprite is { } fillSprite
            && fillSprite.TryGetShader(fillPattern, out var fillShader)
            && fillShader is not null)
        {
            paint.Shader = fillShader;
            paint.Color = SKColors.White;
        }

        _canvas.DrawPath(path, paint);
    }

    private static void AppendRing(SKPath path, List<Point> ring)
    {
        path.MoveTo((float)ring[0].X, (float)ring[0].Y);
        for (var index = 1; index < ring.Count; index++)
        {
            path.LineTo((float)ring[index].X, (float)ring[index].Y);
        }

        path.Close();
    }

    public void DrawPoint(Point geometry, Brush style)
    {
        if (_canvas is null)
        {
            return;
        }

        if (style.Paint.IconImage is { Length: > 0 } iconName
            && Sprite is { } sprite
            && sprite.TryGet(iconName, out var icon))
        {
            DrawIcon(geometry, style, sprite, icon);
            return;
        }

        var color = style.Paint.CircleColor.A == 0 ? style.Paint.FillColor : style.Paint.CircleColor;
        if (color.A == 0)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = ToSKColor(color.WithOpacity(style.Paint.CircleOpacity)),
            IsAntialias = true
        };

        _canvas.DrawCircle((float)geometry.X, (float)geometry.Y, (float)Math.Max(0.5, style.Paint.CircleRadius), paint);
    }

    private void DrawIcon(Point geometry, Brush style, SpriteAtlas sprite, SpriteIcon icon)
    {
        if (_canvas is null)
        {
            return;
        }

        var scale = style.Paint.IconScale <= 0 ? 1 : style.Paint.IconScale;
        var ratio = icon.PixelRatio <= 0 ? 1 : icon.PixelRatio;
        var width = icon.Source.Width * scale / ratio;
        var height = icon.Source.Height * scale / ratio;
        var centerX = geometry.X + (style.Paint.IconOffset.X * scale);
        var centerY = geometry.Y + (style.Paint.IconOffset.Y * scale);
        var destination = new SKRect(
            (float)(centerX - (width / 2)),
            (float)(centerY - (height / 2)),
            (float)(centerX + (width / 2)),
            (float)(centerY + (height / 2)));

        var alpha = (byte)Math.Clamp(Math.Round(255 * style.Paint.IconOpacity), 0, 255);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, alpha)
        };

        var rotated = style.Paint.IconRotate != 0;
        if (rotated)
        {
            _canvas.Save();
            _canvas.RotateDegrees((float)style.Paint.IconRotate, (float)centerX, (float)centerY);
        }

        _canvas.DrawBitmap(sprite.Image, icon.Source, destination, paint);

        if (rotated)
        {
            _canvas.Restore();
        }
    }

    public void DrawText(Point geometry, Brush style)
    {
        if (_canvas is null || string.IsNullOrEmpty(style.Text))
        {
            return;
        }

        using var font = CreateFont(ResolveTypeface(style.Paint.TextFont, style.GlyphsDirectory), style.Paint.TextSize);
        using var paint = CreateTextPaint(style, style.Paint.TextColor, style.Paint.TextOpacity);
        using var strokePaint = style.Paint.TextStrokeWidth > 0
            ? CreateTextStrokePaint(style)
            : null;

        var text = FitText(TransformText(style.Text, style.Paint.TextTransform), font);
        var lines = BreakText(text, font, paint, (float)(style.Paint.TextMaxWidth * style.Paint.TextSize));
        if (lines.Count == 0)
        {
            return;
        }

        var lineHeight = (float)(style.Paint.TextSize * Math.Max(0.1, style.Paint.TextLineHeight));
        var width = lines.Max(line => font.MeasureText(line, paint));
        var height = lineHeight * lines.Count;

        var anchorX = (float)(geometry.X + (style.Paint.TextOffset.X * style.Paint.TextSize));
        var anchorY = (float)(geometry.Y + (style.Paint.TextOffset.Y * style.Paint.TextSize));

        var left = style.Paint.TextJustify switch
        {
            TextAlignment.Left => anchorX,
            TextAlignment.Right => anchorX - width,
            _ => anchorX - (width / 2)
        };

        var top = style.Paint.TextVerticalAnchor switch
        {
            TextVerticalAnchor.Top => anchorY,
            TextVerticalAnchor.Bottom => anchorY - height,
            _ => anchorY - (height / 2)
        };

        var rectangle = new Rect(left, top, width, height).Inflate(Math.Max(0, style.Paint.TextPadding));

        if (ClipOverflow && !_clipRectangle.Contains(rectangle))
        {
            return;
        }

        if (Collides(rectangle))
        {
            return;
        }

        AddTextRectangle(rectangle);

        var align = style.Paint.TextJustify switch
        {
            TextAlignment.Left => SKTextAlign.Left,
            TextAlignment.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        var rotated = style.Paint.TextRotate != 0;
        if (rotated)
        {
            _canvas.Save();
            _canvas.RotateDegrees((float)style.Paint.TextRotate, anchorX, anchorY);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var baseline = top + lineHeight * (index + 1);
            if (strokePaint is not null)
            {
                _canvas.DrawText(lines[index], anchorX, baseline, align, font, strokePaint);
            }

            _canvas.DrawText(lines[index], anchorX, baseline, align, font, paint);
        }

        if (rotated)
        {
            _canvas.Restore();
        }
    }

    public void DrawTextOnPath(List<Point> geometry, Brush style)
    {
        if (_canvas is null || geometry.Count < 2 || string.IsNullOrEmpty(style.Text))
        {
            return;
        }

        var clipped = LineClipper.ClipPolyline(geometry, _clipRectangle);
        if (clipped is null || clipped.Count < 2)
        {
            return;
        }

        if (IsPathSqueezed(clipped))
        {
            return;
        }

        using var path = BuildPath(clipped);
        using var font = CreateFont(ResolveTypeface(style.Paint.TextFont, style.GlyphsDirectory), style.Paint.TextSize);
        using var paint = CreateTextPaint(style, style.Paint.TextColor, style.Paint.TextOpacity);
        using var strokePaint = style.Paint.TextStrokeWidth > 0 ? CreateTextStrokePaint(style) : null;

        var text = FitText(TransformText(style.Text, style.Paint.TextTransform), font);
        if (text.Length == 0)
        {
            return;
        }

        var bounds = path.Bounds;
        var rectangle = new Rect(
            bounds.Left - style.Paint.TextSize,
            bounds.Top - style.Paint.TextSize,
            bounds.Width + (style.Paint.TextSize * 2),
            bounds.Height + (style.Paint.TextSize * 2));

        if (Collides(rectangle))
        {
            return;
        }

        AddTextRectangle(rectangle);

        if (style.Text.Length * style.Paint.TextSize * 0.2 >= PathLength(geometry))
        {
            return;
        }

        var offset = new SKPoint((float)style.Paint.TextOffset.X, (float)style.Paint.TextOffset.Y);
        if (strokePaint is not null)
        {
            _canvas.DrawTextOnPath(text, path, offset, font, strokePaint);
        }

        _canvas.DrawTextOnPath(text, path, offset, font, paint);
    }

    public void DrawImage(Stream imageStream, Brush style)
    {
        if (_canvas is null)
        {
            return;
        }

        if (imageStream.CanSeek)
        {
            imageStream.Position = 0;
        }

        using var bitmap = SKBitmap.Decode(imageStream);
        if (bitmap is null)
        {
            return;
        }
        using var paint = new SKPaint { IsAntialias = true };
        _canvas.DrawBitmap(
            bitmap,
            new SKRect(0, 0, bitmap.Width, bitmap.Height),
            new SKRect(0, 0, _width, _height),
            paint);
    }

    public void DrawUnknown(List<List<Point>> geometry, Brush style)
    {
    }

    public SKBitmap FinishDrawing()
    {
        if (_bitmap is null)
        {
            var info = new SKImageInfo(Math.Max(1, _width), Math.Max(1, _height), SKColorType.Bgra8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap(info);
        }

        _canvas?.Flush();
        _canvas?.Dispose();
        _canvas = null;

        var result = _bitmap;
        _bitmap = null;
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _canvas?.Dispose();
        _bitmap?.Dispose();
        _canvas = null;
        _bitmap = null;
        _disposed = true;
    }

    private static SKPath BuildPath(List<Point> geometry)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        path.MoveTo((float)geometry[0].X, (float)geometry[0].Y);
        for (var index = 1; index < geometry.Count; index++)
        {
            path.LineTo((float)geometry[index].X, (float)geometry[index].Y);
        }

        if (geometry.Count > 2 && geometry[0] == geometry[^1])
        {
            path.Close();
        }

        return path;
    }

    private static SKFont CreateFont(SKTypeface typeface, double size) =>
        new(typeface, (float)Math.Max(1, size))
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true
        };

    private static SKPaint CreateTextPaint(Brush style, Color color, double opacity) => new()
    {
        Color = ToSKColor(color.WithOpacity(opacity)),
        IsAntialias = true
    };

    private static SKPaint CreateTextStrokePaint(Brush style) => new()
    {
        Color = ToSKColor(style.Paint.TextStrokeColor.WithOpacity(style.Paint.TextOpacity)),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = (float)style.Paint.TextStrokeWidth
    };

    /// <summary>Drops characters the resolved typeface cannot draw (emulates SkiaSharp 2 fallback truncation).</summary>
    private static string FitText(string text, SKFont font)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var glyphs = font.CountGlyphs(text);
        if (glyphs >= text.Length)
        {
            return text;
        }

        return glyphs > 0 ? text[..glyphs] : string.Empty;
    }

    private static List<string> BreakText(string input, SKFont font, SKPaint paint, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(input) || maxWidth <= 0)
        {
            lines.Add(input);
            return lines;
        }

        var remaining = input.Trim();
        while (remaining.Length > 0)
        {
            var fit = font.BreakText(remaining, maxWidth, paint);
            if (fit >= remaining.Length)
            {
                lines.Add(remaining.Trim());
                break;
            }

            var lastSpace = remaining.LastIndexOf(' ', Math.Max(0, (int)fit - 1));
            if (lastSpace <= 0)
            {
                lines.Add(remaining.Trim());
                break;
            }

            lines.Add(remaining[..lastSpace].Trim());
            remaining = remaining[lastSpace..].TrimStart();
        }

        return [.. lines.Where(line => line.Length > 0)];
    }

    private bool Collides(Rect rectangle)
    {
        var (minX, minY) = Cell(rectangle.Left, rectangle.Top);
        var (maxX, maxY) = Cell(rectangle.Right, rectangle.Bottom);

        for (var cellX = minX; cellX <= maxX; cellX++)
        {
            for (var cellY = minY; cellY <= maxY; cellY++)
            {
                if (!_textGrid.TryGetValue((cellX, cellY), out var rectangles))
                {
                    continue;
                }

                foreach (var existing in rectangles)
                {
                    if (existing.IntersectsWith(rectangle))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void AddTextRectangle(Rect rectangle)
    {
        var (minX, minY) = Cell(rectangle.Left, rectangle.Top);
        var (maxX, maxY) = Cell(rectangle.Right, rectangle.Bottom);

        for (var cellX = minX; cellX <= maxX; cellX++)
        {
            for (var cellY = minY; cellY <= maxY; cellY++)
            {
                var key = (cellX, cellY);
                if (!_textGrid.TryGetValue(key, out var rectangles))
                {
                    rectangles = [];
                    _textGrid[key] = rectangles;
                }

                rectangles.Add(rectangle);
            }
        }
    }

    private (int X, int Y) Cell(double x, double y) =>
        (Math.Clamp((int)Math.Floor(x / TextCellSize), 0, Math.Max(0, _width / TextCellSize)),
            Math.Clamp((int)Math.Floor(y / TextCellSize), 0, Math.Max(0, _height / TextCellSize)));

    private static double PathLength(List<Point> path)
    {
        var distance = 0.0;
        for (var index = 0; index < path.Count - 1; index++)
        {
            distance += (path[index] - path[index + 1]).Length;
        }

        return distance;
    }

    private static bool IsPathSqueezed(List<Point> path)
    {
        var previousAngle = 0.0;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var vector = path[index] - path[index + 1];
            var angle = Math.Atan2(vector.Y, vector.X);
            if (AbsoluteDiff2Angles(angle, previousAngle) > Math.PI / 3)
            {
                return true;
            }

            previousAngle = angle;
        }

        return false;
    }

    private static double AbsoluteDiff2Angles(double x, double y, double c = Math.PI) =>
        c - Math.Abs((Math.Abs(x - y) % (2 * c)) - c);

    private static string TransformText(string text, TextTransform transform) => transform switch
    {
        TextTransform.Uppercase => text.ToUpperInvariant(),
        TextTransform.Lowercase => text.ToLowerInvariant(),
        _ => text
    };

    private static SKTypeface ResolveTypeface(string[] familyNames, string? glyphsDirectory)
    {
        foreach (var name in familyNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = glyphsDirectory is null ? name : Path.Combine(glyphsDirectory, name);
            if (TypefaceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (glyphsDirectory is not null)
            {
                foreach (var extension in new[] { "ttf", "otf" })
                {
                    var file = Path.Combine(glyphsDirectory, $"{name}.{extension}");
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var fromFile = SKTypeface.FromFile(file);
                    if (fromFile is not null)
                    {
                        return TypefaceCache.GetOrAdd(key, fromFile);
                    }
                }
            }

            var typeface = SKTypeface.FromFamilyName(name);
            if (typeface is not null)
            {
                return TypefaceCache.GetOrAdd(key, typeface);
            }
        }

        var fallbackName = familyNames.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "sans-serif";
        return TypefaceCache.GetOrAdd(fallbackName, _ => SKTypeface.FromFamilyName(fallbackName) ?? SKTypeface.Default);
    }

    private static SKStrokeCap ConvertCap(PenLineCap cap) => cap switch
    {
        PenLineCap.Round => SKStrokeCap.Round,
        PenLineCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt
    };

    private static SKStrokeJoin ConvertJoin(LineJoin join) => join switch
    {
        LineJoin.Round => SKStrokeJoin.Round,
        LineJoin.Bevel => SKStrokeJoin.Bevel,
        _ => SKStrokeJoin.Miter
    };

    private static SKColor ToSKColor(Color color) => new(color.R, color.G, color.B, color.A);
}
