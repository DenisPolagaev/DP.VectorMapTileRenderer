using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DP.VectorMapTileRenderer;

public sealed class Brush
{
    public int ZIndex { get; set; }

    public required Paint Paint { get; init; }

    public string? TextField { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? GlyphsDirectory { get; set; }

    public Layer? Layer { get; set; }
}

public enum SymbolPlacement
{
    Point,
    Line
}

public enum TextTransform
{
    None,
    Uppercase,
    Lowercase
}

public enum PenLineCap
{
    Flat,
    Round,
    Square
}

public enum LineJoin
{
    Miter,
    Round,
    Bevel
}

public enum TextAlignment
{
    Left,
    Center,
    Right
}

public enum TextVerticalAnchor
{
    Center,
    Top,
    Bottom
}

public sealed class Paint
{
    public Color BackgroundColor { get; set; } = Color.Transparent;

    public string? BackgroundPattern { get; set; }

    public double BackgroundOpacity { get; set; } = 1;

    public Color FillColor { get; set; } = Color.Transparent;

    public string? FillPattern { get; set; }

    public Point FillTranslate { get; set; }

    public double FillOpacity { get; set; } = 1;

    public Color LineColor { get; set; } = Color.Transparent;

    public string? LinePattern { get; set; }

    public Point LineTranslate { get; set; }

    public PenLineCap LineCap { get; set; } = PenLineCap.Flat;

    public LineJoin LineJoin { get; set; } = LineJoin.Miter;

    public double LineWidth { get; set; } = 1;

    public double LineOffset { get; set; }

    public double LineBlur { get; set; }

    public double[] LineDashArray { get; set; } = [];

    public double LineOpacity { get; set; } = 1;

    public Color CircleColor { get; set; } = Color.Transparent;

    public double CircleRadius { get; set; } = 3;

    public double CircleOpacity { get; set; } = 1;

    public SymbolPlacement SymbolPlacement { get; set; } = SymbolPlacement.Point;

    public double IconScale { get; set; } = 1;

    public string? IconImage { get; set; }

    public double IconRotate { get; set; }

    public Point IconOffset { get; set; }

    public double IconOpacity { get; set; } = 1;

    public Color TextColor { get; set; } = Color.Transparent;

    public string[] TextFont { get; set; } = ["Open Sans Regular", "Arial Unicode MS Regular"];

    public double TextSize { get; set; } = 16;

    public double TextMaxWidth { get; set; } = 10;

    public TextAlignment TextJustify { get; set; } = TextAlignment.Center;

    public TextVerticalAnchor TextVerticalAnchor { get; set; } = TextVerticalAnchor.Center;

    public double TextLineHeight { get; set; } = 1.2;

    public double TextPadding { get; set; } = 2;

    public double TextRotate { get; set; }

    public Point TextOffset { get; set; }

    public Color TextStrokeColor { get; set; } = Color.Transparent;

    public double TextStrokeWidth { get; set; }

    public double TextStrokeBlur { get; set; }

    public bool TextOptional { get; set; }

    public TextTransform TextTransform { get; set; } = TextTransform.None;

    public double TextOpacity { get; set; } = 1;

    public bool Visibility { get; set; } = true;
}

public sealed class Layer
{
    public int Index { get; set; } = -1;

    public string ID { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public Source? Source { get; set; }

    public string SourceLayer { get; set; } = string.Empty;

    public Dictionary<string, object?> Paint { get; set; } = [];

    public Dictionary<string, object?> Layout { get; set; } = [];

    public object?[] Filter { get; set; } = [];

    public double? MinZoom { get; set; }

    public double? MaxZoom { get; set; }
}

public sealed class Source
{
    public string URL { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Sources.ITileSource? Provider { get; set; }

    public double? MinZoom { get; set; }

    public double? MaxZoom { get; set; }
}

/// <summary>
/// A MapLibre/Mapbox style: sources, layers and the paint/layout expression subset used by the renderer.
/// </summary>
public sealed partial class Style
{
    public Style(string path, double scale = 1)
    {
        FilePath = path;
        Initialize(File.ReadAllText(path), scale);
    }

