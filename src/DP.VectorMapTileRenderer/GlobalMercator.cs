namespace DP.VectorMapTileRenderer;

/// <summary>Spherical Mercator (EPSG:3857) / slippy-map tile math.</summary>
public sealed class GlobalMercator
{
    private readonly int _tileSize;
    private readonly double _initialResolution;
    private readonly double _originShift;

    public GlobalMercator(int tileSize = 256)
    {
        _tileSize = tileSize;
        _initialResolution = (2 * Math.PI * 6378137) / tileSize;
        _originShift = 2 * Math.PI * 6378137 / 2.0;
    }

    public sealed record CoordinatePair(double X, double Y);

    public sealed record TileAddress(int X, int Y);

    public sealed record GeoExtent(double North, double South, double East, double West);

    public CoordinatePair LatLonToMeters(double latitude, double longitude) =>
        new(longitude * _originShift / 180.0,
            Math.Log(Math.Tan((90 + latitude) * Math.PI / 360.0)) / (Math.PI / 180.0) * _originShift / 180.0);

    public CoordinatePair MetersToLatLon(double metersX, double metersY)
    {
        var lon = metersX / _originShift * 180.0;
        var lat = metersY / _originShift * 180.0;
        lat = 180 / Math.PI * ((2 * Math.Atan(Math.Exp(lat * Math.PI / 180.0))) - (Math.PI / 2.0));
        return new CoordinatePair(lon, lat);
    }

    public CoordinatePair PixelsToMeters(double pixelX, double pixelY, int zoom)
    {
        var resolution = Resolution(zoom);
        return new CoordinatePair((pixelX * resolution) - _originShift, (pixelY * resolution) - _originShift);
    }

    public CoordinatePair MetersToPixels(double metersX, double metersY, int zoom)
    {
        var resolution = Resolution(zoom);
        return new CoordinatePair((metersX + _originShift) / resolution, (metersY + _originShift) / resolution);
    }

    public TileAddress PixelsToTile(double pixelX, double pixelY) =>
        new((int)(Math.Ceiling(pixelX / _tileSize) - 1), (int)(Math.Ceiling(pixelY / _tileSize) - 1));

    public TileAddress MetersToTile(double metersX, double metersY, int zoom)
    {
        var pixels = MetersToPixels(metersX, metersY, zoom);
        return PixelsToTile(pixels.X, pixels.Y);
    }

    public TileAddress LatLonToTile(double latitude, double longitude, int zoom)
    {
        var meters = LatLonToMeters(latitude, longitude);
        return MetersToTile(meters.X, meters.Y, zoom);
    }

    public TileAddress LatLonToTileXyz(double latitude, double longitude, int zoom)
    {
        var tile = LatLonToTile(latitude, longitude, zoom);
        return tile with { Y = (int)Math.Pow(2, zoom) - tile.Y - 1 };
    }

    public GeoExtent TileBounds(int tileX, int tileY, int zoom)
    {
        var min = PixelsToMeters(tileX * _tileSize, tileY * _tileSize, zoom);
        var max = PixelsToMeters((tileX + 1) * _tileSize, (tileY + 1) * _tileSize, zoom);
        return new GeoExtent(max.Y, min.Y, max.X, min.X);
    }

    public GeoExtent TileLatLonBounds(int tileX, int tileY, int zoom)
    {
        var bounds = TileBounds(tileX, tileY, zoom);
        var min = MetersToLatLon(bounds.West, bounds.South);
        var max = MetersToLatLon(bounds.East, bounds.North);
        return new GeoExtent(max.Y, min.Y, max.X, min.X);
    }

    public TileAddress GoogleTile(int tileX, int tileY, int zoom) =>
        new(tileX, (int)(Math.Pow(2, zoom) - 1 - tileY));

    public string QuadTree(int tileX, int tileY, int zoom)
    {
        tileY = ((1 << zoom) - 1) - tileY;
        var result = new System.Text.StringBuilder(zoom);
        for (var index = zoom; index >= 1; index--)
        {
            var digit = 0;
            var mask = 1 << (index - 1);
            if ((tileX & mask) != 0)
            {
                digit += 1;
            }

            if ((tileY & mask) != 0)
            {
                digit += 2;
            }

            result.Append(digit);
        }

        return result.ToString();
    }

    public TileAddress QuadTreeToTile(string quadTree, int zoom)
    {
        var tileX = 0;
        var tileY = 0;

        for (var index = zoom; index >= 1; index--)
        {
            var digit = quadTree[zoom - index] - '0';
            var mask = 1 << (index - 1);
            if ((digit & 1) != 0)
            {
                tileX += mask;
            }

            if ((digit & 2) != 0)
            {
                tileY += mask;
            }
        }

        return new TileAddress(tileX, ((1 << zoom) - 1) - tileY);
    }

    public string LatLonToQuadTree(double latitude, double longitude, int zoom)
    {
        var meters = LatLonToMeters(latitude, longitude);
        var tile = MetersToTile(meters.X, meters.Y, zoom);
        return QuadTree(tile.X, tile.Y, zoom);
    }

    private double Resolution(int zoom) => _initialResolution / (1 << zoom);
}
