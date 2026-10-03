using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

public sealed class CommonPage : FlyoutPage
{
    public override string Title => "Common";

    private readonly ChoiceField _clickMode;
    private readonly NumField _box, _nudge;
    private readonly BoolField _intelligent, _highNoise, _acquisition, _sceneOnDrop, _geoOnDrop;
    private readonly BoolField _syncFov, _roll, _flip, _invert, _confirmLaser, _invertTilt;
    private readonly BoolField _puStow, _puCooler;
    private readonly NumField _stowPan, _stowTilt;
    private readonly BoolField _stowOff;
    private readonly NumField _pilotPan, _pilotTilt, _front;
    private readonly NumField _trimPan, _trimTilt;
    private readonly ChoiceField _resetType;

    public CommonPage()
    {
        var s = Gcs.Settings;

        F.Section("Control settings");
        _clickMode = F.Choice("Click-to-track mode", (int)s.ClickTrackMode,
            ((int)ControlMode.TrackVehicle, "Tracking vehicle (VHCL)"),
            ((int)ControlMode.TrackScene, "Tracking scene (SCN)"),
            ((int)ControlMode.TrackStationary, "Tracking stationary"),
            ((int)ControlMode.TrackStatic, "Tracking static"));
        _box = F.Number("Track box size px", s.TrackBoxSize, 0, 255);
        _nudge = F.Number("Nudge step px", s.NudgeStepPx, 1, 50);
        _confirmLaser = F.Check("Ask for confirmation before laser ON", s.ConfirmLaser);
        _invertTilt = F.Check("Invert tilt direction (Up arrow = camera up on Epsilon hardware)", s.InvertTilt);

        F.Section("Tracking advanced (0x14 byte 6)");
        int adv = s.TrackingAdvanced;
        _acquisition = F.Check("Acquisition assist", (adv & TrackingAdvancedBits.AcquisitionAssist) != 0);
        _intelligent = F.Check("Intelligent assist (needs acquisition assist)", (adv & TrackingAdvancedBits.IntelligentAssist) != 0);
        _highNoise = F.Check("High noise compensation", (adv & TrackingAdvancedBits.HighNoiseCompensation) != 0);
        _sceneOnDrop = F.Check("Scene track when vehicle track drops", (adv & TrackingAdvancedBits.SceneOnVehicleDrop) != 0);
        _geoOnDrop = F.Check("GEO mode when vehicle track drops", (adv & TrackingAdvancedBits.GeoOnVehicleDrop) != 0);

        F.Section("Control mode options (0x14 byte 7)");
        int opt = s.ControlOptions;
        _syncFov = F.Check("Sync EO and IR FOV", (opt & ControlOptionBits.SyncEoIrFov) != 0);
        _roll = F.Check("Roll horizon alignment", (opt & ControlOptionBits.RollHorizonAlignment) != 0);
        _flip = F.Check("EO/IR flipping mode", (opt & ControlOptionBits.Flipping) != 0);
        _invert = F.Check("Invert image", (opt & ControlOptionBits.InvertImage) != 0);
        F.Buttons(("Save & apply", ApplyControl));

        F.Section("Power up (0x3C)");
        _puStow = F.Check("Start Epsilon in STOW mode", false);
        _puCooler = F.Check("IR cooler on at power up", true);
        F.Buttons(("Read", () => Req(MessageId.PowerUpSettings)),
                  ("Apply", () => Send(Cmd.PowerUpSettings(_puStow.Value, _puCooler.Value))));

        F.Section("Stow position (0x1D)");
        _stowPan = F.Number("Pan °", 0, 0, 360);
        _stowTilt = F.Number("Tilt °", -90, -90, 90);
        _stowOff = F.Check("Turn off peripherals in stow (power saving)", false);
        F.Buttons(("Read", () => Req(MessageId.StowMode)),
                  ("Apply", () => Send(Cmd.StowMode(_stowPan.Value, _stowTilt.Value, _stowOff.Value))));

        F.Section("Pilot view (0x38) / aircraft front (0x37)");
        _pilotPan = F.Number("Pilot view pan °", 0, 0, 360);
        _pilotTilt = F.Number("Pilot view tilt °", -10, -90, 90);
        F.Buttons(("Read", () => Req(MessageId.PilotViewAngle)),
                  ("Apply pilot view", () => Send(Cmd.PilotViewAngle(_pilotPan.Value, _pilotTilt.Value))));
        _front = F.Number("Pan angle to aircraft front °", 0, 0, 360);
        F.Buttons(("Read", () => Req(MessageId.PanOffset)), ("Apply front angle", () => Send(Cmd.PanOffset(_front.Value))));

        F.Section("Pan / tilt trims (0x11)");
        F.Note("Fine-tunes motor speed to stop slow drift. Gyro bias resets trims.");
        _trimPan = F.Number("Pan trim", 0, -32768, 32767);
        _trimTilt = F.Number("Tilt trim", 0, -32768, 32767);
        F.Buttons(("Read", () => Req(MessageId.SetPanTiltTrims)),
                  ("Apply", () => Send(Cmd.PanTiltTrims(_trimPan.Int, _trimTilt.Int))));

        F.Section("Maintenance");
        F.Buttons(
            ("Gyro bias", () =>
            {
                if (Confirm("Gyro drift compensation must only be done while the gimbal is not moving.\n" +
                            "Motors switch off for ~10 s and the link is lost while it runs.\n\nStart now?"))
                    Send(Cmd.GyroBias());
            }),
            ("Reset gimbal", () => { if (Confirm("Hardware reset the gimbal?")) Send(Cmd.Reset()); }),
            ("Clear SD card", () => { if (Confirm("Delete ALL files on the gimbal SD card?")) Send(Cmd.ClearSdCard()); }));

        _resetType = F.Choice("Reset settings", 0,
            "Video processor settings", "Factory default except network", "Full factory default");
        F.Buttons(("Reset settings", () =>
        {
            if (Confirm("Reset gimbal settings: " + _resetType.Box.SelectedItem + "?"))
                Send(Cmd.ResetDefaultSettings(_resetType.Value));
        }));
    }