    public Style(Stream stream, double scale = 1)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        Initialize(reader.ReadToEnd(), scale);
    }

    private Style()
    {
    }

    /// <summary>Creates a style from a JSON document held in memory.</summary>
    public static Style FromJson(string json, double scale = 1)
    {
        var style = new Style();
        style.Initialize(json, scale);
        return style;
    }

    private void Initialize(string json, double scale)
    {
        Scale = scale;
        Hash = Utils.Sha256(json);

        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject ?? throw new InvalidDataException("Style JSON root must be an object.");

        if (root["metadata"] is JsonObject metadata)
        {
            Metadata = (Dictionary<string, object?>)Plainify(metadata)!;
        }

        if (root["sprite"] is JsonValue sprite)
        {
            SpriteUrl = sprite.ToString();
        }

        if (root["sources"] is JsonObject sources)
        {
            foreach (var (name, node) in sources)
            {
                if (node is not JsonObject sourceObject)
                {
                    continue;
                }

                var source = new Source
                {
                    Name = name,
                    URL = sourceObject["url"] is JsonValue url ? url.ToString() : string.Empty,
                    Type = sourceObject["type"] is JsonValue type ? type.ToString() : string.Empty
                };

                if (sourceObject["minzoom"] is JsonValue minZoom)
                {
                    source.MinZoom = ToDouble(Plainify(minZoom));
                }

                if (sourceObject["maxzoom"] is JsonValue maxZoom)
                {
                    source.MaxZoom = ToDouble(Plainify(maxZoom));
                }

                Sources[name] = source;
            }
        }

        if (root["layers"] is not JsonArray layers)
        {
            return;
        }

        var index = 0;
        foreach (var node in layers)
        {
            if (node is not JsonObject layerObject)
            {
                index++;
                continue;
            }

            var layer = new Layer { Index = index };

            if (layerObject["minzoom"] is JsonNode minZoomNode)
            {
                layer.MinZoom = ToDouble(Plainify(minZoomNode));
            }

            if (layerObject["maxzoom"] is JsonNode maxZoomNode)
            {
                layer.MaxZoom = ToDouble(Plainify(maxZoomNode));
            }

            if (layerObject["id"] is JsonValue id)
            {
                layer.ID = id.ToString();
            }

            if (layerObject["type"] is JsonValue type)
            {
                layer.Type = type.ToString();
            }

            if (layerObject["source"] is JsonValue sourceName)
            {
                layer.SourceName = sourceName.ToString();
                layer.Source = Sources.GetValueOrDefault(layer.SourceName);
            }

            if (layerObject["source-layer"] is JsonValue sourceLayer)
            {
                layer.SourceLayer = sourceLayer.ToString();
            }

            if (layerObject["paint"] is JsonObject paint)
            {
                layer.Paint = (Dictionary<string, object?>)Plainify(paint)!;
            }

            if (layerObject["layout"] is JsonObject layout)
            {
                layer.Layout = (Dictionary<string, object?>)Plainify(layout)!;
            }

            if (layerObject["filter"] is JsonArray filter)
            {
                layer.Filter = (object?[])Plainify(filter)!;
            }

            Layers.Add(layer);
            index++;
        }
    }

    public string Hash { get; private set; } = string.Empty;

    public string? FilePath { get; }

    /// <summary>MapLibre <c>sprite</c> base URL; load it with <c>SpriteAtlas.LoadAsync</c>.</summary>
    public string? SpriteUrl { get; private set; }

    public double Scale { get; private set; } = 1;

    public List<Layer> Layers { get; } = [];

    public Dictionary<string, Source> Sources { get; } = [];

    public Dictionary<string, object?> Metadata { get; private set; } = [];

