using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Epsilon.Core.Protocol;
using Epsilon.Core.Targets;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

/// <summary>
/// "Targets" side tab: lists the targets and splashes held in <see cref="Gcs.Targets"/> (the source of truth).
/// The grids show read-only row snapshots built from the models and are rebuilt whenever the store changes.
/// Selection is synchronised with the map through <see cref="TargetSelected"/> / <see cref="SplashSelected"/>
/// (list -> map) and <see cref="SelectTarget"/> / <see cref="SelectSplash"/> (map marker -> list).
/// </summary>
public sealed class TargetsPage : FlyoutPage
{
    public override string Title => "Targets";

    /// <summary>Raised when the operator selects a target in the list. Arg 2: true = centre the map on it.</summary>
    public event Action<int, bool> TargetSelected;

    /// <summary>Raised when the operator selects a splash in the list. Arg 2: true = centre the map on it.</summary>
    public event Action<int, bool> SplashSelected;

    /// <summary>Raised when nothing is selected any more.</summary>
    public event Action SelectionCleared;

    private readonly DataGrid _targetGrid, _splashGrid;
    private readonly TextBlock _geoNow, _details;
    private readonly TextField _name, _notes;
    private readonly TextField _lat, _lon;                       // selected-target edit (any accepted format)
    private readonly TextField _manName, _manLat, _manLon;       // manual entry of a new target
    private readonly ChoiceField _status;

    // Correction section
    private static readonly string[] Units = { "m", "ft", "yd" };
    private readonly TextBlock _mpi, _leftRight, _addDrop, _correctionInfo, _unitCalc;
    private readonly ChoiceField _units;
    private CorrectionResult _lastCorrection;
    private int? _lastTargetId;   // target the correction refers to (kept while a splash row is selected)
    private bool _syncing;   // true while the selection is set from code (marker click / refresh)
    private bool _centerOnSelect = true;

