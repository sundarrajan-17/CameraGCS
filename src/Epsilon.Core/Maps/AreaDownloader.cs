namespace Epsilon.Core.Maps;

/// <summary>Progress of an area download.</summary>
public sealed record AreaDownloadProgress(
    long Required, long Done, long Downloaded, long AlreadyCached, long Failed, int CurrentZoom, bool Finished, bool Cancelled)
{
    public double Fraction => Required == 0 ? 1 : (double)Done / Required;
}

/// <summary>
/// Pre-downloads (caches) all tiles of a rectangle for a zoom range, for later offline use.
/// Uses a bounded number of parallel downloads (not one thread per tile), limited retries per tile (in
/// <see cref="TileFetcher"/>), and stops cleanly on cancellation; tiles already saved stay in the cache.
/// One failed tile never stops the download.
/// </summary>
public sealed class AreaDownloader
{
    /// <summary>Hard upper limit for one request (protects disk and the tile server).</summary>
    public const long MaxTilesPerDownload = 2_000_000;

    private readonly TileFetcher _fetcher;

    public AreaDownloader(TileFetcher fetcher) => _fetcher = fetcher;

    /// <summary>Tiles per zoom level and total, without downloading anything.</summary>
    public static (IReadOnlyList<TileRange> Ranges, long Total) Estimate(GeoBounds bounds, int minZoom, int maxZoom)
    {
        var ranges = TileMath.Ranges(bounds, minZoom, maxZoom);
        return (ranges, ranges.Sum(r => r.Count));
    }

    /// <summary>Why the provider / mode cannot be used for an area download, or null if it can.</summary>
    public string CheckAllowed(IMapTileProvider p)
    {
        if (p == null) return "No map source selected.";
        if (p.IsLocal) return $"{p.Name} is a local tile folder - it is already available offline.";
        if (!p.AllowBulkDownload) return p.UsageNote ?? $"{p.Name} does not allow downloading areas in advance.";
        if (_fetcher.Mode == MapMode.Offline) return "Switch the map to Online mode to download.";
        return null;
    }

    public async Task<AreaDownloadProgress> RunAsync(IMapTileProvider p, GeoBounds bounds, int minZoom, int maxZoom,
                                                     int parallel, IProgress<AreaDownloadProgress> progress, CancellationToken ct)
    {
        string why = CheckAllowed(p);
        if (why != null) throw new InvalidOperationException(why);
        minZoom = Math.Max(minZoom, p.MinZoom);
        maxZoom = Math.Min(maxZoom, p.MaxZoom);
        if (minZoom > maxZoom) throw new InvalidOperationException($"{p.Name} has zoom {p.MinZoom}..{p.MaxZoom} only.");
        var (ranges, total) = Estimate(bounds, minZoom, maxZoom);
        if (total > MaxTilesPerDownload)
            throw new InvalidOperationException($"{total:N0} tiles is more than the limit of {MaxTilesPerDownload:N0} per download. Reduce the area or zoom range.");

        long done = 0, downloaded = 0, cached = 0, failed = 0;
        int zoomNow = minZoom;
        var gate = new SemaphoreSlim(Math.Clamp(parallel, 1, 16));
        var lastReport = DateTime.MinValue;
        object reportLock = new();

        AreaDownloadProgress Snapshot(bool finished, bool cancelled) =>
            new(total, Interlocked.Read(ref done), Interlocked.Read(ref downloaded), Interlocked.Read(ref cached),
                Interlocked.Read(ref failed), zoomNow, finished, cancelled);

        void Report(bool force = false)
        {
            lock (reportLock)
            {
                var now = DateTime.UtcNow;
                if (!force && (now - lastReport).TotalMilliseconds < 150) return;   // throttle UI updates
                lastReport = now;
            }
            progress?.Report(Snapshot(false, false));
        }

        try
        {
            foreach (var range in ranges)
            {
                zoomNow = range.Zoom;
                Report(force: true);
                var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallel, 1, 16), CancellationToken = ct };
                await Parallel.ForEachAsync(TileMath.Enumerate(range), options, async (t, token) =>
                {
                    if (_fetcher.Cache.Contains(p.Id, range.Zoom, t.X, t.Y))
                    {
                        Interlocked.Increment(ref cached);
                    }
                    else
                    {
                        var r = await _fetcher.DownloadAsync(p, range.Zoom, t.X, t.Y, gate, token).ConfigureAwait(false);
                        if (r.Ok) Interlocked.Increment(ref downloaded);
                        else Interlocked.Increment(ref failed);
                    }
                    Interlocked.Increment(ref done);
                    Report();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var c = Snapshot(true, true);
            progress?.Report(c);
            return c;
        }

        var final = Snapshot(true, false);
        progress?.Report(final);
        return final;
    }
}
