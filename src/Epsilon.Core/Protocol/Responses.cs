using System.Text;

namespace Epsilon.Core.Protocol;

/// <summary>STATUS_FLAGS of EPSILON_GLOBAL_STATUS (0x80), section 4.4.</summary>
[Flags]
public enum StatusFlags : uint
{
    None = 0,
    GyroBiasInProgress = 1u << 0,
    AnyError = 1u << 1,
    LaserPointerOn = 1u << 2,
    SecondaryCameraActive = 1u << 3,
    VideoProcessorReady = 1u << 4,
    ActiveCameraIr = 1u << 5,
    GimbalInitDone = 1u << 6,
    NewError = 1u << 7,
    StabilizeOnTrack = 1u << 8,
    VideoStabilization = 1u << 9,
    ManualFocus = 1u << 10,
    VideoRecording = 1u << 11,
    OnScreenInfo = 1u << 12,
    GeoInactive = 1u << 13,          // documented as "Geo active (0 - active)"
    GeoGpsCalibrationOk = 1u << 14,
    GeoGpsFix = 1u << 15,
    LaserSwitchOn = 1u << 24,
    LrfBusy = 1u << 25,
    SlantRangeMeasured = 1u << 26,
}

/// <summary>EPSILON_GLOBAL_STATUS packet 0x80 (reply to RATE_CONTROL).</summary>
public sealed class GlobalStatus
{
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
    public uint RawFlags { get; init; }
    public StatusFlags Flags => (StatusFlags)RawFlags;
    public ControlMode Mode => (ControlMode)((RawFlags >> 16) & 0xFF);

    public double PanDeg { get; init; }
    public double TiltDeg { get; init; }
    public int ZoomPosition { get; init; }
    public int FocusPosition { get; init; }
    public double FovVerticalDeg { get; init; }
    public double FovHorizontalDeg { get; init; }
    public int TempPanC { get; init; }
    public int TempTiltC { get; init; }
    public int TempVideoProcessorC { get; init; }
    public double CamLat { get; init; }
    public double CamLon { get; init; }
    public int CamAltM { get; init; }
    public double ExtRollDeg { get; init; }
    public double ExtPitchDeg { get; init; }
    public double ExtYawDeg { get; init; }
    public double GeoAzimuthDeg { get; init; }
    public double GeoTiltDeg { get; init; }
    public int GeoDistanceM { get; init; }
    public double TargetLat { get; init; }
    public double TargetLon { get; init; }
    public double PdopA { get; init; }
    public double PdopB { get; init; }
    public int SatCountA { get; init; }
    public int SatCountB { get; init; }
    public int SignalLevelA { get; init; }
    public int SignalLevelB { get; init; }
    public double AttitudeUncertaintyDeg { get; init; }

    public bool Has(StatusFlags f) => (Flags & f) == f;
    public bool HasCameraPosition => CamLat != 0 || CamLon != 0;
    public bool HasTarget => TargetLat != 0 || TargetLon != 0;

