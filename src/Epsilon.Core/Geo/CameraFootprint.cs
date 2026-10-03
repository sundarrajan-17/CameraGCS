namespace Epsilon.Core.Geo;

/// <summary>
/// Ground footprint of the camera field of view ("camera view polygon" on the map).
/// Flat-earth model around the camera: each image corner is a ray from the camera that is
/// intersected with the ground plane <c>heightAboveGround</c> metres below the camera.
/// Rays at or above the horizon are clipped at <c>maxRangeM</c>.
/// </summary>
public static class CameraFootprint
{
    private const double MetersPerDegLat = 111320.0;

    /// <summary>
    /// Returns the 4 ground corners (top-left, top-right, bottom-right, bottom-left of the image),
    /// or null when the inputs cannot give a footprint.
    /// </summary>
    /// <param name="azimuthDeg">Line-of-sight azimuth, degrees clockwise from true north.</param>
    /// <param name="elevationDeg">Line-of-sight elevation, degrees (negative = looking down).</param>
    public static (double Lat, double Lon)[] Compute(double camLat, double camLon, double heightAboveGround,
        double azimuthDeg, double elevationDeg, double hfovDeg, double vfovDeg, double maxRangeM = 20000)
    {
        if (heightAboveGround <= 0.5 || hfovDeg <= 0 || vfovDeg <= 0 || hfovDeg >= 179 || vfovDeg >= 179) return null;
        if (camLat == 0 && camLon == 0) return null;

        double az = azimuthDeg * Math.PI / 180, el = elevationDeg * Math.PI / 180;
        // North-East-Up unit vectors of the camera frame.
        var f = (N: Math.Cos(el) * Math.Cos(az), E: Math.Cos(el) * Math.Sin(az), U: Math.Sin(el));
        var r = (N: -Math.Sin(az), E: Math.Cos(az), U: 0.0);
        var u = (N: f.E * r.U - f.U * r.E, E: f.U * r.N - f.N * r.U, U: f.N * r.E - f.E * r.N); // f x r
        double th = Math.Tan(hfovDeg / 2 * Math.PI / 180), tv = Math.Tan(vfovDeg / 2 * Math.PI / 180);

        var corners = new (double sx, double sy)[] { (-1, 1), (1, 1), (1, -1), (-1, -1) };
        var result = new (double Lat, double Lon)[4];
        double cosLat = Math.Cos(camLat * Math.PI / 180);
        if (Math.Abs(cosLat) < 1e-6) return null;

        for (int i = 0; i < 4; i++)
        {
            var (sx, sy) = corners[i];
            double dn = f.N + sx * th * r.N + sy * tv * u.N;
            double de = f.E + sx * th * r.E + sy * tv * u.E;
            double du = f.U + sx * th * r.U + sy * tv * u.U;
            double horiz = Math.Sqrt(dn * dn + de * de);
            if (horiz < 1e-9) { dn = f.N; de = f.E; horiz = Math.Max(1e-9, Math.Sqrt(dn * dn + de * de)); }

            double groundDist;
            if (du < -1e-6)
                groundDist = Math.Min(heightAboveGround / -du * horiz, maxRangeM); // distance along the ground
            else
                groundDist = maxRangeM;                                            // at / above the horizon

            double north = dn / horiz * groundDist, east = de / horiz * groundDist;
            result[i] = (camLat + north / MetersPerDegLat, camLon + east / (MetersPerDegLat * cosLat));
        }
        return result;
    }

    /// <summary>
    /// Height of the camera above the ground. Uses the measured/calculated slant range to the geo target when
    /// available (most reliable), otherwise camera altitude minus the ground altitude, otherwise null.
    /// </summary>
    public static double? HeightAboveGround(double slantRangeM, double losElevationDeg, double camAltM, double? groundAltM)
    {
        if (slantRangeM > 0 && losElevationDeg < -0.5)
            return slantRangeM * Math.Sin(-losElevationDeg * Math.PI / 180);
        if (groundAltM.HasValue && camAltM - groundAltM.Value > 0.5)
            return camAltM - groundAltM.Value;
        return null;
    }
}
