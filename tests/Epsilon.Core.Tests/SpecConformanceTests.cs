using Epsilon.Core.Protocol;
using Xunit;

namespace Epsilon.Core.Tests;

/// <summary>
/// Byte-layout checks for every command against the tables in
/// "Epsilon General Communication Protocol v4.0 rev B" (section 4.2 / 4.4).
/// Each test states the expected payload length and field offsets from the document.
/// </summary>
public class SpecConformanceTests
{
    private static void Len(Packet p, int expected) => Assert.Equal(expected, p.Data.Length);

    [Fact]
    public void FrameLayout_HeaderAndDataChecksumsAndTerminator()
    {
        var p = Cmd.PipSettings(1, 2).Encode();
        Assert.Equal(0xAA, p[0]);
        Assert.Equal(0x55, p[1]);
        Assert.Equal(0x00, p[2]);                 // device id
        Assert.Equal(0x35, p[3]);                 // message id
        Assert.Equal(2, p[4]);                    // length
        Assert.Equal(Crc8.Compute(new byte[] { 0, 0x35, 2 }), p[5]);
        Assert.Equal(Crc8.Compute(new byte[] { 1, 2 }), p[8]);
        Assert.Equal(0xFF, p[9]);
        Assert.Equal(10, p.Length);
    }

    [Fact]
    public void ByteOrder_MatchesSection31Example()
    {
        // 1423 -> 8F 05, -23 -> E9 FF
        Assert.Equal(new byte[] { 0x8F, 0x05, 0xE9, 0xFF }, new ByteWriter().S16(1423).S16(-23).ToArray());
    }

    [Fact]
    public void ZeroLengthCommands_HaveNoDataAndNoDataChecksum()
    {
        foreach (var p in new[] { Cmd.Reset(), Cmd.GetVersion(), Cmd.ClearSdCard(), Cmd.DesignateMti(),
                                  Cmd.ShiftSelectedTrackMti(), Cmd.GetImageSize(), Cmd.SaveAndResetVp(), Cmd.GetErrors() })
        {
            Assert.Equal(7, p.Encode().Length);
        }
    }

    [Fact]
    public void PayloadLengths_MatchDocumentTables()
    {
        Len(Cmd.GyroBias(), 1);
        Len(Cmd.RateControl(0, 0, 0, 0, 0, 0, 0), 8);
        Len(Cmd.VideoStabilization(true), 1);
        Len(Cmd.DoSnapshot(), 5);
        Len(Cmd.DoSnapshot(fileName: "abc"), 8);
        Len(Cmd.VideoRecording(1), 1);
        Len(Cmd.DigitalZoom(1), 1);
        Len(Cmd.OnScreenInformation(0x3FF), 2);
        Len(Cmd.VideoDisplaySize(1, 2), 2);
        Len(Cmd.FocusMode(true), 1);
        Len(Cmd.PanTiltTrims(1, 2), 4);
        Len(Cmd.NetworkSettings(true, "192.168.1.197", "255.255.255.0", "192.168.1.1", 4001, 4002, "192.168.1.10"), 21);
        Len(Cmd.MtiParameters(1, 5), 5);
        Len(Cmd.SetControlMode(ControlMode.Rate), 8);
        Len(Cmd.SetCameraOrder(1), 1);
        Len(Cmd.StowMode(0, -90, false), 5);
        Len(Cmd.SetFalseColorMode(1, 2), 2);
        Len(Cmd.EnableKlv(true), 1);
        Len(Cmd.VideoEnhancement(1, 5, 128, 50, 10, 0, false), 7);
        Len(Cmd.StabilizeOnTrack(true), 1);
        Len(Cmd.VideoStabilizationParameters(1, 2, 3, 0), 4);
        Len(Cmd.GeoLock(1, 2), 8);
        Len(Cmd.GeoParameters(0, 0, 0, 0, 0, 0, 0, 1, true), 12);
        Len(Cmd.IrFlirParameters(0, 50, 50, 50, 0, 0, 0), 7);
        Len(Cmd.IrMwirParameters(50, 50), 12);
        Len(Cmd.GpsAntennaCalibration(0, 0, 0, 0, 0, 0), 12);
        Len(Cmd.MwirIntegrationTime(1), 1);
        Len(Cmd.LaserPointer(true), 1);
        Len(Cmd.GeoSimParameters(1, 1, 2, 3), 11);
        Len(Cmd.LrfSettings(10, 10000, 1, 0, false, false), 6);
        Len(Cmd.DoFfc(), 5);
        Len(Cmd.PipSettings(1, 0), 2);
        Len(Cmd.KlvStaticData(0, "MISSION"), 8);
        Len(Cmd.KlvStaticData(0, ""), 1);
        Len(Cmd.PanOffset(90), 2);
        Len(Cmd.PilotViewAngle(0, -10), 4);
        Len(Cmd.LaserSafetySector(true, false, 0, 90, -90, 0, 0, 0), 13);
        Len(Cmd.IrCooler(true), 1);
        Len(Cmd.PowerUpSettings(true, true), 1);
        Len(Cmd.BlendSettings(1, 1, 128, 0, 0, 0), 6);
        Len(Cmd.BlendAlignment(0, 0, 0, 0, 0), 5);
        Len(Cmd.ResetDefaultSettings(0), 1);
        Len(Cmd.EoSpotterImxSettings(0, 128, 128, 0, 200, 128, 128), 7);
        Len(Cmd.ErrorHandling(0, 0), 3);
        Len(Cmd.EoSettings(true, 0, 0, 0, 0, 1), 7);
        Len(Cmd.EthernetDisplayParameters(1, false, "192.168.1.10", 15004), 7);
        Len(Cmd.VideoOutputMode(2, 0), 2);
        Len(Cmd.H264Parameters(1500, 30, 0, 0, 0), 6);
        Len(Cmd.DeviceDiagnostic(0, 0), 2);
    }

