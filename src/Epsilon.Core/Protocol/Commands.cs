namespace Epsilon.Core.Protocol;

/// <summary>
/// Builders for the packets sent to Epsilon (protocol section 4.2).
/// Every method returns a ready-to-send <see cref="Packet"/>.
/// Use <see cref="Request"/> to read back any setting marked "Req" in the section 4.1 table.
/// </summary>
public static class Cmd
{
    public const double LatLonScale = 3600000.0;

    private static Packet P(MessageId id, ByteWriter w) => new(id, w.ToArray());

    /// <summary>Zero-length packet: requests the current value of a "Req" setting.</summary>
    public static Packet Request(MessageId id) => new(id);

    public static long EncodeLatLon(double degrees) => (long)Math.Round(degrees * LatLonScale);
    public static double DecodeLatLon(int raw) => raw / LatLonScale;

    // ---- 0x01 .. 0x0F ------------------------------------------------------------------

    public static Packet Reset() => new(MessageId.EpsilonReset);
    public static Packet GetVersion() => new(MessageId.GetVersion);

    /// <summary>0x03 Gyro drift compensation. Gimbal must be stationary; link is lost ~10 s.</summary>
    public static Packet GyroBias() => P(MessageId.GyroBias, new ByteWriter().U8(1));

    /// <summary>0x05 Rate control. Speeds in % (-100..100), zoom speed -8..8, focus step -128..127.</summary>
    public static Packet RateControl(int panSpeed, int tiltSpeed, int nudgeColumn, int nudgeRow,
                                     int zoomSpeed, int focusStep, int dtedHeight) =>
        P(MessageId.RateControl, new ByteWriter()
            .S8(Math.Clamp(panSpeed, -100, 100))
            .S8(Math.Clamp(tiltSpeed, -100, 100))
            .S8(nudgeColumn)
            .S8(nudgeRow)
            .S8(Math.Clamp(zoomSpeed, -8, 8))
            .S8(focusStep)
            .S16(dtedHeight));

    public static Packet VideoStabilization(bool on) =>
        P(MessageId.VideoStabilization, new ByteWriter().U8(on ? 1 : 0));

    /// <summary>0x08. source: 0 captured, 1 displayed. format: 0 JPEG,1 PNG,2 JPEG Big,3 RAW. metadata: 0 none,1 KML,2 multi KML,3 NITF.</summary>
    public static Packet DoSnapshot(int frameStep = 1, int count = 1, int source = 1, int format = 0,
                                    int metadata = 0, string fileName = "") =>
        P(MessageId.DoSnapshot, new ByteWriter().U8(frameStep).U8(count).U8(source).U8(format).U8(metadata)
            .Ascii(fileName, 100));

    /// <summary>0x09. state: 0 no change, 1 start, 2 stop.</summary>
    public static Packet VideoRecording(int state, string fileName = "") =>
        P(MessageId.VideoRecording, new ByteWriter().U8(state).Ascii(fileName, 100));

    public static Packet ClearSdCard() => new(MessageId.ClearSdCard);

    /// <summary>0x0B. -1 zoom out one step, +1 zoom in one step (1.0x .. 4.0x).</summary>
    public static Packet DigitalZoom(int direction) =>
        P(MessageId.DigitalZoom, new ByteWriter().S8(Math.Sign(direction)));

    /// <summary>0x0C. 16-bit mask, see <see cref="OsiBits"/>.</summary>
    public static Packet OnScreenInformation(int mask) =>
        P(MessageId.OnScreenInformation, new ByteWriter().U16(mask));

    /// <summary>0x0D. size: 0 SD, 1 HD cropped, 2 HD, 3 Full HD, 4 4K.</summary>
    public static Packet VideoDisplaySize(int frameStep, int frameSize) =>
        P(MessageId.VideoDisplaySize, new ByteWriter().U8(frameStep).U8(frameSize));

    public static Packet DesignateMti() => new(MessageId.DesignateMti);
    public static Packet ShiftSelectedTrackMti() => new(MessageId.ShiftSelectedTrackMti);

    // ---- 0x10 .. 0x1F ------------------------------------------------------------------

