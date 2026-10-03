using Epsilon.Core.Protocol;
using Xunit;

namespace Epsilon.Core.Tests;

public class ProtocolTests
{
    // Worked examples from the protocol document, section 3.2.
    [Fact]
    public void VideoStabilizationOn_MatchesDocumentExample()
    {
        var bytes = Cmd.VideoStabilization(true).Encode();
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x00, 0x07, 0x01, 0x9B, 0x01, 0x00, 0xFF }, bytes);
    }

    [Fact]
    public void ZeroLengthPackets_MatchDocumentExamples()
    {
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x00, 0x01, 0x00, 0x6F, 0xFF }, Cmd.Reset().Encode());
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x00, 0x02, 0x00, 0x3A, 0xFF }, Cmd.GetVersion().Encode());
    }

    [Fact]
    public void CrcTable_MatchesDocumentTableStart()
    {
        Assert.Equal(new byte[] { 0, 94, 188, 226, 97, 63, 221, 131 }, Crc8.Table.Take(8).ToArray());
        Assert.Equal(53, Crc8.Table[255]);
    }

    [Fact]
    public void Parser_ReassemblesSplitAndConcatenatedPackets()
    {
        var stream = new List<byte> { 0x12, 0x34 }; // garbage before
        stream.AddRange(Cmd.VideoStabilization(true).Encode());
        stream.AddRange(Cmd.GeoLock(35.123456, -120.654321).Encode());
        var parser = new PacketParser();
        var got = new List<Packet>();
        parser.PacketReceived += got.Add;
        var all = stream.ToArray();
        parser.Feed(all, 0, 5);
        parser.Feed(all, 5, all.Length - 5);

        Assert.Equal(2, got.Count);
        Assert.Equal(MessageId.VideoStabilization, got[0].Id);
        Assert.Equal(MessageId.GeoLock, got[1].Id);
        Assert.Equal(35.123456, Cmd.DecodeLatLon(ByteReader.S32(got[1].Data, 0)), 6);
        Assert.Equal(-120.654321, Cmd.DecodeLatLon(ByteReader.S32(got[1].Data, 4)), 6);
    }

    [Fact]
    public void Parser_RejectsCorruptChecksum()
    {
        var bytes = Cmd.VideoStabilization(true).Encode();
        bytes[6] ^= 0xFF;
        var parser = new PacketParser();
        int good = 0;
        parser.PacketReceived += _ => good++;
        parser.Feed(bytes);
        Assert.Equal(0, good);
        Assert.Equal(1, parser.BadPackets);
    }

    [Fact]
    public void RateControl_EncodesSignedLittleEndian()
    {
        var p = Cmd.RateControl(-50, 100, 0, 0, -8, 3, -2);
        Assert.Equal(new byte[] { 0xCE, 0x64, 0x00, 0x00, 0xF8, 0x03, 0xFE, 0xFF }, p.Data);
    }

    [Fact]
    public void SetControlMode_HasEightBytes()
    {
        var p = Cmd.SetControlMode(ControlMode.TrackVehicle, 640, 360, 64, 0x20, 0x02);
        Assert.Equal(new byte[] { 4, 0x80, 0x02, 0x68, 0x01, 64, 0x20, 0x02 }, p.Data);
    }

    [Fact]
    public void GlobalStatus_ParsesFields()
    {
        var data = new ByteWriter()
            .U32((uint)StatusFlags.VideoRecording | (7u << 16))
            .U16(12345).S16(-4500).U8(10).U8(20).U16(338).U16(600)
            .S8(30).S8(31).S8(-5)
            .S32(Cmd.EncodeLatLon(13.5)).S32(Cmd.EncodeLatLon(80.25)).S16(500)
            .S16(100).S16(-200).S16(9000)
            .S16(-1234).S16(-450).U16(1500)
            .S32(Cmd.EncodeLatLon(13.51)).S32(Cmd.EncodeLatLon(80.26))
            .U8(12).U8(15).U8(9).U8(8).U8(0).U8(0).U8(0x73).U8(25)
            .ToArray();
        Assert.Equal(55, data.Length);

        var s = GlobalStatus.Parse(data);
        Assert.True(s.Has(StatusFlags.VideoRecording));
        Assert.Equal(ControlMode.GeoLock, s.Mode);
        Assert.Equal(123.45, s.PanDeg, 2);
        Assert.Equal(-45.0, s.TiltDeg, 2);
        Assert.Equal(60.0, s.FovHorizontalDeg, 1);
        Assert.Equal(-5, s.TempVideoProcessorC);
        Assert.Equal(13.5, s.CamLat, 6);
        Assert.Equal(90.0, s.ExtYawDeg, 2);
        Assert.Equal(-123.4, s.GeoAzimuthDeg, 1);
        Assert.Equal(1500, s.GeoDistanceM);
        Assert.Equal(80.26, s.TargetLon, 6);
        Assert.Equal(30, s.SignalLevelA);
        Assert.Equal(70, s.SignalLevelB);
    }
}