    public string? FontDirectory { get; set; }

    public void SetSourceProvider(int index, Sources.ITileSource provider)
    {
        var current = 0;
        foreach (var source in Sources.Values)
        {
            if (current == index)
            {
                SetProvider(source, provider);
                return;
            }

            current++;
        }
    }

    public void SetSourceProvider(string name, Sources.ITileSource provider)
    {
        if (Sources.TryGetValue(name, out var source))
        {
            SetProvider(source, provider);
        }
    }

    private static void SetProvider(Source source, Sources.ITileSource provider)
    {
        source.Provider = provider;
        if (string.IsNullOrEmpty(source.Type))
        {
            source.Type = provider is Sources.IVectorTileSource ? "vector" : "raster";
        }
    }

    public Brush[] GetStyleByType(string type, double zoom, double scale = 1)
    {
        var results = new List<Brush>();
        foreach (var layer in Layers)
        {
            if (layer.Type != type)
            {
                continue;
            }

            var attributes = new Dictionary<string, object?>
            {
                ["$type"] = string.Empty,
                ["$id"] = string.Empty,
                ["$zoom"] = zoom
            };

            results.Add(ParseStyle(layer, scale, attributes));
        }

        return [.. results];
    }

    public Color GetBackgroundColor(double zoom)
    {
        foreach (var brush in GetStyleByType("background", zoom))
        {
            var color = brush.Paint.BackgroundColor;
            return color.WithOpacity(brush.Paint.BackgroundOpacity);
        }

        return Color.White;
    }

