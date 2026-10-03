using Epsilon.Core.Protocol;

namespace EpsilonGCS.Flyouts;

public sealed class ProcessingPage : FlyoutPage
{
    public override string Title => "Processing";

    private readonly NumField _drift, _maxOffset, _rollComp;
    private readonly ChoiceField _edge;
    private readonly ChoiceField _mtiMode, _downsample;
    private readonly NumField _mtiSens;
    private readonly ChoiceField _blendMode;
    private readonly NumField _blendZoom, _blendAmount, _blendHue, _hotStart, _coldEnd;
    private readonly NumField _alV, _alH, _alRot, _alZoom, _alHZoom;

    public ProcessingPage()
    {
        F.Section("Video stabilization (0x07 / 0x22 / 0x23)");
        F.Buttons(("Stab ON", () => Send(Cmd.VideoStabilization(true))), ("Stab OFF", () => Send(Cmd.VideoStabilization(false))),
                  ("Stab on track ON", () => Send(Cmd.StabilizeOnTrack(true))), ("OFF", () => Send(Cmd.StabilizeOnTrack(false))));
        _drift = F.Number("Drift rate", 20, 0, 255);
        _maxOffset = F.Number("Maximum offset", 60, 0, 255);
        _rollComp = F.Number("Roll compensation", 0, 0, 255);
        _edge = F.Choice("Image edge", 0, "Fading to gray", "Solid gray", "Previous image color");
        F.Note("Applies to the active camera.");
        F.Buttons(("Read", () => Req(MessageId.VideoStabilizationParam)),
                  ("Apply", () => Send(Cmd.VideoStabilizationParameters(_drift.Int, _maxOffset.Int, _rollComp.Int, _edge.Value))));

        F.Section("Moving target indication (0x13)");
        _mtiMode = F.Choice("Mode", 1, "Disabled", "Vehicle (baseline)", "Staring", "Aerial (advanced)", "Anomaly", "Maritime");
        _mtiSens = F.Number("Sensitivity (1 high..10 low)", 5, 1, 10);
        _downsample = F.Choice("Downsample", 255, (0, "1x1"), (1, "2x2"), (2, "4x4"), (3, "8x8"), (255, "Auto"));
        F.Buttons(("Read", () => Req(MessageId.MtiParameters)),
                  ("Apply", () => Send(Cmd.MtiParameters(_mtiMode.Value, _mtiSens.Int, _downsample.Value))),
                  ("Reset tracks", () => Send(Cmd.MtiParameters(_mtiMode.Value, 0, _downsample.Value, true))));

        F.Section("Blend (0x3D)");
        _blendMode = F.Choice("Blend mode", 1,
            (1, "Frame blend warped EO"), (2, "Thermal blend warped EO"), (3, "Night blend warped EO"),
            (4, "Color blend warped EO"), (6, "Frame blend fixed EO"), (7, "Thermal blend fixed EO"),
            (8, "Night blend fixed EO"), (9, "Color blend fixed EO"), (10, "Color IR blend fixed EO"),
            (11, "Color IR blend warped EO"));
        _blendZoom = F.Number("Zoom level (1..5)", 1, 0, 5, "0 = no change");
        _blendAmount = F.Number("Amount", 128, 0, 255);
        _blendHue = F.Number("Hue", 0, 0, 255);
        _hotStart = F.Number("Hot start (thermal)", 200, 0, 255);
        _coldEnd = F.Number("Cold end (thermal)", 50, 0, 255);
        F.Note("To show the blend, set PIP display mode to Blend in Video Settings.");
        F.Buttons(("Read", () => Req(MessageId.BlendSettings)),
                  ("Apply", () => Send(Cmd.BlendSettings(_blendMode.Value, _blendZoom.Int, _blendAmount.Int, _blendHue.Int, _hotStart.Int, _coldEnd.Int))));

        F.Section("Blend alignment (0x3E)");
        _alV = F.Number("Vertical", 0, -32, 32);
        _alH = F.Number("Horizontal", 0, -32, 32);
        _alRot = F.Number("Rotation (x0.04°)", 0, -128, 127);
        _alZoom = F.Number("Zoom", 0, -128, 127);
        _alHZoom = F.Number("Horizontal zoom", 0, -128, 127);
        F.Buttons(("Read", () => Req(MessageId.BlendAlignment)),
                  ("Apply", () => Send(Cmd.BlendAlignment(_alV.Int, _alH.Int, _alRot.Int, _alZoom.Int, _alHZoom.Int))));
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.VideoStabilizationParam when d.Length >= 4:
                _drift.Value = d[0]; _maxOffset.Value = d[1]; _rollComp.Value = d[2]; _edge.Value = d[3];
                break;
            case MessageId.MtiParameters when d.Length >= 4:
                _mtiMode.Value = d[0];
                if (d[1] > 0) _mtiSens.Value = d[1];
                _downsample.Value = d[3];
                break;
            case MessageId.BlendSettings when d.Length >= 6:
                _blendMode.Value = d[0]; _blendZoom.Value = d[1]; _blendAmount.Value = d[2];
                _blendHue.Value = d[3]; _hotStart.Value = d[4]; _coldEnd.Value = d[5];
                break;
            case MessageId.BlendAlignment when d.Length >= 5:
                _alV.Value = ByteReader.S8(d, 0); _alH.Value = ByteReader.S8(d, 1); _alRot.Value = ByteReader.S8(d, 2);
                _alZoom.Value = ByteReader.S8(d, 3); _alHZoom.Value = ByteReader.S8(d, 4);
                break;
        }
    }
}
