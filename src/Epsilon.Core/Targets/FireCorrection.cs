namespace Epsilon.Core.Targets;

/// <summary>Result of a correction calculation. Distances are metres.</summary>
public sealed record CorrectionResult(
    double MpiLatitude, double MpiLongitude, int SplashCount,
    double BearingDeg, double TargetRangeM,
    double LeftRightM,   // + = Right, - = Left   (move the fire this much sideways, seen from the camera)
    double AddDropM);    // + = Add,   - = Drop   (move the fire this much along the camera-target line)

/// <summary>
/// Correction from the splashes to the target, in the camera-target frame:
/// MPI (mean point of impact) of the splashes, then Left/Right and Add/Drop that move the MPI onto the target,
/// as seen from the camera position.
/// </summary>
public static class FireCorrection
{
    /// <summary>
    /// Offset of (lat2, lon2) from (lat1, lon1) in a frame rotated by <paramref name="bearing"/>.
    /// With lat1/lon1 = camera, lat2/lon2 = target and bearing = azimuth camera -> target:
    /// [0] = X across the line of sight (+ right), [1] = Y along the line of sight (+ away from the camera), metres.
    /// </summary>
    public static double[] latlon_to_xy_approx(double lat1, double lon1, double lat2, double lon2, double bearing) {
        double PI = 3.14159265358979323846;
        double R = 6378137.0;

        double phi1 = lat1 * PI / 180.0;
        double phi2 = lat2 * PI / 180.0;
        double dphi = phi2 - phi1;
        double dlambda = (lon2 - lon1) * PI / 180.0;
        double mean_phi = 0.5 * (phi1 + phi2);

        double north = R * dphi;
        double east  = R *  Math.Cos(mean_phi) * dlambda;

        double dLat = lat2 - lat1;
        double dLon = lon2 - lon1;

        double latMeanRad = (lat1 + lat2) / 2.0 * Math.PI / 180.0;

        double Y = dLat * Math.PI / 180.0 * R;
        double X = dLon * Math.PI / 180.0 * R * Math.Cos(latMeanRad);

        double bRad = bearing * Math.PI / 180.0;
        double cosB = Math.Cos(bRad);
        double sinB = Math.Sin(bRad);

        double Xb = X * cosB - Y * sinB;
        double Yb = X * sinB + Y * cosB;

        return new double[] {Xb, Yb};
    }

    /// <summary>Initial great-circle azimuth from point 1 to point 2, degrees 0..360 clockwise from north.</summary>
    public static double Azimuth(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>Mean point of impact: average latitude / longitude of the splashes.</summary>
    public static (double Lat, double Lon) Mpi(IReadOnlyCollection<SplashInfo> splashes)
    {
        if (splashes == null || splashes.Count == 0) throw new ArgumentException("At least one splash is needed.", nameof(splashes));
        return (splashes.Average(s => s.Latitude), splashes.Average(s => s.Longitude));
    }

    /// <summary>
    /// Calculates MPI and the Left/Right, Add/Drop correction that moves the MPI onto the target.
    /// Both MPI and target are converted with <see cref="latlon_to_xy_approx"/> from the camera position,
    /// using the azimuth camera -> target, so X is across and Y along the camera-target line.
    /// </summary>
    public static CorrectionResult Calculate(double cameraLat, double cameraLon, TargetInfo target, IReadOnlyCollection<SplashInfo> splashes)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (!TargetStore.IsValidPosition(cameraLat, cameraLon)) throw new ArgumentException("Camera position is not valid.");
        var (mpiLat, mpiLon) = Mpi(splashes);

        double bearing = Azimuth(cameraLat, cameraLon, target.Latitude, target.Longitude);
        double[] t = latlon_to_xy_approx(cameraLat, cameraLon, target.Latitude, target.Longitude, bearing);
        double[] m = latlon_to_xy_approx(cameraLat, cameraLon, mpiLat, mpiLon, bearing);

        // Correction = where the target is minus where the rounds landed.
        double leftRight = t[0] - m[0];   // MPI left of target -> positive -> "Right"
        double addDrop = t[1] - m[1];     // MPI short of target -> positive -> "Add"
        return new CorrectionResult(mpiLat, mpiLon, splashes.Count, bearing, t[1], leftRight, addDrop);
    }

    /// <summary>Metres to the display unit (m, ft or yd).</summary>
    public static double ToUnit(double metres, string unit) => unit switch
    {
        "ft" => metres / 0.3048,
        "yd" => metres / 0.9144,
        _ => metres,
    };

    /// <summary>"Right 25 m" / "Left 25 m" / "0 m".</summary>
    public static string FormatLeftRight(double metres, string unit) => Format(metres, unit, "Right", "Left");

    /// <summary>"Add 40 m" / "Drop 40 m" / "0 m".</summary>
    public static string FormatAddDrop(double metres, string unit) => Format(metres, unit, "Add", "Drop");

    private static string Format(double metres, string unit, string positive, string negative)
    {
        double v = Math.Round(ToUnit(metres, unit));
        if (v == 0) return $"0 {unit}";
        return $"{(v > 0 ? positive : negative)} {Math.Abs(v):0} {unit}";
    }
}
