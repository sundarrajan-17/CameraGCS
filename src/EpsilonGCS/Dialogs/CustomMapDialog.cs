using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Epsilon.Core.Maps;
using EpsilonGCS.Flyouts;
using EpsilonGCS.Services;

namespace EpsilonGCS.Dialogs;

/// <summary>
/// Add / edit a custom XYZ (or TMS) tile map: name, URL template (web or local folder), zoom range, attribution.
/// "Test Connection" loads one real tile at the current map centre. <see cref="Result"/> holds the definition after Save.
/// </summary>
public sealed class CustomMapDialog : ToolWindow
{
    private readonly TextField _name, _url, _attribution;
    private readonly NumField _minZoom, _maxZoom;
    private readonly ChoiceField _scheme;
    private readonly TextBlock _testResult;
    private readonly string _existingId;
    private readonly double _testLat, _testLon;
    private readonly int _testZoom;

    public CustomMapDefinition Result { get; private set; }

    public CustomMapDialog(CustomMapDefinition existing, double centerLat, double centerLon, int zoom)
        : base(existing == null ? "Add Custom XYZ Map" : "Edit Custom Map", 560, 470)
    {
        _existingId = existing?.Id;
        _testLat = centerLat;
        _testLon = centerLon;
        _testZoom = zoom;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        var root = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        var f = new FormBuilder(root) { LabelWidth = 110 };
        f.Section("Map");
        _name = f.Text("Name", existing?.Name ?? "", "Shown in the map source list");
        _url = f.Text("XYZ URL template", existing?.UrlTemplate ?? "https://", "Must contain {z}, {x} and {y}");
        f.Buttons(("Browse local tile folder...", BrowseFolder));
        f.Note("Web: https://server/tiles/{z}/{x}/{y}.png   Local: file:///C:/Maps/MyMap/{z}/{x}/{y}.png or C:\\Maps\\MyMap\\{z}\\{x}\\{y}.png. " +
               "{z} {x} {y} are replaced with the tile numbers. Do not put passwords in the URL.");
        _scheme = f.Choice("Tile scheme", existing?.TileScheme == TileScheme.Tms ? 1 : 0, "XYZ (standard, y = 0 at the top)", "TMS (y = 0 at the bottom)");
        _minZoom = f.Number("Min zoom", existing?.MinZoom ?? 0, 0, TileMath.MaxSupportedZoom);
        _maxZoom = f.Number("Max zoom", existing?.MaxZoom ?? 19, 0, TileMath.MaxSupportedZoom);
        _attribution = f.Text("Attribution", existing?.Attribution ?? "", "Copyright text shown on the map");

        f.Section("Check");
        f.Buttons(("Test Connection", () => _ = TestAsync()));
        _testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 6) };
        f.Add(_testResult);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(MakeButton("Save", Save));
        buttons.Children.Add(MakeButton("Cancel", () => { DialogResult = false; }));
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private CustomMapDefinition Build() => new()
    {
        Id = _existingId,
        Name = _name.Value,
        UrlTemplate = _url.Value,
        MinZoom = _minZoom.Int,
        MaxZoom = _maxZoom.Int,
        Attribution = _attribution.Value,
        Scheme = _scheme.Value == 1 ? "tms" : "xyz",
    };

    private void Save()
    {
        var def = Build();
        string error = def.Validate();
        if (error != null)
        {
            MessageBox.Show(this, error, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = def;
        DialogResult = true;
    }

    /// <summary>Picks a folder laid out as z/x/y.ext and builds the template (and zoom range) from it.</summary>
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder containing the tile pyramid ({z}\\{x}\\{y}.png)" };
        if (dlg.ShowDialog(this) != true) return;
        string folder = dlg.FolderName;
        var zooms = Directory.EnumerateDirectories(folder)
            .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out int z) ? z : -1)
            .Where(z => z is >= 0 and <= TileMath.MaxSupportedZoom).OrderBy(z => z).ToList();
        string ext = ".png";
        foreach (var z in zooms)
        {
            var file = Directory.EnumerateFiles(Path.Combine(folder, z.ToString(CultureInfo.InvariantCulture)), "*.*", SearchOption.AllDirectories)
                                .FirstOrDefault(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp");
            if (file != null) { ext = Path.GetExtension(file).ToLowerInvariant(); break; }
        }
        _url.Value = Path.Combine(folder, "{z}", "{x}", "{y}" + ext);
        if (zooms.Count > 0)
        {
            _minZoom.Value = zooms[0];
            _maxZoom.Value = zooms[^1];
        }
        if (string.IsNullOrWhiteSpace(_name.Value)) _name.Value = Path.GetFileName(folder);
        _testResult.Text = zooms.Count > 0
            ? $"Found zoom levels {zooms[0]}..{zooms[^1]}, tiles *{ext}."
            : "No numeric zoom folders found - check that the folder contains z\\x\\y files.";
    }

    /// <summary>Loads the tile under the map centre (within the zoom range) and checks that it is an image.</summary>
    private async Task TestAsync()
    {
        var def = Build();
        def.Id = "test";
        string error = def.Validate();
        if (error != null) { ShowTest(error, false); return; }
        var p = XyzTileProvider.FromDefinition(def);
        int z = Math.Clamp(_testZoom, def.MinZoom, def.MaxZoom);
        int x = TileMath.LonToTileX(_testLon, z), y = TileMath.LatToTileY(_testLat, z);
        string address = p.GetTileUrl(x, y, z);
        ShowTest($"Loading tile z={z} x={x} y={y} ...", null);
        try
        {
            byte[] data;
            if (p.IsLocal)
            {
                if (!File.Exists(address)) { ShowTest($"No tile file at the map centre:\n{address}", false); return; }
                data = await File.ReadAllBytesAsync(address);
            }
            else
            {
                if (Gcs.Maps.Mode == MapMode.Offline) { ShowTest("Offline mode is on - switch to Online to test a web server.", false); return; }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var resp = await Gcs.Maps.Fetcher.Http.GetAsync(address, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    ShowTest($"Server answered {(int)resp.StatusCode} {resp.ReasonPhrase} for z={z} x={x} y={y} " +
                             "(404 is normal outside the map's coverage - move the map over the area and test again).", false);
                    return;
                }
                data = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            string ext = TileCache.ImageExtension(data);
            ShowTest(TileCache.LooksLikeImage(data)
                    ? $"OK - {ext?.TrimStart('.').ToUpperInvariant()} tile, {data.Length:N0} bytes (z={z} x={x} y={y})."
                    : "The response is not a PNG / JPEG / WebP image - check the URL template.",
                TileCache.LooksLikeImage(data));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowTest("Failed: " + ex.Message, false);
        }
    }

    private void ShowTest(string text, bool? ok)
    {
        _testResult.Text = text;
        _testResult.Foreground = ok == true ? Brushes.DarkGreen : ok == false ? Brushes.Firebrick : Brushes.DimGray;
    }
}
