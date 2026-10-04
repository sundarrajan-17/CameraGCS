using Epsilon.Core.Targets;
using Xunit;

namespace Epsilon.Core.Tests;

public class ManualCoordinateTests
{
    private static (double Lat, double Lon) Ok(string lat, string lon)
    {
        Assert.True(CoordinateParser.TryParse(lat, lon, out double a, out double b, out string error), error);
        return (a, b);
    }

    private static string Fails(string lat, string lon)
    {
        Assert.False(CoordinateParser.TryParse(lat, lon, out _, out _, out string error));
        Assert.False(string.IsNullOrEmpty(error));
        return error;
    }

    [Theory]
    [InlineData("13.454576", "80.226849", 13.454576, 80.226849)]
    [InlineData("13.454576N", "80.226849 E", 13.454576, 80.226849)]
    [InlineData("13.454576 S", "W 80.226849", -13.454576, -80.226849)]
    [InlineData("-13.454576", "-80.226849", -13.454576, -80.226849)]
    [InlineData("13°27'16.47\"N", "80°13'36.66\"E", 13.454575, 80.226850)]
    [InlineData("13 27 16.47 N", "80 13 36.66 E", 13.454575, 80.226850)]
    [InlineData("13 27.2746 N", "80 13.6110 E", 13.454577, 80.226850)]
    [InlineData("13,454576", "80,226849", 13.454576, 80.226849)]
    public void AcceptedFormats(string lat, string lon, double expLat, double expLon)
    {
        var (a, b) = Ok(lat, lon);
        Assert.Equal(expLat, a, 5);
        Assert.Equal(expLon, b, 5);
    }

    [Theory]
    [InlineData("13.454576, 80.226849")]
    [InlineData("13.454576; 80.226849")]
    [InlineData("13.454576 80.226849")]
    [InlineData("13.454576N, 80.226849E")]
    public void PairPastedIntoLatitude(string pair)
    {
        var (a, b) = Ok(pair, "");
        Assert.Equal(13.454576, a, 6);
        Assert.Equal(80.226849, b, 6);
    }

    [Fact]
    public void Rejected_WithAReason()
    {
        Assert.Contains("Enter a latitude", Fails("", "80"));
        Assert.Contains("Enter a longitude", Fails("13.4", ""));
        Assert.Contains("outside", Fails("91", "80"));
        Assert.Contains("outside", Fails("13", "181"));
        Assert.Contains("0, 0", Fails("0", "0"));
        Assert.Contains("not a coordinate", Fails("abc", "80"));
        Assert.Contains("not a latitude hemisphere", Fails("13.4 E", "80"));
        Assert.Contains("not a longitude hemisphere", Fails("13.4", "80 N"));
        Assert.Contains("minutes", Fails("13 61 0 N", "80"));
        Assert.Contains("seconds", Fails("13 27 60 N", "80"));
        Assert.Contains("either a minus sign or a hemisphere", Fails("-13.4 S", "80"));
    }

    [Fact]
    public void UnitStore_UpdateUnit_ChangesTheModel()
    {
        var store = new UnitStore();
        var u = store.AddUnit(13.45, 80.22, source: TargetSource.Manual);
        Assert.Equal(TargetSource.Manual, u.Source);
        int changed = 0;
        store.Changed += () => changed++;
        Assert.True(store.UpdateUnit(u.Id, "Gun line", 13.46, 80.21));
        Assert.Equal(("Gun line", 13.46, 80.21), (u.Name, u.Latitude, u.Longitude));
        Assert.True(store.UpdateUnit(u.Id, "  ", 13.47, 80.20));    // blank name keeps the old one
        Assert.Equal("Gun line", u.Name);
        Assert.False(store.UpdateUnit(99, "x", 13.4, 80.2));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdateUnit(u.Id, "x", 95, 80.2));
        Assert.Equal(2, changed);
    }

    [Fact]
    public void ManualTarget_IsStoredWithManualSource()
    {
        var store = new TargetStore();
        var t = store.AddTarget(13.454576, 80.226849, TargetSource.Manual, "Bridge");
        Assert.Equal(TargetSource.Manual, t.Source);
        Assert.Equal("Bridge", t.Name);
    }
}
