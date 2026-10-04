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
/// "Units" side tab. Units live in <see cref="Gcs.Units"/>; targets and splashes are NOT copied - the selectors
/// list the existing <see cref="TargetInfo"/> / <see cref="SplashInfo"/> objects from <see cref="Gcs.Targets"/>.
/// Calculate hands the three model objects to <see cref="UnitFireCalculator"/> and only displays the result.
/// Map synchronisation follows TargetsPage: list -> map through the events below, map -> list via <see cref="SelectUnit"/>.
/// </summary>
public sealed class UnitsPage : FlyoutPage
{
    public override string Title => "Units";

    /// <summary>Operator selected a unit in the list. Arg 2: true = centre the map on it.</summary>
    public event Action<int, bool> UnitSelected;

    /// <summary>Operator picked a target in the calculation selector (highlight / centre it).</summary>
    public event Action<int> TargetPicked;

    /// <summary>Operator picked a splash in the calculation selector (highlight / centre it).</summary>
    public event Action<int> SplashPicked;

    /// <summary>Nothing selected any more.</summary>
    public event Action SelectionCleared;

    private readonly TextBlock _geoNow, _result, _calcNote;
    private readonly DataGrid _unitGrid;
    private readonly ComboBox _targetBox, _splashBox;
    private bool _syncing;
    private bool _centerOnSelect = true;
    private UnitFireCalculation _shown;
    private (double, double, double, double, double, double) _shownInputs;   // unit / target / splash positions used
    private readonly TextField _manName, _manLat, _manLon;

