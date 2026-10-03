using Epsilon.Video;
using Xunit;

namespace Epsilon.Core.Tests;

public class TsInspectorTests
{
    private static byte[] TsPacket(int pid, byte[] payload)
    {
        var p = Enumerable.Repeat((byte)0xFF, 188).ToArray();
        p[0] = 0x47;
        p[1] = (byte)(0x40 | ((pid >> 8) & 0x1F)); // payload unit start
        p[2] = (byte)(pid & 0xFF);
        p[3] = 0x10;                               // payload only
        Array.Copy(payload, 0, p, 4, payload.Length);
        return p;
    }

    private static byte[] BuildDatagram(byte klvStreamType, bool klvaDescriptor)
    {
        // PAT: program 1 -> PMT PID 0x1000
        byte[] pat = { 0x00, 0x00, 0xB0, 13, 0, 1, 0xC1, 0, 0, 0, 1, 0xF0, 0x00, 0, 0, 0, 0 };
        // PMT: H.264 on 0x100, metadata on 0x101
        var es = new List<byte> { 0x1B, 0xE1, 0x00, 0xF0, 0x00, klvStreamType, 0xE1, 0x01, 0xF0 };
        if (klvaDescriptor) es.AddRange(new byte[] { 0x06, 0x05, 0x04, (byte)'K', (byte)'L', (byte)'V', (byte)'A' });
        else es.Add(0x00);
        var body = new List<byte> { 0, 1, 0xC1, 0, 0, 0xE1, 0x00, 0xF0, 0x00 };
        body.AddRange(es);
        var pmt = new List<byte> { 0x00, 0x02, 0xB0, (byte)(body.Count + 4) };
        pmt.AddRange(body);
        pmt.AddRange(new byte[4]);

        byte[] klvPes = { 0x00, 0x00, 0x01, 0xBD, 0x00, 0x10, 0x06, 0x0E, 0x2B, 0x34 };
        byte[] videoPes = { 0x00, 0x00, 0x01, 0xE0 };
        return TsPacket(0, pat).Concat(TsPacket(0x1000, pmt.ToArray()))
            .Concat(TsPacket(0x100, videoPes)).Concat(TsPacket(0x101, klvPes))
            .Concat(TsPacket(0x100, videoPes)).Concat(TsPacket(0x101, klvPes))
            .Concat(TsPacket(0x101, klvPes)).ToArray();
    }

    [Fact]
    public void CountsKlvPackets_WithKlvaRegistration()
    {
        var ts = new TsInspector();
        var d = BuildDatagram(0x06, klvaDescriptor: true);
        ts.Feed(d, d.Length);
        Assert.True(ts.HasKlvTrack);
        Assert.Equal("H.264", ts.VideoCodec);
        Assert.Equal(3, ts.KlvPackets);
    }

    [Fact]
    public void CountsKlvPackets_MetadataStreamType()
    {
        var ts = new TsInspector();
        var d = BuildDatagram(0x15, klvaDescriptor: false);
        ts.Feed(d, d.Length);
        Assert.Equal(3, ts.KlvPackets);
    }

    [Fact]
    public void IgnoresPrivateDataWithoutKlva()
    {
        var ts = new TsInspector();
        var d = BuildDatagram(0x06, klvaDescriptor: false);
        ts.Feed(d, d.Length);
        Assert.False(ts.HasKlvTrack);
        Assert.Equal(0, ts.KlvPackets);
    }

    [Fact]
    public void HandlesRtpHeader()
    {
        var ts = new TsInspector();
        var d = new byte[12].Concat(BuildDatagram(0x15, false)).ToArray();
        ts.Feed(d, d.Length);
        Assert.Equal(3, ts.KlvPackets);
    }
}
