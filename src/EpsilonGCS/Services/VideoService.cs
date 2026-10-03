using Epsilon.Video;
using LibVLCSharp.Shared;

namespace EpsilonGCS.Services;

/// <summary>
/// Receives the gimbal video and plays it with LibVLC.
///   UDP MPEG-TS:  gimbal :15004 -> UdpFanout -> 127.0.0.1:15010 (display)
///                                            -> 127.0.0.1:15011 (RTSP restreamer, when running)
///   RTSP URL:     LibVLC and the restreamer both open the URL directly.
/// The gimbal burns its own OSD into the video, so no overlay drawing is needed here.
/// </summary>
public sealed class VideoService : IDisposable
{
    public const int DisplayLoopbackPort = 15010;
    public const int RestreamLoopbackPort = 15011;

    private LibVLC _vlc;
    private bool _initialized;

    public MediaPlayer Player { get; private set; }
    public UdpFanout Fanout { get; } = new();
    public bool IsReceiving { get; private set; }
    public string CurrentSource { get; private set; } = "";
    public string PlayerState { get; private set; } = "Stopped";

    public event Action<string> Log;
    public event Action StateChanged;

    public void Initialize()
    {
        if (_initialized) return;
        Core.Initialize();
        // Low-latency live playback: no audio, late frames dropped instead of queued, no clock smoothing.
        _vlc = new LibVLC("--no-video-title-show", "--quiet", "--no-snapshot-preview", "--no-audio",
                          "--drop-late-frames", "--skip-frames", "--clock-jitter=0", "--clock-synchro=0");
        Player = new MediaPlayer(_vlc) { EnableHardwareDecoding = true, EnableMouseInput = false, EnableKeyInput = false };
        Player.Playing += (_, _) => SetPlayerState("Running");
        Player.Buffering += (_, e) => { if (e.Cache < 100) SetPlayerState($"Buffering {e.Cache:0}%"); };
        Player.Stopped += (_, _) => SetPlayerState("Stopped");
        Player.Paused += (_, _) => SetPlayerState("Paused");
        Player.EncounteredError += (_, _) => SetPlayerState("Error");
        Player.EndReached += (_, _) => SetPlayerState("No signal");
        Fanout.Log += m => Log?.Invoke(m);
        _initialized = true;
    }

    /// <summary>True while a recorded file is playing instead of the live stream.</summary>
    public bool IsPlayback { get; private set; }

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
                Fanout.AddTarget(DisplayLoopbackPort);
                mrl = $"udp://@127.0.0.1:{DisplayLoopbackPort}";
                CurrentSource = $"UDP :{s.VideoPort}";
            }
            else
            {
                if (Fanout.IsRunning) Fanout.Stop();
                mrl = s.VideoRtspUrl;
                CurrentSource = s.VideoRtspUrl;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("Video receiver failed: " + ex.Message);
            SetPlayerState("Error: " + ex.Message);
            return;
        }

        if (Player.IsPlaying) Player.Stop();
        using (var media = new Media(_vlc, mrl, FromType.FromLocation))
        {
            int caching = Math.Max(0, s.NetworkCachingMs);
            media.AddOption($":network-caching={caching}");
            media.AddOption($":live-caching={caching}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":clock-synchro=0");
            media.AddOption(":no-audio");
            if (s.VideoInput == VideoInputKind.UdpMpegTs)
            {
                media.AddOption(":demux=ts");          // skip format probing: picture starts faster after a restart
                media.AddOption(":ts-trust-pcr=0");    // tolerate the PCR jumps a gimbal stream has after VP restarts
            }
            if (s.VideoInput == VideoInputKind.RtspUrl) media.AddOption(":rtsp-tcp");
            Player.Play(media);
        }
        IsPlayback = false;
        IsReceiving = true;
        _lastRestartUtc = DateTime.UtcNow;
        _notPlayingSinceUtc = null;
        _stalled = false;
        Log?.Invoke("Video LIVE: " + CurrentSource);
        StateChanged?.Invoke();
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
                if (_hadFlow) _stalled = true;
                return;
            }
            _hadFlow = true;
            if (_stalled)
            {
                Restart("Video stream resumed - re-synchronising the player");
                return;
            }
        }

        bool playing = Player.State == VLCState.Playing || Player.State == VLCState.Buffering || Player.State == VLCState.Opening;
        if (playing)
        {
            _notPlayingSinceUtc = null;
            return;
        }
        _notPlayingSinceUtc ??= now;
        if ((now - _notPlayingSinceUtc.Value).TotalSeconds > 3 && (now - _lastRestartUtc).TotalSeconds > 5)
            Restart($"Video player {Player.State} - restarting");
    }

    /// <summary>Restarts the player on the live stream (the UDP receiver and RTSP restream keep running).</summary>
    public void Resync() => Restart("Video re-synchronised by the operator");

    private void Restart(string reason)
    {
        AutoResyncs++;
        Log?.Invoke(reason);
        Start();
    }

    /// <summary>Plays a recorded file (e.g. copied from the gimbal SD card). The live receiver keeps running.</summary>
    public void PlayFile(string path)
    {
        Initialize();
        if (!System.IO.File.Exists(path))
        {
            Log?.Invoke("File not found: " + path);
            return;
        }
        if (Player.IsPlaying) Player.Stop();
        using (var media = new Media(_vlc, path, FromType.FromPath))
            Player.Play(media);
        IsPlayback = true;
        CurrentSource = "File: " + System.IO.Path.GetFileName(path);
        Log?.Invoke("Playing " + path);
        StateChanged?.Invoke();
    }

    public void TogglePause()
    {
        if (Player != null && Player.CanPause) Player.Pause();
    }

    public void StopPlayback()
    {
        if (Player != null && IsPlayback) Player.Stop();
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
        if (Player != null && Player.IsPlaying) Player.Stop();
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
        Stop();
        Player?.Dispose();
        _vlc?.Dispose();
    }
}
