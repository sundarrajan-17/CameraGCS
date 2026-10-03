using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Dialogs;

/// <summary>Base for small code-built tool windows.</summary>
public abstract class ToolWindow : Window
{
    protected ToolWindow(string title, double width, double height)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("WindowBg");
        Owner = Application.Current.MainWindow;
        ShowInTaskbar = false;
    }

    protected static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("PanelButton") };
        b.Click += (_, _) => click();
        return b;
    }

    protected static TextBox MakeMonoBox() => new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(6),
    };

    protected void Ui(Action a) => Dispatcher.BeginInvoke(a);
}

/// <summary>GET_VERSION (0x02) -> EPSILON_VERSION (0x81).</summary>
public sealed class DeviceInfoWindow : ToolWindow
{
    private readonly TextBox _text = MakeMonoBox();

    public DeviceInfoWindow() : base("Device Info", 560, 520)
    {
        var dp = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(MakeButton("Refresh", () => Gcs.Send(Cmd.GetVersion())));
        DockPanel.SetDock(bar, Dock.Bottom);
        dp.Children.Add(bar);
        dp.Children.Add(_text);
        Content = dp;

        Gcs.Gimbal.VersionReceived += OnVersion;
        Closed += (_, _) => Gcs.Gimbal.VersionReceived -= OnVersion;
        if (Gcs.Gimbal.Version != null) Render(Gcs.Gimbal.Version);
        else _text.Text = "Waiting for EPSILON_VERSION...";
        Gcs.Send(Cmd.GetVersion());
    }

    private void OnVersion(VersionInfo v) => Ui(() => Render(v));

    private void Render(VersionInfo v)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Type                 {v.TypeName}");
        sb.AppendLine($"Unique ID            {v.UniqueId:X16}");
        sb.AppendLine($"Firmware             {v.Firmware}");
        sb.AppendLine($"Pan firmware         {v.PanFirmware}");
        sb.AppendLine($"Tilt bootloader      {v.TiltBootloader}");
        sb.AppendLine($"Pan bootloader       {v.PanBootloader}");
        sb.AppendLine($"Tilt / pan PCB       {v.TiltPcb} / {v.PanPcb}");
        sb.AppendLine($"Video processor FW   {v.VideoProcessor}");
        sb.AppendLine($"EO sensor            {v.EoSensorName}");
        sb.AppendLine($"IR sensor            {v.IrSensorName}");
        sb.AppendLine($"IR optics            {v.IrOpticsName}");
        sb.AppendLine($"LRF / laser / GEO    {(v.HasLrf ? "yes" : "no")} / {(v.HasLaserAim ? "yes" : "no")} / {(v.HasGeo ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine("Video processor features:");
        foreach (var f in v.VideoProcessorFeatureNames()) sb.AppendLine("  • " + f);
        sb.AppendLine();
        sb.AppendLine("Epsilon features:");
        foreach (var f in v.EpsilonFeatureNames()) sb.AppendLine("  • " + f);
        _text.Text = sb.ToString();
    }
}

/// <summary>Legacy errors (0x82) and the runtime error / log interface (0x42 / 0x43).</summary>
public sealed class ErrorsWindow : ToolWindow
{
    public sealed class Row
    {
        public string Source { get; init; }
        public string Severity { get; init; }
        public string Code { get; init; }
        public string Time { get; init; }
        public string Count { get; init; }
        public string Text { get; init; }
    }

    private readonly ObservableCollection<Row> _rows = new();
    private int _requestCommand;