    public TargetsPage()
    {
        F.LabelWidth = 90;

        // ---------------- camera geo point (what [+] will record)
        _geoNow = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 2), TextWrapping = TextWrapping.Wrap };
        F.Add(_geoNow);

        // ---------------- targets
        F.Section("Targets");
        F.Buttons(("+ Add target (camera geo point)", AddTargetFromCamera), ("Delete", DeleteSelectedTarget));
        F.Note("Or right-click the map: \"Add target here\". Click a target on the map to select it here.");
        _targetGrid = MakeGrid(190,
            ("No.", nameof(TargetRow.Id), 34),
            ("Name", nameof(TargetRow.Name), 72),
            ("Latitude", nameof(TargetRow.Lat), 74),
            ("Longitude", nameof(TargetRow.Lon), 74),
            ("Date/Time", nameof(TargetRow.Time), 110),
            ("Status", nameof(TargetRow.Status), 54));
        _targetGrid.SelectionChanged += (_, _) => OnTargetSelectionChanged();
        F.Add(_targetGrid);

        // ---------------- manual entry
        F.Section("Add target manually");
        _manName = F.Text("Name", "", "Optional - default \"Target N\"");
        _manLat = F.Text("Latitude", "", CoordinateHelp);
        _manLon = F.Text("Longitude", "", CoordinateHelp);
        F.Buttons(("+ Add manual target", AddManualTarget), ("Clear", () => { _manName.Value = _manLat.Value = _manLon.Value = ""; }));
        F.Note("Decimal degrees (13.454576 / 80.226849), D M S (13 27 16.47 N) or D M.m (13 27.2746 N). " +
               "A whole pair \"13.454576, 80.226849\" can be pasted into Latitude.");

        // ---------------- selected target details
        F.Section("Selected target");
        _details = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        F.Add(_details);
        _name = F.Text("Name", "");
        _lat = F.Text("Latitude", "", CoordinateHelp);
        _lon = F.Text("Longitude", "", CoordinateHelp);
        _status = F.Choice("Status", 0, "Active", "Inactive");
        _notes = F.Text("Notes", "");
        F.Buttons(("Apply", ApplyDetails), ("Center map", () => { if (SelectedTargetId is int id) TargetSelected?.Invoke(id, true); }),
                  ("Geo lock", GeoLockSelected));

        // ---------------- splashes
        F.Section("Splashes");
        var plus = F.Buttons(("+  Splash at camera geo point", AddSplashFromCamera), ("Delete", DeleteSelectedSplash));
        if (plus.Children.Count > 0 && plus.Children[0] is Button b) { b.FontWeight = FontWeights.Bold; b.ToolTip = "Record a splash at the gimbal's current GEO latitude / longitude"; }
        _splashGrid = MakeGrid(170,
            ("No.", nameof(SplashRow.Id), 34),
            ("Date/Time", nameof(SplashRow.Time), 130),
            ("Latitude", nameof(SplashRow.Lat), 80),
            ("Longitude", nameof(SplashRow.Lon), 80),
            ("Target", nameof(SplashRow.Target), 66));
        _splashGrid.SelectionChanged += (_, _) => OnSplashSelectionChanged();
        F.Add(_splashGrid);

        // ---------------- correction (below the splashes, as in Epsilon Control)
        F.Section("Correction");
        _mpi = F.Value("MPI:");
        _mpi.TextWrapping = TextWrapping.Wrap;
        _units = F.Choice("Units", 0, Units);
        _units.Box.SelectionChanged += (_, _) => { ShowCorrection(); if (_unitCalc != null) ShowUnitCalculation(); };
        _leftRight = F.Value("Left/Right:");
        _addDrop = F.Value("Add/Drop:");
        foreach (var tb in new[] { _leftRight, _addDrop }) { tb.FontWeight = FontWeights.Bold; tb.FontSize = 13; }
        var calculate = new Button
        {
            Content = "Calculate", Style = (Style)Application.Current.FindResource("PanelButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 26, Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "MPI of the selected target's splashes and the Left/Right, Add/Drop correction seen from the camera",
        };
        calculate.Click += (_, _) => Calculate();
        F.Add(calculate);
        _correctionInfo = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        F.Add(_correctionInfo);

        // Latest Units-page calculation (Unit -> Target line). Shown separately: it does not use or change the MPI above.
        _unitCalc = new TextBlock
        {
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x3C, 0x9C)),
        };
        F.Add(_unitCalc);
        Gcs.Units.Changed += ShowUnitCalculation;
        Gcs.Targets.Changed += ShowUnitCalculation;
        ShowUnitCalculation();
        ClearCorrection("Select a target, record its splashes with [+], then press Calculate.");

        Gcs.Targets.Changed += Refresh;
        Refresh();
        ShowDetails(null);
        UpdateGeoNow();
    }

    // ================================================================ public API (map -> list)

    public int? SelectedTargetId => (_targetGrid.SelectedItem as TargetRow)?.Id;
    public int? SelectedSplashId => (_splashGrid.SelectedItem as SplashRow)?.Id;

    /// <summary>Selects a target in the list (from a map marker click). Does not move the map.</summary>
    public void SelectTarget(int id)
    {
        var row = _targetGrid.Items.OfType<TargetRow>().FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        _centerOnSelect = false;
        try
        {
            _targetGrid.SelectedItem = row;   // raises OnTargetSelectionChanged -> details + highlight
            _targetGrid.ScrollIntoView(row);
        }
        finally { _centerOnSelect = true; }
    }

    /// <summary>Selects a splash in the list (from a map marker click). Does not move the map.</summary>
    public void SelectSplash(int id)
    {
        var row = _splashGrid.Items.OfType<SplashRow>().FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        _centerOnSelect = false;
        try
        {
            _splashGrid.SelectedItem = row;
            _splashGrid.ScrollIntoView(row);
        }
        finally { _centerOnSelect = true; }
    }

    public override void OnOpened() => UpdateGeoNow();

    public override void OnStatus(GlobalStatus s) => UpdateGeoNow();

    // ================================================================ list -> map

    private void OnTargetSelectionChanged()
    {
        if (_syncing) return;
        var t = SelectedTargetId is int id ? Gcs.Targets.FindTarget(id) : null;
        ShowDetails(t);
        if (t != null && t.Id != _lastTargetId)
        {
            _lastTargetId = t.Id;
            ClearCorrection($"Target: {t.Name}. Press Calculate.");
        }
        if (t == null)
        {
            if (_splashGrid.SelectedItem == null) SelectionCleared?.Invoke();
            return;
        }
        _syncing = true;
        try { _splashGrid.SelectedItem = null; } finally { _syncing = false; }
        TargetSelected?.Invoke(t.Id, _centerOnSelect);
    }

    private void OnSplashSelectionChanged()
    {
        if (_syncing) return;
        var s = SelectedSplashId is int id ? Gcs.Targets.FindSplash(id) : null;
        if (s == null)
        {
            if (_targetGrid.SelectedItem == null) SelectionCleared?.Invoke();
            return;
        }
        _syncing = true;
        try { _targetGrid.SelectedItem = null; } finally { _syncing = false; }
        ShowDetails(null);
        SplashSelected?.Invoke(s.Id, _centerOnSelect);
    }

    // ================================================================ commands

    internal const string CoordinateHelp =
        "Decimal degrees, D M S or D M.m; N/S/E/W or a minus sign. A \"lat, lon\" pair can be pasted into Latitude.";

    /// <summary>Manual target: typed coordinates, validated, stored as <see cref="TargetSource.Manual"/>.</summary>
    private void AddManualTarget()
    {
        if (!CoordinateParser.TryParse(_manLat.Value, _manLon.Value, out double lat, out double lon, out string error))
        {
            Warn("Add target manually", error);
            return;
        }
        var t = Gcs.Targets.AddTarget(lat, lon, TargetSource.Manual, string.IsNullOrWhiteSpace(_manName.Value) ? null : _manName.Value);
        AppLog.Write($"[targets] {t.Name} added manually at {Fmt(t.Latitude)}, {Fmt(t.Longitude)}");
        _manName.Value = _manLat.Value = _manLon.Value = "";
        SelectTargetAndCenter(t.Id);
    }

    /// <summary>Selects a target in the list and centres the map on it (newly added targets).</summary>
    private void SelectTargetAndCenter(int id)
    {
        var row = _targetGrid.Items.OfType<TargetRow>().FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        _targetGrid.SelectedItem = row;           // raises TargetSelected(id, center: true)
        _targetGrid.ScrollIntoView(row);
    }

    private void AddTargetFromCamera()
    {
        if (!Gcs.Controller.TryGetGeoPoint(out var gp, out var reason))
        {
            Warn("Cannot add a target from the camera", reason + "\n\nYou can also right-click the map and choose \"Add target here\".");
            return;
        }
        var t = Gcs.Targets.AddTarget(gp.Latitude, gp.Longitude, TargetSource.CameraGeo,
                                      rangeM: gp.RangeM > 0 ? gp.RangeM : null, rangeByLrf: gp.RangeByLrf);
        AppLog.Write($"[targets] {t.Name} added from camera geo point {Fmt(t.Latitude)}, {Fmt(t.Longitude)}");
        _targetGrid.SelectedItem = _targetGrid.Items.OfType<TargetRow>().FirstOrDefault(r => r.Id == t.Id);
    }

    /// <summary>
    /// Splash [+]: reads the gimbal's CURRENT geo latitude / longitude (EPSILON_GLOBAL_STATUS, written by the
    /// existing receive thread), validates it, stores a <see cref="SplashInfo"/> and selects it.
    /// The map marker appears through the store's Changed event.
    /// </summary>
    private void AddSplashFromCamera()
    {
        if (!Gcs.Controller.TryGetGeoPoint(out var gp, out var reason))
        {
            AppLog.Write("[targets] splash not added: " + reason);
            Warn("Cannot record a splash", reason);
            return;
        }
        var s = Gcs.Targets.AddSplash(gp.Latitude, gp.Longitude, SelectedTargetId);
        AppLog.Write($"[targets] {s.Name} at {Fmt(s.Latitude)}, {Fmt(s.Longitude)}" +
                     (s.TargetId is int tid ? $" (target {tid})" : ""));
        _splashGrid.SelectedItem = _splashGrid.Items.OfType<SplashRow>().FirstOrDefault(r => r.Id == s.Id);
        if (_splashGrid.SelectedItem != null) _splashGrid.ScrollIntoView(_splashGrid.SelectedItem);
    }

    private void ApplyDetails()
    {
        if (SelectedTargetId is not int id) return;
        if (!CoordinateParser.TryParse(_lat.Value, _lon.Value, out double lat, out double lon, out string error))
        {
            Warn("Targets", error);   // nothing is changed on a typing error
            return;
        }
        try
        {
            Gcs.Targets.UpdateTarget(id, _name.Value, lat, lon,
                                     _status.Value == 1 ? TargetStatus.Inactive : TargetStatus.Active, _notes.Value);
            AppLog.Write($"[targets] target {id} updated: {Fmt(lat)}, {Fmt(lon)}");
        }
        catch (ArgumentOutOfRangeException)
        {
            Warn("Targets", "Latitude / longitude are not valid.");
        }
    }

    private void DeleteSelectedTarget()
    {
        if (SelectedTargetId is not int id) return;
        var t = Gcs.Targets.FindTarget(id);
        if (t == null || !Confirm($"Delete {t.Name}?")) return;
        Gcs.Targets.RemoveTarget(id);
        AppLog.Write($"[targets] {t.Name} deleted");
    }

    private void DeleteSelectedSplash()
    {
        if (SelectedSplashId is not int id) return;
        var s = Gcs.Targets.FindSplash(id);
        if (s == null || !Confirm($"Delete {s.Name}?")) return;
        Gcs.Targets.RemoveSplash(id);
        AppLog.Write($"[targets] {s.Name} deleted");
    }

    private void GeoLockSelected()
    {
        if (SelectedTargetId is not int id || Gcs.Targets.FindTarget(id) is not { } t) return;
        if (!Gcs.Controller.IsLinkOpen) { Warn("Geo lock", "Not connected to the gimbal."); return; }
        Gcs.Controller.GeoLock(t.Latitude, t.Longitude);
        AppLog.Write($"[targets] geo lock on {t.Name} {Fmt(t.Latitude)}, {Fmt(t.Longitude)}");
    }

    // ================================================================ view

    private void Refresh()
    {
        int? selTarget = SelectedTargetId, selSplash = SelectedSplashId;
        var targets = Gcs.Targets.Targets;
        _syncing = true;
        try
        {
            _targetGrid.ItemsSource = targets.Select(t => new TargetRow(t)).ToList();
            _splashGrid.ItemsSource = Gcs.Targets.Splashes
                .Select(s => new SplashRow(s, s.TargetId is int tid ? targets.FirstOrDefault(t => t.Id == tid)?.Name ?? $"#{tid}" : ""))
                .ToList();
            if (selTarget is int ti) _targetGrid.SelectedItem = _targetGrid.Items.OfType<TargetRow>().FirstOrDefault(r => r.Id == ti);
            if (selSplash is int si) _splashGrid.SelectedItem = _splashGrid.Items.OfType<SplashRow>().FirstOrDefault(r => r.Id == si);
        }
        finally { _syncing = false; }
        ShowDetails(SelectedTargetId is int id ? Gcs.Targets.FindTarget(id) : null);
        if (selTarget != null && SelectedTargetId == null && SelectedSplashId == null) SelectionCleared?.Invoke();
    }

    private void ShowDetails(TargetInfo t)
    {
        bool has = t != null;
        foreach (var box in new Control[] { _name.Box, _lat.Box, _lon.Box, _status.Box, _notes.Box }) box.IsEnabled = has;
        if (!has)
        {
            _details.Text = "Select a target in the list or click it on the map.";
            _name.Value = ""; _notes.Value = ""; _lat.Value = ""; _lon.Value = "";
            return;
        }
        _name.Value = t.Name;
        _lat.Value = Fmt(t.Latitude);
        _lon.Value = Fmt(t.Longitude);
        _status.Value = t.Status == TargetStatus.Inactive ? 1 : 0;
        _notes.Value = t.Notes;
        string range = t.RangeM is int r ? $", range {r} m ({(t.RangeMeasuredByLrf ? "LRF" : "calculated")})" : "";
        int splashes = Gcs.Targets.Splashes.Count(s => s.TargetId == t.Id);
        _details.Text = $"ID {t.Id}  ·  {t.Name}  ·  created {t.DateTime:dd/MM/yy HH:mm:ss}  ·  source {t.Source}{range}  ·  splashes {splashes}";
    }

    private void UpdateGeoNow()
    {
        if (Gcs.Controller.TryGetGeoPoint(out var gp, out var reason))
        {
            _geoNow.Text = $"Camera geo point: {Fmt(gp.Latitude)}, {Fmt(gp.Longitude)}" +
                           (gp.RangeM > 0 ? $"   range {gp.RangeM} m{(gp.RangeByLrf ? " (LRF)" : "")}" : "");
            _geoNow.Foreground = Brushes.DarkGreen;
        }
        else
        {
            _geoNow.Text = "Camera geo point: not available - " + reason;
            _geoNow.Foreground = Brushes.Firebrick;
        }
    }

    private static DataGrid MakeGrid(double height, params (string Header, string Path, double Width)[] columns)
    {
        var grid = new DataGrid
        {
            Height = height, IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), FontSize = 11.5,
            Background = Brushes.White, RowBackground = Brushes.White, Margin = new Thickness(0, 2, 0, 4),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
        };
        foreach (var (header, path, width) in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width) });
        return grid;
    }

    // ================================================================ correction

    /// <summary>The target the correction is for: the selected target, else the selected splash's target, else the last one.</summary>
    private TargetInfo CorrectionTarget()
    {
        if (SelectedTargetId is int id) return Gcs.Targets.FindTarget(id);
        if (SelectedSplashId is int sid && Gcs.Targets.FindSplash(sid)?.TargetId is int tid) return Gcs.Targets.FindTarget(tid);
        return _lastTargetId is int last ? Gcs.Targets.FindTarget(last) : null;
    }

    /// <summary>
    /// Calculate: MPI = mean of the target's splashes. Camera position = CAM_LATITUDE / CAM_LONGITUDE of the latest
    /// gimbal status. Target and MPI are converted with latlon_to_xy_approx(camera, point, azimuth camera->target);
    /// the difference gives Left/Right (across) and Add/Drop (along the camera-target line).
    /// </summary>
    private void Calculate()
    {
        var target = CorrectionTarget();
        if (target == null) { ClearCorrection("Select a target first."); return; }
        var splashes = Gcs.Targets.Splashes.Where(s => s.TargetId == target.Id).ToList();
        if (splashes.Count == 0)
        {
            ClearCorrection($"{target.Name} has no splashes. Select the target, then press Splash [+] to record them.");
            return;
        }
        if (!Gcs.Controller.TryGetCameraPosition(out double camLat, out double camLon, out string reason))
        {
            ClearCorrection("Camera position not available: " + reason);
            return;
        }
        try
        {
            _lastCorrection = FireCorrection.Calculate(camLat, camLon, target, splashes);
            _lastTargetId = target.Id;
            ShowCorrection();
            _correctionInfo.Text = $"{target.Name}  ·  camera {Fmt(camLat)}, {Fmt(camLon)}  ·  " +
                                   $"azimuth {_lastCorrection.BearingDeg:0.0}°  ·  range {_lastCorrection.TargetRangeM:0} m";
            AppLog.Write($"[targets] correction for {target.Name}: MPI {Fmt(_lastCorrection.MpiLatitude)}, {Fmt(_lastCorrection.MpiLongitude)} " +
                         $"({splashes.Count} splashes), {FireCorrection.FormatLeftRight(_lastCorrection.LeftRightM, "m")}, " +
                         $"{FireCorrection.FormatAddDrop(_lastCorrection.AddDropM, "m")}, azimuth {_lastCorrection.BearingDeg:0.0}°");
        }
        catch (ArgumentException ex)
        {
            ClearCorrection(ex.Message);
        }
    }

    private void ShowCorrection()
    {
        if (_lastCorrection == null || _mpi == null) return;
        string unit = Units[Math.Clamp(_units.Value, 0, Units.Length - 1)];
        var c = _lastCorrection;
        _mpi.Text = $"{Fmt(c.MpiLatitude)}, {Fmt(c.MpiLongitude)}  ({c.SplashCount} splash{(c.SplashCount == 1 ? "" : "es")})";
        _leftRight.Text = FireCorrection.FormatLeftRight(c.LeftRightM, unit);
        _addDrop.Text = FireCorrection.FormatAddDrop(c.AddDropM, unit);
    }

    /// <summary>Read-only summary of the last Unit / Target / Splash calculation from the Units page.</summary>
    private void ShowUnitCalculation()
    {
        var c = Gcs.Units.LastCalculation;
        var u = c != null ? Gcs.Units.FindUnit(c.UnitId) : null;
        var t = c != null ? Gcs.Targets.FindTarget(c.TargetId) : null;
        var s = c != null ? Gcs.Targets.FindSplash(c.SplashId) : null;
        if (c == null || u == null || t == null || s == null)
        {
            _unitCalc.Text = "Unit calculation: none (Units tab > Calculate).";
            return;
        }
        string unit = Units[Math.Clamp(_units.Value, 0, Units.Length - 1)];
        _unitCalc.Text = $"Unit calculation ({c.CalculatedAt:HH:mm:ss}) {u.Name} → {t.Name}, {s.Name}: " +
                         $"{FireCorrection.FormatAddDrop(c.CorrectionAddDropM, unit)}, {FireCorrection.FormatLeftRight(c.CorrectionLeftRightM, unit)} " +
                         $"(splash {Math.Abs(FireCorrection.ToUnit(c.AddDropM, unit)):0} {unit} {(c.AddDropM >= 0 ? "beyond" : "short")}, " +
                         $"{Math.Abs(FireCorrection.ToUnit(c.LeftRightM, unit)):0} {unit} {(c.LeftRightM >= 0 ? "right" : "left")})";
    }

    private void ClearCorrection(string message)
    {
        _lastCorrection = null;
        _mpi.Text = _leftRight.Text = _addDrop.Text = "";
        _correctionInfo.Text = message;
    }

    private static string Fmt(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

    private static void Warn(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // Read-only row snapshots for the grids (never the source of truth).
    public sealed class TargetRow
    {
        public TargetRow(TargetInfo t)
        {
            Id = t.Id; Name = t.Name; Lat = Fmt(t.Latitude); Lon = Fmt(t.Longitude);
            Time = t.DateTime.ToString("dd/MM/yy HH:mm:ss"); Status = t.Status.ToString();
        }
        public int Id { get; }
        public string Name { get; }
        public string Lat { get; }
        public string Lon { get; }
        public string Time { get; }
        public string Status { get; }
    }

    public sealed class SplashRow
    {
        public SplashRow(SplashInfo s, string target)
        {
            Id = s.Id; Lat = Fmt(s.Latitude); Lon = Fmt(s.Longitude);
            Time = s.DateTime.ToString("dd/MM/yy HH:mm:ss"); Target = target;
        }
        public int Id { get; }
        public string Time { get; }
        public string Lat { get; }
        public string Lon { get; }
        public string Target { get; }
    }
}
