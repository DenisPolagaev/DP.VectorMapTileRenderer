namespace DP.VectorMapTileRenderer;

/// <summary>
/// Offsets a polyline along its normals, approximating MapLibre's <c>line-offset</c>
/// (positive values shift to the left of the direction of travel).
/// </summary>
public static class LineOffsetter
{
    public static List<Point> Offset(List<Point> line, double offset)
    {
        if (line.Count < 2 || offset == 0)
        {
            return line;
        }

        var closed = line[0] == line[^1];
        if (closed)
        {
            var unique = line.GetRange(0, line.Count - 1);
            var shifted = OffsetOpen(unique, offset, wrap: true);
            if (shifted.Count > 0)
            {
                shifted.Add(shifted[0]);
            }

            return shifted;
        }

        return OffsetOpen(line, offset, wrap: false);
    }

    private static List<Point> OffsetOpen(List<Point> line, double offset, bool wrap)
    {
        var count = line.Count;
        var normals = new (double X, double Y)[count];
        for (var index = 0; index < count; index++)
        {
            var next = (index + 1) % count;
            if (!wrap && index == count - 1)
            {
                normals[index] = normals[index - 1];
                continue;
            }

            var dx = line[next].X - line[index].X;
            var dy = line[next].Y - line[index].Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            normals[index] = length < 1e-9 ? (0, 0) : (dy / length, -dx / length);
        }

        var result = new List<Point>(count);
        for (var index = 0; index < count; index++)
        {
            var previous = index == 0
                ? normals[wrap ? ^1 : 0]
                : normals[index - 1];
            var next = normals[index];

            var nx = previous.X + next.X;
            var ny = previous.Y + next.Y;
            var length = Math.Sqrt((nx * nx) + (ny * ny));
            if (length < 1e-9)
            {
                nx = next.X;
                ny = next.Y;
                length = Math.Sqrt((nx * nx) + (ny * ny));
            }

            if (length < 1e-9)
            {
                result.Add(line[index]);
                continue;
            }

            result.Add(new Point(
                line[index].X + (nx / length * offset),
                line[index].Y + (ny / length * offset)));
        }

        return result;
    }
}
