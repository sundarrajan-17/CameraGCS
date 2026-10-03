using Epsilon.Core.Protocol;

namespace EpsilonGCS.Flyouts;

public sealed class SensorsPage : FlyoutPage
{
    public override string Title => "Sensors";

    private readonly ChoiceField _camera, _fcCamera, _fcMode;
    private readonly BoolField _icr;
    private readonly ChoiceField _ae, _defog;
    private readonly NumField _shutter, _iris, _gain;
    private readonly ChoiceField _agc, _preset;
    private readonly NumField _contrast, _brightness, _sso, _dde, _ace;
    private readonly NumField _mwirBrightness, _mwirContrast;
    private readonly BoolField _ffcLong, _ffcStatic;
    private readonly NumField _ffcPan, _ffcTilt;

    private static readonly (int, string)[] FalseColors =
    {
        (0, "No false color"), (2, "White hot"), (3, "Black hot"), (4, "Rainbow"), (5, "Rainbow inv."),
        (6, "Iron"), (7, "Iron inv."), (8, "Hot/Cold"), (9, "Hot/Cold inv."), (10, "Jet"), (11, "Jet inv."),
        (12, "Hot"), (13, "Hot inv."), (14, "HSV"), (15, "HSV inv."), (16, "470CLR_S"), (17, "470CLR_S inv."),
        (20, "Color2"), (21, "Color2 inv."), (22, "Color3"), (23, "Color3 inv."), (24, "Hot iron"), (25, "Hot iron inv."),
        (26, "Ice fire"), (27, "Ice fire inv."), (28, "IDDEF"), (29, "IDDEF inv."), (30, "Iron256"), (31, "Iron256 inv."),
        (32, "Rain"), (33, "Rain inv."), (34, "XVolcano"), (35, "XVolcano inv."), (36, "Red"), (37, "Red inv."),
        (38, "Green"), (39, "Green inv."), (40, "Blue"), (41, "Blue inv."),
    };

    public SensorsPage()
    {
        F.Section("Active camera (0x1A)");
        _camera = F.Choice("Camera", 0, "EO sensor - zoom", "IR sensor - main", "EO sensor - spotter", "IR sensor - secondary");
        F.Buttons(("Read", () => Req(MessageId.SetCameraOrder)), ("Select", () => Send(Cmd.SetCameraOrder(_camera.Value))));

        F.Section("False color (0x1E)");
        _fcCamera = F.Choice("Apply to", 1, (0, "EO sensor"), (1, "IR sensor"), (255, "All sensors"));
        _fcMode = F.Choice("Palette", 2, FalseColors);
        F.Buttons(("Read", () => Req(MessageId.SetFalseColorMode)), ("Apply", () => Send(Cmd.SetFalseColorMode(_fcCamera.Value, _fcMode.Value))));

        F.Section("EO camera (0x91)");
        _icr = F.Check("ICR (IR cut filter removed / night mode)", false);
        _ae = F.Choice("AE mode", 0, "Full auto", "Manual", "Shutter priority", "Iris priority");
        _shutter = F.Number("Shutter speed", 10, 0, 21);
        _iris = F.Number("Iris position", 6, 0, 13);
        _gain = F.Number("Gain position", 0, 0, 14);
        _defog = F.Choice("Defog", 1, (1, "Off"), (2, "Level 1"), (3, "Level 2"), (4, "Level 3"));
        F.Buttons(("Read", () => Req(MessageId.EoSettings)),
                  ("Apply", () => Send(Cmd.EoSettings(_icr.Value, _ae.Value, _shutter.Int, _iris.Int, _gain.Int, _defog.Value))));

        F.Section("IR camera - FLIR (0x27)");
        F.Note("Models 140Z/F, 140LC, 140G2Z, 140G3.");
        _agc = F.Choice("AGC", 0, (0, "Auto"), (3, "Manual"), (5, "Linear AGC"));
        _contrast = F.Number("Contrast", 50, 0, 100);
        _brightness = F.Number("Brightness", 50, 0, 100);
        _sso = F.Number("Smart scene opt.", 15, 0, 100);
        _dde = F.Number("DDE", 0, -20, 100);
        _ace = F.Number("ACE", 0, -8, 8);
        _preset = F.Choice("Preset", 0, "Default", "Sky/Sea", "Indoors", "Outdoors");
        F.Buttons(("Read", () => Req(MessageId.IrFlirParam)),
                  ("Apply", () => Send(Cmd.IrFlirParameters(_agc.Value, _contrast.Int, _brightness.Int, _sso.Int, _dde.Int, _ace.Int, _preset.Value))));

        F.Section("IR camera - MWIR (0x28)");
        F.Note("Models 180, 180HD, 140MWIR, 95. 50% = default.");
        _mwirBrightness = F.Number("Brightness %", 50, 0, 100);
        _mwirContrast = F.Number("Contrast %", 50, 0, 100);
        F.Buttons(("Read", () => Req(MessageId.IrMwirParam)),
                  ("Apply", () => Send(Cmd.IrMwirParameters(_mwirBrightness.Int, _mwirContrast.Int))),
                  ("IT -", () => Send(Cmd.MwirIntegrationTime(-1))),
                  ("IT +", () => Send(Cmd.MwirIntegrationTime(1))));

        F.Section("IR cooler (0x3B)");
        F.Buttons(("Cooler ON", () => Send(Cmd.IrCooler(true))), ("Cooler OFF", () => Send(Cmd.IrCooler(false))),
                  ("Read", () => Req(MessageId.IrCoolerSettings)));

        F.Section("FFC / NUC (0x31)");
        _ffcLong = F.Check("Long FFC", false);
        _ffcStatic = F.Check("Move to static position first", false);
        _ffcPan = F.Number("Static pan °", 0, 0, 360);
        _ffcTilt = F.Number("Static tilt °", 0, -90, 90);
        F.Buttons(("Do FFC / NUC", () => Send(Cmd.DoFfc(_ffcLong.Value, _ffcStatic.Value, _ffcPan.Value, _ffcTilt.Value))));
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.SetCameraOrder when d.Length >= 1:
                _camera.Value = d[0];
                break;
            case MessageId.SetFalseColorMode when d.Length >= 2:
                _fcCamera.Value = d[0];
                _fcMode.Value = d[1];
                break;
            case MessageId.EoSettings when d.Length >= 7:
                _icr.Value = (ByteReader.U16(d, 0) & 0x08) != 0;
                _ae.Value = d[2]; _shutter.Value = d[3]; _iris.Value = d[4]; _gain.Value = d[5]; _defog.Value = d[6];
                break;
            case MessageId.IrFlirParam when d.Length >= 7:
                _agc.Value = d[0]; _contrast.Value = d[1]; _brightness.Value = d[2]; _sso.Value = d[3];
                _dde.Value = ByteReader.S8(d, 4); _ace.Value = ByteReader.S8(d, 5); _preset.Value = d[6];
                break;
            case MessageId.IrMwirParam when d.Length >= 9:
                _mwirBrightness.Value = d[7];
                _mwirContrast.Value = d[8];
                break;
        }
    }
}
