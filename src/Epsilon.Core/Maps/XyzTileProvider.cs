using System.Globalization;

namespace Epsilon.Core.Maps;

/// <summary>
/// Any tile server or local tile folder addressed with a {z}/{x}/{y} template (XYZ by default, TMS optional).
/// Used for the standard maps (OpenStreetMap, OpenTopoMap, Esri) and for every custom map.
/// </summary>
public sealed class XyzTileProvider : IMapTileProvider
{
    private readonly string _template;

    public XyzTileProvider(string id, string name, string group, string urlTemplate, int minZoom, int maxZoom,
                           string attribution, TileScheme scheme = TileScheme.Xyz, bool allowBulkDownload = true,
                           string usageNote = null)
    {
        Id = id;
        Name = name;
        Group = group;
        _template = urlTemplate ?? "";
        MinZoom = minZoom;
        MaxZoom = maxZoom;
        Attribution = attribution ?? "";
        Scheme = scheme;
        IsLocal = IsLocalTemplate(_template);
        AllowBulkDownload = allowBulkDownload && !IsLocal;   // local tiles are already on disk
        UsageNote = IsLocal ? "Local tile folder - tiles are read directly, nothing is downloaded." : usageNote;
    }

    public static XyzTileProvider FromDefinition(CustomMapDefinition d) =>
        new(d.Id, d.Name, "Custom", d.UrlTemplate, d.MinZoom, d.MaxZoom,
            string.IsNullOrWhiteSpace(d.Attribution) ? d.Name : d.Attribution, d.TileScheme);

    public string Id { get; }
    public string Name { get; }
    public string Group { get; }
    public int MinZoom { get; }
    public int MaxZoom { get; }
    public string Attribution { get; }
    public TileScheme Scheme { get; }
    public string UrlTemplate => _template;
    public bool IsLocal { get; }
    public bool AllowBulkDownload { get; }
    public bool AllowOffline => true;
    public bool RespectCacheHeaders => false;
    public string UsageNote { get; }

    public Task EnsureReadyAsync(HttpClient http, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Fills the template. <paramref name="y"/> is XYZ; for TMS providers it is converted with
    /// yTms = (2^zoom - 1) - yXyz. For local templates the result is a file path.
    /// </summary>
    public string GetTileUrl(int x, int y, int zoom)
    {
        int row = Scheme == TileScheme.Tms ? TileMath.XyzToTmsY(y, zoom) : y;
        string filled = Fill(_template, x, row, zoom);
        return IsLocal ? ToLocalPath(filled) : filled;
    }

    public Task<string> GetViewportAttributionAsync(HttpClient http, GeoBounds view, int zoom, CancellationToken ct) =>
        Task.FromResult<string>(null);

    /// <summary>Deterministic replacement of {z}, {x}, {y} (case-insensitive).</summary>
    public static string Fill(string template, int x, int y, int zoom) =>
        (template ?? "")
            .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    /// <summary>file:///..., or a rooted Windows / UNC path.</summary>
    public static bool IsLocalTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return false;
        string t = template.Trim();
        if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        return (t.Length > 2 && char.IsLetter(t[0]) && t[1] == ':') || t.StartsWith(@"\\") || t.StartsWith("/");
    }

    private static string ToLocalPath(string filled)
    {
        if (!filled.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return filled;
        try { return new Uri(filled).LocalPath; }
        catch (UriFormatException) { return filled.Substring("file:".Length).TrimStart('/'); }
    }
}
