using System.IO;
using System.Windows;
using Epsilon.Core.Client;
using Epsilon.Core.Protocol;
using Epsilon.Core.Transport;
using Epsilon.Video;

namespace EpsilonGCS.Services;

/// <summary>Application-wide services (kept deliberately simple: one gimbal, one video pipeline).</summary>
public static class Gcs
{
    public static AppSettings Settings { get; private set; }
    public static GimbalClient Gimbal { get; private set; }
    /// <summary>Operator-level camera/gimbal commands; the UI uses this instead of raw packets.</summary>
    public static GimbalController Controller { get; private set; }
    public static VideoService Video { get; private set; }
    public static RtspRestreamer Restreamer { get; private set; }

    public static string AppDir => AppContext.BaseDirectory;
    public static string ToolsDir => Path.Combine(AppDir, "tools");
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpsilonGCS");

    public static void Initialize()
    {
        Directory.CreateDirectory(DataDir);
        Settings = AppSettings.Load();

        Gimbal = new GimbalClient { LogTraffic = Settings.LogTraffic };
        Gimbal.Log += AppLog.Write;
        Controller = new GimbalController(Gimbal);
        Controller.CommandRejected += AppLog.Write;
        SyncTrackingProfile();

        Restreamer = new RtspRestreamer(ToolsDir, DataDir);
        Restreamer.Log += AppLog.Write;

        Video = new VideoService();
        Video.Log += AppLog.Write;
    }

    public static void ConnectGimbal()
    {
        try
        {
            IGimbalTransport t = Settings.Link == LinkType.Udp
                ? new UdpTransport(Settings.GimbalIp, Settings.GimbalPort, Settings.LocalPort)
                : new SerialTransport(Settings.SerialPort, Settings.BaudRate);
            Gimbal.LogTraffic = Settings.LogTraffic;
            Gimbal.Connect(t);
        }
        catch (Exception ex)
        {
            AppLog.Write("Connect failed: " + ex.Message);
            MessageBox.Show("Could not open the gimbal link:\n" + ex.Message, "Epsilon GCS",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Sends a settings packet only when a link is open; logs otherwise.</summary>
    public static void Send(Packet p) => Controller.Send(p);

    /// <summary>Copies the persisted tracking box / advanced / option bits into the controller.</summary>
    public static void SyncTrackingProfile()
    {
        Controller.Tracking.BoxSize = Settings.TrackBoxSize;
        Controller.Tracking.Advanced = Settings.TrackingAdvanced;
        Controller.Tracking.Options = Settings.ControlOptions;
        Controller.InvertTilt = Settings.InvertTilt;
    }

    public static void StartRestream()
    {
        var input = Video.RestreamInputUrl();
        if (input == null) return;
        Restreamer.Start(Settings.Restream, input);
    }

    public static void StopRestream()
    {
        Restreamer.Stop();
        Video.EnableRestreamTap(false);
    }

    public static void Shutdown()
    {
        try { Restreamer.Stop(); } catch { }
        try { Gimbal.Disconnect(); } catch { }
        try { Video.Dispose(); } catch { }
        Settings.Save();
    }
}

/// <summary>In-memory ring log, also appended to %LocalAppData%\EpsilonGCS\gcs.log.</summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static readonly LinkedList<string> Lines = new();
    private const int MaxLines = 2000;

    public static event Action<string> LineAdded;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        lock (Lock)
        {
            Lines.AddLast(line);
            while (Lines.Count > MaxLines) Lines.RemoveFirst();
            try { File.AppendAllText(Path.Combine(Gcs.DataDir, "gcs.log"), line + Environment.NewLine); } catch { }
        }
        LineAdded?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Lock) return Lines.ToArray();
    }
}
