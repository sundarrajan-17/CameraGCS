using Epsilon.Core.Targets;
using Xunit;

namespace Epsilon.Core.Tests;

public class FireCorrectionTests
{
    private const double R = 6378137.0;

    /// <summary>Point offset from (lat, lon) by north / east metres (same flat model as latlon_to_xy_approx).</summary>
    private static (double Lat, double Lon) Offset(double lat, double lon, double north, double east) =>
        (lat + north / R * 180 / Math.PI, lon + east / (R * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI);

    private static TargetInfo Target(double lat, double lon) => new() { Id = 1, Name = "Target 1", Latitude = lat, Longitude = lon };

    private static SplashInfo Splash(int id, (double Lat, double Lon) p) =>
        new() { Id = id, Latitude = p.Lat, Longitude = p.Lon, TargetId = 1, DateTime = DateTime.Now };

    private static void Near(double expected, double actual, double tol = 0.5) =>
        Assert.InRange(actual, expected - tol, expected + tol);

    [Fact]
    public void Xy_TargetOnTheBearingLine_HasZeroAcrossAndFullRangeAlong()
    {
        double camLat = 13.40, camLon = 80.20;
        var t = Offset(camLat, camLon, 3000, 3000);
        double az = FireCorrection.Azimuth(camLat, camLon, t.Lat, t.Lon);
        double[] xy = FireCorrection.latlon_to_xy_approx(camLat, camLon, t.Lat, t.Lon, az);
        Near(0, xy[0], 2);
        Near(Math.Sqrt(2) * 3000, xy[1], 3);
    }

    [Fact]
    public void Mpi_IsTheMeanOfTheSplashes()
    {
        var (lat, lon) = FireCorrection.Mpi(new[]
        {
            new SplashInfo { Id = 1, Latitude = 13.0, Longitude = 80.0 },
            new SplashInfo { Id = 2, Latitude = 13.2, Longitude = 80.4 },
        });
        Assert.Equal(13.1, lat, 9);
        Assert.Equal(80.2, lon, 9);
    }

    [Fact]
    public void CameraLookingNorth_MpiEastAndBeyond_GivesLeftAndDrop()
    {
        double camLat = 13.40, camLon = 80.20;
        var tgt = Offset(camLat, camLon, 2000, 0);                 // target 2 km due north
        var splash = Offset(tgt.Lat, tgt.Lon, 30, 50);              // 50 m right (east), 30 m beyond
        var c = FireCorrection.Calculate(camLat, camLon, Target(tgt.Lat, tgt.Lon), new[] { Splash(1, splash) });
        Near(0, c.BearingDeg, 0.1);
        Near(2000, c.TargetRangeM, 1);
        Near(-50, c.LeftRightM);                                    // move fire LEFT 50
        Near(-30, c.AddDropM);                                      // DROP 30
        Assert.Equal("Left 50 m", FireCorrection.FormatLeftRight(c.LeftRightM, "m"));
        Assert.Equal("Drop 30 m", FireCorrection.FormatAddDrop(c.AddDropM, "m"));
    }

    [Fact]
    public void CameraLookingEast_MpiNorthAndShort_GivesLeftAndAdd()
    {
        double camLat = 13.40, camLon = 80.20;
        var tgt = Offset(camLat, camLon, 0, 3000);                 // target 3 km due east
        var splash = Offset(tgt.Lat, tgt.Lon, 40, -100);            // 40 m north (= left of the line), 100 m short
        var c = FireCorrection.Calculate(camLat, camLon, Target(tgt.Lat, tgt.Lon), new[] { Splash(1, splash) });
        Near(90, c.BearingDeg, 0.1);
        Near(40, c.LeftRightM, 1);                                  // MPI is left -> correct RIGHT 40
        Near(100, c.AddDropM, 1);                                   // MPI is short -> ADD 100
        Assert.Equal("Right 40 m", FireCorrection.FormatLeftRight(c.LeftRightM, "m"));
        Assert.Equal("Add 100 m", FireCorrection.FormatAddDrop(c.AddDropM, "m"));
    }

    [Fact]
    public void Correction_UsesTheMpiOfAllSplashes()
    {
        double camLat = 13.40, camLon = 80.20;
        var tgt = Offset(camLat, camLon, 2000, 0);
        var c = FireCorrection.Calculate(camLat, camLon, Target(tgt.Lat, tgt.Lon), new[]
        {
            Splash(1, Offset(tgt.Lat, tgt.Lon, 60, 20)),
            Splash(2, Offset(tgt.Lat, tgt.Lon, 0, 40)),
        });                                                         // MPI: 30 m beyond, 30 m right
        Assert.Equal(2, c.SplashCount);
        Near(-30, c.LeftRightM);
        Near(-30, c.AddDropM);
    }

    [Fact]
    public void Units_AreConverted()
    {
        Assert.Equal("Right 328 ft", FireCorrection.FormatLeftRight(100, "ft"));
        Assert.Equal("Drop 109 yd", FireCorrection.FormatAddDrop(-100, "yd"));
        Assert.Equal("0 m", FireCorrection.FormatAddDrop(0.2, "m"));
    }

    [Fact]
    public void NoSplashes_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            FireCorrection.Calculate(13.4, 80.2, Target(13.5, 80.2), Array.Empty<SplashInfo>()));
    }
}
