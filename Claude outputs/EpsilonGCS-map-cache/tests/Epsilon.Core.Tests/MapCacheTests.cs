using System.Net;
using System.Net.Http.Headers;
using Epsilon.Core.Maps;
using Xunit;

namespace Epsilon.Core.Tests;

public class MapCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "epsilon-mapcache-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    /// <summary>Smallest byte sequence that passes the PNG checks (signature ... IEND).</summary>
    private static byte[] Png(byte marker = 0) =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82 };

    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3, 0xFF, 0xD9 };

    // ================================================================ tile maths

    [Fact]
    public void TileMath_MatchesStandardXyz()
    {
        Assert.Equal(23685, TileMath.LonToTileX(80.22, 15));
        Assert.Equal(15148, TileMath.LatToTileY(13.45, 15));
        Assert.Equal((511, 340), (TileMath.LonToTileX(-0.12, 10), TileMath.LatToTileY(51.5, 10)));
        Assert.Equal((1, 1), (TileMath.LonToTileX(0, 1), TileMath.LatToTileY(0, 1)));   // y grows southwards
    }

    [Fact]
    public void TileMath_ClampsToTheWebMercatorWorld()
    {
        Assert.Equal(0, TileMath.LatToTileY(85.1, 3));
        Assert.Equal(7, TileMath.LatToTileY(-85.1, 3));
        Assert.Equal(0, TileMath.LonToTileX(-180, 3));
        Assert.Equal(7, TileMath.LonToTileX(180, 3));
    }

    [Fact]
    public void TileMath_TmsConversionIsItsOwnInverse()
    {
        Assert.Equal(17619, TileMath.XyzToTmsY(15148, 15));
        Assert.Equal(15148, TileMath.XyzToTmsY(17619, 15));
        Assert.Equal(0, TileMath.XyzToTmsY(1, 1));
    }

    [Fact]
    public void TileMath_CountsAreaTiles()
    {
        var b = new GeoBounds(13.50, 13.35, 80.15, 80.30);
        var ranges = TileMath.Ranges(b, 10, 17);
        Assert.Equal(new long[] { 2, 4, 9, 25, 64, 225, 841, 3192 }, ranges.Select(r => r.Count).ToArray());
        Assert.Equal(4362, TileMath.CountTiles(b, 10, 17));
        Assert.Equal(225, TileMath.Enumerate(ranges[5]).Count());
        Assert.Throws<ArgumentException>(() => TileMath.Ranges(new GeoBounds(13.35, 13.50, 80.15, 80.30), 10, 12));   // N < S
    }

    // ================================================================ providers / custom maps

    [Fact]
    public void XyzProvider_FillsTemplateDeterministically()
    {
        var p = new XyzTileProvider("t", "T", "Custom", "https://server/{z}/{x}/{y}.png", 0, 19, "a");
        Assert.Equal("https://server/15/23451/14562.png", p.GetTileUrl(23451, 14562, 15));
        Assert.Equal("https://s/15/1/2.png", XyzTileProvider.Fill("https://s/{Z}/{X}/{Y}.png", 1, 2, 15));
        var tms = new XyzTileProvider("t2", "T2", "Custom", "https://server/{z}/{x}/{y}.png", 0, 19, "a", TileScheme.Tms);
        Assert.Equal("https://server/15/23685/17619.png", tms.GetTileUrl(23685, 15148, 15));
    }

    [Fact]
    public void XyzProvider_RecognisesLocalFolders()
    {
        Assert.True(XyzTileProvider.IsLocalTemplate(@"C:\Maps\M\{z}\{x}\{y}.png"));
        Assert.True(XyzTileProvider.IsLocalTemplate("file:///C:/Maps/M/{z}/{x}/{y}.png"));
        Assert.True(XyzTileProvider.IsLocalTemplate(@"\\server\share\{z}\{x}\{y}.png"));
        Assert.False(XyzTileProvider.IsLocalTemplate("https://x/{z}/{x}/{y}.png"));
        var local = new XyzTileProvider("l", "L", "Custom", @"C:\Maps\M\{z}\{x}\{y}.png", 0, 19, "a");
        Assert.True(local.IsLocal);
        Assert.False(local.AllowBulkDownload);
        Assert.Equal(@"C:\Maps\M\5\1\2.png", local.GetTileUrl(1, 2, 5));
    }

    [Fact]
    public void CustomDefinition_Validates()
    {
        CustomMapDefinition D(string url, int min = 5, int max = 19, string name = "M") =>
            new() { Name = name, UrlTemplate = url, MinZoom = min, MaxZoom = max };
        Assert.Null(D("https://example.com/tiles/{z}/{x}/{y}.png").Validate());
        Assert.Null(D(@"C:\Maps\{z}\{x}\{y}.jpg").Validate());
        Assert.Contains("{y}", D("https://example.com/{z}/{x}.png").Validate());
        Assert.Contains("http", D("ftp://example.com/{z}/{x}/{y}.png").Validate());
        Assert.Contains("Min zoom", D("https://e/{z}/{x}/{y}", 15, 10).Validate());
        Assert.Contains("name", D("https://e/{z}/{x}/{y}", name: " ").Validate());
    }

    [Fact]
    public void CustomMaps_ImportExportAndUniqueIds()
    {
        var mgr = new MapProviderManager(new MapSettings(), Path.Combine(_dir, "cache"), Path.Combine(_dir, "custom-maps.json"), () => "");
        string json = """
            { "name": "My Survey Map", "urlTemplate": "https://example.com/tiles/{z}/{x}/{y}.png",
              "minZoom": 10, "maxZoom": 18, "attribution": "My Map Provider" }
            """;
        var (a, errA) = mgr.ImportCustomMaps(json);
        var (b, errB) = mgr.ImportCustomMaps(json);   // same map again -> second, independent id
        Assert.Empty(errA);
        Assert.Empty(errB);
        Assert.Equal("custom-my-survey-map", a[0]);
        Assert.NotEqual(a[0], b[0]);

        var (_, bad) = mgr.ImportCustomMaps("""[{ "name": "x", "urlTemplate": "https://e/{z}/{x}.png" }]""");
        Assert.Single(bad);

        var p = mgr.Find(a[0]);
        Assert.Equal("My Survey Map", p.Name);
        Assert.Equal((10, 18), (p.MinZoom, p.MaxZoom));
        Assert.True(p.AllowBulkDownload);

        var exported = CustomMapDefinition.ParseJson(mgr.ExportCustomMaps(new[] { a[0] }));
        Assert.Equal("https://example.com/tiles/{z}/{x}/{y}.png", exported.Single().UrlTemplate);

        // persisted: a new manager reads the same file
        var again = new MapProviderManager(new MapSettings(), Path.Combine(_dir, "cache"), Path.Combine(_dir, "custom-maps.json"), () => "");
        Assert.Equal(2, again.CustomMaps.Count);
    }

    [Fact]
    public void Manager_SelectsAndFallsBack()
    {
        var settings = new MapSettings { ProviderId = "does-not-exist" };
        var mgr = new MapProviderManager(settings, Path.Combine(_dir, "cache"), null, () => "");
        Assert.Equal(MapProviderManager.DefaultProviderId, mgr.Current.Id);
        IMapTileProvider changed = null;
        mgr.ProviderChanged += p => changed = p;
        Assert.True(mgr.Select("google-hybrid"));
        Assert.Equal("google-hybrid", settings.ProviderId);
        Assert.Same(mgr.GoogleHybrid, changed);
        Assert.False(mgr.Select("nope"));
        Assert.False(mgr.GoogleHybrid.AllowBulkDownload);
        Assert.False(mgr.GoogleHybrid.AllowOffline);
        Assert.False(mgr.Find("osm-street").AllowBulkDownload);   // OSM tile policy
        Assert.Equal(5, mgr.Providers.Select(p => p.Id).Distinct().Count());
    }

    // ================================================================ cache

    [Fact]
    public void Cache_StoresPerProviderWithoutCollisions()
    {
        var c = new TileCache(_dir);
        Assert.True(c.Write("google-satellite", 15, 23451, 14562, Png(1)));
        Assert.True(c.Write("google-hybrid", 15, 23451, 14562, Png(2)));
        Assert.True(c.TryRead("google-satellite", 15, 23451, 14562, false, out var a, out _));
        Assert.True(c.TryRead("google-hybrid", 15, 23451, 14562, false, out var b, out _));
        Assert.Equal(1, a[8]);
        Assert.Equal(2, b[8]);
        Assert.EndsWith(Path.Combine("google-satellite", "15", "23451", "14562.png"), c.FindFile("google-satellite", 15, 23451, 14562));
        Assert.True(c.Write("x", 1, 0, 0, Jpeg()));
        Assert.EndsWith(".jpg", c.FindFile("x", 1, 0, 0));
    }

    [Fact]
    public void Cache_RejectsAndRemovesCorruptTiles()
    {
        var c = new TileCache(_dir);
        Assert.False(c.Write("p", 1, 0, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));   // not an image
        var truncated = Png().Take(15).ToArray();
        Assert.False(c.Write("p", 1, 0, 0, truncated));

        Assert.True(c.Write("p", 1, 0, 0, Png()));
        File.WriteAllBytes(c.FindFile("p", 1, 0, 0), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0 });   // damaged on disk
        Assert.False(c.TryRead("p", 1, 0, 0, false, out _, out _));
        Assert.Null(c.FindFile("p", 1, 0, 0));   // deleted
    }

    [Fact]
    public void Cache_ExpiryByAgeAndByHeader()
    {
        var c = new TileCache(_dir) { MaxAgeDays = 7 };
        c.Write("p", 2, 1, 1, Png());
        File.SetLastWriteTimeUtc(c.FindFile("p", 2, 1, 1), DateTime.UtcNow.AddDays(-8));
        Assert.False(c.TryRead("p", 2, 1, 1, allowExpired: false, out _, out _));
        Assert.True(c.TryRead("p", 2, 1, 1, allowExpired: true, out _, out bool expired));
        Assert.True(expired);

        c.Write("g", 2, 1, 1, Png(), DateTime.UtcNow.AddSeconds(-1));      // header said max-age, now past
        Assert.False(c.Contains("g", 2, 1, 1));
        c.Write("g", 2, 1, 1, Png(), DateTime.UtcNow.AddHours(1));
        Assert.True(c.Contains("g", 2, 1, 1));
    }

    [Fact]
    public void Cache_StatsAndClear()
    {
        var c = new TileCache(_dir);
        c.Write("a", 1, 0, 0, Png());
        c.Write("a", 1, 1, 0, Png());
        c.Write("b", 1, 0, 0, Jpeg());
        Assert.Equal(3, c.GetStats().Tiles);
        Assert.Equal(2, c.GetStats("a").Tiles);
        Assert.Equal(Png().Length * 2L, c.GetStats("a").Bytes);
        c.ClearProvider("a");
        Assert.Equal(1, c.GetStats().Tiles);
        Assert.Throws<ArgumentException>(() => c.ClearProvider(".."));
        c.ClearAll();
        Assert.Equal(0, c.GetStats().Tiles);
    }

    [Fact]
    public void Cache_MigratesTilesOfEarlierVersions()
    {
        string legacy = Path.Combine(_dir, "tiles");
        Directory.CreateDirectory(Path.Combine(legacy, "StreetOpenStreetMap", "5", "10"));
        File.WriteAllBytes(Path.Combine(legacy, "StreetOpenStreetMap", "5", "10", "12.png"), Png());
        var c = new TileCache(Path.Combine(_dir, "MapCache"));
        int moved = c.MigrateLegacy(legacy, new Dictionary<string, string> { ["StreetOpenStreetMap"] = "osm-street" });
        Assert.Equal(1, moved);
        Assert.True(c.Contains("osm-street", 5, 10, 12));
        Assert.False(Directory.Exists(Path.Combine(legacy, "StreetOpenStreetMap")));
    }

    // ================================================================ fetcher (fake network)

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Calls);
            return Task.FromResult(Respond(request, n));
        }
    }

    private static HttpResponseMessage Ok(byte[] data, CacheControlHeaderValue cc = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        if (cc != null) r.Headers.CacheControl = cc;
        return r;
    }

    private static XyzTileProvider Web(string id = "custom-web") =>
        new(id, "Web", "Custom", "https://tiles.example/{z}/{x}/{y}.png", 0, 18, "a");

    [Fact]
    public async Task Online_DownloadsOnceThenServesFromCache()
    {
        var h = new FakeHandler { Respond = (_, _) => Ok(Png()) };
        var f = new TileFetcher(new TileCache(_dir), new HttpClient(h));
        var r1 = await f.GetTileAsync(Web(), 10, 5, 6, CancellationToken.None);
        var r2 = await f.GetTileAsync(Web(), 10, 5, 6, CancellationToken.None);
        Assert.Equal(TileOrigin.Network, r1.Origin);
        Assert.Equal(TileOrigin.Cache, r2.Origin);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Offline_NeverTouchesTheNetwork()
    {
        var h = new FakeHandler { Respond = (_, _) => Ok(Png()) };
        var cache = new TileCache(_dir);
        cache.Write("custom-web", 10, 1, 1, Png());
        var f = new TileFetcher(cache, new HttpClient(h)) { Mode = MapMode.Offline };
        Assert.True((await f.GetTileAsync(Web(), 10, 1, 1, CancellationToken.None)).Ok);
        Assert.False((await f.GetTileAsync(Web(), 10, 2, 2, CancellationToken.None)).Ok);   // not cached -> missing
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Retries_AreLimitedAnd404IsNotRetried()
    {
        var h = new FakeHandler { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        var f = new TileFetcher(new TileCache(_dir), new HttpClient(h)) { MaxRetries = 2 };
        var r = await f.GetTileAsync(Web(), 10, 1, 1, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(3, h.Calls);   // first try + 2 retries

        var h404 = new FakeHandler { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var f404 = new TileFetcher(new TileCache(_dir), new HttpClient(h404));
        Assert.False((await f404.GetTileAsync(Web(), 10, 1, 1, CancellationToken.None)).Ok);
        Assert.Equal(1, h404.Calls);
    }

    [Fact]
    public async Task NonImageResponse_IsNotCached()
    {
        var h = new FakeHandler { Respond = (_, _) => Ok(System.Text.Encoding.UTF8.GetBytes("<html>error page</html>")) };
        var cache = new TileCache(_dir);
        var f = new TileFetcher(cache, new HttpClient(h)) { MaxRetries = 0 };
        Assert.False((await f.GetTileAsync(Web(), 10, 1, 1, CancellationToken.None)).Ok);
        Assert.Null(cache.FindFile("custom-web", 10, 1, 1));
    }

    [Fact]
    public async Task StaleCopy_IsUsedWhenTheServerIsDown()
    {
        var cache = new TileCache(_dir) { MaxAgeDays = 1 };
        cache.Write("custom-web", 10, 1, 1, Png());
        File.SetLastWriteTimeUtc(cache.FindFile("custom-web", 10, 1, 1), DateTime.UtcNow.AddDays(-3));
        var h = new FakeHandler { Respond = (_, _) => throw new HttpRequestException("no network") };
        var f = new TileFetcher(cache, new HttpClient(h)) { MaxRetries = 0 };
        var r = await f.GetTileAsync(Web(), 10, 1, 1, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(TileOrigin.StaleCache, r.Origin);
    }

    [Fact]
    public void HeaderRespectingProviders_CacheOnlyWithMaxAge()
    {
        var g = new GoogleTileProvider(GoogleMapKind.Satellite, () => "k", () => "en-US", () => "US");
        Assert.False(TileFetcher.CacheAllowed(g, null, out _));
        Assert.False(TileFetcher.CacheAllowed(g, new CacheControlHeaderValue { NoStore = true, MaxAge = TimeSpan.FromHours(1) }, out _));
        Assert.True(TileFetcher.CacheAllowed(g, new CacheControlHeaderValue { Private = true, MaxAge = TimeSpan.FromHours(1) }, out var exp));
        Assert.InRange(exp.Value, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.True(TileFetcher.CacheAllowed(Web(), null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task Google_WithoutKey_GivesAReadableMissingTile()
    {
        var h = new FakeHandler { Respond = (_, _) => Ok(Png()) };
        var f = new TileFetcher(new TileCache(_dir), new HttpClient(h));
        var g = new GoogleTileProvider(GoogleMapKind.Hybrid, () => "", () => "en-US", () => "US");
        var r = await f.GetTileAsync(g, 5, 1, 1, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("API key", r.Error);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task LocalProvider_ReadsFilesAndNeverDownloads()
    {
        string root = Path.Combine(_dir, "local");
        Directory.CreateDirectory(Path.Combine(root, "3", "2"));
        File.WriteAllBytes(Path.Combine(root, "3", "2", "1.png"), Png());
        var p = new XyzTileProvider("custom-local", "Local", "Custom", Path.Combine(root, "{z}", "{x}", "{y}.png"), 0, 10, "a");
        var h = new FakeHandler { Respond = (_, _) => Ok(Png()) };
        var cache = new TileCache(Path.Combine(_dir, "cache"));
        var f = new TileFetcher(cache, new HttpClient(h)) { Mode = MapMode.Offline };
        var r = await f.GetTileAsync(p, 3, 2, 1, CancellationToken.None);
        Assert.Equal(TileOrigin.Local, r.Origin);
        Assert.False((await f.GetTileAsync(p, 3, 2, 2, CancellationToken.None)).Ok);
        Assert.Equal(0, h.Calls);
        Assert.Equal(0, cache.GetStats().Tiles);
    }

    // ================================================================ area download

    [Fact]
    public async Task AreaDownload_CountsDownloadedCachedAndFailed()
    {
        var b = new GeoBounds(13.50, 13.35, 80.15, 80.30);   // z10..12 = 2 + 4 + 9 tiles
        var h = new FakeHandler
        {
            Respond = (req, _) => req.RequestUri.AbsolutePath.StartsWith("/12/") && req.RequestUri.AbsolutePath.EndsWith("/1893.png")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Ok(Png()),
        };
        var cache = new TileCache(_dir);
        var f = new TileFetcher(cache, new HttpClient(h)) { MaxRetries = 1 };
        var p = Web();
        var r10 = TileMath.Range(b, 10);
        cache.Write(p.Id, 10, r10.MinX, r10.MinY, Png());                    // one tile already cached

        var dl = new AreaDownloader(f);
        var final = await dl.RunAsync(p, b, 10, 12, 4, null, CancellationToken.None);

        int failedTiles = TileMath.Enumerate(TileMath.Range(b, 12)).Count(t => t.Y == 1893);
        Assert.True(final.Finished);
        Assert.Equal(15, final.Required);
        Assert.Equal(15, final.Done);
        Assert.Equal(1, final.AlreadyCached);
        Assert.Equal(failedTiles, final.Failed);
        Assert.Equal(15 - 1 - failedTiles, final.Downloaded);
        Assert.Equal(15 - failedTiles, cache.GetStats(p.Id).Tiles);
    }

    [Fact]
    public async Task AreaDownload_CanBeCancelledAndKeepsTiles()
    {
        var b = new GeoBounds(13.50, 13.35, 80.15, 80.30);
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler
        {
            Respond = (_, n) =>
            {
                if (n == 20) cts.Cancel();
                return Ok(Png());
            },
        };
        var cache = new TileCache(_dir);
        var dl = new AreaDownloader(new TileFetcher(cache, new HttpClient(h)));
        var final = await dl.RunAsync(Web(), b, 10, 17, 2, null, cts.Token);
        Assert.True(final.Cancelled);
        Assert.True(final.Done < final.Required);
        Assert.True(cache.GetStats().Tiles >= 19);   // what was saved stays
    }

    [Fact]
    public void AreaDownload_RefusesProvidersThatForbidIt()
    {
        var mgr = new MapProviderManager(new MapSettings(), Path.Combine(_dir, "cache"), null, () => "k");
        Assert.NotNull(mgr.Downloader.CheckAllowed(mgr.GoogleSatellite));
        Assert.NotNull(mgr.Downloader.CheckAllowed(mgr.Find("osm-street")));
        Assert.Null(mgr.Downloader.CheckAllowed(Web()));
        mgr.SetMode(MapMode.Offline);
        Assert.NotNull(mgr.Downloader.CheckAllowed(Web()));
    }
}