    /// <summary>0x10. Bit0: 0 auto / 1 manual. Bit1: start one-shot autofocus.</summary>
    public static Packet FocusMode(bool manual, bool oneShot = false) =>
        P(MessageId.FocusMode, new ByteWriter().U8((manual ? 1 : 0) | (oneShot ? 2 : 0)));

    public static Packet PanTiltTrims(int panTrim, int tiltTrim) =>
        P(MessageId.SetPanTiltTrims, new ByteWriter().S16(panTrim).S16(tiltTrim));

    /// <summary>0x12 Gimbal network (UDP server) settings. Use 0 / "0.0.0.0" for "no change".</summary>
    public static Packet NetworkSettings(bool staticIp, string ip, string mask, string gateway,
                                         int inputPort, int destPort, string destIp) =>
        P(MessageId.NetworkSettings, new ByteWriter()
            .U8(staticIp ? 1 : 0)
            .Ip(ip).Ip(mask).Ip(gateway)
            .U16(inputPort).U16(destPort)
            .Ip(destIp));

    /// <summary>0x13. mode 0 disable,1 vehicle,2 staring,3 aerial,4 anomaly,5 maritime. sensitivity 1 (highest) .. 10, 0 no change.</summary>
    public static Packet MtiParameters(int mode, int sensitivity, int downsample = 255, bool reset = false) =>
        P(MessageId.MtiParameters, new ByteWriter().U8(mode).U8(sensitivity).U8(0).U8(downsample).U8(reset ? 1 : 0));

    /// <summary>0x14 Set control mode. Pixel (0,0) = track to the cross position.</summary>
    public static Packet SetControlMode(ControlMode mode, int pixelX = 0, int pixelY = 0, int boxSize = 0,
                                        int trackingAdvanced = 0, int options = 0) =>
        P(MessageId.SetControlMode, new ByteWriter()
            .U8((byte)mode).U16(pixelX).U16(pixelY).U8(boxSize).U8(trackingAdvanced).U8(options));

    /// <summary>0x1A. 0 EO zoom, 1 IR main, 2 EO spotter, 3 IR secondary.</summary>
    public static Packet SetCameraOrder(int camera) =>
        P(MessageId.SetCameraOrder, new ByteWriter().U8(camera));

    /// <summary>0x1D. Angles in degrees.</summary>
    public static Packet StowMode(double panDeg, double tiltDeg, bool turnOffPeripherals) =>
        P(MessageId.StowMode, new ByteWriter()
            .U16((int)Math.Round(panDeg * 10)).S16((int)Math.Round(tiltDeg * 10)).U8(turnOffPeripherals ? 1 : 0));

    /// <summary>0x1E. camera: 0 EO, 1 IR, 255 all. colorMode: see protocol table (0..41).</summary>
    public static Packet SetFalseColorMode(int camera, int colorMode) =>
        P(MessageId.SetFalseColorMode, new ByteWriter().U8(camera).U8(colorMode));

    public static Packet EnableKlv(bool on) => P(MessageId.EnableKlv, new ByteWriter().U8(on ? 1 : 0));

    // ---- 0x21 .. 0x2F ------------------------------------------------------------------

    /// <summary>0x21. filter: 0 none, 1 CLAHE, 2 LAP. camera: 0 EO, 1 IR.</summary>
    public static Packet VideoEnhancement(int filterMode, int sharpening, int alphaBlend, int strength,
                                          int denoise, int camera, bool setDefaults) =>
        P(MessageId.VideoEnhancement, new ByteWriter()
            .U8(filterMode).U8(Math.Min(sharpening, 15)).U8(alphaBlend).U8(Math.Min(strength, 127))
            .U8(denoise).U8(camera).U8(setDefaults ? 1 : 0));

    public static Packet StabilizeOnTrack(bool on) =>
        P(MessageId.StabilizeOnTrack, new ByteWriter().U8(on ? 1 : 0));

    /// <summary>0x23. imageEdge: 0 fading to gray, 1 solid gray, 2 previous image.</summary>
    public static Packet VideoStabilizationParameters(int driftRate, int maximumOffset, int rollComp, int imageEdge) =>
        P(MessageId.VideoStabilizationParam, new ByteWriter().U8(driftRate).U8(maximumOffset).U8(rollComp).U8(imageEdge));

