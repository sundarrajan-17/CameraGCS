using Epsilon.Core.Protocol;
using Epsilon.Core.Targets;

namespace Epsilon.Core.Client;

/// <summary>Tracking parameters used whenever a control mode is set (SET_CONTROL_MODE bytes 5-7).</summary>
public sealed class TrackingProfile
{
    public int BoxSize { get; set; } = 64;
    public int Advanced { get; set; }
    public int Options { get; set; }
}

/// <summary>
/// Operator-level camera / gimbal commands. The UI talks to this class instead of building
/// protocol packets itself; state queries read the latest EPSILON_GLOBAL_STATUS held by
/// <see cref="GimbalClient"/>, so every view sees the same telemetry.
/// </summary>
public sealed class GimbalController
{
    public GimbalController(GimbalClient client) => Client = client;

    public GimbalClient Client { get; }
    public TrackingProfile Tracking { get; } = new();

    /// <summary>
    /// Sign of TILT_SPEED for "tilt up". The protocol does not define the direction; on the Epsilon hardware
    /// a positive TILT_SPEED tilts the camera DOWN, so "up" is sent as a negative value (default true).
    /// Applies to the keyboard, the on-screen pad and the gamepad.
    /// </summary>
    public bool InvertTilt { get; set; } = true;

    private int TiltSign(int upPositivePct) => InvertTilt ? -upPositivePct : upPositivePct;

    /// <summary>Raised when a command is dropped because no link is open.</summary>
    public event Action<string> CommandRejected;

    // ------------------------------------------------------------------ state

    public GlobalStatus Status => Client.LastStatus;
    public VersionInfo Version => Client.Version;
    public bool IsLinkOpen => Client.IsOpen;
    public bool IsConnected => Client.IsConnected;

    private bool Has(StatusFlags f) => Client.LastStatus?.Has(f) == true;

    public ControlMode Mode => Client.LastStatus?.Mode ?? ControlMode.NoChange;

    /// <summary>Status older than this is not used for new targets / splashes.</summary>
    public static readonly TimeSpan GeoPointMaxAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The camera's own position: CAM_LATITUDE / CAM_LONGITUDE (bytes 17-24 of EPSILON_GLOBAL_STATUS 0x80),
    /// from the gimbal's GPS. Thread-safe snapshot read, same freshness rule as <see cref="TryGetGeoPoint"/>.
    /// </summary>
    public bool TryGetCameraPosition(out double lat, out double lon, out string reason)
    {
        lat = lon = 0;
        var s = Client.LastStatus;
        if (s == null) { reason = "No status received from the gimbal yet."; return false; }
        var age = DateTime.UtcNow - s.ReceivedUtc;
        if (age > GeoPointMaxAge) { reason = $"Gimbal status is {age.TotalSeconds:0.0} s old - link lost?"; return false; }
        if (!TargetStore.IsValidPosition(s.CamLat, s.CamLon))
        {
            reason = "The gimbal reports no camera position (no GPS fix?).";
            return false;
        }
        lat = s.CamLat;
        lon = s.CamLon;
        reason = null;
        return true;
    }

    /// <summary>
    /// The camera's current geo point: GEO_LATITUDE / GEO_LONGITUDE (bytes 39-46 of EPSILON_GLOBAL_STATUS 0x80),
    /// i.e. where the line of sight meets the ground. Thread-safe (reads one immutable status snapshot).
    /// Returns false with a reason when there is no fresh, valid geo solution.
    /// </summary>
    public bool TryGetGeoPoint(out GeoPoint point, out string reason)
    {
        point = default;
        var s = Client.LastStatus;                       // one snapshot: all fields below come from the same packet
        if (s == null) { reason = "No status received from the gimbal yet."; return false; }
        var age = DateTime.UtcNow - s.ReceivedUtc;
        if (age > GeoPointMaxAge) { reason = $"Gimbal status is {age.TotalSeconds:0.0} s old - link lost?"; return false; }
        if (s.Has(StatusFlags.GeoInactive)) { reason = "GEO is not active on the gimbal (no GPS / GEO solution)."; return false; }
        if (!TargetStore.IsValidPosition(s.TargetLat, s.TargetLon))
        {
            reason = $"The gimbal reports no valid geo point (lat {s.TargetLat:0.000000}, lon {s.TargetLon:0.000000}).";
            return false;
        }
        point = new GeoPoint(s.TargetLat, s.TargetLon, s.GeoDistanceM, s.Has(StatusFlags.SlantRangeMeasured), s.ReceivedUtc);
        reason = null;
        return true;
    }
    public bool IsIrActive => Has(StatusFlags.ActiveCameraIr);
    public bool IsRecording => Has(StatusFlags.VideoRecording);
    public bool IsLaserOn => Has(StatusFlags.LaserPointerOn);
    public bool IsStabilized => Has(StatusFlags.VideoStabilization);
    public bool IsStabilizeOnTrack => Has(StatusFlags.StabilizeOnTrack);
    public bool IsAutoFocus => Client.LastStatus != null && !Has(StatusFlags.ManualFocus);
    public bool IsOsiOn => Has(StatusFlags.OnScreenInfo);

