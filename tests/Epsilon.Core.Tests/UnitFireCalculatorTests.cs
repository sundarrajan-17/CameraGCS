using Epsilon.Core.Targets;
using Xunit;

namespace Epsilon.Core.Tests;

public class UnitFireCalculatorTests
{
    private const double R = UnitFireCalculator.EarthRadiusM;

    /// <summary>Point offset from (lat, lon) by north / east metres on the same local plane the calculator uses.</summary>
    private static (double Lat, double Lon) Offset((double Lat, double Lon) p, double north, double east) =>
        (p.Lat + north / R * 180 / Math.PI, p.Lon + east / (R * Math.Cos(p.Lat * Math.PI / 180)) * 180 / Math.PI);

    private static readonly (double Lat, double Lon) U0 = (13.45, 80.22);
    private static UnitInfo Unit((double Lat, double Lon) p) => new() { Id = 1, Name = "Unit 1", Latitude = p.Lat, Longitude = p.Lon };
    private static TargetInfo Target((double Lat, double Lon) p) => new() { Id = 2, Name = "Target 1", Latitude = p.Lat, Longitude = p.Lon };
    private static SplashInfo Splash((double Lat, double Lon) p) => new() { Id = 3, Latitude = p.Lat, Longitude = p.Lon };

    private static void Near(double expected, double actual, double tol = 0.05) =>
        Assert.InRange(actual, expected - tol, expected + tol);

    [Fact]
    public void TargetNorth_SplashBeyondAndRight_IsPositivePositive()
    {
        var t = Offset(U0, 2000, 0);
        var c = UnitFireCalculator.Calculate(Unit(U0), Target(t), Splash(Offset(t, 30, 50)));
        Near(2000, c.UnitToTargetDistanceM);
        Near(0, c.UnitToTargetBearingDeg, 0.01);
        Near(2030.62, c.UnitToSplashDistanceM);
        Near(1.411, c.UnitToSplashBearingDeg, 0.01);
        Near(30, c.AddDropM);        // beyond
        Near(50, c.LeftRightM);      // right
        Near(-30, c.CorrectionAddDropM);
        Near(-50, c.CorrectionLeftRightM);
        Assert.Equal((1, 2, 3), (c.UnitId, c.TargetId, c.SplashId));
    }

    [Fact]
    public void TargetEast_SplashShortAndNorth_IsShortAndLeft()
    {
        var t = Offset(U0, 0, 3000);
        var c = UnitFireCalculator.Calculate(Unit(U0), Target(t), Splash(Offset(t, 40, -100)));
        Near(90, c.UnitToTargetBearingDeg, 0.01);
        Near(-100, c.AddDropM);      // short
        Near(-40, c.LeftRightM);     // north of an eastward line = left
    }

    [Fact]
    public void SplashOnTarget_GivesZeroDeviation_AndBearingIsNormalised()
    {
        var t = Offset(U0, -1500, -1500);
        var c = UnitFireCalculator.Calculate(Unit(U0), Target(t), Splash(t));
        Near(225, c.UnitToTargetBearingDeg, 0.01);    // south-west, 0..360 not -135
        Near(0, c.AddDropM);
        Near(0, c.LeftRightM);
        Near(c.UnitToTargetDistanceM, c.UnitToSplashDistanceM);
    }

    [Fact]
    public void SplashPerpendicularLeft_HasNoAddDrop()
    {
        var c = UnitFireCalculator.Calculate(Unit(U0), Target(Offset(U0, 1000, 1000)), Splash(Offset(U0, 1500, 500)));
        Near(0, c.AddDropM, 0.05);
        Near(-707.10, c.LeftRightM, 0.05);
        Near(18.43, c.UnitToSplashBearingDeg, 0.01);
    }

    [Fact]
    public void ToLocal_UsesTheDocumentedFormula()
    {
        var (east, north) = UnitFireCalculator.ToLocal(13.0, 80.0, 13.01, 80.01);
        double mean = 13.005 * Math.PI / 180;
        Near(0.01 * Math.PI / 180 * R, north, 1e-6);
        Near(0.01 * Math.PI / 180 * R * Math.Cos(mean), east, 1e-6);
    }

    [Fact]
    public void MissingOrDegenerateInput_IsRejected()
    {
        var t = Target(Offset(U0, 1000, 0));
        var s = Splash(Offset(U0, 1000, 10));
        Assert.Throws<ArgumentNullException>(() => UnitFireCalculator.Calculate(null, t, s));
        Assert.Throws<ArgumentNullException>(() => UnitFireCalculator.Calculate(Unit(U0), null, s));
        Assert.Throws<ArgumentNullException>(() => UnitFireCalculator.Calculate(Unit(U0), t, null));
        Assert.Throws<ArgumentException>(() => UnitFireCalculator.Calculate(Unit(U0), Target(U0), s));   // unit == target
    }

    [Fact]
    public void UnitStore_AddFindRemove_AndLastCalculation()
    {
        var store = new UnitStore();
        int changed = 0;
        store.Changed += () => changed++;
        var a = store.AddUnit(13.45, 80.22);
        var b = store.AddUnit(13.46, 80.21);
        Assert.Equal(("Unit 1", "Unit 2"), (a.Name, b.Name));
        Assert.Same(b, store.FindUnit(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddUnit(0, 0));

        var t = Offset((a.Latitude, a.Longitude), 1000, 0);
        store.SetLastCalculation(UnitFireCalculator.Calculate(a, Target(t), Splash(t)));
        Assert.NotNull(store.LastCalculation);
        Assert.True(store.RemoveUnit(a.Id));
        Assert.Null(store.LastCalculation);            // its unit is gone
        Assert.Equal(3, store.AddUnit(13.47, 80.20).Id); // IDs are not reused
        Assert.Equal(5, changed);
    }
}