    public static Packet GeoLock(double latDeg, double lonDeg) =>
        P(MessageId.GeoLock, new ByteWriter().S32(EncodeLatLon(latDeg)).S32(EncodeLatLon(lonDeg)));

    /// <summary>0x26 GEO mode parameters.</summary>
    public static Packet GeoParameters(double panOffsetDeg, double tiltOffsetDeg, int altOffsetM, int lrfMode,
                                       int timeZone, int coordinateSystem, int measurementUnit,
                                       int gpsAntennaMode, bool jammingDetection) =>
        P(MessageId.GeoParameters, new ByteWriter()
            .S16((int)Math.Round(panOffsetDeg * 100)).S16((int)Math.Round(tiltOffsetDeg * 100)).S16(altOffsetM)
            .U8(lrfMode).S8(timeZone).U8(coordinateSystem).U8(measurementUnit)
            .U8(gpsAntennaMode).U8(jammingDetection ? 1 : 0));

    /// <summary>0x27 FLIR IR parameters. agc: 0 auto, 3 manual, 5 linear.</summary>
    public static Packet IrFlirParameters(int agcType, int contrast, int brightness, int sso, int dde, int ace, int preset) =>
        P(MessageId.IrFlirParam, new ByteWriter()
            .U8(agcType).U8(contrast).U8(brightness).U8(sso).S8(dde).S8(ace).U8(preset));

    /// <summary>0x28 MWIR parameters (180, 180HD, 140MWIR, 95). Only brightness/contrast are documented.</summary>
    public static Packet IrMwirParameters(int brightnessPct, int contrastPct)
    {
        var w = new ByteWriter();
        for (int i = 0; i < 7; i++) w.U8(0);
        w.U8(brightnessPct).U8(contrastPct);
        for (int i = 0; i < 3; i++) w.U8(0);
        return P(MessageId.IrMwirParam, w);
    }

    /// <summary>0x29. Positions in metres relative to the Epsilon base.</summary>
    public static Packet GpsAntennaCalibration(double ax, double ay, double az, double bx, double by, double bz) =>
        P(MessageId.GpsAntennaCalibration, new ByteWriter()
            .S16(Mm(ax)).S16(Mm(ay)).S16(Mm(az)).S16(Mm(bx)).S16(Mm(by)).S16(Mm(bz)));

    private static int Mm(double m) => (int)Math.Round(m * 1000);

    public static Packet MwirIntegrationTime(int step) =>
        P(MessageId.MwirIntegrationTime, new ByteWriter().S8(Math.Sign(step)));

    public static Packet LaserPointer(bool on) => P(MessageId.LaserPointer, new ByteWriter().U8(on ? 1 : 0));

    /// <summary>0x2F GEO simulation (fake camera position for testing).</summary>
    public static Packet GeoSimParameters(int simMode, double latDeg, double lonDeg, int altitudeM) =>
        P(MessageId.GeoSimParameters, new ByteWriter()
            .U8(simMode).S32(EncodeLatLon(latDeg)).S32(EncodeLatLon(lonDeg)).S16(altitudeM));

    // ---- 0x30 .. 0x3F ------------------------------------------------------------------

    /// <summary>0x30. mode: 0 continuous, 1 single manual, 2 single auto.</summary>
    public static Packet LrfSettings(int winMinM, int winMaxM, int frequencyHz, int mode, bool sensitivity, bool lrfPointer) =>
        P(MessageId.LrfSettings, new ByteWriter()
            .U16(winMinM).U16(winMaxM).U8(frequencyHz)
            .U8((mode & 0x07) | (sensitivity ? 0x08 : 0) | (lrfPointer ? 0x10 : 0)));

    /// <summary>0x31 FFC / NUC.</summary>
    public static Packet DoFfc(bool longFfc = false, bool useStaticPosition = false, double panDeg = 0, double tiltDeg = 0) =>
        P(MessageId.DoFfc, new ByteWriter()
            .U8((longFfc ? 1 : 0) | (useStaticPosition ? 2 : 0))
            .U16((int)Math.Round(panDeg * 10)).S16((int)Math.Round(tiltDeg * 10)));

