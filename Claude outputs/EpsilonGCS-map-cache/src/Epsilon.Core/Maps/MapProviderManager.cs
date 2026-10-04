namespace Epsilon.Core.Maps;

/// <summary>Persisted map settings (part of the application's settings.json). Contains no credentials.</summary>
public sealed class MapSettings
{
    public string ProviderId { get; set; } = MapProviderManager.DefaultProviderId;
    public bool Offline { get; set; }
    public string GoogleLanguage { get; set; } = "en-US";
    public string GoogleRegion { get; set; } = "US";
    public int MaxConcurrentDownloads { get; set; } = 4;
    /// <summary>0 = never expire; otherwise tiles older than this are refreshed when online.</summary>
    public int CacheExpiryDays { get; set; }
    /// <summary>Ask for confirmation above this many tiles.</summary>
    public long DownloadWarnTiles { get; set; } = 20_000;
}

/// <summary>
/// The one place that knows all map sources. The map control, the Map page and the downloader use it to find and
/// select providers; the selected provider and the online/offline mode are announced through events. Independent of
/// targets / splashes / units (the map only stores tiles).
/// </summary>
public sealed class MapProviderManager
{
    public const string DefaultProviderId = "osm-street";

    private const string OsmNote =
        "OpenStreetMap's tile usage policy does not allow bulk downloading; tiles you view are cached and work offline.";

    private readonly MapSettings _settings;
    private readonly string _customMapsFile;
    private readonly List<IMapTileProvider> _builtIn;
    private List<CustomMapDefinition> _customDefs;
    private List<IMapTileProvider> _custom = new();

    public MapProviderManager(MapSettings settings, string cacheRoot, string customMapsFile, Func<string> googleApiKey)
    {
        _settings = settings ?? new MapSettings();
        _customMapsFile = customMapsFile;
        Cache = new TileCache(cacheRoot) { MaxAgeDays = Math.Max(0, _settings.CacheExpiryDays) };
        Fetcher = new TileFetcher(Cache, maxConcurrent: _settings.MaxConcurrentDownloads)
        {
            Mode = _settings.Offline ? MapMode.Offline : MapMode.Online,
        };
        Downloader = new AreaDownloader(Fetcher);

        GoogleSatellite = new GoogleTileProvider(GoogleMapKind.Satellite, googleApiKey, () => _settings.GoogleLanguage, () => _settings.GoogleRegion);
        GoogleHybrid = new GoogleTileProvider(GoogleMapKind.Hybrid, googleApiKey, () => _settings.GoogleLanguage, () => _settings.GoogleRegion);
        _builtIn = new List<IMapTileProvider>
        {
            new XyzTileProvider(DefaultProviderId, "Street (OpenStreetMap)", "Standard", "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
                                0, 19, "© OpenStreetMap contributors", allowBulkDownload: false, usageNote: OsmNote),
            new XyzTileProvider("opentopomap", "Topographic (OpenTopoMap)", "Standard", "https://tile.opentopomap.org/{z}/{x}/{y}.png",
                                0, 17, "© OpenStreetMap contributors, SRTM | © OpenTopoMap (CC-BY-SA)", allowBulkDownload: false,
                                usageNote: "OpenTopoMap does not allow bulk downloading; tiles you view are cached and work offline."),
            new XyzTileProvider("esri-world-imagery", "Satellite (Esri World Imagery)", "Standard",
                                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
                                0, 19, "Imagery © Esri, Maxar, Earthstar Geographics", allowBulkDownload: false,
                                usageNote: "Esri basemap terms restrict bulk export; tiles you view are cached and work offline."),
            GoogleSatellite,
            GoogleHybrid,
        };

        _customDefs = string.IsNullOrEmpty(customMapsFile) ? new() : CustomMapStore.Load(customMapsFile);
        RebuildCustom();
        Current = Find(_settings.ProviderId) ?? Find(DefaultProviderId);
    }

    public TileCache Cache { get; }
    public TileFetcher Fetcher { get; }
    public AreaDownloader Downloader { get; }
    public GoogleTileProvider GoogleSatellite { get; }
    public GoogleTileProvider GoogleHybrid { get; }
    public MapSettings Settings => _settings;

    public IMapTileProvider Current { get; private set; }
    public MapMode Mode => Fetcher.Mode;

    /// <summary>Raised after the selected provider changed (or the custom map it uses was edited).</summary>
    public event Action<IMapTileProvider> ProviderChanged;

    /// <summary>Raised after the online / offline mode changed.</summary>
    public event Action<MapMode> ModeChanged;

    /// <summary>Raised after custom maps were added, edited, removed or imported.</summary>
    public event Action CustomMapsChanged;

    public IReadOnlyList<IMapTileProvider> Providers => _builtIn.Concat(_custom).ToList();
    public IReadOnlyList<CustomMapDefinition> CustomMaps => _customDefs.Select(d => d.Clone()).ToList();