    public UnitsPage()
    {
        F.LabelWidth = 70;

        // ---------------- unit position
        F.Section("Unit Position");
        _geoNow = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
        F.Add(_geoNow);
        var add = F.Buttons(("+ Add Unit at camera", AddUnitAtCamera), ("Delete", DeleteSelectedUnit));
        if (add.Children.Count > 0 && add.Children[0] is Button b)
        {
            b.FontWeight = FontWeights.Bold;
            b.ToolTip = "Create a unit at the gimbal's current GEO latitude / longitude";
        }

        // ---------------- manual entry / edit
        F.Section("Manual coordinates");
        _manName = F.Text("Name", "", "Optional for a new unit - default \"Unit N\"");
        _manLat = F.Text("Latitude", "", TargetsPage.CoordinateHelp);
        _manLon = F.Text("Longitude", "", TargetsPage.CoordinateHelp);
        F.Buttons(("+ Add manual unit", AddManualUnit), ("Apply to selected", ApplyToSelectedUnit),
                  ("Clear", () => { _manName.Value = _manLat.Value = _manLon.Value = ""; }));
        F.Note("Decimal degrees (13.450000 / 80.220000), D M S (13 27 00 N) or D M.m (13 27.0 N). " +
               "Selecting a unit fills these fields so it can be corrected with \"Apply to selected\".");

        // ---------------- units
        F.Section("Units");
        _unitGrid = new DataGrid
        {
            Height = 170, IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), FontSize = 11.5,
            Background = Brushes.White, RowBackground = Brushes.White, Margin = new Thickness(0, 2, 0, 4),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
        };
        foreach (var (header, path, width) in new[]
                 {
                     ("No.", nameof(UnitRow.Id), 34.0), ("Name", nameof(UnitRow.Name), 72.0), ("Latitude", nameof(UnitRow.Lat), 80.0),
                     ("Longitude", nameof(UnitRow.Lon), 80.0), ("Date/Time", nameof(UnitRow.Time), 116.0),
                 })
            _unitGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width) });
        _unitGrid.SelectionChanged += (_, _) => OnUnitSelectionChanged();
        F.Add(_unitGrid);

        // ---------------- calculation
        F.Section("Splash / Target Calculation");
        _targetBox = new ComboBox { Height = 22 };
        _splashBox = new ComboBox { Height = 22 };
        F.Row("Target:", _targetBox);
        F.Row("Splash:", _splashBox);
        _targetBox.SelectionChanged += (_, _) => OnTargetPicked();
        _splashBox.SelectionChanged += (_, _) => OnSplashPicked();
        var calculate = new Button
        {
            Content = "Calculate", Style = (Style)Application.Current.FindResource("PanelButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 26, Margin = new Thickness(0, 6, 0, 2),
            ToolTip = "Range / bearing from the selected unit, and the splash deviation relative to the Unit -> Target line",
        };
        calculate.Click += (_, _) => Calculate();
        F.Add(calculate);
        _calcNote = new TextBlock { Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
        F.Add(_calcNote);

        F.Section("Calculation result");
        _result = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Margin = new Thickness(0, 2, 0, 8) };
        F.Add(_result);
        F.Note("Deviation: + Add/Drop = splash beyond the target, - = short. + Left/Right = splash right of the Unit -> Target line " +
               "(seen from the unit), - = left. The correction to apply is the opposite (same wording as the Targets > Correction section).");

        Gcs.Units.Changed += OnUnitsChanged;
        Gcs.Targets.Changed += OnTargetsChanged;
        RefreshUnits();
        RefreshSelectors();
        ClearResult("Select a unit, a target and a splash, then press Calculate.");
        UpdateGeoNow();
    }

    // ================================================================ public API (map -> list)

    public int? SelectedUnitId => (_unitGrid.SelectedItem as UnitRow)?.Id;
    private int? PickedTargetId => (_targetBox.SelectedItem as PickItem)?.Id;
    private int? PickedSplashId => (_splashBox.SelectedItem as PickItem)?.Id;

    /// <summary>Selects a unit in the list (from a map marker click). Does not move the map.</summary>
    public void SelectUnit(int id)
    {
        var row = _unitGrid.Items.OfType<UnitRow>().FirstOrDefault(r => r.Id == id);
        if (row == null) return;
        _centerOnSelect = false;
        try
        {
            _unitGrid.SelectedItem = row;
            _unitGrid.ScrollIntoView(row);
        }
        finally { _centerOnSelect = true; }
    }

    public override void OnOpened()
    {
        UpdateGeoNow();
        RefreshSelectors();
    }

    public override void OnStatus(GlobalStatus s) => UpdateGeoNow();

    // ================================================================ commands

    /// <summary>
    /// "+ Add Unit at camera": reads the current camera geo point through the existing
    /// <c>Gcs.Controller.TryGetGeoPoint</c> (latest status from the existing receive thread), validates it and
    /// stores a <see cref="UnitInfo"/>. The marker appears through <see cref="UnitStore.Changed"/>.
    /// </summary>
    private void AddUnitAtCamera()
    {
        if (!Gcs.Controller.TryGetGeoPoint(out var gp, out var reason))
        {
            AppLog.Write("[units] unit not added: " + reason);
            MessageBox.Show(reason, "Cannot add a unit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var u = Gcs.Units.AddUnit(gp.Latitude, gp.Longitude);
        AppLog.Write($"[units] {u.Name} added at camera geo point {Fmt(u.Latitude)}, {Fmt(u.Longitude)}");
        _unitGrid.SelectedItem = _unitGrid.Items.OfType<UnitRow>().FirstOrDefault(r => r.Id == u.Id);
        if (_unitGrid.SelectedItem != null) _unitGrid.ScrollIntoView(_unitGrid.SelectedItem);
    }

    /// <summary>Manual unit: typed coordinates, validated, stored as <see cref="TargetSource.Manual"/>.</summary>
    private void AddManualUnit()
    {
        if (!CoordinateParser.TryParse(_manLat.Value, _manLon.Value, out double lat, out double lon, out string error))
        {
            MessageBox.Show(error, "Add unit manually", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var u = Gcs.Units.AddUnit(lat, lon, string.IsNullOrWhiteSpace(_manName.Value) ? null : _manName.Value, source: TargetSource.Manual);
        AppLog.Write($"[units] {u.Name} added manually at {Fmt(u.Latitude)}, {Fmt(u.Longitude)}");
        var row = _unitGrid.Items.OfType<UnitRow>().FirstOrDefault(r => r.Id == u.Id);
        if (row != null)
        {
            _unitGrid.SelectedItem = row;   // centres the map on the new unit
            _unitGrid.ScrollIntoView(row);
        }
    }

    /// <summary>Corrects the selected unit's name / position from the manual fields.</summary>
    private void ApplyToSelectedUnit()
    {
        if (SelectedUnitId is not int id || Gcs.Units.FindUnit(id) is not { } u)
        {
            MessageBox.Show("Select a unit in the Units list first.", "Units", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!CoordinateParser.TryParse(_manLat.Value, _manLon.Value, out double lat, out double lon, out string error))
        {
            MessageBox.Show(error, "Units", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Gcs.Units.UpdateUnit(id, _manName.Value, lat, lon);
        AppLog.Write($"[units] {u.Name} updated: {Fmt(lat)}, {Fmt(lon)}");
        UnitSelected?.Invoke(id, true);
    }

    private void DeleteSelectedUnit()
    {
        if (SelectedUnitId is not int id || Gcs.Units.FindUnit(id) is not { } u) return;
        if (!Confirm($"Delete {u.Name}?")) return;
        Gcs.Units.RemoveUnit(id);
        AppLog.Write($"[units] {u.Name} deleted");
    }

    /// <summary>Gets the three model objects, calls the calculator, shows the result. No maths here.</summary>
    private void Calculate()
    {
        var unit = SelectedUnitId is int uid ? Gcs.Units.FindUnit(uid) : null;
        var target = PickedTargetId is int tid ? Gcs.Targets.FindTarget(tid) : null;
        var splash = PickedSplashId is int sid ? Gcs.Targets.FindSplash(sid) : null;
        string missing = unit == null ? "Select a unit in the Units list."
                       : target == null ? "Select a target."
                       : splash == null ? "Select a splash." : null;
        if (missing != null)
        {
            ClearResult(missing);
            return;
        }
        try
        {
            var c = UnitFireCalculator.Calculate(unit, target, splash);
            Gcs.Units.SetLastCalculation(c);   // also shown in Targets > Correction
            Show(c, unit, target, splash);
            AppLog.Write($"[units] {unit.Name} -> {target.Name}: {c.UnitToTargetDistanceM:0.00} m / {c.UnitToTargetBearingDeg:0.00}°; " +
                         $"-> {splash.Name}: {c.UnitToSplashDistanceM:0.00} m / {c.UnitToSplashBearingDeg:0.00}°; " +
                         $"deviation Add/Drop {Signed(c.AddDropM)} m, Left/Right {Signed(c.LeftRightM)} m; " +
                         $"correction {FireCorrection.FormatAddDrop(c.CorrectionAddDropM, "m")}, {FireCorrection.FormatLeftRight(c.CorrectionLeftRightM, "m")}");
        }
        catch (ArgumentException ex)
        {
            ClearResult(ex.Message);
        }
    }

    // ================================================================ list / selector -> map

    private void OnUnitSelectionChanged()
    {
        if (_syncing) return;
        if (SelectedUnitId is int id && Gcs.Units.FindUnit(id) is { } u)
        {
            _manName.Value = u.Name;
            _manLat.Value = Fmt(u.Latitude);
            _manLon.Value = Fmt(u.Longitude);
            UnitSelected?.Invoke(id, _centerOnSelect);
        }
        else SelectionCleared?.Invoke();
    }

    private void OnTargetPicked()
    {
        if (_syncing || PickedTargetId is not int id) return;
        TargetPicked?.Invoke(id);
    }

    private void OnSplashPicked()
    {
        if (_syncing || PickedSplashId is not int id) return;
        // Keep the splash's own target association: pick its target when none (or another one) is picked.
        var s = Gcs.Targets.FindSplash(id);
        if (s?.TargetId is int tid && PickedTargetId != tid && Gcs.Targets.FindTarget(tid) != null)
        {
            _syncing = true;
            try { _targetBox.SelectedItem = _targetBox.Items.OfType<PickItem>().FirstOrDefault(p => p.Id == tid); }
            finally { _syncing = false; }
        }
        SplashPicked?.Invoke(id);
    }

    // ================================================================ view

    private void OnUnitsChanged()
    {
        RefreshUnits();
        CheckShownStillValid();
    }

    private void OnTargetsChanged()
    {
        RefreshSelectors();
        CheckShownStillValid();
    }

    /// <summary>Clears the shown result when its unit / target / splash was deleted or moved (no stale numbers).</summary>
    private void CheckShownStillValid()
    {
        if (_shown == null) return;
        var u = Gcs.Units.FindUnit(_shown.UnitId);
        var t = Gcs.Targets.FindTarget(_shown.TargetId);
        var s = Gcs.Targets.FindSplash(_shown.SplashId);
        if (u == null || t == null || s == null)
            ClearResult("The unit, target or splash of the last calculation was deleted.");
        else if (Positions(u, t, s) != _shownInputs)
            ClearResult("A position changed since the last calculation - press Calculate again.");
    }

    private static (double, double, double, double, double, double) Positions(UnitInfo u, TargetInfo t, SplashInfo s) =>
        (u.Latitude, u.Longitude, t.Latitude, t.Longitude, s.Latitude, s.Longitude);

    private void RefreshUnits()
    {
        int? sel = SelectedUnitId;
        _syncing = true;
        try
        {
            _unitGrid.ItemsSource = Gcs.Units.Units.Select(u => new UnitRow(u)).ToList();
            if (sel is int id) _unitGrid.SelectedItem = _unitGrid.Items.OfType<UnitRow>().FirstOrDefault(r => r.Id == id);
        }
        finally { _syncing = false; }
        if (sel != null && SelectedUnitId == null) SelectionCleared?.Invoke();
    }

    /// <summary>Rebuilds the Target / Splash selectors from the existing store, keeping the picked IDs.</summary>
    private void RefreshSelectors()
    {
        int? t = PickedTargetId, s = PickedSplashId;
        var targets = Gcs.Targets.Targets;
        _syncing = true;
        try
        {
            _targetBox.ItemsSource = targets.Select(x => new PickItem(x.Id, $"{x.Id} - {x.Name}")).ToList();
            _splashBox.ItemsSource = Gcs.Targets.Splashes.Select(x => new PickItem(x.Id,
                $"{x.Id} - {x.Name}" + (x.TargetId is int tid ? $"  ({targets.FirstOrDefault(tt => tt.Id == tid)?.Name ?? "target " + tid})" : ""))).ToList();
            _targetBox.SelectedItem = t is int ti ? _targetBox.Items.OfType<PickItem>().FirstOrDefault(p => p.Id == ti) : null;
            _splashBox.SelectedItem = s is int si ? _splashBox.Items.OfType<PickItem>().FirstOrDefault(p => p.Id == si) : null;
        }
        finally { _syncing = false; }
        _calcNote.Text = targets.Count == 0 || Gcs.Targets.Splashes.Count == 0
            ? "Targets and splashes come from the Targets tab (add them there first)."
            : _calcNote.Text;
    }

    private void Show(UnitFireCalculation c, UnitInfo unit, TargetInfo target, SplashInfo splash)
    {
        _shown = c;
        _shownInputs = Positions(unit, target, splash);
        _result.Text =
            $"UNIT → TARGET   ({unit.Name} → {target.Name})\n" +
            $"  Range   : {c.UnitToTargetDistanceM,10:0.00} m\n" +
            $"  Bearing : {c.UnitToTargetBearingDeg,10:0.00}°\n" +
            $"\nUNIT → SPLASH   ({unit.Name} → {splash.Name})\n" +
            $"  Range   : {c.UnitToSplashDistanceM,10:0.00} m\n" +
            $"  Bearing : {c.UnitToSplashBearingDeg,10:0.00}°\n" +
            $"\nSPLASH DEVIATION (vs Unit → Target line)\n" +
            $"  Add/Drop   : {Signed(c.AddDropM),9} m  ({(c.AddDropM >= 0 ? "beyond" : "short")})\n" +
            $"  Left/Right : {Signed(c.LeftRightM),9} m  ({(c.LeftRightM >= 0 ? "right" : "left")})\n" +
            $"\nCORRECTION TO APPLY\n" +
            $"  {FireCorrection.FormatAddDrop(c.CorrectionAddDropM, "m")},  {FireCorrection.FormatLeftRight(c.CorrectionLeftRightM, "m")}";
        _calcNote.Text = splash.TargetId is int tid && tid != target.Id
            ? $"Note: {splash.Name} was recorded for {(Gcs.Targets.FindTarget(tid)?.Name ?? "target " + tid)}, not {target.Name}."
            : $"Calculated {c.CalculatedAt:HH:mm:ss}.";
    }

    private void ClearResult(string message)
    {
        _shown = null;
        _result.Text = "UNIT → TARGET\n  Range   : -\n  Bearing : -\n\nUNIT → SPLASH\n  Range   : -\n  Bearing : -\n\n" +
                       "SPLASH DEVIATION\n  Add/Drop   : -\n  Left/Right : -";
        _calcNote.Text = message;
    }

    private void UpdateGeoNow()
    {
        if (Gcs.Controller.TryGetGeoPoint(out var gp, out var reason))
        {
            _geoNow.Text = $"Camera geo point:\n{Fmt(gp.Latitude)}, {Fmt(gp.Longitude)}";
            _geoNow.Foreground = Brushes.DarkGreen;
        }
        else
        {
            _geoNow.Text = "Camera geo point: not available\n" + reason;
            _geoNow.Foreground = Brushes.Firebrick;
        }
    }

    private static string Fmt(double v) => v.ToString("0.000000", CultureInfo.InvariantCulture);
    private static string Signed(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    /// <summary>Selector entry: the model ID plus its display text (the model itself stays in the store).</summary>
    private sealed record PickItem(int Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Read-only row snapshot for the grid (never the source of truth).</summary>
    public sealed class UnitRow
    {
        public UnitRow(UnitInfo u)
        {
            Id = u.Id; Name = u.Name; Lat = Fmt(u.Latitude); Lon = Fmt(u.Longitude);
            Time = u.DateTime.ToString("dd/MM/yy HH:mm:ss");
        }
        public int Id { get; }
        public string Name { get; }
        public string Lat { get; }
        public string Lon { get; }
        public string Time { get; }
    }
}