    public Brush ParseStyle(Layer layer, double scale, Dictionary<string, object?>? attributes)
    {
        attributes ??= [];

        var brush = new Brush
        {
            ZIndex = layer.Index,
            Layer = layer,
            GlyphsDirectory = FontDirectory,
            Paint = new Paint()
        };
        var paint = brush.Paint;

        foreach (var (key, value) in layer.Paint)
        {
            switch (key)
            {
                case "fill-color":
                    paint.FillColor = ParseColor(GetValue(value, attributes));
                    break;
                case "background-color":
                    paint.BackgroundColor = ParseColor(GetValue(value, attributes));
                    break;
                case "text-color":
                    paint.TextColor = ParseColor(GetValue(value, attributes));
                    break;
                case "line-color":
                    paint.LineColor = ParseColor(GetValue(value, attributes));
                    break;
                case "circle-color":
                    paint.CircleColor = ParseColor(GetValue(value, attributes));
                    break;
                case "line-pattern":
                    paint.LinePattern = GetValue(value, attributes) as string;
                    break;
                case "background-pattern":
                    paint.BackgroundPattern = GetValue(value, attributes) as string;
                    break;
                case "fill-pattern":
                    paint.FillPattern = GetValue(value, attributes) as string;
                    break;
                case "text-opacity":
                    paint.TextOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "icon-opacity":
                    paint.IconOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "line-opacity":
                    paint.LineOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "fill-opacity":
                    paint.FillOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "circle-opacity":
                    paint.CircleOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "background-opacity":
                    paint.BackgroundOpacity = ToDouble(GetValue(value, attributes));
                    break;
                case "line-width":
                    paint.LineWidth = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "line-offset":
                    paint.LineOffset = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "line-blur":
                    paint.LineBlur = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "line-dasharray":
                    if (GetValue(value, attributes) is object?[] dash)
                    {
                        paint.LineDashArray = [.. dash.Select(item => ToDouble(item) * scale)];
                    }

                    break;
                case "circle-radius":
                    paint.CircleRadius = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "text-halo-color":
                    paint.TextStrokeColor = ParseColor(GetValue(value, attributes));
                    break;
                case "text-halo-width":
                    paint.TextStrokeWidth = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "text-halo-blur":
                    paint.TextStrokeBlur = ToDouble(GetValue(value, attributes)) * scale;
                    break;
            }
        }

        foreach (var (key, value) in layer.Layout)
        {
            switch (key)
            {
                case "line-cap":
                    paint.LineCap = (GetValue(value, attributes) as string) switch
                    {
                        "round" => PenLineCap.Round,
                        "square" => PenLineCap.Square,
                        _ => PenLineCap.Flat
                    };
                    break;
                case "line-join":
                    paint.LineJoin = (GetValue(value, attributes) as string) switch
                    {
                        "round" => LineJoin.Round,
                        "bevel" => LineJoin.Bevel,
                        _ => LineJoin.Miter
                    };
                    break;
                case "visibility":
                    paint.Visibility = GetValue(value, attributes) is not "none";
                    break;
                case "text-field":
                    if (GetValue(value, attributes) is string textField)
                    {
                        brush.TextField = textField;
                        brush.Text = RenderTextField(textField, attributes);
                    }

                    break;
                case "text-font":
                    if (GetValue(value, attributes) is object?[] fonts)
                    {
                        paint.TextFont = [.. fonts.Select(font => font?.ToString() ?? string.Empty)
                            .Where(font => font.Length > 0)];
                    }

                    break;
                case "text-size":
                    paint.TextSize = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "text-max-width":
                    paint.TextMaxWidth = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "text-offset":
                    if (GetValue(value, attributes) is object?[] offset && offset.Length >= 2)
                    {
                        paint.TextOffset = new Point(ToDouble(offset[0]) * scale, ToDouble(offset[1]) * scale);
                    }

                    break;
                case "text-optional":
                    paint.TextOptional = GetValue(value, attributes) is true;
                    break;
                case "text-transform":
                    paint.TextTransform = (GetValue(value, attributes) as string) switch
                    {
                        "uppercase" => TextTransform.Uppercase,
                        "lowercase" => TextTransform.Lowercase,
                        _ => TextTransform.None
                    };
                    break;
                case "text-justify":
                    paint.TextJustify = (GetValue(value, attributes) as string) switch
                    {
                        "left" => TextAlignment.Left,
                        "right" => TextAlignment.Right,
                        _ => TextAlignment.Center
                    };
                    break;
                case "text-anchor":
                    if (GetValue(value, attributes) is string anchor)
                    {
                        paint.TextJustify = anchor switch
                        {
                            "left" or "top-left" or "bottom-left" => TextAlignment.Left,
                            "right" or "top-right" or "bottom-right" => TextAlignment.Right,
                            _ => TextAlignment.Center
                        };
                        paint.TextVerticalAnchor = anchor switch
                        {
                            "top" or "top-left" or "top-right" => TextVerticalAnchor.Top,
                            "bottom" or "bottom-left" or "bottom-right" => TextVerticalAnchor.Bottom,
                            _ => TextVerticalAnchor.Center
                        };
                    }

                    break;
                case "text-line-height":
                    paint.TextLineHeight = ToDouble(GetValue(value, attributes));
                    break;
                case "text-padding":
                    paint.TextPadding = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "symbol-placement":
                    paint.SymbolPlacement = (GetValue(value, attributes) as string) == "line"
                        ? SymbolPlacement.Line
                        : SymbolPlacement.Point;
                    break;
                case "icon-size":
                    paint.IconScale = ToDouble(GetValue(value, attributes)) * scale;
                    break;
                case "icon-image":
                    if (GetValue(value, attributes) is string iconImage)
                    {
                        paint.IconImage = RenderTextField(iconImage, attributes);
                    }

                    break;
                case "icon-rotate":
                    paint.IconRotate = ToDouble(GetValue(value, attributes));
                    break;
                case "icon-offset":
                    if (GetValue(value, attributes) is object?[] iconOffset && iconOffset.Length >= 2)
                    {
                        paint.IconOffset = new Point(ToDouble(iconOffset[0]), ToDouble(iconOffset[1]));
                    }

                    break;
                case "text-rotate":
                    paint.TextRotate = ToDouble(GetValue(value, attributes));
                    break;
            }
        }

        return brush;
    }

