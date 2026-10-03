using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Epsilon.Core.Protocol;
using Epsilon.Video;

namespace EpsilonGCS.Services;

public enum LinkType { Udp, Serial }

public enum VideoInputKind
{
    /// <summary>Gimbal sends MPEG-TS over UDP to this PC (ETHERNET_DISPLAY_PARAMETERS protocol 1 or 8).</summary>
    UdpMpegTs,
    /// <summary>Read the gimbal video from an RTSP URL.</summary>
    RtspUrl,
}

/// <summary>All user settings, saved as JSON in %AppData%\EpsilonGCS\settings.json.</summary>
public sealed class AppSettings
{
    // Gimbal link
    public LinkType Link { get; set; } = LinkType.Udp;
    public string GimbalIp { get; set; } = "192.168.1.197";
    public int GimbalPort { get; set; } = 4001;
    public int LocalPort { get; set; } = 4002;
    public string SerialPort { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public bool AutoConnect { get; set; } = true;
    public bool LogTraffic { get; set; }

    // Video input
    public VideoInputKind VideoInput { get; set; } = VideoInputKind.UdpMpegTs;
    public int VideoPort { get; set; } = 15004;
    public string VideoMulticastGroup { get; set; } = "";
    public string VideoRtspUrl { get; set; } = "rtsp://192.168.1.197:554/stream";
    public int NetworkCachingMs { get; set; } = 150;

    // Restream
    public RestreamSettings Restream { get; set; } = new();

    // Control
    public int SpeedPercent { get; set; } = 50;
    public int TrackBoxSize { get; set; } = 64;
    public ControlMode ClickTrackMode { get; set; } = ControlMode.TrackVehicle;
    public int TrackingAdvanced { get; set; }
    public int ControlOptions { get; set; }
    public int OsiMask { get; set; } = OsiBits.AllOn;
    public int NudgeStepPx { get; set; } = 4;
    /// <summary>Up arrow sends a negative TILT_SPEED (matches the Epsilon hardware). Untick if tilt runs the wrong way.</summary>
    public bool InvertTilt { get; set; } = true;
    public bool ConfirmLaser { get; set; } = true;

    // Map
    public double MapLat { get; set; } = 20.0;
    public double MapLon { get; set; } = 0.0;
    public int MapZoom { get; set; } = 3;
    public bool MapFollowGimbal { get; set; } = true;
    /// <summary>Selected map layer (Street / Topographic / Satellite, or "Custom" = <see cref="TileUrl"/>).</summary>
    public string MapLayerName { get; set; } = "Street (OpenStreetMap)";
    /// <summary>Optional own tile server ({z}/{x}/{y}); appears as the "Custom" layer when it differs from OSM.</summary>
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    // Window
    public double WindowWidth { get; set; } = 1366;
    public double WindowHeight { get; set; } = 768;
    public bool WindowMaximized { get; set; } = true;

    [JsonIgnore]
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpsilonGCS");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* ignore */ }
    }
}
