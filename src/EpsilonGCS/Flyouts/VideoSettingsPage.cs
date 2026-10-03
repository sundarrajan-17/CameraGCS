using Epsilon.Core.Protocol;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

public sealed class VideoSettingsPage : FlyoutPage
{
    public override string Title => "Video Settings";

    private readonly List<(int Bit, BoolField Field)> _osi = new();
    private readonly NumField _frameStep;
    private readonly ChoiceField _frameSize;
    private readonly NumField _bitrate, _iframe;
    private readonly ChoiceField _deblock, _profile, _brc;
    private readonly ChoiceField _output, _analog;
    private readonly BoolField _klv;
    private readonly ChoiceField _klvField;
    private readonly TextField _klvValue;
    private readonly ChoiceField _pipMode, _pipScale;

    public VideoSettingsPage()
    {
        var s = Gcs.Settings;

        F.Section("On screen information (0x0C)");
        foreach (var (bit, name) in OsiBits.Names)
            _osi.Add((bit, F.Check(name, (s.OsiMask & bit) != 0)));
        F.Buttons(("Read", () => Req(MessageId.OnScreenInformation)), ("Apply", ApplyOsi));

        F.Section("Digital zoom (0x0B)");
        F.Buttons(("Zoom out", () => Send(Cmd.DigitalZoom(-1))), ("Zoom in", () => Send(Cmd.DigitalZoom(1))));

        F.Section("Video display size (0x0D)");
        _frameStep = F.Number("Frame step", 1, 0, 255);
        _frameSize = F.Choice("Frame size", 2, "SD 640x480", "HD cropped 960x720", "HD 1280x720",
            "Full HD 1920x1080 (140G2 zoom, 180)", "4K 3840x2160 (180)");
        F.Buttons(("Read", () => Req(MessageId.VideoDisplaySize)),
                  ("Apply", () => Send(Cmd.VideoDisplaySize(_frameStep.Int, _frameSize.Value))));

        F.Section("H.264 encoder (0x97)");
        _bitrate = F.Number("Bitrate kbit/s", 1500, 100, 65535);
        _iframe = F.Number("Key frame interval", 30, 1, 255);
        _deblock = F.Choice("Deblocking", 0, "Filter all edges", "Disable all filtering", "Disable slice edge filter");
        _profile = F.Choice("Profile", 0, "Baseline", "Main", "High");
        _brc = F.Choice("Bit rate control", 0, "Legacy", "Variable bit rate", "Constrained");
        F.Note("RTP protocols always use the Baseline profile.");
        F.Buttons(("Read", () => Req(MessageId.H264Parameters)),
                  ("Apply", () => Send(Cmd.H264Parameters(_bitrate.Int, _iframe.Int, _deblock.Value, _profile.Value, _brc.Value))));

        F.Section("Video output mode (0x96)");
        _output = F.Choice("Output", 2, "None", "Analog (135, 140, 140Z, 175)", "Network", "Analog + network");
        _analog = F.Choice("Analog standard", 1, "NTSC", "PAL");
        F.Buttons(("Read", () => Req(MessageId.VideoOutputMode)),
                  ("Apply", () => Send(Cmd.VideoOutputMode(_output.Value, _analog.Value))));

        F.Section("Picture in picture (0x35)");
        _pipMode = F.Choice("Display mode", 1, "One up", "Picture in picture", "Zoom to track", "Blend");
        _pipScale = F.Choice("PIP scale", 0, "1/4", "3/8", "1/2");
        F.Buttons(("Read", () => Req(MessageId.PipSettings)),
                  ("Apply", () => Send(Cmd.PipSettings(_pipMode.Value, _pipScale.Value))));

        F.Section("KLV metadata (0x1F / 0x36)");
        _klv = F.Check("KLV stream enabled", true);
        F.Buttons(("Read", () => Req(MessageId.EnableKlv)), ("Apply", () => Send(Cmd.EnableKlv(_klv.Value))));
        _klvField = F.Choice("Static field", 0,
            (0, "Mission ID"), (1, "Platform designation"), (2, "Image source sensor"), (3, "Image coordinate system"),
            (4, "Security: classification"), (5, "Security: country coding method"), (6, "Security: classifying country"),
            (7, "Security: SCI/SHI"), (8, "Security: caveats"), (9, "Security: releasing instructions"),
            (10, "Security: object country coding"), (11, "Security: object country"),
            (13, "Platform tail number"), (17, "Platform call sign"));
        _klvValue = F.Text("Value", "", "Empty = remove field from stream (max 127 chars)");
        F.Buttons(("Set field", () => Send(Cmd.KlvStaticData(_klvField.Value, _klvValue.Value))));

        F.Section("Video processor");
        F.Buttons(("Save & reset VP", () =>
        {
            if (Confirm("Save settings and restart the video processor? Video will drop for a few seconds."))
                Send(Cmd.SaveAndResetVp());
        }));
    }

    private void ApplyOsi()
    {
        int mask = 0;
        foreach (var (bit, field) in _osi) if (field.Value) mask |= bit;
        Gcs.Settings.OsiMask = mask;
        Gcs.Settings.Save();
        Send(Cmd.OnScreenInformation(mask));
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.OnScreenInformation when d.Length >= 2:
                int mask = ByteReader.U16(d, 0);
                foreach (var (bit, field) in _osi) field.Value = (mask & bit) != 0;
                break;
            case MessageId.VideoDisplaySize when d.Length >= 2:
                _frameStep.Value = d[0]; _frameSize.Value = d[1];
                break;
            case MessageId.H264Parameters when d.Length >= 6:
                _bitrate.Value = ByteReader.U16(d, 0); _iframe.Value = d[2];
                _deblock.Value = d[3]; _profile.Value = d[4]; _brc.Value = d[5];
                break;
            case MessageId.VideoOutputMode when d.Length >= 2:
                _output.Value = d[0]; _analog.Value = d[1];
                break;
            case MessageId.PipSettings when d.Length >= 2:
                _pipMode.Value = d[0]; _pipScale.Value = d[1];
                break;
            case MessageId.EnableKlv when d.Length >= 1:
                _klv.Value = d[0] != 0;
                break;
        }
    }
}
