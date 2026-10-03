using Epsilon.Video;
using LibVLCSharp.Shared;

namespace EpsilonGCS.Services;

/// <summary>
/// Receives the gimbal video and plays it with LibVLC in the main window's <c>VideoView</c>.
///   UDP MPEG-TS:  gimbal :15004 -> UdpFanout -> 127.0.0.1:15010/15012 (display, alternates on every restart)
///                                            -> 127.0.0.1:15011       (RTSP restreamer, when running)
///   RTSP URL:     the display opens the camera URL; while the restreamer is serving that same camera,
///                 the display reads the local restream instead, so the camera only has ONE RTSP client.
/// The gimbal burns its own OSD into the video, so no overlay drawing is needed here.
///
/// Threading: LibVLC's Stop() must not run on the WPF UI thread - the VLC video output window is a child of the
/// VideoView's HWND, and tearing it down while the UI thread is blocked inside Stop() leaves the view detached
/// (white picture, "Buffering 0%"). All Stop/Play sequences therefore run on one background worker, in order.
/// </summary>
public sealed class VideoService : IDisposable
{
    public const int DisplayLoopbackPort = 15010;
    public const int DisplayLoopbackPortAlt = 15012;
    public const int RestreamLoopbackPort = 15011;

    private LibVLC _vlc;
    private bool _initialized;

    // Player commands are serialised on this chain; _generation drops commands superseded by a newer one.
    private readonly object _playerLock = new();
    private Task _playerChain = Task.CompletedTask;
    private int _generation;

    private int _displayPort = DisplayLoopbackPort;
    private string _currentMrl;
    private volatile bool _firstFrameLogged;
    private string _lastVlcError = "";
    private DateTime _lastVlcErrorLogUtc = DateTime.MinValue;

    public MediaPlayer Player { get; private set; }
    public UdpFanout Fanout { get; } = new();
    public bool IsReceiving { get; private set; }
    public string CurrentSource { get; private set; } = "";
    public string PlayerState { get; private set; } = "Stopped";

    /// <summary>True once the current live stream has produced a video output (first frame shown).</summary>
    public bool HasPicture { get; private set; }

    public event Action<string> Log;
    public event Action StateChanged;

    private void L(string text) => Log?.Invoke("[video] " + text);

    public void Initialize()
    {
        if (_initialized) return;
        L("initialising LibVLC");
        Core.Initialize();
        // Low-latency live playback: no audio, late frames dropped instead of queued, no clock smoothing.
        _vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-audio",
                          "--drop-late-frames", "--skip-frames", "--clock-jitter=0", "--clock-synchro=0");
        _vlc.Log += OnVlcLog;
        Player = new MediaPlayer(_vlc) { EnableHardwareDecoding = true, EnableMouseInput = false, EnableKeyInput = false };