    /// <summary>False only when the gimbal reported that no laser pointer is fitted.</summary>
    public bool HasLaser => Client.Version == null || Client.Version.HasLaserAim;

    // ------------------------------------------------------------------ transport

    public bool Send(Packet p)
    {
        if (!Client.IsOpen)
        {
            CommandRejected?.Invoke($"Not connected - {p.Id} not sent");
            return false;
        }
        Client.Send(p);
        return true;
    }

    // ------------------------------------------------------------------ modes

    /// <summary>Sets a control mode. Tracking modes started this way lock on the cross (pixel 0,0).</summary>
    public void SetMode(ControlMode mode) =>
        Send(Cmd.SetControlMode(mode, 0, 0, Tracking.BoxSize, Tracking.Advanced, Tracking.Options));

    /// <summary>Starts tracking at a video pixel (1-based, in GET_IMAGE_SIZE coordinates).</summary>
    public void TrackAt(ControlMode trackingMode, int pixelX, int pixelY) =>
        Send(Cmd.SetControlMode(trackingMode, pixelX, pixelY, Tracking.BoxSize, Tracking.Advanced, Tracking.Options));

    /// <summary>Re-sends tracking box / advanced / option bits without changing the mode.</summary>
    public void ApplyTrackingProfile() => SetMode(ControlMode.NoChange);

    public void GeoLock(double lat, double lon)
    {
        Send(Cmd.GeoLock(lat, lon));
        Send(Cmd.SetControlMode(ControlMode.GeoLock, 0, 0, Tracking.BoxSize, Tracking.Advanced, Tracking.Options));
    }

    /// <summary>"Center / home" for the gimbal: the configured pilot-view direction.</summary>
    public void Center() => SetMode(ControlMode.PilotView);

    // ------------------------------------------------------------------ continuous motion (RATE_CONTROL)

    /// <param name="tiltPct">Positive = tilt the camera UP (converted to the gimbal's sign convention).</param>
    public void SetPanTilt(int panPct, int tiltPct)
    {
        Client.Rate.PanSpeed = panPct;
        Client.Rate.TiltSpeed = TiltSign(tiltPct);
    }

    public void SetPan(int pct) => Client.Rate.PanSpeed = pct;
    /// <param name="pct">Positive = tilt the camera UP.</param>
    public void SetTilt(int pct) => Client.Rate.TiltSpeed = TiltSign(pct);
    public void SetZoomSpeed(int speed) => Client.Rate.ZoomSpeed = speed;
    public void SetFocusStep(int step) => Client.Rate.FocusStep = step;
    public void SetNudge(int column, int row)
    {
        Client.Rate.NudgeColumn = column;
        Client.Rate.NudgeRow = row;
    }
    public void SetNudgeColumn(int px) => Client.Rate.NudgeColumn = px;
    public void SetNudgeRow(int px) => Client.Rate.NudgeRow = px;

    /// <summary>Stops all manual motion (pan, tilt, zoom, focus, nudge).</summary>
    public void StopMotion() => Client.Rate.StopAll();

    // ------------------------------------------------------------------ camera functions

    public void SetStabilization(bool on) => Send(Cmd.VideoStabilization(on));
    public void ToggleStabilization() => SetStabilization(!IsStabilized);
    public void ToggleStabilizeOnTrack() => Send(Cmd.StabilizeOnTrack(!IsStabilizeOnTrack));

    public void ToggleAutoFocus() => Send(Cmd.FocusMode(manual: IsAutoFocus));
    public void SingleShotFocus() => Send(Cmd.FocusMode(manual: !IsAutoFocus, oneShot: true));

    /// <summary>0 EO zoom, 1 IR main, 2 EO spotter, 3 IR secondary.</summary>
    public void SelectCamera(int camera) => Send(Cmd.SetCameraOrder(camera));
    public void ToggleCamera() => SelectCamera(IsIrActive ? 0 : 1);

    public void ToggleRecording() => Send(Cmd.VideoRecording(IsRecording ? 2 : 1));
    public void Snapshot() => Send(Cmd.DoSnapshot());
    public void SetLaser(bool on) => Send(Cmd.LaserPointer(on));
    public void Nuc() => Send(Cmd.DoFfc());
    public void SetPip(bool on) => Send(Cmd.PipSettings(on ? 1 : 0, 0));
    public void SetOnScreenInfo(int mask) => Send(Cmd.OnScreenInformation(mask));

    public void SetMti(int mode, int sensitivity) => Send(Cmd.MtiParameters(mode, sensitivity));
    public void DisableMti() => Send(Cmd.MtiParameters(0, 0));
    public void ResetMti(int mode) => Send(Cmd.MtiParameters(mode, 0, 255, true));
    public void ShiftMti() => Send(Cmd.ShiftSelectedTrackMti());
    public void DesignateMti() => Send(Cmd.DesignateMti());

    public void SetEnhancement(int filter, int sharpening, int blend, int strength, int denoise, bool defaults) =>
        Send(Cmd.VideoEnhancement(filter, sharpening, blend, strength, denoise, IsIrActive ? 1 : 0, defaults));
}

/// <summary>A geo point reported by the gimbal, with the range and time it was measured.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude, int RangeM, bool RangeByLrf, DateTime TimeUtc);
