using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Epsilon.Core.Maps;
using Epsilon.Core.Targets;
using EpsilonGCS.Controls;
using EpsilonGCS.Dialogs;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

/// <summary>
/// "Map" side tab: map source and online / offline mode, Google API key, custom XYZ maps (add / edit / delete /
/// import / export), the offline tile cache (size, folder, clear, expiry) and area download.
/// Everything goes through <see cref="Gcs.Maps"/>; the map control only receives the selected source. Nothing here
/// touches targets, splashes or units.
/// </summary>
public sealed class MapCachePage : FlyoutPage
{
    public override string Title => "Map sources & offline cache";

    private readonly MapControl _map;
    private readonly ComboBox _source, _mode, _expiry;
    private readonly TextBlock _sourceNote, _keyState, _cacheInfo, _estimate, _progressText;
    private readonly PasswordBox _apiKey;
    private readonly ListBox _customList;
    private readonly NumField _parallel, _minZoom, _maxZoom;
    private readonly TextField _north, _south, _west, _east;
    private readonly ProgressBar _progress;
    private readonly Button _downloadButton, _cancelButton;
    private CancellationTokenSource _downloadCts;
    private CancellationTokenSource _statsCts;
    private bool _syncing;

    private static readonly (int Days, string Text)[] ExpiryChoices = { (0, "Never"), (7, "7 days"), (30, "30 days"), (90, "90 days") };