    public IMapTileProvider Find(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Selects a provider; markers and overlays are not affected (only tiles change).</summary>
    public bool Select(string id)
    {
        var p = Find(id);
        if (p == null) return false;
        Current = p;
        _settings.ProviderId = p.Id;
        ProviderChanged?.Invoke(p);
        return true;
    }

    public void SetMode(MapMode mode)
    {
        if (Fetcher.Mode == mode) return;
        Fetcher.Mode = mode;
        _settings.Offline = mode == MapMode.Offline;
        ModeChanged?.Invoke(mode);
    }

    public void SetMaxConcurrent(int n)
    {
        _settings.MaxConcurrentDownloads = Math.Clamp(n, 1, 16);
        Fetcher.MaxConcurrent = _settings.MaxConcurrentDownloads;
    }

    public void SetCacheExpiryDays(int days)
    {
        _settings.CacheExpiryDays = Math.Max(0, days);
        Cache.MaxAgeDays = _settings.CacheExpiryDays;
    }

    /// <summary>Google language / region / key changed: new session on next use.</summary>
    public void ResetGoogleSessions()
    {
        GoogleSatellite.ResetSession();
        GoogleHybrid.ResetSession();
    }

    // ------------------------------------------------------------------ custom maps

    /// <summary>Adds a new custom map or replaces the one with the same id. Returns the stored id. Throws on invalid input.</summary>
    public string SaveCustomMap(CustomMapDefinition def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));
        def = def.Clone();
        def.Scheme = def.TileScheme == TileScheme.Tms ? "tms" : "xyz";
        string error = def.Validate();
        if (error != null) throw new ArgumentException(error);
        int existing = string.IsNullOrEmpty(def.Id) ? -1 : _customDefs.FindIndex(d => d.Id == def.Id);
        if (existing >= 0) _customDefs[existing] = def;
        else
        {
            def.EnsureId(_builtIn.Select(b => b.Id).Concat(_customDefs.Select(d => d.Id)));
            _customDefs.Add(def);
        }
        PersistCustom();
        if (Current?.Id == def.Id) Select(def.Id);   // refresh the map with the edited definition
        return def.Id;
    }

    public bool RemoveCustomMap(string id)
    {
        int removed = _customDefs.RemoveAll(d => d.Id == id);
        if (removed == 0) return false;
        PersistCustom();
        if (Current?.Id == id) Select(DefaultProviderId);
        return true;
    }

    /// <summary>Imports one or several definitions from JSON. Returns (imported ids, problems).</summary>
    public (List<string> Imported, List<string> Errors) ImportCustomMaps(string json)
    {
        var imported = new List<string>();
        var errors = new List<string>();
        List<CustomMapDefinition> defs;
        try { defs = CustomMapDefinition.ParseJson(json); }
        catch (System.Text.Json.JsonException ex) { errors.Add("Not valid JSON: " + ex.Message); return (imported, errors); }
        foreach (var d in defs)
        {
            try
            {
                d.Id = null;   // imported maps always get a new local id (never overwrite an existing map)
                imported.Add(SaveCustomMap(d));
            }
            catch (ArgumentException ex) { errors.Add($"{d.Name ?? "(no name)"}: {ex.Message}"); }
        }
        return (imported, errors);
    }

    public string ExportCustomMaps(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids ?? Array.Empty<string>());
        var list = _customDefs.Where(d => set.Count == 0 || set.Contains(d.Id)).ToList();
        return list.Count == 1 ? list[0].ToJson() : CustomMapDefinition.ToJson(list);
    }

    private void PersistCustom()
    {
        if (!string.IsNullOrEmpty(_customMapsFile)) CustomMapStore.Save(_customMapsFile, _customDefs);
        RebuildCustom();
        CustomMapsChanged?.Invoke();
    }

    private void RebuildCustom() => _custom = _customDefs.Select(d => (IMapTileProvider)XyzTileProvider.FromDefinition(d)).ToList();

    // ------------------------------------------------------------------ migration from earlier versions

    /// <summary>Provider id for a map layer name saved by earlier versions.</summary>
    public static string ProviderIdFromLegacyLayerName(string name) => name switch
    {
        "Topographic (OpenTopoMap)" => "opentopomap",
        "Satellite (Esri World Imagery)" => "esri-world-imagery",
        "Custom" => "custom-legacy",
        _ => DefaultProviderId,
    };

    /// <summary>
    /// Turns the TileUrl setting of earlier versions into a custom map (once) and returns the mapping
    /// "old cache folder name -> provider id" for <see cref="TileCache.MigrateLegacy"/>. Cheap; call at start-up,
    /// then move the files in the background with <c>Cache.MigrateLegacy(legacyRoot, folders)</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PrepareLegacyMigration(string legacyCustomTileUrl)
    {
        var folders = new Dictionary<string, string>
        {
            ["StreetOpenStreetMap"] = DefaultProviderId,
            ["TopographicOpenTopoMap"] = "opentopomap",
            ["SatelliteEsriWorldImagery"] = "esri-world-imagery",
        };
        const string osm = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        if (!string.IsNullOrWhiteSpace(legacyCustomTileUrl) && legacyCustomTileUrl.Trim() != osm)
        {
            if (_customDefs.All(d => d.Id != "custom-legacy"))
            {
                var def = new CustomMapDefinition
                {
                    Id = "custom-legacy", Name = "Custom (from earlier settings)", UrlTemplate = legacyCustomTileUrl.Trim(),
                    MinZoom = 0, MaxZoom = 19, Attribution = "Custom tile server",
                };
                if (def.Validate() == null)
                {
                    _customDefs.Add(def);
                    PersistCustom();
                }
            }
            if (_customDefs.Any(d => d.Id == "custom-legacy")) folders["Custom"] = "custom-legacy";
        }
        return folders;
    }
}