    [Fact]
    public void MessageIds_MatchSection41Table()
    {
        Assert.Equal(0x05, (byte)Cmd.RateControl(0, 0, 0, 0, 0, 0, 0).Id);
        Assert.Equal(0x14, (byte)Cmd.SetControlMode(ControlMode.Rate).Id);
        Assert.Equal(0x1D, (byte)Cmd.StowMode(0, 0, false).Id);
        Assert.Equal(0x24, (byte)Cmd.GeoLock(0, 0).Id);
        Assert.Equal(0x26, (byte)Cmd.GeoParameters(0, 0, 0, 0, 0, 0, 0, 0, false).Id);
        Assert.Equal(0x37, (byte)Cmd.PanOffset(0).Id);
        Assert.Equal(0x42, (byte)Cmd.ErrorHandling(0, 0).Id);
        Assert.Equal(0x43, (byte)MessageId.ErrorHandlingResponse);
        Assert.Equal(0x82, (byte)Cmd.GetErrors().Id);
        Assert.Equal(0x91, (byte)Cmd.EoSettings(false, 0, 0, 0, 0, 1).Id);
        Assert.Equal(0x95, (byte)Cmd.EthernetDisplayParameters(1, false, "1.2.3.4", 1).Id);
        Assert.Equal(0xFA, (byte)Cmd.DeviceDiagnostic(0, 0).Id);
    }

    [Fact]
    public void NetworkSettings_FieldOffsets()
    {
        var d = Cmd.NetworkSettings(true, "192.168.1.197", "255.255.255.0", "192.168.1.1", 4001, 4002, "192.168.1.10").Data;
        Assert.Equal(new byte[] { 1, 192, 168, 1, 197, 255, 255, 255, 0, 192, 168, 1, 1, 0xA1, 0x0F, 0xA2, 0x0F, 192, 168, 1, 10 }, d);
    }

    [Fact]
    public void StowAndPilotView_AreDegreesTimesTen()
    {
        Assert.Equal(new byte[] { 0x10, 0x0E, 0x7C, 0xFC, 1 }, Cmd.StowMode(360, -90, true).Data); // 3600, -900
        Assert.Equal(new byte[] { 0x84, 0x03, 0x9C, 0xFF }, Cmd.PilotViewAngle(90, -10).Data);     // 900, -100
    }

    [Fact]
    public void GeoParameters_FieldOffsets()
    {
        var d = Cmd.GeoParameters(1.5, -2.25, 100, 2, -5, 3, 1, 2, true).Data;
        Assert.Equal(150, ByteReader.S16(d, 0));
        Assert.Equal(-225, ByteReader.S16(d, 2));
        Assert.Equal(100, ByteReader.S16(d, 4));
        Assert.Equal(2, d[6]);
        Assert.Equal(-5, ByteReader.S8(d, 7));
        Assert.Equal(3, d[8]);
        Assert.Equal(1, d[9]);
        Assert.Equal(2, d[10]);
        Assert.Equal(1, d[11]);
    }

    [Fact]
    public void LrfSettings_ModeBits()
    {
        var d = Cmd.LrfSettings(5, 20000, 10, 2, true, true).Data;
        Assert.Equal(new byte[] { 5, 0, 0x20, 0x4E, 10, 0x02 | 0x08 | 0x10 }, d);
    }