    public ErrorsWindow() : base("Errors", 820, 460)
    {
        var grid = new DataGrid
        {
            ItemsSource = _rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(6),
            HeadersVisibility = DataGridHeadersVisibility.Column, CanUserAddRows = false,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Source", Binding = new System.Windows.Data.Binding(nameof(Row.Source)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Severity", Binding = new System.Windows.Data.Binding(nameof(Row.Severity)), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Code", Binding = new System.Windows.Data.Binding(nameof(Row.Code)), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new System.Windows.Data.Binding(nameof(Row.Time)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = new System.Windows.Data.Binding(nameof(Row.Count)), Width = 50 });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Description", Binding = new System.Windows.Data.Binding(nameof(Row.Text)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });

        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(MakeButton("Legacy errors (0x82)", () => { Clear("Legacy"); Gcs.Send(Cmd.GetErrors()); }));
        bar.Children.Add(MakeButton("Runtime errors", () => { Clear("Runtime"); _requestCommand = 0; Gcs.Send(Cmd.ErrorHandling(0, 0)); }));
        bar.Children.Add(MakeButton("Error log", () => { Clear("Log"); _requestCommand = 1; Gcs.Send(Cmd.ErrorHandling(1, 0)); }));
        bar.Children.Add(MakeButton("Clear log on gimbal", () =>
        {
            if (MessageBox.Show(this, "Clear the complete error log on the gimbal?", "Errors", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Gcs.Send(Cmd.ErrorHandling(3, 0));
        }));

        var dp = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dp.Children.Add(bar);
        dp.Children.Add(grid);
        Content = dp;

        Gcs.Gimbal.LegacyErrorsReceived += OnLegacy;
        Gcs.Gimbal.ErrorReportReceived += OnReport;
        Closed += (_, _) =>
        {
            Gcs.Gimbal.LegacyErrorsReceived -= OnLegacy;
            Gcs.Gimbal.ErrorReportReceived -= OnReport;
        };
        // Protocol v4.0: use the ERROR_HANDLING (0x42) mechanism by default; do not mix it with legacy 0x82.
        _requestCommand = 0;
        Gcs.Send(Cmd.ErrorHandling(0, 0));
    }

    private void Clear(string source)
    {
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (_rows[i].Source == source) _rows.RemoveAt(i);
    }

    private void OnLegacy(LegacyErrors e) => Ui(() =>
    {
        Clear("Legacy");
        foreach (var (sev, text) in e.ActiveErrors())
            _rows.Add(new Row { Source = "Legacy", Severity = sev, Text = text, Code = "", Time = "", Count = "" });
        if (!e.ActiveErrors().Any())
            _rows.Add(new Row { Source = "Legacy", Severity = "-", Text = "No active errors", Code = "", Time = "", Count = "" });
    });

    private void OnReport(ErrorReport r) => Ui(() =>
    {
        string src = r.Command == 1 ? "Log" : "Runtime";
        if (r.ErrorCode == 0 && string.IsNullOrEmpty(r.Description))
        {
            _rows.Add(new Row { Source = src, Severity = "-", Text = "No entries", Code = "", Time = "", Count = "" });
            return;
        }
        _rows.Add(new Row
        {
            Source = src, Severity = r.SeverityName, Code = r.ErrorCode.ToString(),
            Time = r.TimeText, Count = r.OccurrenceCount.ToString(), Text = r.Description,
        });
        // Walk the linked list: request the next entry until NEXT_ID is 0.
        if (r.NextId != 0 && _rows.Count < 500)
            Gcs.Send(Cmd.ErrorHandling(_requestCommand, r.NextId));
    });
}

/// <summary>DEVICE_DIAGNOSTIC (0xFA).</summary>
public sealed class DiagnosticWindow : ToolWindow
{
    private readonly TextBox _text = MakeMonoBox();
    private readonly ComboBox _device = new() { Width = 200, Margin = new Thickness(3) };
    private readonly TextBox _page = new() { Width = 40, Text = "0", Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };

    public DiagnosticWindow() : base("Device Diagnostic", 640, 520)
    {
        string[] devices = { "0 - Epsilon (tilt)", "1 - Pan", "2 - GPS", "3 - IR camera", "4 - EO camera", "5 - LRF", "6 - Video processor" };
        foreach (var d in devices) _device.Items.Add(d);
        _device.SelectedIndex = 0;

        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(new TextBlock { Text = "Device:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
        bar.Children.Add(_device);
        bar.Children.Add(new TextBlock { Text = "Page:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) });
        bar.Children.Add(_page);
        bar.Children.Add(MakeButton("Get", () =>
        {
            int.TryParse(_page.Text, out int page);
            Gcs.Send(Cmd.DeviceDiagnostic(Math.Max(0, _device.SelectedIndex), page));
        }));
        bar.Children.Add(MakeButton("Clear", () => _text.Clear()));
        var note = new TextBlock
        {
            Text = "Device numbering is model specific; see the gimbal documentation.",
            Foreground = Brushes.DimGray, Margin = new Thickness(6, 0, 6, 0),
        };

        var dp = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(note, Dock.Top);
        dp.Children.Add(bar);
        dp.Children.Add(note);
        dp.Children.Add(_text);
        Content = dp;

        Gcs.Gimbal.DiagnosticReceived += OnDiag;
        Closed += (_, _) => Gcs.Gimbal.DiagnosticReceived -= OnDiag;
    }

    private void OnDiag(string text) => Ui(() =>
    {
        _text.AppendText(text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine + "----" + Environment.NewLine);
        _text.ScrollToEnd();
    });
}

/// <summary>Application / link / restream log.</summary>
public sealed class LogWindow : ToolWindow
{
    private readonly TextBox _text = MakeMonoBox();

    public LogWindow() : base("Log", 900, 500)
    {
        _text.FontSize = 11;
        _text.Text = string.Join(Environment.NewLine, AppLog.Snapshot()) + Environment.NewLine;
        var traffic = new CheckBox
        {
            Content = "Log protocol traffic", IsChecked = Gcs.Settings.LogTraffic,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
        };
        traffic.Click += (_, _) =>
        {
            Gcs.Settings.LogTraffic = traffic.IsChecked == true;
            Gcs.Gimbal.LogTraffic = Gcs.Settings.LogTraffic;
        };
        var bar = new WrapPanel { Margin = new Thickness(4) };
        bar.Children.Add(MakeButton("Clear", () => _text.Clear()));
        bar.Children.Add(MakeButton("Open log folder", () =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", Gcs.DataDir); } catch { }
        }));
        bar.Children.Add(traffic);

        var dp = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dp.Children.Add(bar);
        dp.Children.Add(_text);
        Content = dp;

        AppLog.LineAdded += OnLine;
        Closed += (_, _) => AppLog.LineAdded -= OnLine;
        Loaded += (_, _) => _text.ScrollToEnd();
    }

    private void OnLine(string line) => Ui(() =>
    {
        _text.AppendText(line + Environment.NewLine);
        if (_text.LineCount > 3000) _text.Text = _text.Text.Substring(_text.Text.Length / 2);
        _text.ScrollToEnd();
    });
}
