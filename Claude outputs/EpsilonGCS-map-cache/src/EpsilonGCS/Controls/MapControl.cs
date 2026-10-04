using System.Collections.Concurrent;
using System.IO;
using Epsilon.Core.Maps;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;

namespace EpsilonGCS.Controls;

public enum MapTool { Pan, Ruler, Poi }

public enum MapMarkerKind { Target, Splash, Unit }

/// <summary>
/// A clickable marker. <see cref="Kind"/> + <see cref="Id"/> identify the model object it shows
/// (TargetInfo.Id / SplashInfo.Id / UnitInfo.Id); the map never stores the model itself.
/// </summary>
public sealed record MapMarker(MapMarkerKind Kind, int Id, double Lat, double Lon, string Label, bool Dimmed = false);

/// <summary>
/// Lightweight Web-Mercator tile map. Tiles come from the selected <see cref="IMapTileProvider"/> through the shared
/// <see cref="TileFetcher"/> (local tile cache, online / offline mode, bounded downloads); changing the source only
/// swaps the tile layer - markers and overlays are kept. Shows the gimbal/aircraft position, the geo target, the
/// line of sight, targets / splashes / units, a scale bar and an optional ruler.
/// </summary>
public sealed class MapControl : Grid
{
    private const int TileSize = 256;
    private const int MinZoom = 2;

    private static readonly ConcurrentDictionary<string, BitmapSource> MemoryCache = new();