    /// <summary>0x35. mode: 0 one up, 1 PIP, 2 zoom to track, 3 blend. scale: 0 1/4, 1 3/8, 2 1/2.</summary>
    public static Packet PipSettings(int displayMode, int scale) =>
        P(MessageId.PipSettings, new ByteWriter().U8(displayMode).U8(scale));

    /// <summary>0x36. Empty string disables the field.</summary>
    public static Packet KlvStaticData(int fieldId, string value) =>
        P(MessageId.KlvStaticData, new ByteWriter().U8(fieldId).Ascii(value, 127));

    public static Packet PanOffset(double aircraftFrontDeg) =>
        P(MessageId.PanOffset, new ByteWriter().U16((int)Math.Round(aircraftFrontDeg * 10)));

    public static Packet PilotViewAngle(double panDeg, double tiltDeg) =>
        P(MessageId.PilotViewAngle, new ByteWriter().U16((int)Math.Round(panDeg * 10)).S16((int)Math.Round(tiltDeg * 10)));

    public static Packet GetImageSize() => new(MessageId.GetImageSize);

    public static Packet LaserSafetySector(bool enabled, bool inverted, double fromPan, double toPan,
                                           double fromTilt, double toTilt, int altMinM, int altMaxM) =>
        P(MessageId.LaserSafetySector, new ByteWriter()
            .U8((enabled ? 1 : 0) | (inverted ? 2 : 0))
            .U16((int)Math.Round(fromPan * 10)).U16((int)Math.Round(toPan * 10))
            .S16((int)Math.Round(fromTilt * 10)).S16((int)Math.Round(toTilt * 10))
            .S16(altMinM).S16(altMaxM));

    public static Packet IrCooler(bool on) => P(MessageId.IrCoolerSettings, new ByteWriter().U8(on ? 1 : 0));

    public static Packet PowerUpSettings(bool startInStow, bool irCoolerOn) =>
        P(MessageId.PowerUpSettings, new ByteWriter().U8((startInStow ? 1 : 0) | (irCoolerOn ? 2 : 0)));

    public static Packet BlendSettings(int mode, int zoomLevel, int amount, int hue, int hotStart, int coldEnd) =>
        P(MessageId.BlendSettings, new ByteWriter().U8(mode).U8(zoomLevel).U8(amount).U8(hue).U8(hotStart).U8(coldEnd));

    public static Packet BlendAlignment(int vertical, int horizontal, int rotation, int zoom, int hzoom) =>
        P(MessageId.BlendAlignment, new ByteWriter().S8(vertical).S8(horizontal).S8(rotation).S8(zoom).S8(hzoom));

    /// <summary>0x3F. 0 video processor, 1 factory except network, 2 full factory.</summary>
    public static Packet ResetDefaultSettings(int resetType) =>
        P(MessageId.ResetDefaultSettings, new ByteWriter().U8(resetType));

    public static Packet EoSpotterImxSettings(int camMode, int brightness, int exposure, int digitalGain,
                                              int analogGain, int saturation, int colorTemp) =>
        P(MessageId.EoSpotterImxSettings, new ByteWriter()
            .U8(camMode).U8(brightness).U8(exposure).U8(digitalGain).U8(analogGain).U8(saturation).U8(colorTemp));

    /// <summary>0x42. command: 0 request runtime error, 1 request log entry, 2 clear runtime error, 3 clear all log.</summary>
    public static Packet ErrorHandling(int command, int id) =>
        P(MessageId.ErrorHandlingReqClr, new ByteWriter().U8(command).U16(id));

    /// <summary>0x82 zero length: legacy GET_ERRORS.</summary>
    public static Packet GetErrors() => new(MessageId.Errors);

    // ---- 0x91 .. 0xFA ------------------------------------------------------------------