    public static GlobalStatus Parse(byte[] d)
    {
        uint flags = ByteReader.U32(d, 0);
        bool calibrated = (flags & (uint)StatusFlags.GeoGpsCalibrationOk) != 0;
        int sig = ByteReader.U8(d, 53);
        int unc = ByteReader.U8(d, 54);
        return new GlobalStatus
        {
            RawFlags = flags,
            PanDeg = ByteReader.U16(d, 4) / 100.0,
            TiltDeg = ByteReader.S16(d, 6) / 100.0,
            ZoomPosition = ByteReader.U8(d, 8),
            FocusPosition = ByteReader.U8(d, 9),
            FovVerticalDeg = ByteReader.U16(d, 10) / 10.0,
            FovHorizontalDeg = ByteReader.U16(d, 12) / 10.0,
            TempPanC = ByteReader.S8(d, 14),
            TempTiltC = ByteReader.S8(d, 15),
            TempVideoProcessorC = ByteReader.S8(d, 16),
            CamLat = Cmd.DecodeLatLon(ByteReader.S32(d, 17)),
            CamLon = Cmd.DecodeLatLon(ByteReader.S32(d, 21)),
            CamAltM = ByteReader.S16(d, 25),
            ExtRollDeg = ByteReader.S16(d, 27) / 100.0,
            ExtPitchDeg = ByteReader.S16(d, 29) / 100.0,
            ExtYawDeg = ByteReader.S16(d, 31) / 100.0,
            GeoAzimuthDeg = ByteReader.S16(d, 33) / 10.0,
            GeoTiltDeg = ByteReader.S16(d, 35) / 10.0,
            GeoDistanceM = ByteReader.U16(d, 37),
            TargetLat = Cmd.DecodeLatLon(ByteReader.S32(d, 39)),
            TargetLon = Cmd.DecodeLatLon(ByteReader.S32(d, 43)),
            PdopA = ByteReader.U8(d, 47) / 10.0,
            PdopB = ByteReader.U8(d, 48) / 10.0,
            SatCountA = ByteReader.U8(d, 49),
            SatCountB = ByteReader.U8(d, 50),
            SignalLevelA = (sig & 0x0F) * 10,
            SignalLevelB = ((sig >> 4) & 0x0F) * 10,
            AttitudeUncertaintyDeg = calibrated ? unc / 10.0 : unc,
        };
    }

    public static readonly (StatusFlags Flag, string Name)[] FlagNames =
    {
        (StatusFlags.GyroBiasInProgress, "Gyro bias in progress"),
        (StatusFlags.AnyError, "Error present"),
        (StatusFlags.LaserPointerOn, "Laser pointer on"),
        (StatusFlags.SecondaryCameraActive, "Secondary camera active"),
        (StatusFlags.VideoProcessorReady, "Video processor ready"),
        (StatusFlags.ActiveCameraIr, "Active camera IR"),
        (StatusFlags.GimbalInitDone, "Gimbal init done"),
        (StatusFlags.NewError, "New error"),
        (StatusFlags.StabilizeOnTrack, "Stabilize on track"),
        (StatusFlags.VideoStabilization, "Video stabilization"),
        (StatusFlags.ManualFocus, "Manual focus"),
        (StatusFlags.VideoRecording, "Recording"),
        (StatusFlags.OnScreenInfo, "On screen info"),
        (StatusFlags.GeoInactive, "Geo inactive"),
        (StatusFlags.GeoGpsCalibrationOk, "GPS calibration OK"),
        (StatusFlags.GeoGpsFix, "GPS fix"),
        (StatusFlags.LaserSwitchOn, "Laser switch on"),
        (StatusFlags.LrfBusy, "LRF busy"),
        (StatusFlags.SlantRangeMeasured, "Slant range measured by LRF"),
    };
}

/// <summary>EPSILON_VERSION packet 0x81 (reply to GET_VERSION).</summary>
public sealed class VersionInfo
{
    public int Type { get; init; }
    public ulong UniqueId { get; init; }
    public string Firmware { get; init; }
    public string TiltBootloader { get; init; }
    public string PanBootloader { get; init; }
    public int TiltPcb { get; init; }
    public int PanPcb { get; init; }
    public string VideoProcessor { get; init; }
    public uint VideoProcessorFeatures { get; init; }
    public int EoSensor { get; init; }
    public int IrSensor { get; init; }
    public int IrOptics { get; init; }
    public bool HasLrf { get; init; }
    public bool HasLaserAim { get; init; }
    public bool HasGeo { get; init; }
    public uint EpsilonFeatures { get; init; }
    public string PanFirmware { get; init; }

    /// <summary>Unique ID reported by the bundled Epsilon.Simulator, so the GCS can flag simulated data.</summary>
    public const ulong SimulatorUniqueId = 0x0000_5EED_0000_0180UL;

    public bool IsSimulator => UniqueId == SimulatorUniqueId;

