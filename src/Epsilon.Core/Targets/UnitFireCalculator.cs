namespace Epsilon.Core.Targets;

/// <summary>
/// Result of <see cref="UnitFireCalculator.Calculate"/>. Distances in metres, bearings in degrees 0..360
/// clockwise from true north, all measured from the Unit.
///
/// SIGN CONVENTION (deviation of the SPLASH from the TARGET, relative to the Unit -> Target line):
///   AddDropM   &gt; 0 : splash is BEYOND the target (over)      &lt; 0 : splash is SHORT of the target
///   LeftRightM &gt; 0 : splash is RIGHT of the Unit -> Target line &lt; 0 : splash is LEFT of the line
///   (right/left as seen from the Unit looking towards the Target)
///
/// This is a deviation, not a correction. The correction that moves the next splash onto the target has the
/// opposite sign: see <see cref="CorrectionLeftRightM"/> / <see cref="CorrectionAddDropM"/>, which use the same
/// convention as the existing <see cref="FireCorrection"/> (+ = Right / Add).
/// </summary>
public sealed record UnitFireCalculation(
    int UnitId, int TargetId, int SplashId,
    double UnitToTargetDistanceM, double UnitToTargetBearingDeg,
    double UnitToSplashDistanceM, double UnitToSplashBearingDeg,
    double AddDropM, double LeftRightM,
    DateTime CalculatedAt)
{
    /// <summary>Correction to apply along the line: + = Add, - = Drop (= -AddDropM).</summary>
    public double CorrectionAddDropM => -AddDropM;

    /// <summary>Correction to apply across the line: + = Right, - = Left (= -LeftRightM).</summary>
    public double CorrectionLeftRightM => -LeftRightM;
}

/// <summary>
/// Unit / Target / Splash geometry on a local tangent plane centred on the Unit (East / North metres).
/// Adequate for the short distances involved (errors grow with distance; ~0.1 % at tens of km).
/// </summary>
public static class UnitFireCalculator
{
    /// <summary>Earth radius used for the local tangent plane, metres.</summary>
    public const double EarthRadiusM = 6371000.0;

    /// <summary>Minimum Unit -> Target distance for a meaningful reference line.</summary>
    public const double MinBaselineM = 1.0;

    /// <summary>
    /// Local East / North (metres) of a point relative to the origin:
    ///   north = dLat(rad) * R
    ///   east  = dLon(rad) * R * cos(mean latitude)
    /// </summary>
    public static (double East, double North) ToLocal(double originLat, double originLon, double lat, double lon)
    {
        const double d2r = Math.PI / 180.0;
        double meanLat = (originLat + lat) / 2.0 * d2r;
        double dLon = lon - originLon;
        if (dLon > 180) dLon -= 360;          // shortest way across the antimeridian
        if (dLon < -180) dLon += 360;
        double north = (lat - originLat) * d2r * EarthRadiusM;
        double east = dLon * d2r * EarthRadiusM * Math.Cos(meanLat);
        return (east, north);
    }

    /// <summary>Bearing of a local vector: atan2(East, North), degrees normalised to 0..360.</summary>
    public static double Bearing(double east, double north)
    {
        double deg = Math.Atan2(east, north) * 180.0 / Math.PI;
        return (deg % 360 + 360) % 360;
    }

    /// <summary>
    /// 1. Converts Target and Splash to local East/North relative to the Unit.
    /// 2. Unit -> Target and Unit -> Splash distance and bearing.
    /// 3. Forward axis f = unit vector Unit -> Target; right axis r = f rotated 90° clockwise = (f.North, -f.East).
    /// 4. SplashForward = S·f; AddDrop = SplashForward - |T|.  LeftRight = S·r.
    /// </summary>
    /// <exception cref="ArgumentNullException">A position is missing.</exception>
    /// <exception cref="ArgumentException">A position is invalid, or Unit and Target are (almost) the same point.</exception>
    public static UnitFireCalculation Calculate(UnitInfo unit, TargetInfo target, SplashInfo splash)
    {
        if (unit == null) throw new ArgumentNullException(nameof(unit), "No unit selected.");
        if (target == null) throw new ArgumentNullException(nameof(target), "No target selected.");
        if (splash == null) throw new ArgumentNullException(nameof(splash), "No splash selected.");
        if (!TargetStore.IsValidPosition(unit.Latitude, unit.Longitude)) throw new ArgumentException("Unit position is not valid.");
        if (!TargetStore.IsValidPosition(target.Latitude, target.Longitude)) throw new ArgumentException("Target position is not valid.");
        if (!TargetStore.IsValidPosition(splash.Latitude, splash.Longitude)) throw new ArgumentException("Splash position is not valid.");

        var t = ToLocal(unit.Latitude, unit.Longitude, target.Latitude, target.Longitude);
        var s = ToLocal(unit.Latitude, unit.Longitude, splash.Latitude, splash.Longitude);

        double targetDist = Math.Sqrt(t.East * t.East + t.North * t.North);
        if (targetDist < MinBaselineM)
            throw new ArgumentException($"{target.Name} is at the unit position - the Unit -> Target direction is undefined.");
        double splashDist = Math.Sqrt(s.East * s.East + s.North * s.North);

        // Forward (Unit -> Target) and right-hand perpendicular, both unit vectors in (East, North).
        double fE = t.East / targetDist, fN = t.North / targetDist;
        double rE = fN, rN = -fE;

        double splashForward = s.East * fE + s.North * fN;   // projection on the Unit -> Target line
        double addDrop = splashForward - targetDist;         // + beyond / - short
        double leftRight = s.East * rE + s.North * rN;       // + right / - left

        return new UnitFireCalculation(
            unit.Id, target.Id, splash.Id,
            targetDist, Bearing(t.East, t.North),
            splashDist, Bearing(s.East, s.North),
            addDrop, leftRight, DateTime.Now);
    }
}
