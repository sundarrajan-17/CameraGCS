using System.Windows.Controls;
using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

/// <summary>Live values from EPSILON_GLOBAL_STATUS (0x80).</summary>
public sealed class TelemetryPage : FlyoutPage
{
    public override string Title => "Telemetry";

    private readonly TextBlock _mode, _pan, _tilt, _zoom, _focus, _fov, _temps;
    private readonly TextBlock _camPos, _camAlt, _att, _geoAngles, _geoDist, _target, _gps, _unc, _link;
    private readonly TextBlock _flags, _source;

    public TelemetryPage()
    {
        F.LabelWidth = 120;
        F.Section("Data source");
        _source = F.Value("Source");
        F.Section("Gimbal");
        _mode = F.Value("Control mode");
        _pan = F.Value("Pan");
        _tilt = F.Value("Tilt");
        _zoom = F.Value("Zoom / focus pos");
        _focus = F.Value("Focus mode");
        _fov = F.Value("FOV H x V");
        _temps = F.Value("Temp pan/tilt/VP");

        F.Section("Platform");
        _camPos = F.Value("Camera position");
        _camAlt = F.Value("Camera altitude");
        _att = F.Value("Roll/pitch/yaw");

        F.Section("Geo");
        _geoAngles = F.Value("Azimuth / tilt");
        _geoDist = F.Value("Distance");
        _target = F.Value("Target");
        _gps = F.Value("GPS A / B");
        _unc = F.Value("Att. uncertainty");

        F.Section("Link");
        _link = F.Value("Packets");

        F.Section("Status flags");
        _flags = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = System.Windows.TextWrapping.Wrap };
        F.Add(_flags);
    }

    public override void OnOpened()
    {
        if (Gcs.Gimbal.LastStatus != null) OnStatus(Gcs.Gimbal.LastStatus);
    }

    public override void OnStatus(GlobalStatus s)
    {
        var v = Gcs.Gimbal.Version;
        bool sim = v?.IsSimulator == true;
        _source.Text = sim ? "SIMULATOR - simulated values" : v != null ? $"{v.TypeName} (live)" : "live gimbal";
        _source.Foreground = sim ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.ForestGreen;
        _mode.Text = $"{s.Mode} ({(int)s.Mode})";
        _pan.Text = $"{s.PanDeg:0.00}°";
        _tilt.Text = $"{s.TiltDeg:0.00}°";
        _zoom.Text = $"{s.ZoomPosition} / {s.FocusPosition}";
        _focus.Text = s.Has(StatusFlags.ManualFocus) ? "Manual" : "Auto";
        _fov.Text = $"{s.FovHorizontalDeg:0.0}° x {s.FovVerticalDeg:0.0}°";
        _temps.Text = $"{s.TempPanC} / {s.TempTiltC} / {s.TempVideoProcessorC} °C";
        _camPos.Text = s.HasCameraPosition ? $"{s.CamLat:0.000000}, {s.CamLon:0.000000}" : "no position";
        _camAlt.Text = $"{s.CamAltM} m (WGS84)";
        _att.Text = $"{s.ExtRollDeg:0.0}° / {s.ExtPitchDeg:0.0}° / {s.ExtYawDeg:0.0}°";
        _geoAngles.Text = $"{s.GeoAzimuthDeg:0.0}° / {s.GeoTiltDeg:0.0}°";
        _geoDist.Text = $"{s.GeoDistanceM} m" + (s.Has(StatusFlags.SlantRangeMeasured) ? " (LRF)" : " (calc)");
        _target.Text = s.HasTarget ? $"{s.TargetLat:0.000000}, {s.TargetLon:0.000000}" : "-";
        _gps.Text = $"sats {s.SatCountA}/{s.SatCountB}  PDOP {s.PdopA:0.0}/{s.PdopB:0.0}  sig {s.SignalLevelA}%/{s.SignalLevelB}%";
        _unc.Text = $"{s.AttitudeUncertaintyDeg:0.0}°";
        _link.Text = $"rx {Gcs.Gimbal.PacketsReceived}  tx {Gcs.Gimbal.PacketsSent}  bad {Gcs.Gimbal.BadPackets}";
        _flags.Text = string.Join("\n", GlobalStatus.FlagNames.Select(f => (s.Has(f.Flag) ? "■ " : "□ ") + f.Name));
    }
}
