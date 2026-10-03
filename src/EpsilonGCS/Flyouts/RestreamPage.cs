using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Epsilon.Video;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

/// <summary>Configures and controls the RTSP recast of the gimbal video.</summary>
public sealed class RestreamPage : FlyoutPage
{
    public override string Title => "RTSP Restream";

    private readonly ChoiceField _mode;
    private readonly NumField _port;
    private readonly TextField _customUrl, _path, _allowed, _user, _pass, _pushUrl;
    private readonly BoolField _tcp, _klv, _auto;
    private readonly TextBlock _status, _url;
    private readonly TextBox _log;

    public RestreamPage()
    {
        var r = Gcs.Settings.Restream;

        F.Section("Mode");
        _mode = F.Choice("Mode", (int)r.Mode,
            "Serve - this PC is the RTSP server", "Push - send to an RTSP server at the destination");
        F.Note("Serve: the destination opens rtsp://<this PC>:<port>/<path> in VLC, a GCS or a video system. " +
               "Push: this PC publishes the stream to an RTSP server (e.g. MediaMTX) running at the destination.");

        F.Section("Serve settings");
        _customUrl = F.Text("Stream URL", r.CustomUrl,
            "Custom RTSP URL clients will open, e.g. rtsp://192.168.1.10:8554/live. Empty = automatic.");
        F.Note("Custom URL (optional): rtsp://<this PC's IP>:<port>/<path>. When set, its port and path are used " +
               "instead of the two fields below. This PC's addresses: " + string.Join(", ", RtspRestreamer.LocalIPv4Addresses()) +
               ". Leave empty to use rtsp://" + RtspRestreamer.BestLocalIp() + ":<port>/<path>.");
        _port = F.Number("RTSP port", r.RtspPort, 1, 65535);
        _path = F.Text("Stream path", r.Path);
        _allowed = F.Text("Allowed client IPs", r.AllowedIps, "Comma separated IPs or CIDRs. Empty = any client.");
        _user = F.Text("Username", r.Username, "Optional");
        _pass = F.Text("Password", r.Password, "Optional");

        F.Section("Push settings");
        _pushUrl = F.Text("Destination URL", r.PushUrl, "e.g. rtsp://192.168.1.50:8554/epsilon");
        _tcp = F.Check("Use TCP transport", r.PushUseTcp);

        F.Section("Options");
        _klv = F.Check("Include KLV metadata track (experimental)", r.IncludeKlv);
        _auto = F.Check("Start restream automatically", r.AutoStart);
        F.Note("Video is copied without re-encoding: no extra latency or quality loss.");

        F.Buttons(("Start", Start), ("Stop", () => Gcs.StopRestream()), ("Copy URL", CopyUrl));

        F.Section("Status");
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        _url = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 4) };
        _log = new TextBox
        {
            Height = 180, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 10.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
        };
        F.Add(_status);
        F.Add(_url);
        F.Add(_log);

        Gcs.Restreamer.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateStatus));
        AppLog.LineAdded += line =>
        {
            if (!line.Contains("[restream]") && !line.Contains("[ffmpeg]") && !line.Contains("[mediamtx]")) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _log.AppendText(line + "\n");
                if (_log.LineCount > 400) _log.Text = _log.Text.Substring(_log.Text.Length / 2);
                _log.ScrollToEnd();
            }));
        };
    }

    public override void OnOpened() => UpdateStatus();

    private void Save()
    {
        var r = Gcs.Settings.Restream;
        r.Mode = (RestreamMode)_mode.Value;
        r.RtspPort = _port.Int;
        r.Path = string.IsNullOrWhiteSpace(_path.Value) ? "epsilon" : _path.Value.Trim('/');
        r.AllowedIps = _allowed.Value;
        r.Username = _user.Value;
        r.Password = _pass.Value;
        r.CustomUrl = _customUrl.Value;
        r.PushUrl = _pushUrl.Value;
        r.PushUseTcp = _tcp.Value;
        r.IncludeKlv = _klv.Value;
        r.AutoStart = _auto.Value;
        Gcs.Settings.Save();
    }

    private void Start()
    {
        Save();
        var r = Gcs.Settings.Restream;
        if (!Gcs.Restreamer.ToolsAvailable(r.Mode, out var missing))
        {
            MessageBox.Show("The restreamer needs FFmpeg" + (r.Mode == RestreamMode.Serve ? " and MediaMTX" : "") + ".\n\n" +
                            "Missing: " + missing + "\n\nRun tools\\get-tools.ps1 (PowerShell) once to download them.",
                "Epsilon GCS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (r.Mode == RestreamMode.Serve && !string.IsNullOrWhiteSpace(r.CustomUrl))
        {
            if (!RtspRestreamer.TryParseServeUrl(r.CustomUrl, r.RtspPort, out var ep, out var error))
            {
                MessageBox.Show(error, "Epsilon GCS - stream URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Keep the port / path fields in step with the custom URL.
            _port.Value = ep.Port;
            _path.Value = ep.Path;
            r.RtspPort = ep.Port;
            r.Path = ep.Path;
            Gcs.Settings.Save();
        }
        Gcs.StartRestream();
        UpdateStatus();
    }

    private void CopyUrl()
    {
        var url = Gcs.Restreamer.OutputUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Clipboard.SetText(url); } catch { /* clipboard busy */ }
    }

    private void UpdateStatus()
    {
        var st = Gcs.Restreamer.State;
        _status.Text = $"{st}: {Gcs.Restreamer.StatusText}";
        _status.Foreground = st == RestreamState.Running ? Brushes.ForestGreen
            : st == RestreamState.Error ? Brushes.Firebrick : Brushes.DarkGoldenrod;
        _url.Text = string.IsNullOrEmpty(Gcs.Restreamer.OutputUrl) ? "" : "Stream URL: " + Gcs.Restreamer.OutputUrl;
    }
}
