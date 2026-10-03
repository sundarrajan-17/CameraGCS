using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Epsilon.Core.Protocol;
using Epsilon.Video;
using EpsilonGCS.Controls;
using EpsilonGCS.Dialogs;
using EpsilonGCS.Flyouts;
using EpsilonGCS.Services;

namespace EpsilonGCS;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FlyoutPage> _pages = new();
    private FlyoutPage _activePage;
    private string _activePageKey;

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _joyTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _enhancementDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _mtiDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _zoomPulse = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool _ready;
    private GlobalStatus _status;
    private DateTime _lastMapFollow = DateTime.MinValue;
    private bool _mapHadPosition;

    // local UI state (not reported in STATUS_FLAGS)
    private bool _pipOn;
    private bool _clickToTrack;
    private bool _nudgeMode;
    private bool _joystickOn;
    private bool _mtiEnabled;
    private int _enhancementFilter;
    private ushort _lastJoyButtons;
    private readonly HashSet<string> _heldPads = new();

    // displayed image size for click-to-track pixel mapping (GET_IMAGE_SIZE 0x39)
    private int _imageW = 1280, _imageH = 720;
    private long _lastVideoPackets;
    private bool _seeking;

    // fullscreen video
    private bool _fullscreen;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private GridLength _restoreBottomRow, _restoreMapColumn;

    private static readonly string[] EnhancementFilters = { "NONE", "CLAHE", "LAP" };

    public MainWindow()
    {
        InitializeComponent();

        var s = Gcs.Settings;
        Map.CacheDirectory = Path.Combine(Gcs.DataDir, "tiles");
        Map.SetLayer(FindLayer(s.MapLayerName));
        Map.SetView(s.MapLat, s.MapLon, s.MapZoom);
        Map.CursorMoved += OnMapCursor;
        Map.MapRightClicked += OnMapRightClick;
        Map.UserPanned += () => SetFollow(false);
        Map.MarkerClicked += OnMapMarkerClicked;
        Gcs.Targets.Changed += UpdateTargetMarkers;
        SetFollow(s.MapFollowGimbal);
        UpdateMapToolButtons();

        SpeedSlider.Value = s.SpeedPercent;
        SpeedText.Text = s.SpeedPercent + "%";
        BtnRoll.IsActive = (s.ControlOptions & ControlOptionBits.RollHorizonAlignment) != 0;

        if (s.WindowWidth > 400) Width = s.WindowWidth;
        if (s.WindowHeight > 300) Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        var g = Gcs.Gimbal;
        g.StatusReceived += st => Dispatcher.BeginInvoke(new Action(() => OnStatus(st)));
        g.ConnectionChanged += c => Dispatcher.BeginInvoke(new Action(() => OnConnectionChanged(c)));
        g.VersionReceived += v => Dispatcher.BeginInvoke(new Action(() =>
        {
            BadgeSim.Visibility = v.IsSimulator ? Visibility.Visible : Visibility.Collapsed;
            // Grey out LSR (as in Epsilon Control) when the gimbal reports no laser pointer fitted.
            BtnLsr.IsEnabled = v.HasLaserAim;
            BtnLsr.ToolTip = v.HasLaserAim ? "Laser pointer" : "No laser pointer fitted on this gimbal";
            if (v.IsSimulator) AppLog.Write("Connected to the SIMULATOR - all telemetry and video are simulated");
        }));
        g.ImageSizeReceived += (w, h) =>
        {
            if (w > 0 && h > 0) { _imageW = w; _imageH = h; }
        };
        g.PacketReceived += p =>
        {
            if (p.IsZeroLength || p.Id == MessageId.GlobalStatus) return;
            Dispatcher.BeginInvoke(new Action(() => _activePage?.OnSetting(p)));
        };
        Gcs.Restreamer.StateChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateVideoInfo));

        _uiTimer.Tick += (_, _) =>
        {
            try { Gcs.Video.CheckHealth(); } catch (Exception ex) { AppLog.Write("Video watchdog: " + ex.Message); }
            UpdateVideoInfo();
            if (_activePage is VideoPlayerPage vp) vp.UpdateInfo();
        };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackBar();
        _joyTimer.Tick += (_, _) => PollJoystick();
        _enhancementDebounce.Tick += (_, _) => { _enhancementDebounce.Stop(); SendEnhancement(false); };
        _mtiDebounce.Tick += (_, _) => { _mtiDebounce.Stop(); if (_mtiEnabled) SendMti(); };
        _zoomPulse.Tick += (_, _) => { _zoomPulse.Stop(); Gcs.Controller.SetZoomSpeed(0); };

        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        VideoOverlay.PreviewKeyDown += OnKeyDown;
        VideoOverlay.PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => StopManualMotion();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // =====================================================================================
    // Startup / shutdown
    // =====================================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Gcs.Video.Initialize();
            // Attach the player to the view BEFORE anything is played, so LibVLC renders into the VideoView's window.
            VideoView.MediaPlayer = Gcs.Video.Player;
            AppLog.Write($"[video] video view attached: {VideoView.ActualWidth:0}x{VideoView.ActualHeight:0}, visible={VideoView.IsVisible}");
            if (VideoView.ActualWidth < 2 || VideoView.ActualHeight < 2)
                AppLog.Write("[video] WARNING: video view has no size yet - the picture appears once the layout gives it space");
            Gcs.Video.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Video init failed: " + ex.Message);
            VideoStatusText.Text = "Status: video error - " + ex.Message;
        }

        if (Gcs.Settings.AutoConnect) Gcs.ConnectGimbal();
        if (Gcs.Settings.Restream.AutoStart) Gcs.StartRestream();

        UpdateConnectMenu();
        OnConnectionChanged(false);
        _uiTimer.Start();
        _playbackTimer.Start();
        _ready = true;
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        StopManualMotion();
        _uiTimer.Stop();
        _playbackTimer.Stop();
        _joyTimer.Stop();
        var s = Gcs.Settings;
        s.MapLat = Map.CenterLat;
        s.MapLon = Map.CenterLon;
        s.MapZoom = Map.Zoom;
        s.MapLayerName = Map.Layer.Name;
        s.SpeedPercent = (int)SpeedSlider.Value;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        s.Save();
        try { VideoView.MediaPlayer = null; } catch { /* ignore */ }
    }

    // =====================================================================================
    // Status from the gimbal
    // =====================================================================================

    private void OnStatus(GlobalStatus s)
    {
        _status = s;

        // control mode buttons
        var mode = s.Mode;
        BtnRate.IsActive = mode == ControlMode.Rate;
        BtnAid.IsActive = mode == ControlMode.RateAid;
        BtnScn.IsActive = mode == ControlMode.TrackScene;
        BtnVhcl.IsActive = mode == ControlMode.TrackVehicle || mode == ControlMode.TrackStationary || mode == ControlMode.TrackStatic;
        BtnGeo.IsActive = mode == ControlMode.GeoLock;
        BtnPilot.IsActive = mode == ControlMode.PilotView;
        BtnStow.IsActive = mode == ControlMode.Stow;

        BtnStab.IsActive = s.Has(StatusFlags.VideoStabilization);
        BtnStabTrack.IsActive = s.Has(StatusFlags.StabilizeOnTrack);
        BtnAf.IsActive = !s.Has(StatusFlags.ManualFocus);
        BtnInfo.IsActive = s.Has(StatusFlags.OnScreenInfo);
        bool ir = s.Has(StatusFlags.ActiveCameraIr);
        BtnIr.IsActive = ir;
        BtnEo.IsActive = !ir;
        BtnRec.IsActive = s.Has(StatusFlags.VideoRecording);
        BtnLsr.IsActive = s.Has(StatusFlags.LaserPointerOn);
        bool secondary = s.Has(StatusFlags.SecondaryCameraActive);
        EnhCameraText.Text = ir ? (secondary ? "IR Secondary" : "IR Main") : (secondary ? "EO Spotter" : "EO Zoom");

        // status bar badges
        bool vpReady = s.Has(StatusFlags.VideoProcessorReady);
        bool initDone = s.Has(StatusFlags.GimbalInitDone);
        BadgeVp.Background = vpReady && initDone ? Res("ActiveGreen") : Res("WarnYellow");
        BadgeVpText.Text = vpReady ? (initDone ? "OK" : "Init...") : "Not ready";
        bool fix = s.Has(StatusFlags.GeoGpsFix), cal = s.Has(StatusFlags.GeoGpsCalibrationOk);
        BadgeGps.Background = fix && cal ? Res("ActiveGreen") : fix ? Res("WarnYellow") : Res("AlarmRed");
        BadgeGps.ToolTip = $"GPS fix: {fix}, calibration OK: {cal}, satellites {s.SatCountA}/{s.SatCountB}";
        BadgeRec.Visibility = s.Has(StatusFlags.VideoRecording) ? Visibility.Visible : Visibility.Collapsed;
        BadgeError.Visibility = s.Has(StatusFlags.AnyError) || s.Has(StatusFlags.NewError) ? Visibility.Visible : Visibility.Collapsed;
        if (s.Has(StatusFlags.GyroBiasInProgress)) BadgeVpText.Text = "Gyro bias";

        StatusMiddle.Text = $"{mode}   Pan {s.PanDeg:0.0}°   Tilt {s.TiltDeg:0.0}°   Zoom {s.ZoomPosition}   " +
                            $"FOV {s.FovHorizontalDeg:0.0}°   " + (s.GeoDistanceM > 0 ? $"Range {s.GeoDistanceM} m" : "");

        // map
        if (s.HasCameraPosition)
        {
            Map.SetAircraft(s.CamLat, s.CamLon, s.ExtYawDeg);
            if (!_mapHadPosition)
            {
                _mapHadPosition = true;
                if (Map.Zoom < 13) Map.SetView(s.CamLat, s.CamLon, 15);
            }
            else if (Gcs.Settings.MapFollowGimbal && (DateTime.UtcNow - _lastMapFollow).TotalMilliseconds > 900)
            {
                _lastMapFollow = DateTime.UtcNow;
                Map.SetView(s.CamLat, s.CamLon);
            }
        }
        if (s.HasTarget && !s.Has(StatusFlags.GeoInactive)) Map.SetTarget(s.TargetLat, s.TargetLon);
        else Map.SetTarget(null, null);
        Map.SetFootprint(ComputeFootprint(s));

        _activePage?.OnStatus(s);
    }

    /// <summary>Camera view polygon from the gimbal's position, line of sight and field of view.</summary>
    private static (double Lat, double Lon)[] ComputeFootprint(GlobalStatus s)
    {
        if (!s.HasCameraPosition || s.FovHorizontalDeg <= 0) return null;
        // Absolute LOS from the GEO solution when the gimbal provides it, otherwise vehicle yaw + gimbal angles.
        bool geoAngles = !s.Has(StatusFlags.GeoInactive) && (s.GeoAzimuthDeg != 0 || s.GeoTiltDeg != 0);
        double az = geoAngles ? s.GeoAzimuthDeg : s.ExtYawDeg + s.PanDeg;
        double el = geoAngles ? s.GeoTiltDeg : s.TiltDeg;
        int dted = Gcs.Gimbal.Rate.DtedHeight;
        double? h = Epsilon.Core.Geo.CameraFootprint.HeightAboveGround(s.GeoDistanceM, el, s.CamAltM, dted != 0 ? (double?)dted : null);
        if (!h.HasValue) return null;
        double vfov = s.FovVerticalDeg > 0 ? s.FovVerticalDeg : s.FovHorizontalDeg * 9 / 16;
        return Epsilon.Core.Geo.CameraFootprint.Compute(s.CamLat, s.CamLon, h.Value, az, el, s.FovHorizontalDeg, vfov);
    }

    private void OnConnectionChanged(bool connected)
    {
        BadgeLink.Background = connected ? Res("ActiveGreen") : Res("AlarmRed");
        BadgeLinkText.Text = connected ? "Connected" : (Gcs.Gimbal.IsOpen ? "No reply" : "Disconnected");
        if (!connected)
        {
            BadgeVp.Background = (Brush)new BrushConverter().ConvertFrom("#D8D8D8");
            BadgeGps.Background = (Brush)new BrushConverter().ConvertFrom("#D8D8D8");
            BadgeVpText.Text = "-";
        }
        StatusRight.Text = Gcs.Gimbal.TransportDescription;
        UpdateConnectMenu();
    }

    private void UpdateConnectMenu() => MenuConnect.Header = Gcs.Gimbal.IsOpen ? "_Disconnect" : "_Connect";

    private void UpdateVideoInfo()
    {
        var v = Gcs.Video;
        double mbps = v.Fanout.SampleBitrateMbps();
        long packets = v.Fanout.Packets;
        bool flowing = packets != _lastVideoPackets;
        _lastVideoPackets = packets;

        bool udp = Gcs.Settings.VideoInput == VideoInputKind.UdpMpegTs;
        bool hasVideo = v.IsPlayback || v.HasPicture;
        NoVideoText.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible;
        NoVideoText.Text = udp
            ? (flowing ? $"RECEIVING VIDEO\nstarting player ({v.PlayerState})" : $"NO VIDEO\nwaiting on UDP {Gcs.Settings.VideoPort}")
            : $"NO VIDEO\n{v.PlayerState}";
        NoVideoText.TextAlignment = TextAlignment.Center;

        VideoStatusText.Text = !v.IsPlayback && udp && !flowing ? "No signal" : v.PlayerState;
        var ts = v.Fanout.Inspector;
        KlvPacketsText.Text = ts.KlvPackets.ToString();
        KlvPacketsText.ToolTip = ts.HasKlvTrack ? "KLV metadata track found in the stream" : "No KLV track found in the stream (yet)";
        VideoStatsText.Text = v.IsPlayback
            ? v.CurrentSource
            : udp ? $"{(ts.VideoCodec.Length > 0 ? ts.VideoCodec + "  " : "")}{mbps:0.00} Mbit/s   {v.CurrentSource}" : v.CurrentSource;

        var r = Gcs.Restreamer;
        VideoRestreamText.Text = r.State == RestreamState.Stopped ? "RTSP: off" : $"RTSP: {r.StatusText}";
        VideoRestreamText.Foreground = r.State == RestreamState.Running ? Brushes.ForestGreen
            : r.State == RestreamState.Error ? Brushes.Firebrick : (Brush)Res("LinkBlue");
    }

    private void UpdatePlaybackBar()
    {
        var v = Gcs.Video;
        VideoTimeline.IsEnabled = v.IsPlayback;
        BtnPlayPause.Visibility = v.IsPlayback ? Visibility.Visible : Visibility.Collapsed;
        BtnPlayPause.Content = v.PlayerState == "Paused" ? "▶" : "❚❚";
        if (_seeking) return;
        VideoTimeline.Value = v.IsPlayback ? v.Position * 1000 : 1000;
        PlaybackTimeText.Text = v.IsPlayback ? $"{v.Time:hh\\:mm\\:ss} / {v.Length:hh\\:mm\\:ss}" : "LIVE";
    }

    private void VideoTimeline_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = Gcs.Video.IsPlayback;

    private void VideoTimeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        // IsMoveToPointEnabled has already moved the thumb; apply after the slider updated its value
        Dispatcher.BeginInvoke(new Action(() => Gcs.Video.Seek(VideoTimeline.Value / 1000.0)), DispatcherPriority.Input);
    }

    private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => Gcs.Video.TogglePause();

    private void BtnLive_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write($"[video] LIVE clicked - view {VideoView.ActualWidth:0}x{VideoView.ActualHeight:0}, " +
                     $"player attached={ReferenceEquals(VideoView.MediaPlayer, Gcs.Video.Player)}, restream={Gcs.Restreamer.State}");
        // Re-attach if the view lost its player (e.g. after a shutdown attempt); harmless when already attached.
        if (!ReferenceEquals(VideoView.MediaPlayer, Gcs.Video.Player)) VideoView.MediaPlayer = Gcs.Video.Player;
        Gcs.Video.GoLive();
        UpdatePlaybackBar();
    }

    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>Video only, borderless, maximised. F11 or double-click the video to toggle.</summary>
    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        var hide = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        if (_fullscreen)
        {
            CloseFlyout();
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreBottomRow = BottomRow.Height;
            _restoreMapColumn = MapColumn.Width;
            BottomRow.Height = new GridLength(0);
            MapColumn.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximise to cover the taskbar
            WindowState = WindowState.Maximized;
        }
        else
        {
            BottomRow.Height = _restoreBottomRow;
            MapColumn.Width = _restoreMapColumn;
            WindowStyle = _restoreStyle;
            WindowState = _restoreState;
        }
        MainMenu.Visibility = hide;
        RailScroll.Visibility = hide;
        SideTabsScroll.Visibility = hide;
        StatusBarBorder.Visibility = hide;
        MapPane.Visibility = _fullscreen || !MenuMap.IsChecked ? Visibility.Collapsed : Visibility.Visible;
        MapSplitter.Visibility = MapPane.Visibility;
        BottomPanels.Visibility = hide;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    // =====================================================================================
    // Rail buttons
    // =====================================================================================

    private int SpeedPct => (int)SpeedSlider.Value;

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && byte.TryParse(fe.Tag?.ToString(), out var m))
            SetMode((ControlMode)m);
    }

    // Tracking modes started from a button track at the cross (pixel 0,0).
    private static void SetMode(ControlMode mode) => Gcs.Controller.SetMode(mode);

    private void BtnStab_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ToggleStabilization();

    private void BtnStabTrack_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ToggleStabilizeOnTrack();

    private void BtnRoll_Click(object sender, RoutedEventArgs e)
    {
        var s = Gcs.Settings;
        s.ControlOptions ^= ControlOptionBits.RollHorizonAlignment;
        BtnRoll.IsActive = (s.ControlOptions & ControlOptionBits.RollHorizonAlignment) != 0;
        s.Save();
        Gcs.SyncTrackingProfile();
        Gcs.Controller.ApplyTrackingProfile();
    }

    private void BtnAf_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ToggleAutoFocus();

    private void BtnSf_Click(object sender, RoutedEventArgs e) => Gcs.Controller.SingleShotFocus();

    private void BtnInfo_Click(object sender, RoutedEventArgs e)
    {
        var s = Gcs.Settings;
        bool on = _status?.Has(StatusFlags.OnScreenInfo) ?? true;
        s.OsiMask = on ? s.OsiMask & ~OsiBits.Global : s.OsiMask | OsiBits.Global;
        s.Save();
        Gcs.Controller.SetOnScreenInfo(s.OsiMask);
    }

    private void BtnIr_Click(object sender, RoutedEventArgs e) => Gcs.Controller.SelectCamera(1);
    private void BtnEo_Click(object sender, RoutedEventArgs e) => Gcs.Controller.SelectCamera(0);

    private static void ToggleCamera() => Gcs.Controller.ToggleCamera();

    private void BtnRec_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ToggleRecording();

    private void BtnSnap_Click(object sender, RoutedEventArgs e) => Gcs.Controller.Snapshot();

    private void BtnPip_Click(object sender, RoutedEventArgs e)
    {
        _pipOn = !_pipOn;
        BtnPip.IsActive = _pipOn;
        Gcs.Controller.SetPip(_pipOn);
    }

    private void BtnTrack_Click(object sender, RoutedEventArgs e)
    {
        _clickToTrack = !_clickToTrack;
        BtnTrack.IsActive = _clickToTrack;
        TrackModeText.Visibility = _clickToTrack ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlay.Cursor = _clickToTrack ? Cursors.Cross : null;
        if (!_clickToTrack) TrackHint.Visibility = Visibility.Collapsed;
    }

    private void BtnLsr_Click(object sender, RoutedEventArgs e)
    {
        if (!Gcs.Controller.HasLaser) return;
        bool on = Gcs.Controller.IsLaserOn;
        if (!on && Gcs.Settings.ConfirmLaser &&
            MessageBox.Show(this, "Switch the laser pointer ON?\n\nMake sure the laser is pointing in a safe direction.",
                "Laser pointer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Gcs.Controller.SetLaser(!on);
    }

    private void BtnSpeed_Click(object sender, RoutedEventArgs e)
    {
        int cur = SpeedPct;
        SpeedSlider.Value = cur < 25 ? 25 : cur < 50 ? 50 : cur < 100 ? 100 : 25;
    }

    private void BtnNudge_Click(object sender, RoutedEventArgs e)
    {
        _nudgeMode = !_nudgeMode;
        BtnNudge.IsActive = _nudgeMode;
        StopManualMotion();
    }

    private void BtnJoy_Click(object sender, RoutedEventArgs e)
    {
        if (!_joystickOn && XInputJoystick.Read() == null)
        {
            MessageBox.Show(this, "No XInput (Xbox-compatible) gamepad found.", "J.STICK", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _joystickOn = !_joystickOn;
        BtnJoy.IsActive = _joystickOn;
        if (_joystickOn) _joyTimer.Start();
        else { _joyTimer.Stop(); StopManualMotion(); }
    }

    private void BtnNuc_Click(object sender, RoutedEventArgs e) => Gcs.Controller.Nuc();

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedText == null || BtnSpeed == null) return;
        SpeedText.Text = $"{(int)e.NewValue}%";
        BtnSpeed.IsActive = (int)e.NewValue != 50;
        if (Gcs.Settings != null) Gcs.Settings.SpeedPercent = (int)e.NewValue;
    }

    // =====================================================================================
    // Manual motion: keyboard, pad buttons, gamepad
    // =====================================================================================

    private void ApplyMotion(string action, bool pressed)
    {
        var g = Gcs.Controller;
        int speed = SpeedPct;
        int nudge = Gcs.Settings.NudgeStepPx;
        int zoom = Math.Max(1, (int)Math.Round(8 * speed / 100.0));
        switch (action)
        {
            case "left":
                if (_nudgeMode) g.SetNudgeColumn(pressed ? -nudge : 0); else g.SetPan(pressed ? -speed : 0);
                break;
            case "right":
                if (_nudgeMode) g.SetNudgeColumn(pressed ? nudge : 0); else g.SetPan(pressed ? speed : 0);
                break;
            case "up":
                if (_nudgeMode) g.SetNudgeRow(pressed ? -nudge : 0); else g.SetTilt(pressed ? speed : 0);
                break;
            case "down":
                if (_nudgeMode) g.SetNudgeRow(pressed ? nudge : 0); else g.SetTilt(pressed ? -speed : 0);
                break;
            case "zoom-": g.SetZoomSpeed(pressed ? -zoom : 0); break;
            case "zoom+": g.SetZoomSpeed(pressed ? zoom : 0); break;
            case "focus-": g.SetFocusStep(pressed ? -2 : 0); break;
            case "focus+": g.SetFocusStep(pressed ? 2 : 0); break;
        }
    }

    private void StopManualMotion()
    {
        _heldPads.Clear();
        Gcs.Controller?.StopMotion();
    }

    private void PadCenter_Click(object sender, RoutedEventArgs e) => Gcs.Controller.Center();

    private void Pad_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && b.Tag is string action)
        {
            _heldPads.Add(action);
            ApplyMotion(action, true);
            b.CaptureMouse();
        }
    }

    private void Pad_Up(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && b.Tag is string action)
        {
            _heldPads.Remove(action);
            ApplyMotion(action, false);
            b.ReleaseMouseCapture();
        }
    }

    private void Pad_Lost(object sender, MouseEventArgs e)
    {
        if (sender is Button b && b.Tag is string action && _heldPads.Remove(action))
            ApplyMotion(action, false);
    }

    private static bool IsTyping() =>
        Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox ||
        Keyboard.FocusedElement is ComboBoxItem || Keyboard.FocusedElement is MenuItem;

    private static string KeyAction(Key k) => k switch
    {
        Key.Left => "left",
        Key.Right => "right",
        Key.Up => "up",
        Key.Down => "down",
        Key.Z => "zoom-",
        Key.X => "zoom+",
        Key.F => "focus-",
        Key.G => "focus+",
        _ => null,
    };

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTyping() || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var action = KeyAction(e.Key);
        if (action != null)
        {
            ApplyMotion(action, true);
            e.Handled = true;
            return;
        }
        if (e.IsRepeat) return;
        switch (e.Key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.F5: Gcs.Video.Resync(); break;
            case Key.Space: SetMode(Gcs.Settings.ClickTrackMode); break;
            case Key.Escape: SetMode(ControlMode.Rate); break;
            case Key.D1: SetMode(ControlMode.Rate); break;
            case Key.D2: SetMode(ControlMode.RateAid); break;
            case Key.D3: SetMode(ControlMode.TrackScene); break;
            case Key.D4: SetMode(ControlMode.TrackVehicle); break;
            case Key.D5: SetMode(ControlMode.GeoLock); break;
            case Key.D6: SetMode(ControlMode.PilotView); break;
            case Key.D7: SetMode(ControlMode.Stow); break;
            case Key.I: ToggleCamera(); break;
            case Key.S: Gcs.Controller.Snapshot(); break;
            case Key.R: BtnRec_Click(this, null); break;
            case Key.N: Gcs.Controller.Nuc(); break;
            case Key.C: Gcs.Controller.Center(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var action = KeyAction(e.Key);
        if (action == null) return;
        ApplyMotion(action, false);
        e.Handled = !IsTyping();
    }

    private void PollJoystick()
    {
        var pad = XInputJoystick.Read();
        var g = Gcs.Controller;
        if (pad == null)
        {
            g.StopMotion();
            return;
        }
        var p = pad.Value;
        double scale = SpeedPct / 100.0;
        int x = XInputJoystick.Axis(p.ThumbLX), y = XInputJoystick.Axis(p.ThumbLY);
        if (_nudgeMode)
        {
            g.SetNudge((int)Math.Round(x / 100.0 * Gcs.Settings.NudgeStepPx * 2),
                       (int)Math.Round(-y / 100.0 * Gcs.Settings.NudgeStepPx * 2));
            g.SetPanTilt(0, 0);
        }
        else
        {
            g.SetPanTilt((int)Math.Round(x * scale), (int)Math.Round(y * scale));
        }
        int zoom = (p.RightTrigger - p.LeftTrigger) * 8 / 255;
        g.SetZoomSpeed(zoom);
        bool lb = (p.Buttons & XInputJoystick.ButtonLeftShoulder) != 0;
        bool rb = (p.Buttons & XInputJoystick.ButtonRightShoulder) != 0;
        g.SetFocusStep(lb ? -2 : rb ? 2 : 0);

        ushort pressed = (ushort)(p.Buttons & ~_lastJoyButtons);
        _lastJoyButtons = p.Buttons;
        if ((pressed & XInputJoystick.ButtonA) != 0) SetMode(Gcs.Settings.ClickTrackMode);
        if ((pressed & XInputJoystick.ButtonB) != 0) SetMode(ControlMode.Rate);
        if ((pressed & XInputJoystick.ButtonX) != 0) Gcs.Controller.Snapshot();
        if ((pressed & XInputJoystick.ButtonY) != 0) ToggleCamera();
    }

    // =====================================================================================
    // Video overlay: click-to-track, wheel zoom
    // =====================================================================================

    /// <summary>Maps a point on the overlay to video pixel coordinates (1-based), honouring letterboxing.</summary>
    private bool TryMapToVideoPixel(Point p, out int px, out int py, out Rect videoRect)
    {
        double w = VideoOverlay.ActualWidth, h = VideoOverlay.ActualHeight;
        px = py = 0;
        videoRect = Rect.Empty;
        if (w <= 0 || h <= 0 || _imageW <= 0 || _imageH <= 0) return false;
        double aspect = (double)_imageW / _imageH;
        double dw = w, dh = h;
        if (w / h > aspect) dw = h * aspect; else dh = w / aspect;
        videoRect = new Rect((w - dw) / 2, (h - dh) / 2, dw, dh);
        if (!videoRect.Contains(p)) return false;
        px = (int)Math.Clamp(Math.Round((p.X - videoRect.X) / dw * _imageW) + 1, 1, _imageW);
        py = (int)Math.Clamp(Math.Round((p.Y - videoRect.Y) / dh * _imageH) + 1, 1, _imageH);
        return true;
    }

    private void VideoOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_clickToTrack)
        {
            if (e.ClickCount == 2) ToggleFullscreen();
            return;
        }
        if (!TryMapToVideoPixel(e.GetPosition(VideoOverlay), out int px, out int py, out _)) return;
        var s = Gcs.Settings;
        Gcs.Controller.TrackAt(s.ClickTrackMode, px, py);
        AppLog.Write($"Track {s.ClickTrackMode} at pixel {px},{py}");
    }

    private void VideoOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_clickToTrack) return;
        var p = e.GetPosition(VideoOverlay);
        if (!TryMapToVideoPixel(p, out _, out _, out var rect))
        {
            TrackHint.Visibility = Visibility.Collapsed;
            return;
        }
        double size = Math.Max(16, Gcs.Settings.TrackBoxSize * rect.Width / _imageW);
        TrackHint.Width = TrackHint.Height = size;
        Canvas.SetLeft(TrackHint, p.X - size / 2);
        Canvas.SetTop(TrackHint, p.Y - size / 2);
        TrackHint.Visibility = Visibility.Visible;
    }

    private void VideoOverlay_MouseLeave(object sender, MouseEventArgs e) => TrackHint.Visibility = Visibility.Collapsed;

    private void VideoOverlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        int zoom = Math.Max(1, (int)Math.Round(8 * SpeedPct / 100.0));
        Gcs.Controller.SetZoomSpeed(e.Delta > 0 ? zoom : -zoom);
        _zoomPulse.Stop();
        _zoomPulse.Start();
        e.Handled = true;
    }

    // =====================================================================================
    // MTI / Enhancement panels
    // =====================================================================================

    private int MtiModeValue => MtiMode.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var v) ? v : 1;

    /// <summary>Slider: 1 = Low ... 10 = High; protocol: 1 = highest ... 10 = lowest.</summary>
    private int MtiSensitivityValue => 11 - (int)MtiSensitivity.Value;

    private void SendMti() => Gcs.Controller.SetMti(MtiModeValue, MtiSensitivityValue);

    private void BtnMtiEnable_Click(object sender, RoutedEventArgs e)
    {
        _mtiEnabled = !_mtiEnabled;
        BtnMtiEnable.Content = _mtiEnabled ? "Disable" : "Enable";
        if (_mtiEnabled) SendMti();
        else Gcs.Controller.DisableMti();
    }

    private void BtnMtiShift_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ShiftMti();
    private void BtnMtiDesignate_Click(object sender, RoutedEventArgs e) => Gcs.Controller.DesignateMti();
    private void BtnMtiReset_Click(object sender, RoutedEventArgs e) => Gcs.Controller.ResetMti(MtiModeValue);

    private void MtiSensitivity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _mtiDebounce.Stop();
        _mtiDebounce.Start();
    }

    private void BtnEnhFilter_Click(object sender, RoutedEventArgs e)
    {
        _enhancementFilter = (_enhancementFilter + 1) % EnhancementFilters.Length;
        BtnEnhFilter.Content = EnhancementFilters[_enhancementFilter];
        SendEnhancement(false);
    }

    private void BtnEnhReset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        EnhSharpening.Value = 0;
        EnhDenoise.Value = 0;
        EnhStrength.Value = 0;
        EnhBlend.Value = 0;
        _enhancementFilter = 0;
        BtnEnhFilter.Content = EnhancementFilters[0];
        _ready = true;
        SendEnhancement(true);
    }

    private void Enhancement_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _enhancementDebounce.Stop();
        _enhancementDebounce.Start();
    }

    private void SendEnhancement(bool defaults)
    {
        Gcs.Controller.SetEnhancement(_enhancementFilter, (int)EnhSharpening.Value, (int)EnhBlend.Value,
            (int)EnhStrength.Value, (int)EnhDenoise.Value, defaults);
    }

    // =====================================================================================
    // Map
    // =====================================================================================

    private void SetFollow(bool follow)
    {
        Gcs.Settings.MapFollowGimbal = follow;
        MapFollowButton.BorderBrush = follow ? Res("AccentBlue") : Brushes.Transparent;
    }

    private void UpdateMapToolButtons()
    {
        MapRulerButton.BorderBrush = Map.Tool == MapTool.Ruler ? Res("AccentBlue") : Brushes.Transparent;
        MapPoiButton.BorderBrush = Map.Tool == MapTool.Poi ? Res("AccentBlue") : Brushes.Transparent;
        MapPanButton.BorderBrush = Map.Tool == MapTool.Pan ? Res("AccentBlue") : Brushes.Transparent;
    }

    private static MapLayer FindLayer(string name)
    {
        var custom = Gcs.Settings.TileUrl;
        if (name == "Custom" && !string.IsNullOrWhiteSpace(custom))
            return new MapLayer("Custom", custom, "Custom tile server", 19);
        return MapLayer.BuiltIn.FirstOrDefault(l => l.Name == name) ?? MapLayer.BuiltIn[0];
    }

    private void OnMapCursor(double lat, double lon)
    {
        string text = $"Lat {lat:0.000000}   Lon {lon:0.000000}";
        if (_status != null && _status.HasCameraPosition)
        {
            double d = MapControl.DistanceMeters(_status.CamLat, _status.CamLon, lat, lon);
            text += d >= 1000 ? $"   ({d / 1000:0.00} km from gimbal)" : $"   ({d:0} m from gimbal)";
        }
        MapCursorText.Text = text;
    }

    private void OnMapRightClick(double lat, double lon, Point p)
    {
        var menu = new ContextMenu();
        var geo = new MenuItem { Header = $"Geo lock here  ({lat:0.00000}, {lon:0.00000})" };
        geo.Click += (_, _) =>
        {
            Gcs.Controller.GeoLock(lat, lon);
            AppLog.Write($"Geo lock {lat:0.000000}, {lon:0.000000}");
        };
        var copy = new MenuItem { Header = "Copy coordinates" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.000000}, {1:0.000000}", lat, lon)); }
            catch { /* clipboard busy */ }
        };
        var center = new MenuItem { Header = "Center map here" };
        center.Click += (_, _) => { SetFollow(false); Map.SetView(lat, lon); };
        var home = Map.Home;
        var centerHome = new MenuItem
        {
            Header = home.HasValue ? $"Center on home  ({home.Value.Lat:0.00000}, {home.Value.Lon:0.00000})" : "Center on home (no position yet)",
            IsEnabled = home.HasValue,
        };
        centerHome.Click += (_, _) => { SetFollow(false); Map.SetView(home.Value.Lat, home.Value.Lon); };
        var setHome = new MenuItem { Header = "Set home here" };
        setHome.Click += (_, _) => { Map.SetHome(lat, lon); AppLog.Write($"Home set to {lat:0.000000}, {lon:0.000000}"); };
        var addTarget = new MenuItem { Header = $"Add target here  ({lat:0.00000}, {lon:0.00000})" };
        addTarget.Click += (_, _) =>
        {
            var t = Gcs.Targets.AddTarget(lat, lon, Epsilon.Core.Targets.TargetSource.Map);
            AppLog.Write($"[targets] {t.Name} added on the map at {lat:0.000000}, {lon:0.000000}");
            if (_activePageKey != "Targets") OpenPage("Targets");
            TargetsPageInstance?.SelectTarget(t.Id);
            Map.SetSelectedMarker(MapMarkerKind.Target, t.Id);
        };
        var hit = Map.HitTestMarker(p);
        menu.Items.Add(geo);
        menu.Items.Add(addTarget);
        if (hit != null)
        {
            var open = new MenuItem { Header = $"Show {hit.Label}", FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => OnMapMarkerClicked(hit);
            menu.Items.Insert(0, open);
            menu.Items.Insert(1, new Separator());
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(copy);
        menu.Items.Add(center);
        menu.Items.Add(centerHome);
        menu.Items.Add(setHome);
        menu.Items.Add(new Separator());
        var clearTrack = new MenuItem { Header = "Clear aircraft track" };
        clearTrack.Click += (_, _) => Map.ClearTrail();
        var clearPois = new MenuItem { Header = $"Clear POIs ({Map.Pois.Count})", IsEnabled = Map.Pois.Count > 0 };
        clearPois.Click += (_, _) => Map.ClearPois();
        var clearRuler = new MenuItem { Header = "Clear ruler" };
        clearRuler.Click += (_, _) => Map.ClearRuler();
        menu.Items.Add(clearTrack);
        menu.Items.Add(clearPois);
        menu.Items.Add(clearRuler);
        menu.Items.Add(new Separator());
        var footprint = new MenuItem { Header = "Show camera view polygon", IsCheckable = true, IsChecked = Map.ShowFootprint };
        footprint.Click += (_, _) => { Map.ShowFootprint = footprint.IsChecked; Map.Refresh(); };
        menu.Items.Add(footprint);
        menu.PlacementTarget = Map;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void MapFollow_Click(object sender, RoutedEventArgs e)
    {
        // first click centres on the gimbal and follows it; clicking again while following stops following
        bool follow = !Gcs.Settings.MapFollowGimbal || _status?.HasCameraPosition != true;
        SetFollow(follow);
        if (_status?.HasCameraPosition == true)
            Map.SetView(_status.CamLat, _status.CamLon, Math.Max(Map.Zoom, 13));
    }

    private void MapTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_status?.HasTarget != true)
        {
            AppLog.Write("No geo target available yet");
            return;
        }
        SetFollow(false);
        Map.SetView(_status.TargetLat, _status.TargetLon);
    }

    private void MapZoomIn_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(1);
    private void MapZoomOut_Click(object sender, RoutedEventArgs e) => Map.ZoomBy(-1);

    private void MapLayers_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var layers = MapLayer.BuiltIn.ToList();
        if (!string.IsNullOrWhiteSpace(Gcs.Settings.TileUrl) && MapLayer.BuiltIn.All(l => l.UrlTemplate != Gcs.Settings.TileUrl))
            layers.Add(FindLayer("Custom"));
        foreach (var layer in layers)
        {
            var item = new MenuItem { Header = layer.Name, IsCheckable = true, IsChecked = layer.Name == Map.Layer.Name };
            var l = layer;
            item.Click += (_, _) =>
            {
                Map.SetLayer(l);
                Gcs.Settings.MapLayerName = l.Name;
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = MapLayersButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MapPoi_Click(object sender, RoutedEventArgs e)
    {
        Map.SetTool(Map.Tool == MapTool.Poi ? MapTool.Pan : MapTool.Poi);
        UpdateMapToolButtons();
    }

    private void MapRuler_Click(object sender, RoutedEventArgs e)
    {
        Map.SetTool(Map.Tool == MapTool.Ruler ? MapTool.Pan : MapTool.Ruler);
        UpdateMapToolButtons();
    }

    private void MapPan_Click(object sender, RoutedEventArgs e)
    {
        Map.SetTool(MapTool.Pan);
        UpdateMapToolButtons();
    }

    private void CloseMap_Click(object sender, MouseButtonEventArgs e) => SetMapVisible(false);

    private void MenuMap_Click(object sender, RoutedEventArgs e) => SetMapVisible(MenuMap.IsChecked);

    private void SetMapVisible(bool visible)
    {
        MenuMap.IsChecked = visible;
        MapPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MapSplitter.Visibility = MapPane.Visibility;
        MapColumn.Width = visible ? new GridLength(440) : new GridLength(0);
        if (!visible) CloseFlyout();
    }

    // =====================================================================================
    // Flyouts (side tabs)
    // =====================================================================================

    private FlyoutPage CreatePage(string key) => key switch
    {
        "Sensors" => new SensorsPage(),
        "Telemetry" => new TelemetryPage(),
        "Network" => new NetworkPage(),
        "Common" => new CommonPage(),
        "Keys" => new KeyBindingsPage(),
        "Video" => new VideoSettingsPage(),
        "Processing" => new ProcessingPage(),
        "Geo" => new GeoPage(),
        "Restream" => new RestreamPage(),
        "Player" => new VideoPlayerPage(),
        "Targets" => CreateTargetsPage(),
        _ => null,
    };

    // =====================================================================================
    // Targets & splashes (model: Gcs.Targets; views: TargetsPage + map markers)
    // =====================================================================================

    private const double TargetsPanelWidth = 450;
    private GridLength? _mapWidthBeforeTargets;

    private TargetsPage TargetsPageInstance => _pages.TryGetValue("Targets", out var p) ? p as TargetsPage : null;

    private TargetsPage CreateTargetsPage()
    {
        var page = new TargetsPage();
        // list -> map
        page.TargetSelected += (id, center) =>
        {
            var t = Gcs.Targets.FindTarget(id);
            if (t == null) return;
            Map.SetSelectedMarker(MapMarkerKind.Target, id);
            if (center) CenterMapOn(t.Latitude, t.Longitude);
        };
        page.SplashSelected += (id, center) =>
        {
            var s = Gcs.Targets.FindSplash(id);
            if (s == null) return;
            Map.SetSelectedMarker(MapMarkerKind.Splash, id);
            if (center) CenterMapOn(s.Latitude, s.Longitude);
        };
        page.SelectionCleared += () => Map.SetSelectedMarker(null);
        return page;
    }

    /// <summary>Map marker click -> find the model by ID -> open the Targets page and select it there.</summary>
    private void OnMapMarkerClicked(MapMarker m)
    {
        bool exists = m.Kind == MapMarkerKind.Target ? Gcs.Targets.FindTarget(m.Id) != null : Gcs.Targets.FindSplash(m.Id) != null;
        if (!exists) { UpdateTargetMarkers(); return; }  // stale marker: redraw from the store
        if (_activePageKey != "Targets") OpenPage("Targets");
        var page = TargetsPageInstance;
        if (page == null) return;
        if (m.Kind == MapMarkerKind.Target) page.SelectTarget(m.Id);
        else page.SelectSplash(m.Id);
        Map.SetSelectedMarker(m.Kind, m.Id);
    }

    /// <summary>Rebuilds the map markers from the store (the store raises Changed on the UI thread).</summary>
    private void UpdateTargetMarkers()
    {
        var markers = new List<MapMarker>();
        foreach (var s in Gcs.Targets.Splashes)
            markers.Add(new MapMarker(MapMarkerKind.Splash, s.Id, s.Latitude, s.Longitude, s.Name));
        foreach (var t in Gcs.Targets.Targets)
            markers.Add(new MapMarker(MapMarkerKind.Target, t.Id, t.Latitude, t.Longitude, t.Name,
                                      Dimmed: t.Status == Epsilon.Core.Targets.TargetStatus.Inactive));
        Map.SetMarkers(markers);
    }

    private void CenterMapOn(double lat, double lon)
    {
        SetFollow(false);
        bool panelLeft = FlyoutHost.Visibility == Visibility.Visible && FlyoutHost.HorizontalAlignment == HorizontalAlignment.Left;
        bool panelRight = FlyoutHost.Visibility == Visibility.Visible && FlyoutHost.HorizontalAlignment == HorizontalAlignment.Right;
        Map.CenterOn(lat, lon, panelLeft ? FlyoutHost.ActualWidth : 0, panelRight ? FlyoutHost.ActualWidth : 0);
    }

    /// <summary>
    /// The Targets page sits on the LEFT of the map (as in Epsilon Control) and is wider than the other pages;
    /// the map column is widened while it is open so the map stays usable, and restored afterwards.
    /// </summary>
    private void ApplyFlyoutLayout(string key)
    {
        bool targets = key == "Targets";
        FlyoutHost.HorizontalAlignment = targets ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FlyoutHost.Width = targets ? TargetsPanelWidth : 390;
        if (targets)
        {
            _mapWidthBeforeTargets ??= MapColumn.Width;
            double wanted = TargetsPanelWidth + 330;
            if (MapColumn.ActualWidth < wanted) MapColumn.Width = new GridLength(wanted);
        }
        else if (_mapWidthBeforeTargets.HasValue)
        {
            MapColumn.Width = _mapWidthBeforeTargets.Value;
            _mapWidthBeforeTargets = null;
        }
    }

    private void OpenPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            if (page == null) return;
            _pages[key] = page;
        }
        if (MapPane.Visibility != Visibility.Visible) SetMapVisible(true);
        _activePage = page;
        _activePageKey = key;
        ApplyFlyoutLayout(key);
        FlyoutTitle.Text = page.Title;
        FlyoutContent.Content = page;
        FlyoutHost.Visibility = Visibility.Visible;
        foreach (var tb in SideTabs.Children.OfType<ToggleButton>())
            tb.IsChecked = (tb.Tag as string) == key;
        page.OnOpened();
        if (_status != null) page.OnStatus(_status);
    }

    private void CloseFlyout()
    {
        FlyoutHost.Visibility = Visibility.Collapsed;
        FlyoutContent.Content = null;
        _activePage = null;
        _activePageKey = null;
        ApplyFlyoutLayout(null);
        foreach (var tb in SideTabs.Children.OfType<ToggleButton>()) tb.IsChecked = false;
    }

    private void SideTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string key) return;
        if (_activePageKey == key) CloseFlyout();
        else OpenPage(key);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e) => CloseFlyout();

    private void MenuOpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string key) OpenPage(key);
    }

    // =====================================================================================
    // Docked panels
    // =====================================================================================

    private void ClosePanel_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void ClosePanel_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is FrameworkElement panel) SetPanelVisible(panel, false);
    }

    private void MenuPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string name && FindName(name) is FrameworkElement panel)
            SetPanelVisible(panel, mi.IsChecked);
    }

    private void SetPanelVisible(FrameworkElement panel, bool visible)
    {
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (panel == MtiPanel) { MenuMti.IsChecked = visible; MtiColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0); }
        if (panel == EnhancementPanel) { MenuEnhancement.IsChecked = visible; EnhancementColumn.Width = visible ? new GridLength(1.7, GridUnitType.Star) : new GridLength(0); }
        if (panel == ControlsPanel) { MenuControls.IsChecked = visible; ControlsColumn.Width = visible ? new GridLength(230) : new GridLength(0); }
        bool any = MtiPanel.Visibility == Visibility.Visible || EnhancementPanel.Visibility == Visibility.Visible ||
                   ControlsPanel.Visibility == Visibility.Visible;
        BottomRow.Height = any ? new GridLength(158) : new GridLength(0);
    }

    // =====================================================================================
    // Menu
    // =====================================================================================

    private void MenuConnect_Click(object sender, RoutedEventArgs e)
    {
        if (Gcs.Gimbal.IsOpen) Gcs.Gimbal.Disconnect();
        else Gcs.ConnectGimbal();
        OnConnectionChanged(Gcs.Gimbal.IsConnected);
    }

    private void MenuRestartVideo_Click(object sender, RoutedEventArgs e)
    {
        bool restream = Gcs.Restreamer.State != RestreamState.Stopped;
        if (restream) Gcs.StopRestream();
        Gcs.Video.Start(restartReceiver: true);
        if (restream) Gcs.StartRestream();
    }

    private void MenuLocalSnapshot_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "EpsilonGCS");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"epsilon_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        if (Gcs.Video.TakeLocalSnapshot(file)) AppLog.Write("Saved frame " + file);
        else MessageBox.Show(this, "No video frame available.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MenuErrors_Click(object sender, RoutedEventArgs e) => new ErrorsWindow().Show();
    private void BadgeError_Click(object sender, MouseButtonEventArgs e) => new ErrorsWindow().Show();
    private void MenuDiagnostic_Click(object sender, RoutedEventArgs e) => new DiagnosticWindow().Show();
    private void MenuLog_Click(object sender, RoutedEventArgs e) => new LogWindow().Show();
    private void MenuDeviceInfo_Click(object sender, RoutedEventArgs e) => new DeviceInfoWindow().Show();
    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", AppSettings.Folder); } catch { /* ignore */ }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        MessageBox.Show(this,
            $"Epsilon GCS {version}\n\n" +
            "Ground control station for Redwire Epsilon gimbals.\n" +
            "Protocol: Epsilon General Communication Protocol v4.0.\n" +
            "Video: LibVLC.  RTSP restream: FFmpeg + MediaMTX.\n" +
            "Map data © OpenStreetMap contributors.\n\n" +
            $"Settings: {AppSettings.FilePath}\nLogs: {Gcs.DataDir}",
            "About Epsilon GCS", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