    private void ApplyControl()
    {
        var s = Gcs.Settings;
        s.ClickTrackMode = (ControlMode)_clickMode.Value;
        s.TrackBoxSize = _box.Int;
        s.NudgeStepPx = _nudge.Int;
        s.ConfirmLaser = _confirmLaser.Value;
        s.InvertTilt = _invertTilt.Value;
        s.TrackingAdvanced =
            (_intelligent.Value ? TrackingAdvancedBits.IntelligentAssist : 0) |
            (_highNoise.Value ? TrackingAdvancedBits.HighNoiseCompensation : 0) |
            (_acquisition.Value ? TrackingAdvancedBits.AcquisitionAssist : 0) |
            (_sceneOnDrop.Value ? TrackingAdvancedBits.SceneOnVehicleDrop : 0) |
            (_geoOnDrop.Value ? TrackingAdvancedBits.GeoOnVehicleDrop : 0);
        s.ControlOptions =
            (_syncFov.Value ? ControlOptionBits.SyncEoIrFov : 0) |
            (_roll.Value ? ControlOptionBits.RollHorizonAlignment : 0) |
            (_flip.Value ? ControlOptionBits.Flipping : 0) |
            (_invert.Value ? ControlOptionBits.InvertImage : 0);
        s.Save();
        Gcs.SyncTrackingProfile();
        Gcs.Controller.ApplyTrackingProfile();
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.PowerUpSettings when d.Length >= 1:
                _puStow.Value = (d[0] & 1) != 0;
                _puCooler.Value = (d[0] & 2) != 0;
                break;
            case MessageId.StowMode when d.Length >= 5:
                _stowPan.Value = ByteReader.U16(d, 0) / 10.0;
                _stowTilt.Value = ByteReader.S16(d, 2) / 10.0;
                _stowOff.Value = (d[4] & 1) != 0;
                break;
            case MessageId.PilotViewAngle when d.Length >= 4:
                _pilotPan.Value = ByteReader.U16(d, 0) / 10.0;
                _pilotTilt.Value = ByteReader.S16(d, 2) / 10.0;
                break;
            case MessageId.PanOffset when d.Length >= 2:
                _front.Value = ByteReader.U16(d, 0) / 10.0;
                break;
            case MessageId.SetPanTiltTrims when d.Length >= 4:
                _trimPan.Value = ByteReader.S16(d, 0);
                _trimTilt.Value = ByteReader.S16(d, 2);
                break;
            case MessageId.SetControlMode when d.Length >= 8:
                _box.Value = d[5];
                break;
        }
    }
}
