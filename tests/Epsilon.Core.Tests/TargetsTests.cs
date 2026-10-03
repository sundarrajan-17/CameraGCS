using Epsilon.Core.Client;
using Epsilon.Core.Protocol;
using Epsilon.Core.Targets;
using Epsilon.Core.Transport;
using Xunit;

namespace Epsilon.Core.Tests;

public class TargetsTests
{
    // ---------------------------------------------------------------- store

    [Fact]
    public void Targets_GetUniqueIncrementingIdsAndDefaultNames()
    {
        var store = new TargetStore();
        var a = store.AddTarget(13.1, 80.2, TargetSource.Map);
        var b = store.AddTarget(13.2, 80.3, TargetSource.CameraGeo, rangeM: 1500, rangeByLrf: true);
        Assert.Equal(1, a.Id);
        Assert.Equal(2, b.Id);
        Assert.Equal("Target 1", a.Name);
        Assert.Equal("Target 2", b.Name);
        Assert.Equal(1500, b.RangeM);
        Assert.Same(b, store.FindTarget(2));
    }

    [Fact]
    public void Ids_AreNotReusedAfterDelete()
    {
        var store = new TargetStore();
        store.AddTarget(1, 1, TargetSource.Map);
        var two = store.AddTarget(2, 2, TargetSource.Map);
        Assert.True(store.RemoveTarget(two.Id));
        Assert.Equal(3, store.AddTarget(3, 3, TargetSource.Map).Id);
        Assert.Null(store.FindTarget(2));
    }

    [Fact]
    public void Splashes_StorePositionTimeAndSelectedTarget()
    {
        var store = new TargetStore();
        var t = store.AddTarget(13.45, 80.22, TargetSource.Map);
        var time = new DateTime(2026, 10, 3, 16, 49, 46);
        var s1 = store.AddSplash(13.454576, 80.226849, t.Id, time);
        var s2 = store.AddSplash(13.454600, 80.226900);
        Assert.Equal(1, s1.Id);
        Assert.Equal("Splash 1", s1.Name);
        Assert.Equal(13.454576, s1.Latitude, 9);
        Assert.Equal(80.226849, s1.Longitude, 9);
        Assert.Equal(time, s1.DateTime);
        Assert.Equal(t.Id, s1.TargetId);
        Assert.Equal(2, s2.Id);
        Assert.Null(s2.TargetId);
        Assert.Equal(2, store.Splashes.Count);
    }

    [Fact]
    public void InvalidPositions_AreRejected()
    {
        var store = new TargetStore();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddSplash(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddSplash(91, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.AddTarget(double.NaN, 10, TargetSource.Map));
        Assert.Empty(store.Splashes);
        Assert.Empty(store.Targets);
    }

    [Fact]
    public void Changed_IsRaisedOnEveryModification()
    {
        var store = new TargetStore();
        int n = 0;
        store.Changed += () => n++;
        var t = store.AddTarget(1, 1, TargetSource.Map);
        store.UpdateTarget(t.Id, "Bridge", 1.5, 1.5, TargetStatus.Inactive, "note");
        var s = store.AddSplash(1, 1);
        store.RemoveSplash(s.Id);
        store.RemoveTarget(t.Id);
        Assert.Equal(5, n);
    }

    [Fact]
    public void UpdateTarget_ChangesTheModelNotACopy()
    {
        var store = new TargetStore();
        var t = store.AddTarget(1, 1, TargetSource.Map);
        Assert.True(store.UpdateTarget(t.Id, "Bridge", 2, 3, TargetStatus.Inactive, "n"));
        var again = store.FindTarget(t.Id);
        Assert.Equal("Bridge", again.Name);
        Assert.Equal(2, again.Latitude);
        Assert.Equal(TargetStatus.Inactive, again.Status);
        Assert.False(store.UpdateTarget(99, "x", 1, 1, TargetStatus.Active, ""));
    }

    // ---------------------------------------------------------------- camera geo point (Splash [+] source)

    /// <summary>Stands in for the UDP link: hands bytes to the GimbalClient exactly like the receive thread does.</summary>
    private sealed class FakeTransport : IGimbalTransport
    {
        public string Description => "fake";
        public bool IsOpen { get; private set; }
        public event Action<byte[], int> DataReceived;
        public event Action<Exception> Faulted { add { } remove { } }
        public void Open() => IsOpen = true;
        public void Send(byte[] data) { }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public void Receive(byte[] bytes) => DataReceived?.Invoke(bytes, bytes.Length);
    }

    private static byte[] StatusPacket(double geoLat, double geoLon, bool geoInactive = false, int distance = 1234, bool lrf = false)
    {
        uint flags = (uint)(StatusFlags.GeoGpsFix | StatusFlags.GeoGpsCalibrationOk);
        if (geoInactive) flags |= (uint)StatusFlags.GeoInactive;
        if (lrf) flags |= (uint)StatusFlags.SlantRangeMeasured;
        var d = new ByteWriter()
            .U32(flags).U16(0).S16(-3000).U8(0).U8(0).U16(100).U16(200).S8(30).S8(30).S8(40)
            .S32(Cmd.EncodeLatLon(13.40)).S32(Cmd.EncodeLatLon(80.20)).S16(300)
            .S16(0).S16(0).S16(0).S16(450).S16(-300).U16(distance)
            .S32(Cmd.EncodeLatLon(geoLat)).S32(Cmd.EncodeLatLon(geoLon))
            .U8(10).U8(10).U8(12).U8(12).U8(0).U8(0).U8(0x99).U8(10)
            .ToArray();
        return new Packet(MessageId.GlobalStatus, d).Encode();
    }

    [Fact]
    public void GeoPoint_ComesFromGeoLatitudeLongitudeOfTheLatestStatus()
    {
        var client = new GimbalClient { ReopenIntervalMs = 0 };
        var link = new FakeTransport();
        client.Connect(link);
        try
        {
            var controller = new GimbalController(client);
            Assert.False(controller.TryGetGeoPoint(out _, out var why));
            Assert.Contains("No status", why);

            link.Receive(StatusPacket(13.454576, 80.226849, distance: 1500, lrf: true));
            Assert.True(controller.TryGetGeoPoint(out var gp, out _));
            Assert.Equal(13.454576, gp.Latitude, 6);
            Assert.Equal(80.226849, gp.Longitude, 6);
            Assert.Equal(1500, gp.RangeM);
            Assert.True(gp.RangeByLrf);

            // the newest packet wins
            link.Receive(StatusPacket(13.5, 80.3));
            Assert.True(controller.TryGetGeoPoint(out gp, out _));
            Assert.Equal(13.5, gp.Latitude, 6);
        }
        finally { client.Disconnect(); }
    }

    [Fact]
    public void GeoPoint_IsRefusedWhenGeoInactiveOrZero()
    {
        var client = new GimbalClient { ReopenIntervalMs = 0 };
        var link = new FakeTransport();
        client.Connect(link);
        try
        {
            var controller = new GimbalController(client);
            link.Receive(StatusPacket(13.45, 80.22, geoInactive: true));
            Assert.False(controller.TryGetGeoPoint(out _, out var why));
            Assert.Contains("GEO is not active", why);

            link.Receive(StatusPacket(0, 0));
            Assert.False(controller.TryGetGeoPoint(out _, out why));
            Assert.Contains("no valid geo point", why);
        }
        finally { client.Disconnect(); }
    }
}