    public string TypeName => Type < TypeNames.Length ? TypeNames[Type] : $"Unknown ({Type})";
    public string EoSensorName => EoSensor < EoNames.Length ? EoNames[EoSensor] : $"Unknown ({EoSensor})";
    public string IrSensorName => IrSensor < IrNames.Length ? IrNames[IrSensor] : $"Unknown ({IrSensor})";
    public string IrOpticsName => IrOptics < OpticsNames.Length ? OpticsNames[IrOptics] : $"Unknown ({IrOptics})";

    public static VersionInfo Parse(byte[] d)
    {
        string V(int o) => $"{ByteReader.U8(d, o)}.{ByteReader.U8(d, o + 1)}.{ByteReader.U8(d, o + 2)}.{ByteReader.U8(d, o + 3)}";
        return new VersionInfo
        {
            Type = ByteReader.U8(d, 0),
            UniqueId = ByteReader.U64(d, 1),
            Firmware = V(9),
            TiltBootloader = V(13),
            PanBootloader = V(17),
            TiltPcb = ByteReader.U8(d, 21),
            PanPcb = ByteReader.U8(d, 22),
            VideoProcessor = $"{ByteReader.U8(d, 23)}.{ByteReader.U8(d, 24)}",
            VideoProcessorFeatures = ByteReader.U32(d, 25),
            EoSensor = ByteReader.U8(d, 29),
            IrSensor = ByteReader.U8(d, 30),
            IrOptics = ByteReader.U8(d, 31),
            HasLrf = ByteReader.U8(d, 32) != 0,
            HasLaserAim = ByteReader.U8(d, 33) != 0,
            HasGeo = ByteReader.U8(d, 34) != 0,
            EpsilonFeatures = ByteReader.U32(d, 35),
            PanFirmware = V(39),
        };
    }

    public IEnumerable<string> VideoProcessorFeatureNames() => BitNames(VideoProcessorFeatures, VpFeatureNames);
    public IEnumerable<string> EpsilonFeatureNames() => BitNames(EpsilonFeatures, EpsFeatureNames);

    private static IEnumerable<string> BitNames(uint value, (int Bit, string Name)[] names) =>
        names.Where(n => (value & (1u << n.Bit)) != 0).Select(n => n.Name);

    public static readonly string[] TypeNames =
    {
        "None", "Epsilon 140 Xenics", "Epsilon 140 FLIR 60mm", "Epsilon 140Z FLIR 3.3 Zoom", "Epsilon 175 MWIR",
        "Epsilon 135", "Epsilon 175D", "Epsilon 175K", "Epsilon 175O", "Epsilon 140 LC 25mm", "Epsilon 180",
        "Epsilon 175 G2", "Epsilon 140 G2 Zoom", "Epsilon 95",
    };

    public static readonly string[] EoNames = { "No camera", "Hitachi", "Sony", "Tamron", "Sony 4K", "Ishot", "Tamron + Spotter IMX" };
    public static readonly string[] IrNames = { "No camera", "FLIR TAU2 30Hz", "FLIR TAU2 9Hz", "FLIR Neutrino LC", "Kinglet", "MWIR", "FLIR Boson" };
    public static readonly string[] OpticsNames = { "Default", "60mm fixed", "3.3 zoom", "Topaz zoom", "Ophir zoom", "25mm fixed", "14mm + 35mm fixed" };

    private static readonly (int, string)[] VpFeatureNames =
    {
        (0, "HD output"), (1, "Stabilization"), (2, "Video compression"), (3, "MTI baseline"), (4, "Tracking"),
        (5, "MTI advanced"), (6, "Stab and track telemetry"), (7, "Enhancement + high depth + temp"), (8, "Blend"), (10, "Recording"),
    };

    private static readonly (int, string)[] EpsFeatureNames =
    {
        (0, "SW map"), (1, "PIP"), (2, "Coordinate measurement"), (3, "Blend"), (16, "External GPS"), (17, "Flipping mode"),
        (18, "Roll horizon alignment"), (19, "Zoom sync"), (20, "IR auto focus"), (21, "IR single focus"), (22, "Inverted mode"),
    };
}

/// <summary>Legacy EPSILON_ERRORS packet 0x82.</summary>
public sealed class LegacyErrors
{
    public uint Critical { get; init; }
    public uint General { get; init; }
    public uint Info { get; init; }

