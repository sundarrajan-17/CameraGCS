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
    private readonly NumField _lat, _lon;
    private readonly ChoiceField _status;
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

        // ---------------- selected target details
        F.Section("Selected target");
        _details = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        F.Add(_details);
        _name = F.Text("Name", "");
        _lat = F.Number("Latitude °", 0, -90, 90);
        _lon = F.Number("Longitude °", 0, -180, 180);
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
        try
        {
            Gcs.Targets.UpdateTarget(id, _name.Value, _lat.Value, _lon.Value,
                                     _status.Value == 1 ? TargetStatus.Inactive : TargetStatus.Active, _notes.Value);
            AppLog.Write($"[targets] target {id} updated");
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
            _name.Value = ""; _notes.Value = "";
            return;
        }
        _name.Value = t.Name;
        _lat.Value = t.Latitude;
        _lon.Value = t.Longitude;
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