    /// <summary>0x91. aeMode: 0 full auto, 1 manual, 2 shutter priority, 3 iris priority. defog: 1 off .. 4 level 3.</summary>
    public static Packet EoSettings(bool icr, int aeMode, int shutter, int iris, int gain, int defog) =>
        P(MessageId.EoSettings, new ByteWriter()
            .U16(icr ? 0x08 : 0).U8(aeMode).U8(shutter).U8(iris).U8(gain).U8(defog));

    /// <summary>
    /// 0x95 Ethernet video output. protocol (bits 0-3): 0 RTP MJPEG, 1 H.264 MPEG2-TS, 3 H.264 HD (135),
    /// 5 RTP H.264 (RTSP), 6 RTP MPEG2-TS H.264 (RTSP + metadata), 8 MPEG2-TS H.265,
    /// 9 RTP H.265 (RTSP), 10 RTP MPEG-TS H.265 (RTSP + metadata), 15 none.
    /// </summary>
    public static Packet EthernetDisplayParameters(int protocol, bool padUdp, string destIp, int destPort) =>
        P(MessageId.EthernetDisplayParameters, new ByteWriter()
            .U8((protocol & 0x0F) | (padUdp ? 0x40 : 0))
            .Ip(destIp).U16(destPort));

    /// <summary>0x96. output: 0 none, 1 analog, 2 network, 3 both. mode: 0 NTSC, 1 PAL.</summary>
    public static Packet VideoOutputMode(int output, int analogMode) =>
        P(MessageId.VideoOutputMode, new ByteWriter().U8(output).U8(analogMode));

    /// <summary>0x97. bitrate in kbit/s, profile: 0 baseline, 1 main, 2 high. rateControl: 0 legacy, 1 VBR, 2 constrained.</summary>
    public static Packet H264Parameters(int bitrateKbps, int iFrameInterval, int deblocking, int profile, int rateControl) =>
        P(MessageId.H264Parameters, new ByteWriter()
            .U16(bitrateKbps).U8(iFrameInterval).U8(deblocking).U8(profile).U8(rateControl));

    public static Packet SaveAndResetVp() => new(MessageId.SaveAndResetVp);

    public static Packet DeviceDiagnostic(int deviceId, int page) =>
        P(MessageId.DeviceDiagnostic, new ByteWriter().U8(deviceId).U8(page));
}

/// <summary>ON_SCREEN_INFORMATION (0x0C) bits.</summary>
public static class OsiBits
{
    public const int Global = 1 << 0;
    public const int CentralCross = 1 << 1;
    public const int SystemInfo = 1 << 2;
    public const int Status = 1 << 3;
    public const int TiltPosition = 1 << 4;
    public const int PanPosition = 1 << 5;
    public const int GeoTargetInfo = 1 << 6;
    public const int GeoCompass = 1 << 7;
    public const int UtcTime = 1 << 8;
    public const int GeoScale = 1 << 9;
    public const int CompassGeographicNorth = 1 << 10;
    public const int AllOn = 0x03FF;

    public static readonly (int Bit, string Name)[] Names =
    {
        (Global, "Global OSI"), (CentralCross, "Central cross"), (SystemInfo, "System info"),
        (Status, "Status"), (TiltPosition, "Tilt position"), (PanPosition, "Pan position"),
        (GeoTargetInfo, "Geo target info"), (GeoCompass, "Geo compass"), (UtcTime, "UTC time"),
        (GeoScale, "Geo scale"), (CompassGeographicNorth, "Compass = geographic north"),
    };
}

/// <summary>SET_CONTROL_MODE OPTIONS byte (byte 7).</summary>
public static class ControlOptionBits
{
    public const int SyncEoIrFov = 1 << 0;
    public const int RollHorizonAlignment = 1 << 1;
    public const int Flipping = 1 << 2;
    public const int InvertImage = 1 << 3;
}

/// <summary>SET_CONTROL_MODE TRACKING_ADVANCED byte (byte 6).</summary>
public static class TrackingAdvancedBits
{
    public const int IntelligentAssist = 1 << 3;
    public const int HighNoiseCompensation = 1 << 4;
    public const int AcquisitionAssist = 1 << 5;
    public const int SceneOnVehicleDrop = 1 << 6;
    public const int GeoOnVehicleDrop = 1 << 7;
}