    public MapCachePage(MapControl map)
    {
        _map = map;
        F.LabelWidth = 95;
        var maps = Gcs.Maps;

        // ---------------- source
        F.Section("Map Source");
        _source = new ComboBox { Height = 22 };
        F.Row("Map:", _source);
        _source.SelectionChanged += (_, _) =>
        {
            if (_syncing || _source.SelectedItem is not SourceItem item) return;
            maps.Select(item.Id);
            Gcs.Settings.Save();
            UpdateSourceNote();
        };
        _mode = new ComboBox { Height = 22, ItemsSource = new[] { "Online (cache first, then network)", "Offline (cached / local tiles only)" } };
        F.Row("Mode:", _mode);
        _mode.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            maps.SetMode(_mode.SelectedIndex == 1 ? MapMode.Offline : MapMode.Online);
            Gcs.Settings.Save();
            UpdateSourceNote();
        };
        _sourceNote = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 4) };
        F.Add(_sourceNote);

        // ---------------- google
        F.Section("Google Satellite / Hybrid");
        F.Note("Uses the official Google Maps Platform Map Tiles API with your own API key (Map Tiles API enabled, billing applies). " +
               "Online only: Google's policies do not allow area downloads or offline use.");
        _apiKey = new PasswordBox { Height = 22, VerticalContentAlignment = VerticalAlignment.Center };
        F.Row("API key:", _apiKey);
        F.Buttons(("Save key", SaveKey), ("Remove key", RemoveKey), ("Test Google", () => _ = TestGoogleAsync()));
        _keyState = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        F.Add(_keyState);

        // ---------------- custom maps
        F.Section("Custom Maps");
        _customList = new ListBox { Height = 96, Margin = new Thickness(0, 2, 0, 2) };
        _customList.MouseDoubleClick += (_, _) => EditCustom();
        F.Add(_customList);
        F.Buttons(("+ Add Custom XYZ Map", AddCustom), ("Edit", EditCustom), ("Delete", DeleteCustom));
        F.Buttons(("Import...", ImportCustom), ("Export...", ExportCustom), ("Show on map", ShowSelectedCustom));

        // ---------------- cache
        F.Section("Offline Map Cache");
        _cacheInfo = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        F.Add(_cacheInfo);
        _expiry = new ComboBox { Height = 22, ItemsSource = ExpiryChoices.Select(c => c.Text).ToList() };
        F.Row("Refresh tiles:", _expiry);
        _expiry.SelectionChanged += (_, _) =>
        {
            if (_syncing || _expiry.SelectedIndex < 0) return;
            maps.SetCacheExpiryDays(ExpiryChoices[_expiry.SelectedIndex].Days);
            Gcs.Settings.Save();
        };
        _parallel = F.Number("Parallel downloads", maps.Settings.MaxConcurrentDownloads, 1, 16, "1 .. 16 simultaneous tile downloads");
        _parallel.Box.LostFocus += (_, _) => { maps.SetMaxConcurrent(_parallel.Int); Gcs.Settings.Save(); };
        F.Buttons(("Refresh", () => _ = RefreshStatsAsync()), ("Open Cache Folder", OpenCacheFolder));
        F.Buttons(("Clear Current Map Cache", ClearCurrent), ("Clear All Cache", ClearAll));

        // ---------------- download area
        F.Section("Download Area");
        _north = F.Text("North", "", "Latitude of the northern edge (decimal degrees or D M S)");
        _south = F.Text("South", "", "Latitude of the southern edge");
        _west = F.Text("West", "", "Longitude of the western edge");
        _east = F.Text("East", "", "Longitude of the eastern edge");
        F.Buttons(("Use visible map area", UseVisibleArea));
        _minZoom = F.Number("Min zoom", 10, 0, TileMath.MaxSupportedZoom);
        _maxZoom = F.Number("Max zoom", 16, 0, TileMath.MaxSupportedZoom);
        F.Buttons(("Calculate Tiles", () => Calculate(out _, out _, out _, out _)));
        var dl = F.Buttons(("Download Map", () => _ = DownloadAsync()), ("Cancel", CancelDownload));
        _downloadButton = (Button)dl.Children[0];
        _cancelButton = (Button)dl.Children[1];
        _cancelButton.IsEnabled = false;
        _estimate = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        F.Add(_estimate);
        _progress = new ProgressBar { Height = 14, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 2) };
        F.Add(_progress);
        _progressText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        F.Add(_progressText);

        maps.ProviderChanged += _ => Dispatcher.BeginInvoke(new Action(SyncFromManager));
        maps.ModeChanged += _ => Dispatcher.BeginInvoke(new Action(SyncFromManager));
        maps.CustomMapsChanged += () => Dispatcher.BeginInvoke(new Action(() => { FillSources(); FillCustomList(); }));
        FillSources();
        FillCustomList();
        SyncFromManager();
    }

    public override void OnOpened()
    {
        SyncFromManager();
        UpdateKeyState();
        _ = RefreshStatsAsync();
        if (string.IsNullOrWhiteSpace(_north.Value)) UseVisibleArea();
    }

    // ================================================================ source / mode

    private sealed record SourceItem(string Id, string Text)
    {
        public override string ToString() => Text;
    }

    private void FillSources()
    {
        _syncing = true;
        try
        {
            _source.ItemsSource = Gcs.Maps.Providers.Select(p => new SourceItem(p.Id, $"{(p.Group == "Custom" ? "Custom" : p.Group)}: {p.Name}")).ToList();
            _source.SelectedItem = _source.Items.OfType<SourceItem>().FirstOrDefault(i => i.Id == Gcs.Maps.Current?.Id);
        }
        finally { _syncing = false; }
    }

    private void SyncFromManager()
    {
        var maps = Gcs.Maps;
        _syncing = true;
        try
        {
            _source.SelectedItem = _source.Items.OfType<SourceItem>().FirstOrDefault(i => i.Id == maps.Current?.Id);
            _mode.SelectedIndex = maps.Mode == MapMode.Offline ? 1 : 0;
            int e = Array.FindIndex(ExpiryChoices, c => c.Days == maps.Settings.CacheExpiryDays);
            _expiry.SelectedIndex = e >= 0 ? e : 0;
        }
        finally { _syncing = false; }
        UpdateSourceNote();
    }

    private void UpdateSourceNote()
    {
        var p = Gcs.Maps.Current;
        if (p == null) { _sourceNote.Text = ""; return; }
        string text = $"Zoom {p.MinZoom}..{p.MaxZoom}. " + (p.UsageNote ?? (p.IsLocal ? "" : "Area download and offline use allowed."));
        if (Gcs.Maps.Mode == MapMode.Offline && !p.AllowOffline && !p.IsLocal)
            text = $"{p.Name} is online-only and is NOT shown in Offline mode. " + text;
        if (p is GoogleTileProvider g && !g.HasApiKey) text = "Enter a Google API key below. " + text;
        _sourceNote.Text = text;
        _sourceNote.Foreground = text.Contains("NOT shown") || text.StartsWith("Enter a Google") ? Brushes.Firebrick : Brushes.DimGray;
    }

    // ================================================================ google key

    private void UpdateKeyState()
    {
        bool has = !string.IsNullOrEmpty(Gcs.GoogleApiKey);
        _keyState.Text = has ? "A key is stored (encrypted for this Windows user)." : "No key stored.";
        _keyState.Foreground = has ? Brushes.DarkGreen : Brushes.DimGray;
    }

    private void SaveKey()
    {
        string key = _apiKey.Password.Trim();
        if (key.Length == 0) { MessageBox.Show("Paste the API key first.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Gcs.GoogleApiKey = key;
        _apiKey.Clear();
        AppLog.Write("[map] Google API key saved (encrypted)");   // never the key itself
        UpdateKeyState();
        UpdateSourceNote();
        if (Gcs.Maps.Current is GoogleTileProvider) _map.ReloadTiles();
    }

    private void RemoveKey()
    {
        if (string.IsNullOrEmpty(Gcs.GoogleApiKey) || !Confirm("Remove the stored Google API key?")) return;
        Gcs.GoogleApiKey = "";
        AppLog.Write("[map] Google API key removed");
        UpdateKeyState();
        UpdateSourceNote();
    }

    private async Task TestGoogleAsync()
    {
        if (Gcs.Maps.Mode == MapMode.Offline) { _keyState.Text = "Offline mode is on - Google cannot be tested."; return; }
        _keyState.Text = "Testing Google Map Tiles API ...";
        _keyState.Foreground = Brushes.DimGray;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var g = Gcs.Maps.GoogleHybrid;
            g.ResetSession();
            await g.EnsureReadyAsync(Gcs.Maps.Fetcher.Http, cts.Token);
            var r = await Gcs.Maps.Fetcher.DownloadAsync(g, 2, 2, 1, new SemaphoreSlim(1), cts.Token);
            _keyState.Text = r.Ok ? "Google OK - session created and a tile was loaded." : "Session OK, but no tile: " + r.Error;
            _keyState.Foreground = r.Ok ? Brushes.DarkGreen : Brushes.Firebrick;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            _keyState.Text = "Google failed: " + ex.Message;
            _keyState.Foreground = Brushes.Firebrick;
        }
    }

    // ================================================================ custom maps

    private void FillCustomList()
    {
        string selected = (_customList.SelectedItem as SourceItem)?.Id;
        var items = Gcs.Maps.CustomMaps.Select(d => new SourceItem(d.Id,
            $"{d.Name}   [{(d.IsLocal ? "local" : "web")}, z{d.MinZoom}-{d.MaxZoom}{(d.TileScheme == TileScheme.Tms ? ", TMS" : "")}]")).ToList();
        _customList.ItemsSource = items;
        _customList.SelectedItem = items.FirstOrDefault(i => i.Id == selected);
    }

    private string SelectedCustomId => (_customList.SelectedItem as SourceItem)?.Id;

    private void AddCustom()
    {
        var dlg = new CustomMapDialog(null, _map.CenterLat, _map.CenterLon, _map.Zoom);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        try
        {
            string id = Gcs.Maps.SaveCustomMap(dlg.Result);
            AppLog.Write($"[map] custom map added: {dlg.Result.Name}");
            Gcs.Maps.Select(id);   // show it immediately
            Gcs.Settings.Save();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EditCustom()
    {
        var def = Gcs.Maps.CustomMaps.FirstOrDefault(d => d.Id == SelectedCustomId);
        if (def == null) return;
        var dlg = new CustomMapDialog(def, _map.CenterLat, _map.CenterLon, _map.Zoom);
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        try
        {
            Gcs.Maps.SaveCustomMap(dlg.Result);
            AppLog.Write($"[map] custom map edited: {dlg.Result.Name}");
            if (Gcs.Maps.Current?.Id == def.Id) _map.ReloadTiles();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteCustom()
    {
        var def = Gcs.Maps.CustomMaps.FirstOrDefault(d => d.Id == SelectedCustomId);
        if (def == null || !Confirm($"Delete the custom map \"{def.Name}\"?\n\nIts cached tiles stay until you clear them.")) return;
        Gcs.Maps.RemoveCustomMap(def.Id);
        Gcs.Settings.Save();
        AppLog.Write($"[map] custom map deleted: {def.Name}");
    }

    private void ShowSelectedCustom()
    {
        if (SelectedCustomId is string id && Gcs.Maps.Select(id)) Gcs.Settings.Save();
    }

    private void ImportCustom()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Map definition (*.json)|*.json|All files (*.*)|*.*", Title = "Import custom map" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (imported, errors) = Gcs.Maps.ImportCustomMaps(File.ReadAllText(dlg.FileName));
            string msg = $"Imported {imported.Count} map(s).";
            if (errors.Count > 0) msg += "\n\nNot imported:\n" + string.Join("\n", errors);
            AppLog.Write($"[map] import {Path.GetFileName(dlg.FileName)}: {imported.Count} map(s), {errors.Count} problem(s)");
            MessageBox.Show(msg, "Import custom map", MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (imported.Count == 1) Gcs.Maps.Select(imported[0]);
            Gcs.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Import custom map", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExportCustom()
    {
        var all = Gcs.Maps.CustomMaps;
        if (all.Count == 0) { MessageBox.Show("There are no custom maps.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var ids = SelectedCustomId is string id ? new[] { id } : all.Select(d => d.Id).ToArray();
        string name = ids.Length == 1 ? all.First(d => d.Id == ids[0]).Name : "custom-maps";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Map definition (*.json)|*.json", Title = "Export custom map",
            FileName = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, Gcs.Maps.ExportCustomMaps(ids));
            AppLog.Write($"[map] exported {ids.Length} custom map(s) to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Export custom map", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================ cache

    private async Task RefreshStatsAsync()
    {
        _statsCts?.Cancel();
        var cts = _statsCts = new CancellationTokenSource();
        var maps = Gcs.Maps;
        var current = maps.Current;
        _cacheInfo.Text = $"Cache: {maps.Cache.Root}\nCounting tiles ...";
        try
        {
            var (all, cur) = await Task.Run(() => (maps.Cache.GetStats(null, cts.Token),
                                                    current == null || current.IsLocal ? default : maps.Cache.GetStats(current.Id, cts.Token)), cts.Token);
            _cacheInfo.Text = $"Cache: {maps.Cache.Root}\n" +
                              $"Size:  {FormatBytes(all.Bytes)}\nTiles: {all.Tiles:N0}\n" +
                              (current == null ? "" : current.IsLocal
                                  ? $"{current.Name}: local tiles (not cached)"
                                  : $"{current.Name}: {cur.Tiles:N0} tiles, {FormatBytes(cur.Bytes)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _cacheInfo.Text = "Cache: " + ex.Message; }
    }

    private void OpenCacheFolder()
    {
        try
        {
            Directory.CreateDirectory(Gcs.Maps.Cache.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", Gcs.Maps.Cache.Root) { UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write("[map] cannot open cache folder: " + ex.Message); }
    }

    private void ClearCurrent()
    {
        var p = Gcs.Maps.Current;
        if (p == null || p.IsLocal) return;
        if (!Confirm($"Delete all cached tiles of \"{p.Name}\"?\n\nAreas that are not downloaded again will not be available offline.")) return;
        _ = ClearAsync(() => Gcs.Maps.Cache.ClearProvider(p.Id), $"cache of {p.Name} cleared");
    }

    private void ClearAll()
    {
        if (!Confirm("Delete the WHOLE map cache (all map sources)?\n\nNothing will be available offline until tiles are downloaded again.")) return;
        _ = ClearAsync(() => Gcs.Maps.Cache.ClearAll(), "whole map cache cleared");
    }

    private async Task ClearAsync(Action clear, string logText)
    {
        _cacheInfo.Text = "Deleting ...";
        try
        {
            await Task.Run(clear);
            AppLog.Write("[map] " + logText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show("Could not delete everything: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _map.ReloadTiles();
        await RefreshStatsAsync();
    }

    // ================================================================ area download

    private void UseVisibleArea()
    {
        var b = _map.VisibleBounds();
        _north.Value = Fmt(b.North);
        _south.Value = Fmt(b.South);
        _west.Value = Fmt(b.West);
        _east.Value = Fmt(b.East);
        int z = _map.Zoom;
        _minZoom.Value = Math.Max(0, z - 2);
        _maxZoom.Value = Math.Min(TileMath.MaxSupportedZoom, z + 3);
    }

    /// <summary>Reads the area, shows tiles per zoom and the total. False with the reason shown when the input is invalid.</summary>
    private bool Calculate(out GeoBounds bounds, out int minZoom, out int maxZoom, out long total)
    {
        bounds = default;
        total = 0;
        minZoom = _minZoom.Int;
        maxZoom = _maxZoom.Int;
        if (!CoordinateParser.TryParseAngle(_north.Value, true, out double n, out string e1) ||
            !CoordinateParser.TryParseAngle(_south.Value, true, out double s, out string e2) ||
            !CoordinateParser.TryParseAngle(_west.Value, false, out double w, out string e3) ||
            !CoordinateParser.TryParseAngle(_east.Value, false, out double e, out string e4))
        {
            ShowEstimate("Enter North / South (latitudes) and West / East (longitudes).", true);
            return false;
        }
        bounds = new GeoBounds(n, s, w, e);
        if (!bounds.IsValid) { ShowEstimate("North must be above South and East right of West.", true); return false; }
        if (minZoom > maxZoom) { ShowEstimate("Min zoom must not be above max zoom.", true); return false; }

        var p = Gcs.Maps.Current;
        int zLo = Math.Max(minZoom, p?.MinZoom ?? 0), zHi = Math.Min(maxZoom, p?.MaxZoom ?? TileMath.MaxSupportedZoom);
        if (zLo > zHi) { ShowEstimate($"{p?.Name} has zoom {p?.MinZoom}..{p?.MaxZoom} only.", true); return false; }
        var (ranges, t) = AreaDownloader.Estimate(bounds, zLo, zHi);
        total = t;
        minZoom = zLo;
        maxZoom = zHi;
        var lines = ranges.Select(r => $"  z{r.Zoom,-2} {r.Count,12:N0}").ToList();
        string allowed = Gcs.Maps.Downloader.CheckAllowed(p);
        ShowEstimate($"{p?.Name}, zoom {zLo}-{zHi}\n" + string.Join("\n", lines) +
                     $"\nEstimated tiles: {t:N0}  (≈ {FormatBytes(t * 20_000)} at ~20 KB/tile)" +
                     (allowed != null ? "\n\n" + allowed : ""),
                     allowed != null || t > AreaDownloader.MaxTilesPerDownload);
        return true;
    }

    private async Task DownloadAsync()
    {
        if (_downloadCts != null) return;   // already running
        if (!Calculate(out var bounds, out int zLo, out int zHi, out long total)) return;
        var p = Gcs.Maps.Current;
        string why = Gcs.Maps.Downloader.CheckAllowed(p);
        if (why != null) { MessageBox.Show(why, "Download Map", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (total > AreaDownloader.MaxTilesPerDownload)
        {
            MessageBox.Show($"{total:N0} tiles is above the limit of {AreaDownloader.MaxTilesPerDownload:N0}. Reduce the area or the zoom range.",
                            "Download Map", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (total > Gcs.Maps.Settings.DownloadWarnTiles &&
            MessageBox.Show($"Estimated tiles: {total:N0}  (≈ {FormatBytes(total * 20_000)})\n\n" +
                            "This may require significant storage and network bandwidth.\n\nContinue?",
                            "Download Map", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _downloadCts = new CancellationTokenSource();
        _downloadButton.IsEnabled = false;
        _cancelButton.IsEnabled = true;
        _progress.Value = 0;
        AppLog.Write($"[map] downloading {total:N0} tiles of {p.Name}, zoom {zLo}-{zHi}, " +
                     $"N {Fmt(bounds.North)} S {Fmt(bounds.South)} W {Fmt(bounds.West)} E {Fmt(bounds.East)}");
        var progress = new Progress<AreaDownloadProgress>(ShowProgress);   // reports arrive on the UI thread
        try
        {
            var final = await Task.Run(() => Gcs.Maps.Downloader.RunAsync(p, bounds, zLo, zHi, Gcs.Maps.Settings.MaxConcurrentDownloads,
                                                                            progress, _downloadCts.Token));
            ShowProgress(final);
            AppLog.Write($"[map] download {(final.Cancelled ? "cancelled" : "finished")}: {final.Downloaded:N0} downloaded, " +
                         $"{final.AlreadyCached:N0} already cached, {final.Failed:N0} failed");
        }
        catch (InvalidOperationException ex)
        {
            _progressText.Text = ex.Message;
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            _downloadButton.IsEnabled = true;
            _cancelButton.IsEnabled = false;
            _ = RefreshStatsAsync();
        }
    }

    private void CancelDownload()
    {
        if (_downloadCts == null) return;
        _progressText.Text += "\nCancelling ...";
        _downloadCts.Cancel();
    }

    private void ShowProgress(AreaDownloadProgress pr)
    {
        _progress.Value = pr.Fraction;
        string state = pr.Cancelled ? "Cancelled - tiles downloaded so far stay in the cache."
                     : pr.Finished ? "Finished."
                     : $"Downloading map ...  zoom {pr.CurrentZoom}";
        _progressText.Text = $"{state}\n{pr.Done:N0} / {pr.Required:N0} tiles  ({pr.Fraction:P0})\n" +
                             $"Downloaded:     {pr.Downloaded:N0}\nAlready cached: {pr.AlreadyCached:N0}\nFailed:         {pr.Failed:N0}";
    }

    private void ShowEstimate(string text, bool warn)
    {
        _estimate.Text = text;
        _estimate.Foreground = warn ? Brushes.Firebrick : Brushes.Black;
    }

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{b / 1024.0:0} KB";
}
