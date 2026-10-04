using System.IO;
using System.Windows;
using Epsilon.Core.Client;
using Epsilon.Core.Maps;
using Epsilon.Core.Protocol;
using Epsilon.Core.Targets;
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

        // The restreamer reports from a background task; the display reacts on the UI thread.
        Restreamer.StateChanged += _ =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Video.OnRestreamStateChanged()));

        Targets = new TargetStore();
        Units = new UnitStore();

        InitializeMaps();
    }

    // ------------------------------------------------------------------ maps

    /// <summary>Map sources, tile cache, online/offline mode and area downloader (independent of targets/units).</summary>
    public static MapProviderManager Maps { get; private set; }

    /// <summary>Tile cache root: %LocalAppData%\EpsilonGCS\MapCache (always user-writable, never Program Files).</summary>
    public static string MapCacheDir => Path.Combine(DataDir, "MapCache");

    /// <summary>Custom map definitions: %AppData%\EpsilonGCS\custom-maps.json.</summary>
    public static string CustomMapsFile => Path.Combine(AppSettings.Folder, "custom-maps.json");

    private static string _googleKey;

    /// <summary>Google Maps Platform API key (decrypted in memory only; stored DPAPI-encrypted). Never logged.</summary>
    public static string GoogleApiKey
    {
        get => _googleKey ??= SecretProtector.Unprotect(Settings.GoogleApiKeyProtected);
        set
        {
            _googleKey = (value ?? "").Trim();
            Settings.GoogleApiKeyProtected = SecretProtector.Protect(_googleKey);
            Settings.Save();
            Maps?.ResetGoogleSessions();
        }
    }

    private static void InitializeMaps()
    {
        bool migrate = !Settings.MapMigrated;
        if (migrate) Settings.Map.ProviderId = MapProviderManager.ProviderIdFromLegacyLayerName(Settings.MapLayerName);

        Maps = new MapProviderManager(Settings.Map, MapCacheDir, CustomMapsFile, () => GoogleApiKey);

        if (migrate)
        {
            try
            {
                var folders = Maps.PrepareLegacyMigration(Settings.TileUrl);
                Maps.Select(Settings.Map.ProviderId);   // may only exist now (old "Custom" layer)
                string legacyRoot = Path.Combine(DataDir, "tiles");
                Settings.MapMigrated = true;
                Settings.Save();
                // Moving old tiles can take a while: do it in the background (the map just downloads meanwhile).
                Task.Run(() =>
                {
                    try
                    {
                        int n = Maps.Cache.MigrateLegacy(legacyRoot, folders);
                        if (n > 0) AppLog.Write($"[map] moved {n} cached tiles of the previous version into {MapCacheDir}");
                    }
                    catch (Exception ex) { AppLog.Write("[map] cache migration: " + ex.Message); }
                });
            }
            catch (Exception ex) { AppLog.Write("[map] settings migration: " + ex.Message); }
        }
        AppLog.Write($"[map] source {Maps.Current?.Name}, mode {Maps.Mode}, cache {MapCacheDir}");
    }

    /// <summary>Targets and splashes marked by the operator (in memory, shared by the map and the Targets page).</summary>
    public static TargetStore Targets { get; private set; }

    /// <summary>Units marked by the operator (in memory, shared by the map and the Units page).</summary>
    public static UnitStore Units { get; private set; }

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
        AppLog.Write($"[restream] start requested (video input {Settings.VideoInput}, mode {Settings.Restream.Mode})");
        var input = Video.RestreamInputUrl();
        if (input == null) return;
        Restreamer.Start(Settings.Restream, input);
    }

    public static void StopRestream()
    {
        AppLog.Write("[restream] stop requested");
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
