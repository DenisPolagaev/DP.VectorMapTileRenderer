using SkiaSharp;

namespace DP.VectorMapTileRenderer;

/// <summary>Drawing surface used by <see cref="Renderer"/>. The only implementation is <see cref="SkiaCanvas"/>.</summary>
public interface ICanvas
{
    bool ClipOverflow { get; set; }

    void StartDrawing(double width, double height);

    void DrawBackground(Brush style);

    void DrawLineString(List<Point> geometry, Brush style);

    void DrawPolygon(List<Point> geometry, Brush style);

    /// <summary>Draws a polygon feature as one path: exterior ring plus holes (even-odd fill).</summary>
    void DrawPolygon(IReadOnlyList<List<Point>> rings, Brush style);

    void DrawPoint(Point geometry, Brush style);

    void DrawText(Point geometry, Brush style);

    void DrawTextOnPath(List<Point> geometry, Brush style);

    void DrawImage(Stream imageStream, Brush style);

    void DrawUnknown(List<List<Point>> geometry, Brush style);

    SKBitmap FinishDrawing();
}
