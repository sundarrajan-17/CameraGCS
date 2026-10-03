using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

public sealed class GeoPage : FlyoutPage
{
    public override string Title => "Geo / LRF";

    private readonly NumField _panOff, _tiltOff, _altOff, _tz;
    private readonly ChoiceField _lrfMode, _coord, _unit, _antenna;
    private readonly BoolField _jamming;
    private readonly NumField _lat, _lon;
    private readonly NumField _winMin, _winMax, _freq;
    private readonly ChoiceField _measMode;
    private readonly BoolField _sens, _lrfPointer;
    private readonly BoolField _lsEnabled, _lsInverted;
    private readonly NumField _lsFromPan, _lsToPan, _lsFromTilt, _lsToTilt, _lsAltMin, _lsAltMax;
    private readonly NumField _ax, _ay, _az, _bx, _by, _bz;
    private readonly ChoiceField _simMode;
    private readonly NumField _simLat, _simLon, _simAlt;

    public GeoPage()
    {
        F.Section("GEO parameters (0x26)");
        _panOff = F.Number("Pan offset °", 0, -327, 327);
        _tiltOff = F.Number("Tilt offset °", 0, -327, 327);
        _altOff = F.Number("Altitude offset m", 0, -32768, 32767);
        _lrfMode = F.Choice("LRF mode", 2, "Do not use", "Telemetry only (distance)", "GEO");
        _tz = F.Number("Time zone (h)", 0, -12, 12);
        _coord = F.Choice("Coordinate system", 0, "DD", "DDM", "DMS", "MGRS");
        _unit = F.Choice("Units", 0, "Metric", "Imperial");
        _antenna = F.Choice("GPS antenna", 1, "Off", "Internal GPS antenna", "External GPS data (single antenna)");
        _jamming = F.Check("Automatic GPS jamming detection", true);
        F.Buttons(("Read", () => Req(MessageId.GeoParameters)),
                  ("Apply", () => Send(Cmd.GeoParameters(_panOff.Value, _tiltOff.Value, _altOff.Int, _lrfMode.Value,
                      _tz.Int, _coord.Value, _unit.Value, _antenna.Value, _jamming.Value))));

        F.Section("Geo lock to coordinates (0x24)");
        F.Note("Tip: right-click the map to geo-lock on a point.");
        _lat = F.Number("Latitude °", 0, -90, 90);
        _lon = F.Number("Longitude °", 0, -180, 180);
        F.Buttons(("Geo lock", () =>
        {
            // Through the controller so the tracking box / advanced / option bits are kept (not reset to 0).
            if (Gcs.Controller.IsLinkOpen) Gcs.Controller.GeoLock(_lat.Value, _lon.Value);
            else Send(Cmd.GeoLock(_lat.Value, _lon.Value)); // reports "not connected"
        }));

        F.Section("Laser range finder (0x30)");
        _winMin = F.Number("Window min m", 10, 1, 1000);
        _winMax = F.Number("Window max m", 10000, 10, 32000);
        _freq = F.Number("Frequency Hz", 1, 0, 10);
        _measMode = F.Choice("Mode", 1, "Continuous", "Single shot manual", "Single shot auto");
        _sens = F.Check("Sensitivity mode", false);
        _lrfPointer = F.Check("LRF pointer", false);
        F.Buttons(("Read", () => Req(MessageId.LrfSettings)),
                  ("Apply", () => Send(Cmd.LrfSettings(_winMin.Int, _winMax.Int, _freq.Int, _measMode.Value, _sens.Value, _lrfPointer.Value))));

        F.Section("Laser pointer safety sector (0x3A)");
        F.Note("Laser is disabled inside the sector (or only enabled inside it when inverted). Equal from/to disables a limit.");
        _lsEnabled = F.Check("Sector enabled", false);
        _lsInverted = F.Check("Inverted sector", false);
        _lsFromPan = F.Number("From pan °", 0, 0, 360);
        _lsToPan = F.Number("To pan °", 0, 0, 360);
        _lsFromTilt = F.Number("From tilt °", 0, -90, 90);
        _lsToTilt = F.Number("To tilt °", 0, -90, 90);
        _lsAltMin = F.Number("Min altitude m", 0, -32768, 32767);
        _lsAltMax = F.Number("Max altitude m", 0, -32768, 32767);
        F.Buttons(("Read", () => Req(MessageId.LaserSafetySector)),
                  ("Apply", () => Send(Cmd.LaserSafetySector(_lsEnabled.Value, _lsInverted.Value, _lsFromPan.Value, _lsToPan.Value,
                      _lsFromTilt.Value, _lsToTilt.Value, _lsAltMin.Int, _lsAltMax.Int))));

        F.Section("GPS antenna position (0x29)");
        F.Note("Metres relative to the Epsilon base. Single antenna: leave antenna B at 0.");
        _ax = F.Number("Antenna A x", 0, -32, 32); _ay = F.Number("Antenna A y", 0, -32, 32); _az = F.Number("Antenna A z", 0, -32, 32);
        _bx = F.Number("Antenna B x", 0, -32, 32); _by = F.Number("Antenna B y", 0, -32, 32); _bz = F.Number("Antenna B z", 0, -32, 32);
        F.Buttons(("Read", () => Req(MessageId.GpsAntennaCalibration)),
                  ("Apply", () => Send(Cmd.GpsAntennaCalibration(_ax.Value, _ay.Value, _az.Value, _bx.Value, _by.Value, _bz.Value))));

        F.Section("GEO simulation (0x2F)");
        _simMode = F.Choice("Simulation", 0, "Off", "On");
        _simLat = F.Number("Camera latitude °", 0, -90, 90);
        _simLon = F.Number("Camera longitude °", 0, -180, 180);
        _simAlt = F.Number("Camera altitude m", 100, -1000, 32000);
        F.Buttons(("Read", () => Req(MessageId.GeoSimParameters)),
                  ("Apply", () => Send(Cmd.GeoSimParameters(_simMode.Value, _simLat.Value, _simLon.Value, _simAlt.Int))));
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.GeoParameters when d.Length >= 10:
                _panOff.Value = ByteReader.S16(d, 0) / 100.0;
                _tiltOff.Value = ByteReader.S16(d, 2) / 100.0;
                _altOff.Value = ByteReader.S16(d, 4);
                _lrfMode.Value = d[6];
                _tz.Value = ByteReader.S8(d, 7);
                _coord.Value = d[8];
                _unit.Value = d[9];
                if (d.Length >= 12) { _antenna.Value = d[10]; _jamming.Value = d[11] != 0; }
                break;
            case MessageId.LrfSettings when d.Length >= 6:
                _winMin.Value = ByteReader.U16(d, 0);
                _winMax.Value = ByteReader.U16(d, 2);
                _freq.Value = d[4];
                _measMode.Value = d[5] & 0x07;
                _sens.Value = (d[5] & 0x08) != 0;
                _lrfPointer.Value = (d[5] & 0x10) != 0;
                break;
            case MessageId.LaserSafetySector when d.Length >= 13:
                _lsEnabled.Value = (d[0] & 1) != 0;
                _lsInverted.Value = (d[0] & 2) != 0;
                _lsFromPan.Value = ByteReader.U16(d, 1) / 10.0;
                _lsToPan.Value = ByteReader.U16(d, 3) / 10.0;
                _lsFromTilt.Value = ByteReader.S16(d, 5) / 10.0;
                _lsToTilt.Value = ByteReader.S16(d, 7) / 10.0;
                _lsAltMin.Value = ByteReader.S16(d, 9);
                _lsAltMax.Value = ByteReader.S16(d, 11);
                break;
            case MessageId.GpsAntennaCalibration when d.Length >= 12:
                _ax.Value = ByteReader.S16(d, 0) / 1000.0; _ay.Value = ByteReader.S16(d, 2) / 1000.0; _az.Value = ByteReader.S16(d, 4) / 1000.0;
                _bx.Value = ByteReader.S16(d, 6) / 1000.0; _by.Value = ByteReader.S16(d, 8) / 1000.0; _bz.Value = ByteReader.S16(d, 10) / 1000.0;
                break;
            case MessageId.GeoSimParameters when d.Length >= 11:
                _simMode.Value = d[0];
                _simLat.Value = Cmd.DecodeLatLon(ByteReader.S32(d, 1));
                _simLon.Value = Cmd.DecodeLatLon(ByteReader.S32(d, 5));
                _simAlt.Value = ByteReader.S16(d, 9);
                break;
        }
    }
}