    private readonly Canvas _tiles = new() { ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)) };
    private readonly Canvas _overlay = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Dictionary<string, Image> _visible = new();
    private readonly HashSet<string> _pending = new();

    private double _centerLat = 20, _centerLon = 0;
    private int _zoom = 3;
    private Point? _dragStart;
    private double _dragWorldX, _dragWorldY;
    private bool _dragged;

    private (double Lat, double Lon)? _aircraft;
    private double _aircraftHeading;
    private (double Lat, double Lon)? _target;
    private (double Lat, double Lon)[] _footprint;
    private List<MapMarker> _markers = new();
    private (MapMarkerKind Kind, int Id)? _selectedMarker;
    private const double MarkerHitRadius = 14;
    private (double Lat, double Lon)? _home;
    private (double Lat, double Lon)? _rulerA, _rulerB;
    private Point _mouse;
    private readonly List<(double Lat, double Lon)> _trail = new();
    private readonly List<(double Lat, double Lon)> _pois = new();
    private IMapTileProvider _provider;
    private TileFetcher _fetcher;
    private string _dynamicAttribution;
    private readonly System.Windows.Threading.DispatcherTimer _attributionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _attributionCts;

    /// <summary>The tile source currently shown.</summary>
    public IMapTileProvider Provider => _provider;

    private int MaxZoom => _provider?.MaxZoom ?? 19;

    public MapTool Tool { get; private set; } = MapTool.Pan;

    /// <summary>Points the user placed with the POI tool.</summary>
    public IReadOnlyList<(double Lat, double Lon)> Pois => _pois;

    public int MaxTrailPoints { get; set; } = 3000;

    public double CenterLat => _centerLat;
    public double CenterLon => _centerLon;
    public int Zoom => _zoom;

    /// <summary>Raised with the geographic position under the mouse.</summary>
    public event Action<double, double> CursorMoved;

    /// <summary>Raised on right click with the clicked position.</summary>
    public event Action<double, double, Point> MapRightClicked;

    /// <summary>Raised when the user pans the map (used to cancel "follow gimbal").</summary>
    public event Action UserPanned;

    public MapControl()
    {
        ClipToBounds = true;
        Children.Add(_tiles);
        Children.Add(_overlay);
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
        _attributionTimer.Tick += (_, _) => { _attributionTimer.Stop(); _ = UpdateDynamicAttributionAsync(); };
    }

    // ------------------------------------------------------------------ public API

    public void SetView(double lat, double lon, int? zoom = null)
    {
        _centerLat = Math.Clamp(lat, -85, 85);
        _centerLon = NormalizeLon(lon);
        if (zoom.HasValue) _zoom = Math.Clamp(zoom.Value, MinZoom, MaxZoom);
        Refresh();
    }

    /// <summary>
    /// Centres a point in the part of the map that is not covered by a side panel
    /// (<paramref name="coveredLeft"/> pixels on the left, <paramref name="coveredRight"/> on the right).
    /// </summary>
    public void CenterOn(double lat, double lon, double coveredLeft = 0, double coveredRight = 0)
    {
        if (ActualWidth <= 0) { SetView(lat, lon); return; }
        double visibleCenterX = coveredLeft + Math.Max(0, ActualWidth - coveredLeft - coveredRight) / 2;
        double dx = visibleCenterX - ActualWidth / 2;
        _centerLon = NormalizeLon(XToLon(LonToX(NormalizeLon(lon), _zoom) - dx, _zoom));
        _centerLat = Math.Clamp(lat, -85, 85);
        Refresh();
    }

    public void ZoomBy(int delta)
    {
        _zoom = Math.Clamp(_zoom + delta, MinZoom, MaxZoom);
        Refresh();
    }

    /// <summary>
    /// Shows tiles from another source. Only the tile layer is replaced: markers, trail, footprint, ruler and all
    /// other overlays stay as they are.
    /// </summary>
    public void SetSource(IMapTileProvider provider, TileFetcher fetcher)
    {
        if (provider == null || fetcher == null) return;
        bool same = ReferenceEquals(provider, _provider) && ReferenceEquals(fetcher, _fetcher);
        _provider = provider;
        _fetcher = fetcher;
        _zoom = Math.Min(_zoom, provider.MaxZoom);
        _dynamicAttribution = null;
        if (!same)
        {
            // Same id can mean an edited custom map (other URL) or tiles shown before a mode change: drop them.
            PurgeMemoryTiles(provider.Id);
            ClearTileImages();
        }
        Refresh();
    }

    private static void PurgeMemoryTiles(string providerId)
    {
        string prefix = providerId + "/";
        foreach (var key in MemoryCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            MemoryCache.TryRemove(key, out _);
    }

    /// <summary>
    /// Reloads all visible tiles of the current source (after the cache was cleared, the mode changed or tiles
    /// were downloaded). Markers are not touched.
    /// </summary>
    public void ReloadTiles()
    {
        if (_provider != null) PurgeMemoryTiles(_provider.Id);
        ClearTileImages();
        Refresh();
    }

    private void ClearTileImages()
    {
        foreach (var img in _visible.Values) _tiles.Children.Remove(img);
        _visible.Clear();
    }

    /// <summary>The geographic area currently visible.</summary>
    public Epsilon.Core.Maps.GeoBounds VisibleBounds()
    {
        var nw = ScreenToGeo(new Point(0, 0));
        var se = ScreenToGeo(new Point(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        double west = nw.Lon, east = se.Lon;
        if (east <= west) { west = -180; east = 180; }   // view crosses the antimeridian or shows the whole world
        return new Epsilon.Core.Maps.GeoBounds(Math.Min(85.05, nw.Lat), Math.Max(-85.05, se.Lat), west, east);
    }

    private async Task UpdateDynamicAttributionAsync()
    {
        var p = _provider;
        if (p == null || _fetcher == null || _fetcher.Mode == MapMode.Offline || p.IsLocal || ActualWidth <= 0) return;
        _attributionCts?.Cancel();
        var cts = _attributionCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            string text = await p.GetViewportAttributionAsync(_fetcher.Http, VisibleBounds(), _zoom, cts.Token);
            if (ReferenceEquals(p, _provider) && !cts.IsCancellationRequested && text != _dynamicAttribution)
            {
                _dynamicAttribution = text;
                DrawOverlay();
            }
        }
        catch (Exception) { /* attribution is best effort; the static attribution stays */ }
    }

    private string AttributionText()
    {
        if (_provider == null) return "";
        string text = _dynamicAttribution ?? _provider.Attribution;
        if (_fetcher?.Mode == MapMode.Offline)
            text = (_provider.AllowOffline || _provider.IsLocal ? "OFFLINE · " : "OFFLINE (online-only map, not shown) · ") + text;
        return text;
    }

    public void SetTool(MapTool tool)
    {
        Tool = tool;
        if (tool != MapTool.Ruler) _rulerA = _rulerB = null;
        Cursor = tool == MapTool.Pan ? null : Cursors.Cross;
        DrawOverlay();
    }

    public void ClearTrail()
    {
        _trail.Clear();
        DrawOverlay();
    }

    public void ClearPois()
    {
        _pois.Clear();
        DrawOverlay();
    }

    /// <summary>Home position: the first valid gimbal position of the session unless set explicitly.</summary>
    public (double Lat, double Lon)? Home => _home;

    public void SetHome(double lat, double lon)
    {
        _home = (lat, lon);
        DrawOverlay();
    }

    public void SetAircraft(double lat, double lon, double headingDeg)
    {
        _home ??= (lat, lon);
        if (_trail.Count == 0 || DistanceMeters(_trail[^1].Lat, _trail[^1].Lon, lat, lon) > 3)
        {
            _trail.Add((lat, lon));
            if (_trail.Count > MaxTrailPoints) _trail.RemoveRange(0, _trail.Count - MaxTrailPoints);
        }
        _aircraft = (lat, lon);
        _aircraftHeading = headingDeg;
        DrawOverlay();
    }

    public void SetTarget(double? lat, double? lon)
    {
        _target = lat.HasValue && lon.HasValue ? (lat.Value, lon.Value) : null;
        DrawOverlay();
    }

    /// <summary>Raised when the operator clicks a target / splash marker (Pan tool). Not raised for map clicks.</summary>
    public event Action<MapMarker> MarkerClicked;

    /// <summary>Replaces all target / splash markers (call after the model changed).</summary>
    public void SetMarkers(IEnumerable<MapMarker> markers)
    {
        _markers = markers?.ToList() ?? new List<MapMarker>();
        DrawOverlay();
    }

    /// <summary>Highlights one marker (null clears the highlight).</summary>
    public void SetSelectedMarker(MapMarkerKind? kind, int id = 0)
    {
        _selectedMarker = kind.HasValue ? (kind.Value, id) : null;
        DrawOverlay();
    }

    /// <summary>The marker under a screen point, nearest first; null if none is within the hit radius.</summary>
    public MapMarker HitTestMarker(Point p)
    {
        MapMarker best = null;
        double bestDist = MarkerHitRadius;
        foreach (var m in _markers)
        {
            var q = GeoToScreen(m.Lat, m.Lon);
            double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
            // Targets win over splashes at the same spot.
            if (d < bestDist || (best != null && Math.Abs(d - bestDist) < 0.5 && m.Kind == MapMarkerKind.Target))
            {
                best = m;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>Camera view polygon (ground footprint of the field of view); null hides it.</summary>
    public void SetFootprint((double Lat, double Lon)[] corners)
    {
        _footprint = corners != null && corners.Length >= 3 ? corners : null;
        DrawOverlay();
    }

    /// <summary>Show or hide the camera view polygon.</summary>
    public bool ShowFootprint { get; set; } = true;

    public void ClearRuler()
    {
        _rulerA = _rulerB = null;
        DrawOverlay();
    }

    public (double Lat, double Lon) ScreenToGeo(Point p)
    {
        double worldX = LonToX(_centerLon, _zoom) - ActualWidth / 2 + p.X;
        double worldY = LatToY(_centerLat, _zoom) - ActualHeight / 2 + p.Y;
        return (YToLat(worldY, _zoom), NormalizeLon(XToLon(worldX, _zoom)));
    }

    public Point GeoToScreen(double lat, double lon)
    {
        double cx = LonToX(_centerLon, _zoom), cy = LatToY(_centerLat, _zoom);
        double x = LonToX(lon, _zoom) - cx;
        double world = TileSize * Math.Pow(2, _zoom);
        if (x > world / 2) x -= world;
        if (x < -world / 2) x += world;
        return new Point(ActualWidth / 2 + x, ActualHeight / 2 + LatToY(lat, _zoom) - cy);
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        _dragWorldX = LonToX(_centerLon, _zoom);
        _dragWorldY = LatToY(_centerLat, _zoom);
        _dragged = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        var geo = ScreenToGeo(_mouse);
        CursorMoved?.Invoke(geo.Lat, geo.Lon);

        if (_dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            var d = _mouse - _dragStart.Value;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 3) _dragged = true;
            if (_dragged)
            {
                _centerLon = NormalizeLon(XToLon(_dragWorldX - d.X, _zoom));
                _centerLat = Math.Clamp(YToLat(_dragWorldY - d.Y, _zoom), -85, 85);
                Refresh();
                UserPanned?.Invoke();
            }
        }
        else if (Tool == MapTool.Ruler && _rulerA.HasValue && !_rulerB.HasValue)
        {
            DrawOverlay();
        }
        else if (Tool == MapTool.Pan && e.LeftButton != MouseButtonState.Pressed)
        {
            Cursor = _markers.Count > 0 && HitTestMarker(_mouse) != null ? Cursors.Hand : null;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        bool wasClick = _dragStart.HasValue && !_dragged;
        _dragStart = null;
        if (!wasClick) return;
        if (Tool == MapTool.Pan)
        {
            // A click on a marker selects that target / splash; it never creates anything.
            var hit = HitTestMarker(e.GetPosition(this));
            if (hit != null)
            {
                MarkerClicked?.Invoke(hit);
                e.Handled = true;
                return;
            }
        }
        var geo = ScreenToGeo(e.GetPosition(this));
        if (Tool == MapTool.Ruler)
        {
            if (!_rulerA.HasValue || _rulerB.HasValue) { _rulerA = geo; _rulerB = null; }
            else _rulerB = geo;
            DrawOverlay();
        }
        else if (Tool == MapTool.Poi)
        {
            _pois.Add(geo);
            DrawOverlay();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var p = e.GetPosition(this);
        var geo = ScreenToGeo(p);
        MapRightClicked?.Invoke(geo.Lat, geo.Lon, p);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        var before = ScreenToGeo(p);
        int newZoom = Math.Clamp(_zoom + (e.Delta > 0 ? 1 : -1), MinZoom, MaxZoom);
        if (newZoom == _zoom) return;
        _zoom = newZoom;
        // keep the point under the cursor fixed
        double wx = LonToX(before.Lon, _zoom) - (p.X - ActualWidth / 2);
        double wy = LatToY(before.Lat, _zoom) - (p.Y - ActualHeight / 2);
        _centerLon = NormalizeLon(XToLon(wx, _zoom));
        _centerLat = Math.Clamp(YToLat(wy, _zoom), -85, 85);
        Refresh();
        UserPanned?.Invoke();
    }

    // ------------------------------------------------------------------ rendering

    public void Refresh()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_provider is GoogleTileProvider)   // only providers with a viewport attribution service
        {
            _attributionTimer.Stop();
            _attributionTimer.Start();   // ask for the visible area's attribution once the view settles
        }
        int n = 1 << _zoom;
        double left = LonToX(_centerLon, _zoom) - ActualWidth / 2;
        double top = LatToY(_centerLat, _zoom) - ActualHeight / 2;
        int tx0 = (int)Math.Floor(left / TileSize), tx1 = (int)Math.Floor((left + ActualWidth) / TileSize);
        int ty0 = Math.Max(0, (int)Math.Floor(top / TileSize)), ty1 = Math.Min(n - 1, (int)Math.Floor((top + ActualHeight) / TileSize));

        var needed = new HashSet<string>();
        for (int tx = tx0; tx <= tx1; tx++)
        {
            for (int ty = ty0; ty <= ty1; ty++)
            {
                int wrappedX = ((tx % n) + n) % n;
                string slot = $"{_zoom}/{tx}/{ty}";
                needed.Add(slot);
                if (!_visible.TryGetValue(slot, out var img))
                {
                    img = new Image { Width = TileSize, Height = TileSize, Stretch = Stretch.Fill };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    _visible[slot] = img;
                    _tiles.Children.Add(img);
                    if (_provider != null && _fetcher != null)
                    {
                        string key = $"{_provider.Id}/{_zoom}/{wrappedX}/{ty}";
                        if (MemoryCache.TryGetValue(key, out var bmp)) img.Source = bmp;
                        else _ = LoadTileAsync(key, _provider, _zoom, wrappedX, ty, slot);
                    }
                }
                Canvas.SetLeft(img, Math.Round(tx * TileSize - left));
                Canvas.SetTop(img, Math.Round(ty * TileSize - top));
            }
        }

        foreach (var slot in _visible.Keys.Where(k => !needed.Contains(k)).ToList())
        {
            _tiles.Children.Remove(_visible[slot]);
            _visible.Remove(slot);
        }

        DrawOverlay();
    }

    /// <summary>
    /// Loads one tile without blocking the UI thread: fetch (cache / network per mode) and decode on the thread pool,
    /// then set the image on the UI thread. A tile that cannot be decoded is removed from the cache and, when online,
    /// downloaded once more. Failures leave the tile blank (normal missing-tile look); nothing is thrown.
    /// </summary>
    private async Task LoadTileAsync(string key, IMapTileProvider provider, int z, int x, int y, string slot)
    {
        lock (_pending) { if (!_pending.Add(slot)) return; }
        try
        {
            var fetcher = _fetcher;
            var result = await fetcher.GetTileAsync(provider, z, x, y, CancellationToken.None);
            if (!result.Ok) return;
            var bmp = await Task.Run(() => Decode(result.Data));
            if (bmp == null && result.Origin is TileOrigin.Cache or TileOrigin.StaleCache)
            {
                fetcher.Cache.Invalidate(provider.Id, z, x, y);          // corrupt cached tile
                if (fetcher.Mode == MapMode.Online)
                {
                    result = await fetcher.GetTileAsync(provider, z, x, y, CancellationToken.None);
                    if (result.Ok) bmp = await Task.Run(() => Decode(result.Data));
                }
            }
            if (bmp == null) return;
            if (MemoryCache.Count > 600) MemoryCache.Clear();
            MemoryCache[key] = bmp;
            if (ReferenceEquals(provider, _provider) && _visible.TryGetValue(slot, out var img)) img.Source = bmp;
        }
        catch (Exception) { /* never let a tile take the map down */ }
        finally
        {
            lock (_pending) _pending.Remove(slot);
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }   // NotSupportedException / FileFormatException / ...
    }

    private void DrawOverlay()
    {
        _overlay.Children.Clear();
        if (ActualWidth <= 0) return;

        if (_trail.Count > 1)
        {
            var pts = new PointCollection(_trail.Count);
            foreach (var t in _trail) pts.Add(GeoToScreen(t.Lat, t.Lon));
            _overlay.Children.Add(new Shapes.Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x10, 0x2A, 0x5C)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        if (ShowFootprint && _footprint != null)
        {
            var pts = new PointCollection(_footprint.Length);
            foreach (var c in _footprint) pts.Add(GeoToScreen(c.Lat, c.Lon));
            _overlay.Children.Add(new Shapes.Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC8, 0x00)),
                Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0xE0, 0x8A, 0x00)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
            // Lines from the camera to the two far corners make the view direction obvious.
            if (_aircraft.HasValue)
            {
                var a = GeoToScreen(_aircraft.Value.Lat, _aircraft.Value.Lon);
                foreach (int k in new[] { 0, 1 })
                    _overlay.Children.Add(new Shapes.Line
                    {
                        X1 = a.X, Y1 = a.Y, X2 = pts[k].X, Y2 = pts[k].Y,
                        Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0xE0, 0x8A, 0x00)),
                        StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
                    });
            }
        }

        for (int i = 0; i < _pois.Count; i++)
        {
            var p = GeoToScreen(_pois[i].Lat, _pois[i].Lon);
            var pin = new Shapes.Path
            {
                Data = PinGeometry,
                Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x24)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x80, 0x40, 0x00)),
                StrokeThickness = 1,
            };
            Canvas.SetLeft(pin, p.X - 8);
            Canvas.SetTop(pin, p.Y - 22);
            _overlay.Children.Add(pin);
            AddLabel("P" + (i + 1), p.X + 8, p.Y - 22);
        }

        if (_home.HasValue)
        {
            var h = GeoToScreen(_home.Value.Lat, _home.Value.Lon);
            var ring = new Shapes.Ellipse
            {
                Width = 18, Height = 18,
                Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x3A)),
                Stroke = Brushes.White, StrokeThickness = 1.5,
            };
            Canvas.SetLeft(ring, h.X - 9);
            Canvas.SetTop(ring, h.Y - 9);
            _overlay.Children.Add(ring);
            var letter = new TextBlock { Text = "H", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11 };
            Canvas.SetLeft(letter, h.X - 4);
            Canvas.SetTop(letter, h.Y - 8);
            _overlay.Children.Add(letter);
        }

        if (_aircraft.HasValue && _target.HasValue)
        {
            var a = GeoToScreen(_aircraft.Value.Lat, _aircraft.Value.Lon);
            var t = GeoToScreen(_target.Value.Lat, _target.Value.Lon);
            _overlay.Children.Add(new Shapes.Line
            {
                X1 = a.X, Y1 = a.Y, X2 = t.X, Y2 = t.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(0xD0, 0xE0, 0x20, 0x20)), StrokeThickness = 2,
            });
        }

        DrawMarkers();

        if (_target.HasValue)
        {
            var t = GeoToScreen(_target.Value.Lat, _target.Value.Lon);
            DrawCrosshair(t, Brushes.Red);
        }

        if (_aircraft.HasValue)
        {
            var a = GeoToScreen(_aircraft.Value.Lat, _aircraft.Value.Lon);
            var arrow = new Shapes.Polygon
            {
                Points = new PointCollection { new Point(0, -16), new Point(11, 12), new Point(0, 5), new Point(-11, 12) },
                Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x5C)),
                Stroke = Brushes.White,
                StrokeThickness = 1,
                RenderTransform = new RotateTransform(_aircraftHeading),
            };
            Canvas.SetLeft(arrow, a.X);
            Canvas.SetTop(arrow, a.Y);
            _overlay.Children.Add(arrow);
        }

        if (_rulerA.HasValue)
        {
            var p1 = GeoToScreen(_rulerA.Value.Lat, _rulerA.Value.Lon);
            var geoB = _rulerB ?? ScreenToGeo(_mouse);
            var p2 = _rulerB.HasValue ? GeoToScreen(_rulerB.Value.Lat, _rulerB.Value.Lon) : _mouse;
            _overlay.Children.Add(new Shapes.Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = Brushes.DarkOrange, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 2 },
            });
            double dist = DistanceMeters(_rulerA.Value.Lat, _rulerA.Value.Lon, geoB.Lat, geoB.Lon);
            double brg = BearingDeg(_rulerA.Value.Lat, _rulerA.Value.Lon, geoB.Lat, geoB.Lon);
            AddLabel(dist >= 1000 ? $"{dist / 1000:0.00} km  {brg:0}°" : $"{dist:0} m  {brg:0}°",
                (p1.X + p2.X) / 2 + 6, (p1.Y + p2.Y) / 2 - 18);
        }

        DrawScaleBar();
        var attribution = new TextBlock
        {
            Text = AttributionText(),
            FontSize = 10,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(3, 0, 3, 0),
        };
        attribution.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(attribution, ActualWidth - attribution.DesiredSize.Width - 2);
        Canvas.SetTop(attribution, ActualHeight - attribution.DesiredSize.Height - 2);
        _overlay.Children.Add(attribution);
    }

    private void DrawCrosshair(Point c, Brush brush)
    {
        const double r = 10, gap = 3;
        void Seg(double x1, double y1, double x2, double y2) =>
            _overlay.Children.Add(new Shapes.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = 2 });
        Seg(c.X - r, c.Y, c.X - gap, c.Y);
        Seg(c.X + gap, c.Y, c.X + r, c.Y);
        Seg(c.X, c.Y - r, c.X, c.Y - gap);
        Seg(c.X, c.Y + gap, c.X, c.Y + r);
    }

    private void DrawScaleBar()
    {
        double metersPerPixel = 156543.03392 * Math.Cos(_centerLat * Math.PI / 180) / Math.Pow(2, _zoom);
        double y = ActualHeight - 30;
        // nautical miles (top), kilometres (bottom) - as in Epsilon Control
        double nm = NiceValue(metersPerPixel * 70 / 1852.0);
        double km = NiceValue(metersPerPixel * 70 / 1000.0);
        DrawBar(nm * 1852 / metersPerPixel, y - 4, nm >= 1 ? $"{nm:0.##}nm" : $"{nm * 1852:0}m", above: true);
        DrawBar(km * 1000 / metersPerPixel, y, km >= 1 ? $"{km:0.##}km" : $"{km * 1000:0}m", above: false);
    }

    private static double NiceValue(double target)
    {
        double best = 0.001;
        for (double mag = 0.001; mag < 1e5; mag *= 10)
            foreach (var n in new[] { 1.0, 2.0, 5.0 })
                if (n * mag <= target) best = n * mag;
        return best;
    }

    private void DrawBar(double px, double y, string text, bool above)
    {
        double x = 10;
        double tick = above ? -5 : 5;
        _overlay.Children.Add(new Shapes.Polyline
        {
            Points = new PointCollection { new Point(x, y + tick), new Point(x, y), new Point(x + px, y), new Point(x + px, y + tick) },
            Stroke = Brushes.Black, StrokeThickness = 2,
        });
        AddLabel(text, x, above ? y - 22 : y + 4);
    }

    private static readonly Geometry PinGeometry =
        Geometry.Parse("M8,22 C8,22 1,13 1,8 A7,7 0 1 1 15,8 C15,13 8,22 8,22 Z M8,5 A3,3 0 1 0 8.01,5 Z");


    private static readonly Brush SplashFill = new SolidColorBrush(Color.FromArgb(0x70, 0xF0, 0x80, 0x80));
    private static readonly Brush SplashStroke = new SolidColorBrush(Color.FromRgb(0xD0, 0x40, 0x50));
    private static readonly Brush SplashCross = new SolidColorBrush(Color.FromRgb(0x70, 0x10, 0x40));
    private static readonly Brush TargetRing = new SolidColorBrush(Color.FromRgb(0xC0, 0x10, 0x10));
    private static readonly Brush TargetFill = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0x30, 0x30));
    private static readonly Brush UnitFill = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0xC0, 0xFF));
    private static readonly Brush UnitStroke = new SolidColorBrush(Color.FromRgb(0x10, 0x3C, 0x9C));
    private static readonly Brush SelectedHalo = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xD8, 0x00));

    /// <summary>Targets: red bullseye. Splashes: pink diamond with a dark cross (as in Epsilon Control). Units: blue frame.</summary>
    private void DrawMarkers()
    {
        // Units, then splashes, then targets on top.
        foreach (var m in _markers.OrderBy(m => m.Kind switch { MapMarkerKind.Unit => 0, MapMarkerKind.Splash => 1, _ => 2 }))
        {
            var p = GeoToScreen(m.Lat, m.Lon);
            if (p.X < -50 || p.Y < -50 || p.X > ActualWidth + 50 || p.Y > ActualHeight + 50) continue;
            bool selected = _selectedMarker.HasValue && _selectedMarker.Value.Kind == m.Kind && _selectedMarker.Value.Id == m.Id;
            double opacity = m.Dimmed ? 0.45 : 1.0;

            if (selected)
            {
                var halo = new Shapes.Ellipse { Width = 34, Height = 34, Stroke = SelectedHalo, StrokeThickness = 4 };
                Canvas.SetLeft(halo, p.X - 17);
                Canvas.SetTop(halo, p.Y - 17);
                _overlay.Children.Add(halo);
            }

            if (m.Kind == MapMarkerKind.Unit)
            {
                // Blue frame with a dot (friendly unit), clearly different from targets (red) and splashes (pink).
                var frame = new Shapes.Rectangle
                {
                    Width = 24, Height = 16, Fill = UnitFill, Stroke = UnitStroke, StrokeThickness = 2, Opacity = opacity,
                    RadiusX = 1.5, RadiusY = 1.5,
                };
                Canvas.SetLeft(frame, p.X - 12);
                Canvas.SetTop(frame, p.Y - 8);
                _overlay.Children.Add(frame);
                var centre = new Shapes.Ellipse { Width = 6, Height = 6, Fill = UnitStroke, Opacity = opacity };
                Canvas.SetLeft(centre, p.X - 3);
                Canvas.SetTop(centre, p.Y - 3);
                _overlay.Children.Add(centre);
            }
            else if (m.Kind == MapMarkerKind.Splash)
            {
                _overlay.Children.Add(new Shapes.Polygon
                {
                    Points = new PointCollection { new(p.X, p.Y - 13), new(p.X + 15, p.Y), new(p.X, p.Y + 13), new(p.X - 15, p.Y) },
                    Fill = SplashFill, Stroke = SplashStroke, StrokeThickness = 1.2, Opacity = opacity,
                });
                _overlay.Children.Add(new Shapes.Line { X1 = p.X - 11, Y1 = p.Y, X2 = p.X + 11, Y2 = p.Y, Stroke = SplashCross, StrokeThickness = 2.5, Opacity = opacity });
                _overlay.Children.Add(new Shapes.Line { X1 = p.X, Y1 = p.Y - 10, X2 = p.X, Y2 = p.Y + 10, Stroke = SplashCross, StrokeThickness = 2.5, Opacity = opacity });
            }
            else
            {
                foreach (var (r, fill) in new[] { (11.0, TargetFill), (5.0, (Brush)Brushes.White) })
                {
                    var ring = new Shapes.Ellipse
                    {
                        Width = 2 * r, Height = 2 * r, Fill = fill, Stroke = TargetRing, StrokeThickness = 2.2, Opacity = opacity,
                    };
                    Canvas.SetLeft(ring, p.X - r);
                    Canvas.SetTop(ring, p.Y - r);
                    _overlay.Children.Add(ring);
                }
                var dot = new Shapes.Ellipse { Width = 3, Height = 3, Fill = TargetRing, Opacity = opacity };
                Canvas.SetLeft(dot, p.X - 1.5);
                Canvas.SetTop(dot, p.Y - 1.5);
                _overlay.Children.Add(dot);
            }
            AddLabel(m.Label, p.X + 14, p.Y - 20);
        }
    }

    private void AddLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            Padding = new Thickness(2, 0, 2, 0),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        _overlay.Children.Add(tb);
    }

    // ------------------------------------------------------------------ math

    private static double NormalizeLon(double lon)
    {
        while (lon > 180) lon -= 360;
        while (lon < -180) lon += 360;
        return lon;
    }

    public static double LonToX(double lon, int z) => (lon + 180.0) / 360.0 * TileSize * Math.Pow(2, z);

    public static double LatToY(double lat, int z)
    {
        lat = Math.Clamp(lat, -85.05112878, 85.05112878);
        double r = lat * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * TileSize * Math.Pow(2, z);
    }

    public static double XToLon(double x, int z) => x / (TileSize * Math.Pow(2, z)) * 360.0 - 180.0;

    public static double YToLat(double y, int z)
    {
        double n = Math.PI - 2 * Math.PI * y / (TileSize * Math.Pow(2, z));
        return 180 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }
}
