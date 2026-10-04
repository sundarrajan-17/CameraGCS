namespace Epsilon.Core.Maps;

public enum TileScheme
{
    /// <summary>Standard slippy-map numbering: y = 0 at the top (north). Default.</summary>
    Xyz,
    /// <summary>Tile Map Service numbering: y = 0 at the bottom (south).</summary>
    Tms,
}

/// <summary>
/// A source of 256 x 256 Web Mercator tiles. The map, the cache and the area downloader only talk to this
/// interface; provider-specific URLs, keys and rules stay inside the implementations.
/// </summary>
public interface IMapTileProvider
{
    /// <summary>Stable identifier, also the cache folder name. Unique per provider (no cache collisions).</summary>
    string Id { get; }
    string Name { get; }
    /// <summary>Group shown in the source menu ("Standard", "Google", "Custom").</summary>
    string Group { get; }
    int MinZoom { get; }
    int MaxZoom { get; }
    /// <summary>Attribution that must be shown on the map.</summary>
    string Attribution { get; }

    /// <summary>Tiles are files on this PC (no download, no cache copy).</summary>
    bool IsLocal { get; }

    /// <summary>The source's terms allow downloading whole areas in advance.</summary>
    bool AllowBulkDownload { get; }

    /// <summary>Cached tiles may be used in Offline mode.</summary>
    bool AllowOffline { get; }

    /// <summary>Only cache tiles as the HTTP Cache-Control headers allow (and only for that long).</summary>
    bool RespectCacheHeaders { get; }

    /// <summary>Why bulk download / offline use is not available (shown to the operator), or null.</summary>
    string UsageNote { get; }

    /// <summary>
    /// Prepares the provider (e.g. obtains a session). Must be called before <see cref="GetTileUrl"/>;
    /// cheap when already prepared. Throws with a readable message when the provider cannot be used.
    /// </summary>
    Task EnsureReadyAsync(HttpClient http, CancellationToken ct);

    /// <summary>
    /// URL (or, for local providers, file path) of tile x / y / zoom in XYZ numbering; the provider converts to
    /// its own scheme. Must not be logged when it may contain credentials.
    /// </summary>
    string GetTileUrl(int x, int y, int zoom);

    /// <summary>Optional dynamic attribution for the visible area; null = use <see cref="Attribution"/>.</summary>
    Task<string> GetViewportAttributionAsync(HttpClient http, GeoBounds view, int zoom, CancellationToken ct);
}
