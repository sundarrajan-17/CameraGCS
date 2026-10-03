using Epsilon.Core.Client;
using Epsilon.Core.Geo;
using Epsilon.Core.Protocol;
using Xunit;

namespace Epsilon.Core.Tests;

public class CorrectionsTests
{
    // ---- 1. tilt direction ------------------------------------------------------------

    [Fact]
    public void TiltUp_SendsNegativeTiltSpeed_ByDefault()
    {
        var c = new GimbalController(new GimbalClient());
        c.SetTilt(40);                       // operator: tilt UP
        Assert.Equal(-40, c.Client.Rate.TiltSpeed);
        c.SetPanTilt(10, -25);               // operator: tilt DOWN
        Assert.Equal(10, c.Client.Rate.PanSpeed);
        Assert.Equal(25, c.Client.Rate.TiltSpeed);
    }

    [Fact]
    public void TiltDirection_CanBeSwitchedBack()
    {
        var c = new GimbalController(new GimbalClient()) { InvertTilt = false };
        c.SetTilt(40);
        Assert.Equal(40, c.Client.Rate.TiltSpeed);
        Assert.Equal((sbyte)40, (sbyte)c.Client.Rate.BuildPacket().Data[1]);
    }

    // ---- 2. camera view polygon ---------------------------------------------------------

    private static (double N, double E) Offset((double Lat, double Lon) p, double lat0, double lon0) =>
        ((p.Lat - lat0) * 111320.0, (p.Lon - lon0) * 111320.0 * Math.Cos(lat0 * Math.PI / 180));

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 1.0, expected + 1.0);

    [Fact]
    public void Footprint_LookingNorthAt45Degrees()
    {
        var c = CameraFootprint.Compute(13.0, 80.0, 100, 0, -45, 60, 40);
        Assert.NotNull(c);
        var tl = Offset(c[0], 13, 80); var tr = Offset(c[1], 13, 80);
        var br = Offset(c[2], 13, 80); var bl = Offset(c[3], 13, 80);
        Near(214.5, tl.N); Near(-128.4, tl.E);   // far-left
        Near(214.5, tr.N); Near(128.4, tr.E);    // far-right
        Near(46.6, br.N); Near(59.9, br.E);      // near-right
        Near(46.6, bl.N); Near(-59.9, bl.E);     // near-left
    }

    [Fact]
    public void Footprint_LookingEast_RotatesWithAzimuth()
    {
        var c = CameraFootprint.Compute(0.5, 10.0, 100, 90, -45, 60, 40);
        var far = Offset(c[0], 0.5, 10.0);
        Assert.True(far.E > 200);           // far edge is east of the camera
        Assert.True(far.N > 100);           // left side of an east-looking camera is north
    }

    [Fact]
    public void Footprint_AboveHorizon_IsClippedAtMaxRange()
    {
        var c = CameraFootprint.Compute(13.0, 80.0, 100, 0, 0, 30, 20, maxRangeM: 5000);
        var far = Offset(c[0], 13, 80);
        Assert.InRange(Math.Sqrt(far.N * far.N + far.E * far.E), 4900, 5100);
    }

    [Fact]
    public void Footprint_NoPositionOrHeight_ReturnsNull()
    {
        Assert.Null(CameraFootprint.Compute(0, 0, 100, 0, -45, 60, 40));
        Assert.Null(CameraFootprint.Compute(13, 80, 0, 0, -45, 60, 40));
        Assert.Null(CameraFootprint.HeightAboveGround(0, -30, 0, null));
        Assert.Equal(50, CameraFootprint.HeightAboveGround(100, -30, 0, null).Value, 3);
        Assert.Equal(350, CameraFootprint.HeightAboveGround(0, -30, 450, 100).Value, 3);
    }

    // ---- 5. communication robustness -------------------------------------------------------

    [Fact]
    public void Parser_RecoversPacketHiddenBehindTruncatedPacket()
    {
        // A packet whose tail was lost, immediately followed by a good packet: the good packet must not be lost.
        var truncated = Cmd.GeoLock(13.0, 80.0).Encode().Take(9).ToArray();
        var good = Cmd.VideoStabilization(true).Encode();
        var stream = truncated.Concat(good).Concat(Cmd.PipSettings(1, 2).Encode()).ToArray();

        var parser = new PacketParser();
        var got = new List<Packet>();
        parser.PacketReceived += got.Add;
        parser.Feed(stream);

        Assert.Contains(got, p => p.Id == MessageId.VideoStabilization);
        Assert.Contains(got, p => p.Id == MessageId.PipSettings);
    }

    [Fact]
    public void Parser_RecoversAcrossSplitReadsAfterCorruption()
    {
        var bad = Cmd.VideoStabilization(true).Encode();
        bad[6] ^= 0x55;                                   // corrupt data byte
        var stream = bad.Concat(Cmd.GetVersion().Encode()).Concat(Cmd.PipSettings(0, 1).Encode()).ToArray();
        var parser = new PacketParser();
        var got = new List<Packet>();
        parser.PacketReceived += got.Add;
        foreach (var b in stream) parser.Feed(new[] { b }); // one byte per read (serial)
        Assert.Equal(2, got.Count);
        Assert.Equal(MessageId.GetVersion, got[0].Id);
        Assert.Equal(MessageId.PipSettings, got[1].Id);
        Assert.Equal(1, parser.BadPackets);
    }

    [Fact]
    public void Parser_LongRandomStream_FindsAllPackets()
    {
        var rnd = new Random(7);
        var stream = new List<byte>();
        int expected = 0;
        for (int i = 0; i < 300; i++)
        {
            var noise = new byte[rnd.Next(0, 6)];
            rnd.NextBytes(noise);
            stream.AddRange(noise);
            stream.AddRange(Cmd.RateControl(rnd.Next(-100, 101), rnd.Next(-100, 101), 0, 0, 0, 0, 0).Encode());
            expected++;
        }
        var parser = new PacketParser();
        int got = 0;
        parser.PacketReceived += p => { if (p.Id == MessageId.RateControl) got++; };
        var all = stream.ToArray();
        for (int i = 0; i < all.Length; i += 37) parser.Feed(all, i, Math.Min(37, all.Length - i));
        Assert.Equal(expected, got);
    }
}