    [Fact]
    public void EthernetDisplay_ModeAndPadBit()
    {
        var d = Cmd.EthernetDisplayParameters(8, true, "10.0.0.5", 15004).Data;
        Assert.Equal(new byte[] { 0x48, 10, 0, 0, 5, 0x9C, 0x3A }, d);
    }

    [Fact]
    public void MwirParameters_BrightnessContrastAtBytes7And8()
    {
        var d = Cmd.IrMwirParameters(60, 40).Data;
        Assert.Equal(60, d[7]);
        Assert.Equal(40, d[8]);
        Assert.All(d.Take(7).Concat(d.Skip(9)), b => Assert.Equal(0, b));
    }

    [Fact]
    public void EoSettings_IcrIsBit3OfCamMode()
    {
        Assert.Equal(new byte[] { 0x08, 0x00, 1, 5, 6, 7, 2 }, Cmd.EoSettings(true, 1, 5, 6, 7, 2).Data);
    }

    [Fact]
    public void Version_ParsesAllOffsets()
    {
        var w = new ByteWriter().U8(10).U64(0x1122334455667788UL)
            .U8(4).U8(0).U8(4).U8(1).U8(1).U8(2).U8(3).U8(4).U8(5).U8(6).U8(7).U8(8)
            .U8(9).U8(11).U8(3).U8(5).U32(0x0000_05FF).U8(4).U8(5).U8(6).U8(1).U8(0).U8(1)
            .U32(1u << 22).U8(7).U8(8).U8(9).U8(10);
        var d = w.ToArray();
        Assert.Equal(43, d.Length);
        var v = VersionInfo.Parse(d);
        Assert.Equal("Epsilon 180", v.TypeName);
        Assert.Equal(0x1122334455667788UL, v.UniqueId);
        Assert.Equal("4.0.4.1", v.Firmware);
        Assert.Equal("1.2.3.4", v.TiltBootloader);
        Assert.Equal("5.6.7.8", v.PanBootloader);
        Assert.Equal(9, v.TiltPcb);
        Assert.Equal(11, v.PanPcb);
        Assert.Equal("3.5", v.VideoProcessor);
        Assert.Equal("Sony 4K", v.EoSensorName);
        Assert.Equal("MWIR", v.IrSensorName);
        Assert.Equal("14mm + 35mm fixed", v.IrOpticsName);
        Assert.True(v.HasLrf);
        Assert.False(v.HasLaserAim);
        Assert.True(v.HasGeo);
        Assert.Contains("Inverted mode", v.EpsilonFeatureNames());
        Assert.Equal("7.8.9.10", v.PanFirmware);
    }

    [Fact]
    public void ErrorReport_ParsesAllOffsets()
    {
        var w = new ByteWriter().U8(0).U16(5).U16(6).U16(1234).U8(3).U64(1700000000UL)
            .S32(-7).U8(2).U8(0).U8(4).Ascii("ABCD", 10);
        var r = ErrorReport.Parse(w.ToArray());
        Assert.Equal(5, r.ResponseId);
        Assert.Equal(6, r.NextId);
        Assert.Equal(1234, r.ErrorCode);
        Assert.Equal("CRITICAL", r.SeverityName);
        Assert.Equal(1700000000UL, r.Timestamp);
        Assert.Equal(-7, r.Value);
        Assert.Equal(2, r.OccurrenceCount);
        Assert.Equal("ABCD", r.Description);
    }

    [Fact]
    public void GlobalStatus_UncertaintyScaleDependsOnCalibrationFlag()
    {
        var data = new byte[55];
        data[54] = 25;
        Assert.Equal(25.0, GlobalStatus.Parse(data).AttitudeUncertaintyDeg, 3);
        data[1] = (byte)((uint)StatusFlags.GeoGpsCalibrationOk >> 8);
        Assert.Equal(2.5, GlobalStatus.Parse(data).AttitudeUncertaintyDeg, 3);
    }

    [Fact]
    public void Parser_ZeroLengthAckAndBackToBackHeaderBytes()
    {
        var parser = new PacketParser();
        var got = new List<Packet>();
        parser.PacketReceived += got.Add;
        var stream = new List<byte> { 0xAA };          // stray header byte before a real packet
        stream.AddRange(new Packet(MessageId.VideoStabilization).Encode());
        stream.AddRange(Cmd.VideoStabilization(false).Encode());
        parser.Feed(stream.ToArray());
        Assert.Equal(2, got.Count);
        Assert.True(got[0].IsZeroLength);
        Assert.Equal(new byte[] { 0 }, got[1].Data);
    }
}
