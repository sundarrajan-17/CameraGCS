namespace Epsilon.Core.Targets;

/// <summary>
/// A unit position marked by the operator (taken from the camera's geo point). It is the reference/origin
/// for <see cref="UnitFireCalculator"/>. The map marker and the Units list both show this object.
/// </summary>
public sealed class UnitInfo
{
    public int Id { get; init; }
    public string Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Local time the unit was created.</summary>
    public DateTime DateTime { get; init; }
    /// <summary>Where the position came from (camera geo point, map click or typed by the operator).</summary>
    public TargetSource Source { get; init; } = TargetSource.CameraGeo;
}