    public bool ValidateLayer(Layer layer, double zoom, Dictionary<string, object?>? attributes)
    {
        if (layer.MinZoom is { } minZoom && zoom < minZoom)
        {
            return false;
        }

        if (layer.MaxZoom is { } maxZoom && zoom > maxZoom)
        {
            return false;
        }

        return attributes is null || layer.Filter.Length == 0 || ValidateUsingFilter(layer.Filter, attributes);
    }

    /// <summary>Evaluates a MapLibre filter expression (legacy array syntax).</summary>
    public bool ValidateUsingFilter(object?[] filter, Dictionary<string, object?> attributes)
    {
        if (filter.Length == 0 || filter[0] is not string operation)
        {
            return true;
        }

        switch (operation)
        {
            case "all":
                return filter.Skip(1).All(item => item is object?[] sub && ValidateUsingFilter(sub, attributes));
            case "any":
                return filter.Skip(1).Any(item => item is object?[] sub && ValidateUsingFilter(sub, attributes));
            case "none":
                return !filter.Skip(1).Any(item => item is object?[] sub && ValidateUsingFilter(sub, attributes));
        }

        if (operation is "match" or "case" or "step" or "coalesce" or "geometry-type"
            or "to-string" or "to-number" or "to-boolean" or "concat" or "upcase" or "downcase"
            or "length" or "at")
        {
            return IsTruthy(EvaluateExpression(filter, attributes));
        }

        switch (operation)
        {
            case "==":
            case "!=":
            case ">":
            case ">=":
            case "<":
            case "<=":
            {
                object? actual;

                if (filter.Length > 1 && filter[1] is object?[] operand
                    && operand.Length > 0 && operand[0] is string operandOperator
                    && IsExpressionOperator(operandOperator))
                {
                    actual = GetValue(operand, attributes);
                }
                else
                {
                    var key = filter.Length > 1 ? filter[1] as string : null;
                    if (key is null)
                    {
                        return false;
                    }

                    if (!attributes.TryGetValue(key, out actual))
                    {
                        // MapLibre treats a missing property as "not equal" and satisfies the
                        // negative comparison operators.
                        return operation != "==";
                    }
                }

                var expected = filter.Length > 2 ? GetValue(filter[2], attributes) : null;
                return Compare(operation, actual, expected);
            }
        }

        switch (operation)
        {
            case "has":
                return filter.Length > 1 && attributes.ContainsKey(filter[1] as string ?? string.Empty);
            case "!has":
                return filter.Length > 1 && !attributes.ContainsKey(filter[1] as string ?? string.Empty);
            case "in":
            {
                var key = filter.Length > 1 ? filter[1] as string : null;
                if (key is null || !attributes.TryGetValue(key, out var actual))
                {
                    return false;
                }

                return filter.Skip(2).Any(item => ValuesEqual(actual, GetValue(item, attributes)));
            }

            case "!in":
            {
                var key = filter.Length > 1 ? filter[1] as string : null;
                if (key is null || !attributes.TryGetValue(key, out var actual))
                {
                    return true;
                }

                return !filter.Skip(2).Any(item => ValuesEqual(actual, GetValue(item, attributes)));
            }

            default:
                return false;
        }
    }

    private static bool Compare(string operation, object? actual, object? expected)
    {
        if (Utils.IsNumber(actual) && Utils.IsNumber(expected))
        {
            var left = ToDouble(actual);
            var right = ToDouble(expected);
            return operation switch
            {
                "==" => left == right,
                "!=" => left != right,
                ">" => left > right,
                ">=" => left >= right,
                "<" => left < right,
                "<=" => left <= right,
                _ => false
            };
        }

        if (actual is not IComparable comparable || expected is null || actual.GetType() != expected.GetType())
        {
            return operation == "!=";
        }

        var comparison = comparable.CompareTo(expected);
        return operation switch
        {
            "==" => comparison == 0,
            "!=" => comparison != 0,
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            _ => false
        };
    }

