namespace DP.VectorMapTileRenderer;

/// <summary>A point in tile-local coordinates (the vector-tile grid, <c>0..Extent</c>) or in pixels.</summary>
public readonly record struct Point(double X, double Y)
{
    public static Point Empty => default;

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static Point operator +(Point left, Point right) => new(left.X + right.X, left.Y + right.Y);

    public static Point operator -(Point left, Point right) => new(left.X - right.X, left.Y - right.Y);

    public static Point operator *(Point point, double factor) => new(point.X * factor, point.Y * factor);
}

/// <summary>An axis-aligned rectangle. Replaces <c>System.Windows.Rect</c> so the library has no WPF dependency.</summary>
public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public Point TopLeft => new(Left, Top);

    public Point TopRight => new(Right, Top);

    public Point BottomRight => new(Right, Bottom);

    public Point BottomLeft => new(Left, Bottom);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static Rect FromLtrb(double left, double top, double right, double bottom) =>
        new(left, top, right - left, bottom - top);

    public bool Contains(Point point) =>
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public bool Contains(Rect rect) =>
        rect.Left >= Left && rect.Right <= Right && rect.Top >= Top && rect.Bottom <= Bottom;

    public bool IntersectsWith(Rect rect) =>
        rect.Left < Right && Left < rect.Right && rect.Top < Bottom && Top < rect.Bottom;

    public Rect Inflate(double horizontal, double vertical) =>
        FromLtrb(Left - horizontal, Top - vertical, Right + horizontal, Bottom + vertical);

    public Rect Inflate(double amount) => Inflate(amount, amount);
}

internal static class Utils
{
    public static double ConvertRange(
        double oldValue,
        double oldMin,
        double oldMax,
        double newMin,
        double newMax,
        bool clamp = false)
    {
        var oldRange = oldMax - oldMin;
        var newValue = oldRange == 0
            ? newMin
            : (((oldValue - oldMin) * (newMax - newMin)) / oldRange) + newMin;

        if (clamp)
        {
            newValue = Math.Min(Math.Max(newValue, newMin), newMax);
        }

        return newValue;
    }

    public static string Sha256(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsNumber(object? value) => value is sbyte or byte or short or ushort or int or uint
        or long or ulong or float or double or decimal;
}
