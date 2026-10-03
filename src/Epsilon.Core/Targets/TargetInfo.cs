namespace Epsilon.Core.Targets;

public enum TargetStatus { Active, Inactive }

/// <summary>Where a target's coordinates came from.</summary>
public enum TargetSource
{
    /// <summary>The gimbal's GEO target (GEO_LATITUDE / GEO_LONGITUDE of EPSILON_GLOBAL_STATUS 0x80).</summary>
    CameraGeo,
    /// <summary>A point the operator picked on the map.</summary>
    Map,
    /// <summary>Coordinates typed by the operator.</summary>
    Manual,
}

/// <summary>A target marked by the operator. The map marker and the Targets list both show this object.</summary>
public sealed class TargetInfo
{
    public int Id { get; init; }
    public string Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Local time the target was created.</summary>
    public DateTime DateTime { get; init; }
    public TargetStatus Status { get; set; } = TargetStatus.Active;
    public TargetSource Source { get; init; }
    /// <summary>Slant range from the camera when the target was taken from the camera (metres), otherwise null.</summary>
    public int? RangeM { get; init; }
    /// <summary>Whether the range was measured by the LRF (STATUS_FLAGS bit 26) or calculated.</summary>
    public bool RangeMeasuredByLrf { get; init; }
    public string Notes { get; set; } = "";
}

/// <summary>A splash (observed impact point) recorded from the camera's current geo position.</summary>
public sealed class SplashInfo
{
    public int Id { get; init; }
    public DateTime DateTime { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    /// <summary>Target that was selected when the splash was recorded, or null.</summary>
    public int? TargetId { get; init; }
    public string Name => $"Splash {Id}";
}