    private static bool ValuesEqual(object? left, object? right) =>
        Utils.IsNumber(left) && Utils.IsNumber(right)
            ? ToDouble(left) == ToDouble(right)
            : Equals(left, right);

    private static string RenderTextField(string template, Dictionary<string, object?> attributes) =>
        TextFieldRegex().Replace(template, match =>
            attributes.TryGetValue(match.Groups[1].Value, out var value) ? value?.ToString() ?? string.Empty : string.Empty)
            .Trim();

    [GeneratedRegex(@"\{([A-Za-z0-9\-_:]+)\}")]
    private static partial Regex TextFieldRegex();

    private static Color ParseColor(object? value) =>
        value is Color color ? color : ColorParser.Parse(value?.ToString());

    private static object? GetValue(object? token, Dictionary<string, object?>? attributes)
    {
        if (token is string text && attributes is not null)
        {
            if (text.Length == 0)
            {
                return string.Empty;
            }

            if (text[0] == '$' && attributes.TryGetValue(text, out var attribute))
            {
                return GetValue(attribute, attributes);
            }
        }

        if (token is object?[] array)
        {
            if (array.Length > 0 && array[0] is string operation && IsExpressionOperator(operation))
            {
                return EvaluateExpression(array, attributes);
            }

            return array.Select(item => GetValue(item, attributes)).ToArray();
        }

        if (token is Dictionary<string, object?> dictionary && dictionary.TryGetValue("stops", out var stopsNode)
            && stopsNode is object?[] stops && stops.Length > 0)
        {
            var pointStops = stops
                .OfType<object?[]>()
                .Where(stop => stop.Length >= 2)
                .Select(stop => (Zoom: ToDouble(stop[0]), Value: stop[1]))
                .OrderBy(stop => stop.Zoom)
                .ToArray();

            if (pointStops.Length == 0)
            {
                return null;
            }

            var input = attributes is not null && attributes.TryGetValue("$zoom", out var zoomValue)
                ? ToDouble(zoomValue)
                : pointStops[0].Zoom;

            if (dictionary.TryGetValue("property", out var property) && property is string propertyName
                && attributes is not null && attributes.TryGetValue(propertyName, out var propertyValue)
                && Utils.IsNumber(propertyValue))
            {
                input = ToDouble(propertyValue);
            }

            if (input <= pointStops[0].Zoom)
            {
                return pointStops[0].Value;
            }

            if (input >= pointStops[^1].Zoom)
            {
                return pointStops[^1].Value;
            }

            var zoomA = pointStops[0];
            var zoomB = pointStops[^1];
            for (var index = 1; index < pointStops.Length; index++)
            {
                if (input >= pointStops[index - 1].Zoom && input <= pointStops[index].Zoom)
                {
                    zoomA = pointStops[index - 1];
                    zoomB = pointStops[index];
                    break;
                }
            }

            var power = dictionary.TryGetValue("base", out var baseValue) ? ToDouble(baseValue) : 1;
            return InterpolateValues(zoomA.Value, zoomB.Value, zoomA.Zoom, zoomB.Zoom, input, power);
        }

        return token;
    }

    private static bool IsExpressionOperator(string operation) => operation is
        "literal" or "get" or "has" or "zoom" or "geometry-type" or "id" or "properties" or "coalesce" or "case" or "match"
        or "interpolate" or "step" or "all" or "any" or "!" or "==" or "!=" or ">" or ">=" or "<" or "<="
        or "to-string" or "to-number" or "to-boolean" or "concat" or "upcase" or "downcase" or "length" or "at";

    private static object? EvaluateExpression(object?[] expression, Dictionary<string, object?>? attributes)
    {
        attributes ??= [];
        var operation = expression[0] as string ?? string.Empty;

