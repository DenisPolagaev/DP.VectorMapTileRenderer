namespace DP.VectorMapTileRenderer;

/// <summary>
/// An alpha-premultiplied-free RGBA colour. Replaces <c>System.Windows.Media.Color</c> so the
/// style engine does not depend on WPF.
/// </summary>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color Transparent => new(0, 0, 0, 0);

    public static Color White => new(255, 255, 255, 255);

    public static Color Black => new(255, 0, 0, 0);

    public static Color FromRgb(byte r, byte g, byte b) => new(255, r, g, b);

    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    /// <summary>Multiplies the alpha channel by <paramref name="opacity"/> (0..1).</summary>
    public Color WithOpacity(double opacity) =>
        new((byte)Math.Clamp(Math.Round(A * opacity), 0, 255), R, G, B);
}

/// <summary>Parses MapLibre/Mapbox style colour strings (<c>#rgb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>, <c>hsl()</c>, names).</summary>
public static class ColorParser
{
    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["transparent"] = Color.Transparent,
        ["black"] = Color.FromRgb(0, 0, 0),
        ["silver"] = Color.FromRgb(192, 192, 192),
        ["gray"] = Color.FromRgb(128, 128, 128),
        ["grey"] = Color.FromRgb(128, 128, 128),
        ["white"] = Color.White,
        ["maroon"] = Color.FromRgb(128, 0, 0),
        ["red"] = Color.FromRgb(255, 0, 0),
        ["purple"] = Color.FromRgb(128, 0, 128),
        ["fuchsia"] = Color.FromRgb(255, 0, 255),
        ["magenta"] = Color.FromRgb(255, 0, 255),
        ["green"] = Color.FromRgb(0, 128, 0),
        ["lime"] = Color.FromRgb(0, 255, 0),
        ["olive"] = Color.FromRgb(128, 128, 0),
        ["yellow"] = Color.FromRgb(255, 255, 0),
        ["navy"] = Color.FromRgb(0, 0, 128),
        ["blue"] = Color.FromRgb(0, 0, 255),
        ["teal"] = Color.FromRgb(0, 128, 128),
        ["aqua"] = Color.FromRgb(0, 255, 255),
        ["cyan"] = Color.FromRgb(0, 255, 255),
        ["orange"] = Color.FromRgb(255, 165, 0),
        ["brown"] = Color.FromRgb(165, 42, 42),
        ["pink"] = Color.FromRgb(255, 192, 203),
        ["gold"] = Color.FromRgb(255, 215, 0),
        ["beige"] = Color.FromRgb(245, 245, 220),
        ["ivory"] = Color.FromRgb(255, 255, 240),
        ["khaki"] = Color.FromRgb(240, 230, 140),
        ["salmon"] = Color.FromRgb(250, 128, 114),
        ["coral"] = Color.FromRgb(255, 127, 80),
        ["crimson"] = Color.FromRgb(220, 20, 60),
        ["indigo"] = Color.FromRgb(75, 0, 130),
        ["violet"] = Color.FromRgb(238, 130, 238),
        ["tan"] = Color.FromRgb(210, 180, 140),
        ["turquoise"] = Color.FromRgb(64, 224, 208),
        ["skyblue"] = Color.FromRgb(135, 206, 235),
        ["steelblue"] = Color.FromRgb(70, 130, 180),
        ["slategray"] = Color.FromRgb(112, 128, 144),
        ["slategrey"] = Color.FromRgb(112, 128, 144),
        ["lightgray"] = Color.FromRgb(211, 211, 211),
        ["lightgrey"] = Color.FromRgb(211, 211, 211),
        ["darkgray"] = Color.FromRgb(169, 169, 169),
        ["darkgrey"] = Color.FromRgb(169, 169, 169),
        ["dimgray"] = Color.FromRgb(105, 105, 105),
        ["dimgrey"] = Color.FromRgb(105, 105, 105)
    };

    /// <summary>Parses a style colour. Unknown or malformed values return <see cref="Color.Transparent"/>.</summary>
    public static Color Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Color.Transparent;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            return ParseHex(text[1..]);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRgb(text);
        }

        if (text.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHsl(text);
        }

        return NamedColors.TryGetValue(text, out var named) ? named : Color.Transparent;
    }

    private static Color ParseHex(string hex)
    {
        return hex.Length switch
        {
            3 => Color.FromRgb(Expand(hex[0]), Expand(hex[1]), Expand(hex[2])),
            4 => new Color(Expand(hex[3]), Expand(hex[0]), Expand(hex[1]), Expand(hex[2])),
            6 => Color.FromRgb(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4)),
            8 => new Color(Byte(hex, 6), Byte(hex, 0), Byte(hex, 2), Byte(hex, 4)),
            _ => Color.Transparent
        };
    }

    private static Color ParseRgb(string text)
    {
        var parts = SplitArguments(text);
        if (parts.Length < 3)
        {
            return Color.Transparent;
        }

        var alpha = parts.Length > 3 ? ParseAlpha(parts[3]) : (byte)255;
        return new Color(alpha, ParseChannel(parts[0]), ParseChannel(parts[1]), ParseChannel(parts[2]));
    }

    private static Color ParseHsl(string text)
    {
        var parts = SplitArguments(text);
        if (parts.Length < 3)
        {
            return Color.Transparent;
        }

        var hue = ((ParseNumber(parts[0]) % 360) + 360) % 360 / 360.0;
        var saturation = ParsePercent(parts[1]);
        var lightness = ParsePercent(parts[2]);
        var alpha = parts.Length > 3 ? ParseAlpha(parts[3]) : (byte)255;
        var (r, g, b) = HslToRgb(hue, saturation, lightness);
        return new Color(alpha, r, g, b);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        if (s <= 0)
        {
            var gray = ToByte(l * 255);
            return (gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return (ToByte(HueToRgb(p, q, h + (1.0 / 3)) * 255),
            ToByte(HueToRgb(p, q, h) * 255),
            ToByte(HueToRgb(p, q, h - (1.0 / 3)) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2)
        {
            return q;
        }

        return t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6) : p;
    }

    private static string[] SplitArguments(string text)
    {
        var start = text.IndexOf('(');
        var end = text.LastIndexOf(')');
        if (start < 0 || end <= start)
        {
            return [];
        }

        return text[(start + 1)..end]
            .Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static byte ParseChannel(string text) => ParsePercent(text) is var percent && text.Contains('%')
        ? ToByte(percent * 255)
        : ToByte(ParseNumber(text));

    private static double ParsePercent(string text) => ParseNumber(text.TrimEnd('%')) / 100.0;

    private static byte ParseAlpha(string text) => text.Contains('%')
        ? ToByte(ParsePercent(text) * 255)
        : ToByte(ParseNumber(text) * 255);

    private static double ParseNumber(string text) =>
        double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static byte Expand(char c) => (byte)(Convert.ToInt32(c.ToString() + c, 16) & 0xFF);

    private static byte Byte(string hex, int index) =>
        byte.TryParse(hex.AsSpan(index, 2), System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : (byte)0;

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