        // These run on LibVLC threads: only record state and log, never touch WPF controls or call the player here.
        Player.Opening += (_, _) => SetPlayerState("Opening");
        Player.Playing += (_, _) => { L("PLAYING " + _currentMrl); SetPlayerState("Running"); };
        Player.Buffering += (_, e) =>
        {
            // Buffering events keep arriving after Playing; only show them while the picture is not up yet.
            if (Player.State != VLCState.Playing && e.Cache < 100) SetPlayerState($"Buffering {e.Cache:0}%");
        };
        Player.Vout += (_, e) =>
        {
            HasPicture = e.Count > 0;
            if (e.Count > 0 && !_firstFrameLogged)
            {
                _firstFrameLogged = true;
                L($"first video frame rendered (video outputs: {e.Count})");
            }
            else if (e.Count == 0) L("video output closed");
            StateChanged?.Invoke();
        };
        Player.Stopped += (_, _) => { HasPicture = false; L("player stopped"); SetPlayerState("Stopped"); };
        Player.Paused += (_, _) => SetPlayerState("Paused");
        Player.EncounteredError += (_, _) =>
        {
            HasPicture = false;
            L("ERROR playing " + _currentMrl + (_lastVlcError.Length > 0 ? " - " + _lastVlcError : ""));
            SetPlayerState("Error");
        };
        Player.EndReached += (_, _) => { HasPicture = false; L("EOS (end of stream) on " + _currentMrl); SetPlayerState("No signal"); };
        Fanout.Log += m => Log?.Invoke(m);
        _initialized = true;
        L("LibVLC ready (" + _vlc.Version + ")");
    }

    private void OnVlcLog(object sender, LogEventArgs e)
    {
        if (e.Level != LogLevel.Error) return;
        _lastVlcError = $"{e.Module}: {e.Message}";
        var now = DateTime.UtcNow;
        if ((now - _lastVlcErrorLogUtc).TotalSeconds < 2) return; // VLC can repeat an error many times per second
        _lastVlcErrorLogUtc = now;
        L("libvlc error: " + _lastVlcError);
    }

    /// <summary>True while a recorded file is playing instead of the live stream.</summary>
    public bool IsPlayback { get; private set; }

    /// <summary>
    /// The LIVE button. Returns to the live stream when a recording is playing or the live player is not running.
    /// When the live picture is already up it does nothing: restarting a working player only causes a gap.
    /// </summary>
    public void GoLive()
    {
        Initialize();
        var state = Player.State;
        bool healthy = !IsPlayback && IsReceiving
                       && (state is VLCState.Playing or VLCState.Buffering or VLCState.Opening)
                       && _currentMrl == DesiredLiveMrl();
        L($"LIVE requested - playback={IsPlayback}, receiving={IsReceiving}, player={Player.State}, picture={HasPicture}, " +
          $"source={DesiredLiveMrl() ?? "?"}");
        if (healthy)
        {
            L("already live - player kept running");
            return;
        }
        Start();
    }

    /// <summary>
    /// Plays the live gimbal stream. The UDP receiver is only (re)started when it is not running,
    /// the port changed, or <paramref name="restartReceiver"/> is set, so the RTSP restream keeps
    /// running when switching between a recording and LIVE.
    /// </summary>
    public void Start(bool restartReceiver = false)
    {
        Initialize();
        var s = Gcs.Settings;
        string mrl;
        try
        {
            if (s.VideoInput == VideoInputKind.UdpMpegTs)
            {
                if (restartReceiver || !Fanout.IsRunning || Fanout.InputPort != s.VideoPort)
                    Fanout.Start(s.VideoPort, s.VideoMulticastGroup);
                // Use the other loopback port for the new player instance, so it never competes with the
                // socket of the instance being stopped.
                Fanout.RemoveTarget(_displayPort);
                _displayPort = _displayPort == DisplayLoopbackPort ? DisplayLoopbackPortAlt : DisplayLoopbackPort;
                Fanout.AddTarget(_displayPort);
                mrl = $"udp://@127.0.0.1:{_displayPort}";
                CurrentSource = $"UDP :{s.VideoPort}";
            }
            else
            {
                if (Fanout.IsRunning) Fanout.Stop();
                mrl = DesiredLiveMrl();
                CurrentSource = mrl == s.VideoRtspUrl ? s.VideoRtspUrl : $"{s.VideoRtspUrl} (via local restream)";
            }
        }
        catch (Exception ex)
        {
            L("receiver failed: " + ex.Message);
            SetPlayerState("Error: " + ex.Message);
            return;
        }

        var options = new List<string>
        {
            $":network-caching={Math.Max(0, s.NetworkCachingMs)}",
            $":live-caching={Math.Max(0, s.NetworkCachingMs)}",
            ":clock-jitter=0",
            ":clock-synchro=0",
            ":no-audio",
        };
        if (s.VideoInput == VideoInputKind.UdpMpegTs)
        {
            options.Add(":demux=ts");          // skip format probing: picture starts faster after a restart
            options.Add(":ts-trust-pcr=0");    // tolerate the PCR jumps a gimbal stream has after VP restarts
        }
        else
        {
            options.Add(":rtsp-tcp");
        }

        IsPlayback = false;
        IsReceiving = true;
        _lastRestartUtc = DateTime.UtcNow;
        _notPlayingSinceUtc = null;
        _stalled = false;
        L("starting live player on " + mrl + (s.VideoInput == VideoInputKind.UdpMpegTs ? $" (gimbal UDP :{s.VideoPort})" : ""));
        QueuePlay(mrl, FromType.FromLocation, options);
        StateChanged?.Invoke();
    }

    /// <summary>Display source for the live stream in the current settings (see class summary).</summary>
    private string DesiredLiveMrl()
    {
        var s = Gcs.Settings;
        if (s.VideoInput == VideoInputKind.UdpMpegTs) return $"udp://@127.0.0.1:{_displayPort}";
        var local = Gcs.Restreamer?.LocalReadUrl;
        bool restreamOfCamera = !string.IsNullOrEmpty(local) && Gcs.Restreamer.InputUrl == s.VideoRtspUrl;
        return restreamOfCamera ? local : s.VideoRtspUrl;
    }

    /// <summary>
    /// Called when the restreamer starts or stops. In RTSP mode the display switches between the camera URL and the
    /// local restream so that the camera is never opened twice (many cameras accept only one RTSP client).
    /// </summary>
    public void OnRestreamStateChanged()
    {
        if (!_initialized || IsPlayback || !IsReceiving || Gcs.Settings.VideoInput != VideoInputKind.RtspUrl) return;
        string want = DesiredLiveMrl();
        if (want == _currentMrl) return;
        L("restream state changed - display source " + _currentMrl + " -> " + want);
        Start();
    }

    /// <summary>Stops whatever is playing and plays <paramref name="location"/>, on the background player worker.</summary>
    private void QueuePlay(string location, FromType type, IReadOnlyList<string> options)
    {
        int gen = Interlocked.Increment(ref _generation);
        _currentMrl = location;
        _firstFrameLogged = false;
        HasPicture = false;
        lock (_playerLock)
        {
            _playerChain = _playerChain.ContinueWith(_ =>
            {
                if (gen != Volatile.Read(ref _generation)) return; // superseded by a newer request
                try
                {
                    if (Player.State is not (VLCState.Stopped or VLCState.NothingSpecial or VLCState.Ended or VLCState.Error))
                        Player.Stop();
                    if (gen != Volatile.Read(ref _generation)) return;
                    using var media = new Media(_vlc, location, type);
                    foreach (var o in options) media.AddOption(o);
                    bool ok = Player.Play(media);
                    L($"pipeline created for {location} - Play() {(ok ? "accepted" : "REFUSED")}");
                }
                catch (Exception ex)
                {
                    L("player start failed: " + ex.Message);
                    SetPlayerState("Error: " + ex.Message);
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Stops the player on the background worker (never on the UI thread).</summary>
    private void QueueStop(string reason)
    {
        int gen = Interlocked.Increment(ref _generation);
        lock (_playerLock)
        {
            _playerChain = _playerChain.ContinueWith(_ =>
            {
                if (gen != Volatile.Read(ref _generation)) return;
                try
                {
                    if (Player.State is not (VLCState.Stopped or VLCState.NothingSpecial))
                    {
                        L("stopping player (" + reason + ")");
                        Player.Stop();
                    }
                }
                catch (Exception ex) { L("stop failed: " + ex.Message); }
            }, TaskScheduler.Default);
        }
    }

    // ------------------------------------------------------------------ live-video watchdog

    private DateTime _lastRestartUtc = DateTime.MinValue;
    private DateTime? _notPlayingSinceUtc;
    private bool _hadFlow, _stalled;

    /// <summary>Number of automatic re-synchronisations of the live video since start.</summary>
    public int AutoResyncs { get; private set; }

    /// <summary>
    /// Call about once a second (UI thread). Keeps the live picture in step with the gimbal:
    /// - after the stream stops and comes back (cable, gimbal VP restart, camera switch), the player is restarted
    ///   so it does not keep playing from a stale buffer with growing delay;
    /// - if packets arrive but the player is not playing (decoder error, end of stream), it is restarted.
    /// </summary>
    public void CheckHealth()
    {
        if (!_initialized || IsPlayback || !IsReceiving || Player == null) return;
        var now = DateTime.UtcNow;
        bool udp = Gcs.Settings.VideoInput == VideoInputKind.UdpMpegTs;

        if (udp)
        {
            double sinceLast = (now - Fanout.LastPacketUtc).TotalSeconds;
            if (sinceLast > 2)
            {
                if (_hadFlow && !_stalled) L($"no video packets on UDP :{Gcs.Settings.VideoPort} for {sinceLast:0} s");
                if (_hadFlow) _stalled = true;
                return;
            }
            _hadFlow = true;
            if (_stalled)
            {
                Restart("video stream resumed - re-synchronising the player");
                return;
            }
        }

        var state = Player.State;
        bool playing = state is VLCState.Playing or VLCState.Buffering or VLCState.Opening;
        if (playing)
        {
            _notPlayingSinceUtc = null;
            return;
        }
        _notPlayingSinceUtc ??= now;
        // Reading the local restream: its publisher needs a moment after a start, so retry sooner.
        double retry = _currentMrl != null && _currentMrl.Contains("127.0.0.1") && !udp ? 2 : 5;
        if ((now - _notPlayingSinceUtc.Value).TotalSeconds > 3 && (now - _lastRestartUtc).TotalSeconds > retry)
            Restart($"player {state} while the stream is live - restarting");
    }

    /// <summary>Restarts the player on the live stream (the UDP receiver and RTSP restream keep running).</summary>
    public void Resync() => Restart("re-synchronised by the operator (F5)");

    private void Restart(string reason)
    {
        AutoResyncs++;
        L(reason);
        Start();
    }

    /// <summary>Plays a recorded file (e.g. copied from the gimbal SD card). The live receiver keeps running.</summary>
    public void PlayFile(string path)
    {
        Initialize();
        if (!System.IO.File.Exists(path))
        {
            L("file not found: " + path);
            return;
        }
        IsPlayback = true;
        CurrentSource = "File: " + System.IO.Path.GetFileName(path);
        L("playing recording " + path);
        QueuePlay(path, FromType.FromPath, Array.Empty<string>());
        StateChanged?.Invoke();
    }

    public void TogglePause()
    {
        if (Player != null && Player.CanPause) Player.Pause();
    }

    public void StopPlayback()
    {
        if (Player != null && IsPlayback) QueueStop("recording stopped");
    }

    /// <summary>Seek within a recording, 0..1.</summary>
    public void Seek(double fraction)
    {
        if (Player != null && IsPlayback && Player.IsSeekable)
            Player.Position = (float)Math.Clamp(fraction, 0, 1);
    }

    /// <summary>Playback position 0..1 (recordings only).</summary>
    public double Position => Player != null && IsPlayback ? Player.Position : 1;

    public TimeSpan Time => Player != null && IsPlayback ? TimeSpan.FromMilliseconds(Math.Max(0, Player.Time)) : TimeSpan.Zero;

    public TimeSpan Length => Player != null && IsPlayback ? TimeSpan.FromMilliseconds(Math.Max(0, Player.Length)) : TimeSpan.Zero;

    public void SetRate(float rate)
    {
        if (Player != null && IsPlayback) Player.SetRate(rate);
    }

    public void Stop()
    {
        if (Player != null) QueueStop("video stopped");
        Fanout.Stop();
        IsReceiving = false;
        StateChanged?.Invoke();
    }

    /// <summary>Adds or removes the loopback copy that feeds FFmpeg.</summary>
    public void EnableRestreamTap(bool on)
    {
        if (on) Fanout.AddTarget(RestreamLoopbackPort);
        else Fanout.RemoveTarget(RestreamLoopbackPort);
    }

    /// <summary>Input URL for FFmpeg, matching the current video source.</summary>
    public string RestreamInputUrl()
    {
        var s = Gcs.Settings;
        if (s.VideoInput == VideoInputKind.RtspUrl) return s.VideoRtspUrl;
        if (!Fanout.IsRunning) Start();
        EnableRestreamTap(true);
        return $"udp://127.0.0.1:{RestreamLoopbackPort}";
    }

    public bool TakeLocalSnapshot(string path) =>
        Player != null && Player.TakeSnapshot(0, path, 0, 0);

    private void SetPlayerState(string state)
    {
        PlayerState = state;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Fanout.Stop();
        IsReceiving = false;
        try
        {
            // Final teardown: wait for queued commands, then stop and release LibVLC (off the UI thread).
            Task chain;
            lock (_playerLock) chain = _playerChain;
            Task.Run(async () =>
            {
                await chain.ConfigureAwait(false);
                Player?.Stop();
            }).Wait(TimeSpan.FromSeconds(3));
        }
        catch { /* shutting down */ }
        Player?.Dispose();
        _vlc?.Dispose();
    }
}