        object? Arg(int index) => index >= 0 && index < expression.Length
            ? GetValue(expression[index], attributes)
            : null;

        switch (operation)
        {
            case "literal":
                return expression.Length > 1 ? expression[1] : null;

            case "get":
            {
                var key = Arg(1)?.ToString();
                return key is not null && attributes.TryGetValue(key, out var value) ? value : null;
            }

            case "has":
                return Arg(1)?.ToString() is { } hasKey && attributes.ContainsKey(hasKey);

            case "zoom":
                return attributes.TryGetValue("$zoom", out var zoom) ? zoom : 0d;

            case "geometry-type":
                return attributes.GetValueOrDefault("$type");

            case "id":
                return attributes.GetValueOrDefault("$id");

            case "properties":
                return attributes;

            case "coalesce":
                for (var index = 1; index < expression.Length; index++)
                {
                    var candidate = Arg(index);
                    if (candidate is not null)
                    {
                        return candidate;
                    }
                }

                return null;

            case "case":
            {
                var index = 1;
                while (index + 1 < expression.Length)
                {
                    if (IsTruthy(Arg(index)))
                    {
                        return Arg(index + 1);
                    }

                    index += 2;
                }

                return index < expression.Length ? Arg(index) : null;
            }

            case "match":
            {
                var input = Arg(1);
                var index = 2;
                while (index + 1 < expression.Length)
                {
                    if (Matches(input, expression[index], attributes))
                    {
                        return Arg(index + 1);
                    }

                    index += 2;
                }

                return index < expression.Length ? Arg(index) : null;
            }

            case "interpolate":
                return InterpolateExpression(Arg(2), expression, 3, attributes);

            case "step":
                return StepExpression(Arg(1), expression, 2, attributes);

            case "all":
                for (var index = 1; index < expression.Length; index++)
                {
                    if (!IsTruthy(Arg(index)))
                    {
                        return false;
                    }
                }

                return true;

            case "any":
                for (var index = 1; index < expression.Length; index++)
                {
                    if (IsTruthy(Arg(index)))
                    {
                        return true;
                    }
                }

                return false;

            case "!":
                return !IsTruthy(Arg(1));

            case "==":
            case "!=":
            case ">":
            case ">=":
            case "<":
            case "<=":
                return Compare(operation, Arg(1), Arg(2));

            case "to-string":
                return Arg(1)?.ToString() ?? string.Empty;

            case "to-number":
                return Utils.IsNumber(Arg(1)) ? ToDouble(Arg(1)) : expression.Length > 2 ? Arg(2) : 0d;

            case "to-boolean":
                return IsTruthy(Arg(1));

            case "concat":
                return string.Concat(Enumerable.Range(1, Math.Max(0, expression.Length - 1))
                    .Select(index => Arg(index)?.ToString() ?? string.Empty));

            case "upcase":
                return Arg(1)?.ToString()?.ToUpperInvariant() ?? string.Empty;

            case "downcase":
                return Arg(1)?.ToString()?.ToLowerInvariant() ?? string.Empty;

            case "length":
                return Arg(1) switch
                {
                    string text => text.Length,
                    object?[] values => values.Length,
                    _ => 0
                };

            case "at":
            {
                var values = Arg(1) as object?[];
                var index = (int)ToDouble(Arg(2));
                return values is not null && index >= 0 && index < values.Length ? values[index] : null;
            }

            default:
                return null;
        }
    }

    private static bool Matches(object? input, object? labelsToken, Dictionary<string, object?> attributes)
    {
        if (labelsToken is object?[] labels)
        {
            return labels.Any(label => ValuesEqual(input, GetValue(label, attributes)));
        }

        return ValuesEqual(input, GetValue(labelsToken, attributes));
    }

