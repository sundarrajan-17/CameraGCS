using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Epsilon.Core.Maps;

public enum GoogleMapKind { Satellite, Hybrid }

/// <summary>
/// Google satellite / hybrid tiles through the official Google Maps Platform Map Tiles API (2D tiles).
///   1. POST https://tile.googleapis.com/v1/createSession?key=KEY
///        Satellite: { "mapType": "satellite", "language": ..., "region": ... }
///        Hybrid:    { "mapType": "satellite", "layerTypes": ["layerRoadmap"], "overlay": false, ... }
///   2. GET  https://tile.googleapis.com/v1/2dtiles/{z}/{x}/{y}?session=TOKEN&amp;key=KEY
///   3. GET  https://tile.googleapis.com/tile/v1/viewport?session=..&amp;key=..&amp;zoom=..&amp;north=..&amp;south=..&amp;east=..&amp;west=..
///      -> "copyright", which must be displayed together with "Google Maps".
/// Requires the operator's own API key with the Map Tiles API enabled (billing applies). The key is never logged.
/// Map Tiles API policies forbid pre-fetching, storing beyond the Cache-Control headers, and offline use, so this
/// provider is online-only: <see cref="AllowBulkDownload"/> and <see cref="AllowOffline"/> are false and tiles are
/// cached only as the response headers allow.
/// </summary>
public sealed class GoogleTileProvider : IMapTileProvider
{
    private const string Host = "https://tile.googleapis.com";

    private readonly GoogleMapKind _kind;
    private readonly Func<string> _apiKey;
    private readonly Func<string> _language;
    private readonly Func<string> _region;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile string _session;
    private DateTime _sessionExpiresUtc;
    private string _sessionForKey;

    public GoogleTileProvider(GoogleMapKind kind, Func<string> apiKey, Func<string> language, Func<string> region)
    {
        _kind = kind;
        _apiKey = apiKey;
        _language = language;
        _region = region;
    }

    public string Id => _kind == GoogleMapKind.Satellite ? "google-satellite" : "google-hybrid";
    public string Name => _kind == GoogleMapKind.Satellite ? "Google Satellite" : "Google Hybrid";
    public string Group => "Google";
    public int MinZoom => 0;
    public int MaxZoom => 21;
    public string Attribution => "Google Maps";
    public bool IsLocal => false;
    public bool AllowBulkDownload => false;
    public bool AllowOffline => false;
    public bool RespectCacheHeaders => true;
    public string UsageNote =>
        "Google Map Tiles API: online only. Google's policies do not allow pre-downloading areas or offline use; " +
        "tiles are cached only as long as Google's Cache-Control headers allow. Needs your own API key.";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey?.Invoke());

    /// <summary>Drops the session (call after the API key, language or region changed).</summary>
    public void ResetSession() => _session = null;

    public async Task EnsureReadyAsync(HttpClient http, CancellationToken ct)
    {
        string key = _apiKey?.Invoke()?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Google maps need a Google Maps Platform API key (Map tab > Google API key).");
        if (_session != null && _sessionForKey == key && DateTime.UtcNow < _sessionExpiresUtc) return;

        await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session != null && _sessionForKey == key && DateTime.UtcNow < _sessionExpiresUtc) return;

            var body = new Dictionary<string, object>
            {
                ["mapType"] = "satellite",
                ["language"] = string.IsNullOrWhiteSpace(_language?.Invoke()) ? "en-US" : _language().Trim(),
                ["region"] = string.IsNullOrWhiteSpace(_region?.Invoke()) ? "US" : _region().Trim(),
            };
            if (_kind == GoogleMapKind.Hybrid)
            {
                body["layerTypes"] = new[] { "layerRoadmap" };
                body["overlay"] = false;
            }

            using var resp = await http.PostAsJsonAsync($"{Host}/v1/createSession?key={Uri.EscapeDataString(key)}", body, ct)
                                       .ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google Map Tiles API refused the session ({(int)resp.StatusCode}): {ErrorMessage(text)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(session)) throw new InvalidOperationException("Google Map Tiles API returned no session token.");

            long expiry = 0;
            if (root.TryGetProperty("expiry", out var e))
            {
                if (e.ValueKind == JsonValueKind.Number) e.TryGetInt64(out expiry);
                else if (e.ValueKind == JsonValueKind.String) long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out expiry);
            }
            var expiresUtc = expiry > 0 ? DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime : DateTime.UtcNow.AddDays(13);
            _sessionExpiresUtc = expiresUtc.AddHours(-1);   // renew a little early
            _sessionForKey = key;
            _session = session;
        }
        finally { _sessionLock.Release(); }
    }

    public string GetTileUrl(int x, int y, int zoom)
    {
        string session = _session ?? throw new InvalidOperationException("Google session not ready.");
        string key = _apiKey?.Invoke()?.Trim() ?? "";
        return $"{Host}/v1/2dtiles/{zoom}/{x}/{y}?session={Uri.EscapeDataString(session)}&key={Uri.EscapeDataString(key)}";
    }

    public async Task<string> GetViewportAttributionAsync(HttpClient http, GeoBounds view, int zoom, CancellationToken ct)
    {
        await EnsureReadyAsync(http, ct).ConfigureAwait(false);
        string key = _apiKey().Trim();
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string url = $"{Host}/tile/v1/viewport?session={Uri.EscapeDataString(_session)}&key={Uri.EscapeDataString(key)}" +
                     $"&zoom={zoom}&north={F(view.North)}&south={F(view.South)}&east={F(view.East)}&west={F(view.West)}";
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return Attribution;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        string copyright = doc.RootElement.TryGetProperty("copyright", out var c) ? c.GetString() : null;
        return string.IsNullOrWhiteSpace(copyright) ? Attribution : $"{Attribution} · {copyright}";
    }

    private static string ErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch (JsonException) { }
        return json.Length > 200 ? json[..200] : json;
    }
}
