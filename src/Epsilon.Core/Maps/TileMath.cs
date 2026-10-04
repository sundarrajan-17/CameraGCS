namespace Epsilon.Core.Maps;

/// <summary>A geographic rectangle in degrees.</summary>
public readonly record struct GeoBounds(double North, double South, double West, double East)
{
    public bool IsValid =>
        North is >= -90 and <= 90 && South is >= -90 and <= 90 && West is >= -180 and <= 180 && East is >= -180 and <= 180 &&
        North > South && East > West;
}

/// <summary>Inclusive tile index range at one zoom level.</summary>
public readonly record struct TileRange(int Zoom, int MinX, int MaxX, int MinY, int MaxY)
{
    public long Count => (long)(MaxX - MinX + 1) * (MaxY - MinY + 1);
}

/// <summary>
/// Standard Web Mercator XYZ ("slippy map") tile maths.
/// XYZ convention: tile (0,0) is the TOP-LEFT (north-west) tile, X grows eastwards, Y grows SOUTHWARDS.
/// TMS numbers Y from the bottom instead: yTms = (2^zoom - 1) - yXyz (see <see cref="XyzToTmsY"/>).
/// </summary>
public static class TileMath
{
    /// <summary>Web Mercator latitude limit, degrees.</summary>
    public const double MaxLatitude = 85.0511287798066;

    public const int MaxSupportedZoom = 24;

    /// <summary>x = floor((lon + 180) / 360 * 2^z), clamped to 0 .. 2^z - 1.</summary>
    public static int LonToTileX(double lon, int zoom)
    {
        double n = Math.Pow(2, zoom);
        int x = (int)Math.Floor((lon + 180.0) / 360.0 * n);
        return Math.Clamp(x, 0, (int)n - 1);
    }

    /// <summary>
    /// y = floor((1 - asinh(tan(lat)) / PI) / 2 * 2^z), with the latitude clamped to the Web Mercator range and the
    /// result clamped to 0 .. 2^z - 1. XYZ numbering (0 = north).
    /// </summary>
    public static int LatToTileY(double lat, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double latRad = Math.Clamp(lat, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
        int y = (int)Math.Floor((1.0 - Math.Asinh(Math.Tan(latRad)) / Math.PI) / 2.0 * n);
        return Math.Clamp(y, 0, (int)n - 1);
    }

    /// <summary>North-west corner of a tile, degrees.</summary>
    public static (double Lat, double Lon) TileToLatLon(int x, int y, int zoom)
    {
        double n = Math.Pow(2, zoom);
        double lon = x / n * 360.0 - 180.0;
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180.0 / Math.PI;
        return (lat, lon);
    }

    /// <summary>XYZ row to TMS row (and back - the conversion is its own inverse).</summary>
    public static int XyzToTmsY(int y, int zoom) => (1 << zoom) - 1 - y;

    /// <summary>Tiles covering the bounds at one zoom level.</summary>
    public static TileRange Range(GeoBounds b, int zoom) =>
        new(zoom, LonToTileX(b.West, zoom), LonToTileX(b.East, zoom), LatToTileY(b.North, zoom), LatToTileY(b.South, zoom));

    /// <summary>Tiles per zoom level for the bounds and zoom range.</summary>
    public static IReadOnlyList<TileRange> Ranges(GeoBounds b, int minZoom, int maxZoom)
    {
        if (!b.IsValid) throw new ArgumentException("North must be above South and East right of West, all within range.");
        if (minZoom < 0 || maxZoom > MaxSupportedZoom || minZoom > maxZoom) throw new ArgumentException("Invalid zoom range.");
        var list = new List<TileRange>();
        for (int z = minZoom; z <= maxZoom; z++) list.Add(Range(b, z));
        return list;
    }

    /// <summary>Total number of tiles for the bounds and zoom range.</summary>
    public static long CountTiles(GeoBounds b, int minZoom, int maxZoom) => Ranges(b, minZoom, maxZoom).Sum(r => r.Count);

    /// <summary>All (x, y) of a range, row by row.</summary>
    public static IEnumerable<(int X, int Y)> Enumerate(TileRange r)
    {
        for (int y = r.MinY; y <= r.MaxY; y++)
            for (int x = r.MinX; x <= r.MaxX; x++)
                yield return (x, y);
    }
}