    public static LegacyErrors Parse(byte[] d) => new()
    {
        Critical = ByteReader.U32(d, 0),
        General = ByteReader.U32(d, 4),
        Info = ByteReader.U32(d, 8),
    };

    public IEnumerable<(string Severity, string Text)> ActiveErrors()
    {
        foreach (var e in Bits(Critical, CriticalNames)) yield return ("CRITICAL", e);
        foreach (var e in Bits(General, GeneralNames)) yield return ("GENERAL", e);
        foreach (var e in Bits(Info, InfoNames)) yield return ("INFO", e);
    }

    private static IEnumerable<string> Bits(uint v, string[] names)
    {
        for (int i = 0; i < 32; i++)
            if ((v & (1u << i)) != 0)
                yield return i < names.Length ? names[i] : $"Bit {i}";
    }

    public static readonly string[] CriticalNames =
    {
        "Tilt encoder fault", "Tilt encoder no index", "Tilt motor fault", "Tilt motor overcurrent",
        "Pan encoder fault", "Pan encoder no index", "Pan motor fault", "Pan motor overcurrent",
        "Magnetic angles not set", "Gyroscope sensor fault", "NvRam fault", "Configuration fault",
        "Video processor overheat", "Video processor init failed", "Gyro bias not set",
        "Inner encoder fault", "Inner encoder no index", "Inner motor fault", "Inner motor overcurrent",
    };

    public static readonly string[] GeneralNames =
    {
        "Pan/Tilt communication fault", "NvRam - settings default", "Video processor no communication",
        "Day camera no communication", "IR camera no communication", "IR camera zoom no communication",
        "GPS no communication", "Range finder no communication", "Laser pointer not switching on",
        "Daylight camera zoom init failed", "Daylight camera focus init failed", "IR camera zoom init failed",
        "IR camera focus init failed", "Peripheral power off", "Day camera init failed", "IR camera init failed",
        "GPS init failed", "Range finder init failed", "MWIR focus init failed", "SD card not installed",
    };

    public static readonly string[] InfoNames =
    {
        "IR zoom not connected", "IR zoom motor error", "IR focus motor error", "IR zoom encoder error",
        "IR focus encoder error", "IR zoom communication error", "IR zoom thermistor error",
        "IR zoom shutter error", "IR zoom shutter encoder error",
    };
}

/// <summary>ERROR_HANDLING_RESPONSE packet 0x43.</summary>
public sealed class ErrorReport
{
    public int Command { get; init; }
    public int ResponseId { get; init; }
    public int NextId { get; init; }
    public int ErrorCode { get; init; }
    public int Severity { get; init; }
    public ulong Timestamp { get; init; }
    public int Value { get; init; }
    public int OccurrenceCount { get; init; }
    public int Flags { get; init; }
    public string Description { get; init; }

    public string SeverityName => Severity switch { 1 => "INFO", 2 => "WARNING", 3 => "CRITICAL", _ => Severity.ToString() };

    public string TimeText =>
        Timestamp > 1 && Timestamp < 4102444800UL
            ? DateTimeOffset.FromUnixTimeSeconds((long)Timestamp).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"
            : "no GPS time";

    public static ErrorReport Parse(byte[] d)
    {
        int len = ByteReader.U8(d, 22);
        string desc = "";
        if (d.Length > 23)
        {
            int n = Math.Min(len, d.Length - 23);
            desc = Encoding.ASCII.GetString(d, 23, n).TrimEnd('\0');
        }
        return new ErrorReport
        {
            Command = ByteReader.U8(d, 0),
            ResponseId = ByteReader.U16(d, 1),
            NextId = ByteReader.U16(d, 3),
            ErrorCode = ByteReader.U16(d, 5),
            Severity = ByteReader.U8(d, 7),
            Timestamp = ByteReader.U64(d, 8),
            Value = ByteReader.S32(d, 16),
            OccurrenceCount = ByteReader.U8(d, 20),
            Flags = ByteReader.U8(d, 21),
            Description = desc,
        };
    }
}
