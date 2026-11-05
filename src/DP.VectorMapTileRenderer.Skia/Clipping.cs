namespace DP.VectorMapTileRenderer;

internal static class LineClipper
{
    [Flags]
    private enum OutCode
    {
        Inside = 0,
        Left = 1,
        Right = 2,
        Bottom = 4,
        Top = 8
    }

    private static OutCode ComputeOutCode(double x, double y, Rect rect)
    {
        var code = OutCode.Inside;

        if (x < rect.Left)
        {
            code |= OutCode.Left;
        }

        if (x > rect.Right)
        {
            code |= OutCode.Right;
        }

        if (y < rect.Top)
        {
            code |= OutCode.Top;
        }

        if (y > rect.Bottom)
        {
            code |= OutCode.Bottom;
        }

        return code;
    }

    private static OutCode ComputeOutCode(Point point, Rect rect) => ComputeOutCode(point.X, point.Y, rect);

    private static Point CalculateIntersection(Rect rect, Point p1, Point p2, OutCode clipTo)
    {
        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;

        var slopeY = dx / dy;
        var slopeX = dy / dx;

        if (clipTo.HasFlag(OutCode.Top))
        {
            return new Point(p1.X + (slopeY * (rect.Top - p1.Y)), rect.Top);
        }

        if (clipTo.HasFlag(OutCode.Bottom))
        {
            return new Point(p1.X + (slopeY * (rect.Bottom - p1.Y)), rect.Bottom);
        }

        if (clipTo.HasFlag(OutCode.Right))
        {
            return new Point(rect.Right, p1.Y + (slopeX * (rect.Right - p1.X)));
        }

        if (clipTo.HasFlag(OutCode.Left))
        {
            return new Point(rect.Left, p1.Y + (slopeX * (rect.Left - p1.X)));
        }

        throw new ArgumentOutOfRangeException(nameof(clipTo), clipTo, "Unknown clip region.");
    }

    public static (Point Start, Point End)? ClipSegment(Rect rect, Point p1, Point p2)
    {
        var outCodeP1 = ComputeOutCode(p1, rect);
        var outCodeP2 = ComputeOutCode(p2, rect);

        while (true)
        {
            if ((outCodeP1 | outCodeP2) == OutCode.Inside)
            {
                return (p1, p2);
            }

            if ((outCodeP1 & outCodeP2) != 0)
            {
                return null;
            }

            var outCode = outCodeP1 != OutCode.Inside ? outCodeP1 : outCodeP2;
            var point = CalculateIntersection(rect, p1, p2, outCode);

            if (outCode == outCodeP1)
            {
                p1 = point;
                outCodeP1 = ComputeOutCode(p1, rect);
            }
            else
            {
                p2 = point;
                outCodeP2 = ComputeOutCode(p2, rect);
            }
        }
    }

    private static Rect GetLineRect(List<Point> polyline)
    {
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var point in polyline)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    public static List<Point>? ClipPolyline(List<Point> polyline, Rect bounds)
    {
        if (polyline.Count == 0 || !bounds.IntersectsWith(GetLineRect(polyline)))
        {
            return null;
        }

        List<Point>? result = null;

        for (var index = 1; index < polyline.Count; index++)
        {
            var segment = ClipSegment(bounds, polyline[index - 1], polyline[index]);
            if (segment is not { } clipped)
            {
                continue;
            }

            if (result is null)
            {
                result = [clipped.Start, clipped.End];
            }
            else if (result[^1] == clipped.Start)
            {
                result.Add(clipped.End);
            }
            else
            {
                result.Add(clipped.Start);
                result.Add(clipped.End);
            }
        }

        return result;
    }
}

internal static class PolygonClipper
{
    /// <summary>Clips a ring against an axis-aligned rectangle (Sutherland-Hodgman).</summary>
    public static List<Point>? Clip(List<Point> ring, Rect bounds)
    {
        if (ring.Count < 3)
        {
            return null;
        }

        var output = ring;
        output = ClipEdge(output, point => point.X >= bounds.Left, (a, b) => IntersectVertical(a, b, bounds.Left));
        output = ClipEdge(output, point => point.X <= bounds.Right, (a, b) => IntersectVertical(a, b, bounds.Right));
        output = ClipEdge(output, point => point.Y >= bounds.Top, (a, b) => IntersectHorizontal(a, b, bounds.Top));
        output = ClipEdge(output, point => point.Y <= bounds.Bottom, (a, b) => IntersectHorizontal(a, b, bounds.Bottom));

        return output.Count >= 3 ? output : null;
    }

    private static List<Point> ClipEdge(
        List<Point> input,
        Func<Point, bool> inside,
        Func<Point, Point, Point> intersect)
    {
        var output = new List<Point>(input.Count + 2);
        if (input.Count == 0)
        {
            return output;
        }

        var previous = input[^1];
        var previousInside = inside(previous);

        foreach (var current in input)
        {
            var currentInside = inside(current);
            if (currentInside)
            {
                if (!previousInside)
                {
                    output.Add(intersect(previous, current));
                }

                output.Add(current);
            }
            else if (previousInside)
            {
                output.Add(intersect(previous, current));
            }

            previous = current;
            previousInside = currentInside;
        }

        return output;
    }

    private static Point IntersectVertical(Point a, Point b, double x)
    {
        var t = (x - a.X) / (b.X - a.X);
        return new Point(x, a.Y + (t * (b.Y - a.Y)));
    }

    private static Point IntersectHorizontal(Point a, Point b, double y)
    {
        var t = (y - a.Y) / (b.Y - a.Y);
        return new Point(a.X + (t * (b.X - a.X)), y);
    }
}
