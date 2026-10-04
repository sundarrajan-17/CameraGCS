using System.Net;
using System.Net.Http.Headers;

namespace Epsilon.Core.Maps;

public enum MapMode
{
    /// <summary>Cache first, then network; downloaded tiles are saved.</summary>
    Online,
    /// <summary>Cache / local tiles only. No network request is ever made.</summary>
    Offline,
}

public enum TileOrigin { None, Cache, StaleCache, Network, Local }

/// <summary>Result of a tile request. <see cref="Data"/> is null when the tile is not available.</summary>
public sealed record TileResult(byte[] Data, TileOrigin Origin, string Error = null)
{
    public bool Ok => Data != null;
    public static TileResult Missing(string why) => new(null, TileOrigin.None, why);
}

/// <summary>
/// Gets tiles for the map and the area downloader:
///   Local provider : read the file, never download, never copy into the cache.
///   Offline mode   : cache only (also expired tiles); providers that forbid offline use return nothing; NO network.
///   Online mode    : fresh cache tile -> else download (bounded concurrency, limited retries) -> validate -> save
///                    -> if the download fails, fall back to an expired cached copy when the provider allows it.
/// Never throws for tile failures (returns <see cref="TileResult.Missing"/>); only cancellation propagates.
/// </summary>
public sealed class TileFetcher
{
    private readonly HttpClient _http;
    private SemaphoreSlim _gate;
    private int _maxConcurrent;

    public TileFetcher(TileCache cache, HttpClient http = null, int maxConcurrent = 4)
    {
        Cache = cache;
        _http = http ?? CreateHttp();
        MaxConcurrent = maxConcurrent;
    }

    public TileCache Cache { get; }
    public HttpClient Http => _http;

    /// <summary>Retries after the first failed attempt (network errors, 5xx, 429). 404 is not retried.</summary>
    public int MaxRetries { get; set; } = 2;

    public MapMode Mode { get; set; } = MapMode.Online;

    /// <summary>Maximum simultaneous downloads for the interactive map (1 .. 16).</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            int v = Math.Clamp(value, 1, 16);
            if (v == _maxConcurrent && _gate != null) return;
            _maxConcurrent = v;
            _gate = new SemaphoreSlim(v, v);   // requests already waiting on the old gate still complete
        }
    }

    public static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        // Tile servers (e.g. OpenStreetMap) require an identifying User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EpsilonGCS/1.0 (gimbal ground control station)");
        return http;
    }

    /// <summary>Tile for the map / downloader. <paramref name="gate"/> = concurrency limiter (default: the map's).</summary>
    public async Task<TileResult> GetTileAsync(IMapTileProvider p, int z, int x, int y, CancellationToken ct,
                                               SemaphoreSlim gate = null)
    {
        if (p == null) return TileResult.Missing("no provider");
        if (z < p.MinZoom || z > p.MaxZoom) return TileResult.Missing($"zoom {z} outside {p.MinZoom}..{p.MaxZoom}");
        int n = 1 << z;
        if (x < 0 || y < 0 || x >= n || y >= n) return TileResult.Missing("tile outside the world");

        if (p.IsLocal) return ReadLocal(p, z, x, y);

        if (Mode == MapMode.Offline)
        {
            if (!p.AllowOffline) return TileResult.Missing($"{p.Name} is online-only");
            return Cache.TryRead(p.Id, z, x, y, allowExpired: true, out var off, out bool stale)
                ? new TileResult(off, stale ? TileOrigin.StaleCache : TileOrigin.Cache)
                : TileResult.Missing("not cached (offline)");
        }

        if (Cache.TryRead(p.Id, z, x, y, allowExpired: false, out var cached, out _))
            return new TileResult(cached, TileOrigin.Cache);

        var downloaded = await DownloadAsync(p, z, x, y, gate ?? _gate, ct).ConfigureAwait(false);
        if (downloaded.Ok) return downloaded;

        // Provider unavailable: an expired copy is better than nothing (where the provider allows it).
        if (p.AllowOffline && Cache.TryRead(p.Id, z, x, y, allowExpired: true, out var old, out _))
            return new TileResult(old, TileOrigin.StaleCache, downloaded.Error);
        return downloaded;
    }

    private static TileResult ReadLocal(IMapTileProvider p, int z, int x, int y)
    {
        try
        {
            string path = p.GetTileUrl(x, y, z);
            if (!File.Exists(path)) return TileResult.Missing("no local tile");
            var data = File.ReadAllBytes(path);
            return TileCache.LooksLikeImage(data) ? new TileResult(data, TileOrigin.Local) : TileResult.Missing("local tile is not an image");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return TileResult.Missing(ex.Message);
        }
    }

    /// <summary>Downloads, validates and caches one tile, with up to <see cref="MaxRetries"/> retries.</summary>
    public async Task<TileResult> DownloadAsync(IMapTileProvider p, int z, int x, int y, SemaphoreSlim gate, CancellationToken ct)
    {
        string lastError = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0) await Task.Delay(400 * attempt, ct).ConfigureAwait(false);

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await p.EnsureReadyAsync(_http, ct).ConfigureAwait(false);
                using var resp = await _http.GetAsync(p.GetTileUrl(x, y, z), HttpCompletionOption.ResponseContentRead, ct)
                                            .ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                    return TileResult.Missing("tile does not exist on the server");
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return TileResult.Missing($"server refused access ({(int)resp.StatusCode})");
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode}";
                    continue;
                }
                var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!TileCache.LooksLikeImage(data))
                {
                    lastError = "server did not return an image";
                    continue;
                }
                if (CacheAllowed(p, resp.Headers.CacheControl, out var expires))
                    Cache.Write(p.Id, z, x, y, data, expires);
                return new TileResult(data, TileOrigin.Network);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { lastError = "timeout"; }        // HttpClient timeout
            catch (HttpRequestException ex) { lastError = ex.Message; }
            catch (InvalidOperationException ex) { return TileResult.Missing(ex.Message); }   // e.g. missing API key
            catch (IOException ex) { lastError = ex.Message; }
            finally { gate.Release(); }
        }
        return TileResult.Missing(lastError ?? "download failed");
    }

    /// <summary>
    /// Whether a downloaded tile may be stored. Providers with <see cref="IMapTileProvider.RespectCacheHeaders"/> are
    /// cached only with a positive max-age and never with no-store / no-cache; the max-age becomes the hard expiry.
    /// </summary>
    public static bool CacheAllowed(IMapTileProvider p, CacheControlHeaderValue cc, out DateTime? expiresUtc)
    {
        expiresUtc = null;
        if (!p.RespectCacheHeaders) return true;
        if (cc == null || cc.NoStore || cc.NoCache || cc.MaxAge is not { } age || age <= TimeSpan.Zero) return false;
        expiresUtc = DateTime.UtcNow + age;
        return true;
    }
}