    private static object? InterpolateExpression(
        object? input,
        object?[] expression,
        int start,
        Dictionary<string, object?> attributes)
    {
        var stops = new List<(double Stop, object? Value)>();
        for (var index = start; index + 1 < expression.Length; index += 2)
        {
            stops.Add((ToDouble(GetValue(expression[index], attributes)), GetValue(expression[index + 1], attributes)));
        }

        if (stops.Count == 0)
        {
            return null;
        }

        stops.Sort(static (left, right) => left.Stop.CompareTo(right.Stop));
        var value = ToDouble(input);

        if (value <= stops[0].Stop)
        {
            return stops[0].Value;
        }

        if (value >= stops[^1].Stop)
        {
            return stops[^1].Value;
        }

        for (var index = 1; index < stops.Count; index++)
        {
            if (value <= stops[index].Stop)
            {
                return InterpolateValues(
                    stops[index - 1].Value,
                    stops[index].Value,
                    stops[index - 1].Stop,
                    stops[index].Stop,
                    value,
                    1);
            }
        }

        return stops[^1].Value;
    }

    private static object? StepExpression(
        object? input,
        object?[] expression,
        int start,
        Dictionary<string, object?> attributes)
    {
        var output = start < expression.Length ? GetValue(expression[start], attributes) : null;
        var value = ToDouble(input);

        for (var index = start + 1; index + 1 < expression.Length; index += 2)
        {
            if (value < ToDouble(GetValue(expression[index], attributes)))
            {
                break;
            }

            output = GetValue(expression[index + 1], attributes);
        }

        return output;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        object?[] array => array.Length > 0,
        _ => Utils.IsNumber(value) ? ToDouble(value) != 0 : true
    };

    private static object? InterpolateValues(
        object? startValue,
        object? endValue,
        double zoomA,
        double zoomB,
        double zoom,
        double power)
    {
        if (startValue is string startString)
        {
            // Colour interpolation is intentionally limited to the nearest stop.
            return Math.Abs(zoomA - zoom) <= Math.Abs(zoomB - zoom) ? startString : endValue as string;
        }

        if (startValue is object?[] startArray && endValue is object?[] endArray)
        {
            var result = new object?[startArray.Length];
            for (var index = 0; index < startArray.Length; index++)
            {
                result[index] = InterpolateValues(
                    startArray[index],
                    index < endArray.Length ? endArray[index] : null,
                    zoomA,
                    zoomB,
                    zoom,
                    power);
            }

            return result;
        }

        if (Utils.IsNumber(startValue))
        {
            return InterpolateRange(zoom, zoomA, zoomB, ToDouble(startValue), ToDouble(endValue), power);
        }

        return endValue;
    }

    private static double InterpolateRange(
        double value,
        double fromMin,
        double fromMax,
        double toMin,
        double toMax,
        double power)
    {
        var difference = fromMax - fromMin;
        var progress = value - fromMin;

        double normalized;
        if (difference == 0)
        {
            normalized = 0;
        }
        else if (power == 1)
        {
            normalized = progress / difference;
        }
        else
        {
            normalized = (Math.Pow(power, progress) - 1) / (Math.Pow(power, difference) - 1);
        }

        return (normalized * (toMax - toMin)) + toMin;
    }

    private static object? Plainify(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject jsonObject:
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, value) in jsonObject)
                {
                    result[key] = Plainify(value);
                }

                return result;
            }

            case JsonArray jsonArray:
                return jsonArray.Select(Plainify).ToArray();
            case JsonValue jsonValue:
            {
                if (jsonValue.TryGetValue<bool>(out var boolean))
                {
                    return boolean;
                }

                if (jsonValue.TryGetValue<string>(out var text))
                {
                    return text;
                }

                if (jsonValue.TryGetValue<double>(out var number))
                {
                    return number;
                }

                return jsonValue.ToJsonString();
            }

            default:
                return null;
        }
    }

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        double number => number,
        IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0
    };
}
