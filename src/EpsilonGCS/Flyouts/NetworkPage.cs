using Epsilon.Core.Protocol;
using Epsilon.Core.Transport;
using Epsilon.Video;
using EpsilonGCS.Services;

namespace EpsilonGCS.Flyouts;

public sealed class NetworkPage : FlyoutPage
{
    public override string Title => "Network";

    private readonly ChoiceField _link;
    private readonly TextField _gimbalIp, _serial;
    private readonly NumField _gimbalPort, _localPort, _baud;
    private readonly BoolField _autoConnect, _logTraffic;

    private readonly ChoiceField _videoInput;
    private readonly NumField _videoPort, _caching;
    private readonly TextField _multicast, _rtspUrl;

    private readonly ChoiceField _outProtocol;
    private readonly TextField _outIp;
    private readonly NumField _outPort;
    private readonly BoolField _pad;

    private readonly ChoiceField _netMode;
    private readonly TextField _netIp, _netMask, _netGw, _netDestIp;
    private readonly NumField _netIn, _netDest;

    public NetworkPage()
    {
        var s = Gcs.Settings;

        F.Section("Gimbal link (this PC)");
        _link = F.Choice("Link", (int)s.Link, "UDP (Ethernet)", "Serial (RS-232)");
        _gimbalIp = F.Text("Gimbal IP", s.GimbalIp);
        _gimbalPort = F.Number("Gimbal input port", s.GimbalPort, 1, 65535);
        _localPort = F.Number("Local listen port", s.LocalPort, 1, 65535);
        _serial = F.Text("Serial port", s.SerialPort, "Available: " + string.Join(", ", SerialTransport.AvailablePorts()));
        _baud = F.Number("Baud rate", s.BaudRate, 1200, 3000000);
        _autoConnect = F.Check("Connect automatically on start-up", s.AutoConnect);
        _logTraffic = F.Check("Log protocol traffic (debug)", s.LogTraffic);
        F.Buttons(("Connect", ApplyLinkAndConnect), ("Disconnect", () => Gcs.Gimbal.Disconnect()));

        F.Section("Video input (this PC)");
        _videoInput = F.Choice("Source", (int)s.VideoInput, "UDP MPEG-TS from gimbal", "RTSP URL");
        _videoPort = F.Number("UDP port", s.VideoPort, 1, 65535);
        _multicast = F.Text("Multicast group", s.VideoMulticastGroup, "Leave empty for unicast");
        _rtspUrl = F.Text("RTSP URL", s.VideoRtspUrl);
        _caching = F.Number("Network caching ms", s.NetworkCachingMs, 0, 5000, "Lower = less latency, higher = smoother");
        F.Buttons(("Apply & restart video", ApplyVideo));

        F.Section("Gimbal video output (0x95)");
        F.Note("Tells the gimbal where to stream video. Use H.264 MPEG2-TS (1) or H.265 MPEG2-TS (8) for this GCS.");
        _outProtocol = F.Choice("Protocol", 1,
            (0, "0 - RTP MJPEG"), (1, "1 - H.264 MPEG2-TS"), (3, "3 - H.264 HD (Epsilon 135)"),
            (5, "5 - RTP H.264 (RTSP)"), (6, "6 - RTP MPEG2-TS H.264 (RTSP+meta)"), (8, "8 - MPEG2-TS H.265"),
            (9, "9 - RTP H.265 (RTSP)"), (10, "10 - RTP MPEG-TS H.265 (RTSP+meta)"), (15, "15 - None"));
        _outIp = F.Text("Destination IP", RtspRestreamer.BestLocalIp(),
            "This PC: " + string.Join(", ", RtspRestreamer.LocalIPv4Addresses()));
        _outPort = F.Number("Destination port", 15004, 1, 65535);
        _pad = F.Check("Pad UDP packets to same size", false);
        F.Buttons(("Read", () => Req(MessageId.EthernetDisplayParameters)),
                  ("Send to this PC", SendVideoOutput));

        F.Section("Gimbal network settings (0x12)");
        F.Note("Changes the gimbal's own IP configuration. A wrong value can make the gimbal unreachable.");
        _netMode = F.Choice("Mode", 1, "DHCP", "Static IP");
        _netIp = F.Text("Gimbal IP", s.GimbalIp);
        _netMask = F.Text("Subnet mask", "255.255.255.0");
        _netGw = F.Text("Gateway", "192.168.1.1");
        _netIn = F.Number("Gimbal input port", 4001, 0, 65535);
        _netDest = F.Number("Gimbal dest. port", 4002, 0, 65535);
        _netDestIp = F.Text("Destination IP", RtspRestreamer.BestLocalIp());
        F.Buttons(("Read", () => Req(MessageId.NetworkSettings)), ("Apply to gimbal", SendNetwork));
    }

    private void ApplyLinkAndConnect()
    {
        var s = Gcs.Settings;
        s.Link = (LinkType)_link.Value;
        s.GimbalIp = _gimbalIp.Value;
        s.GimbalPort = _gimbalPort.Int;
        s.LocalPort = _localPort.Int;
        s.SerialPort = _serial.Value;
        s.BaudRate = _baud.Int;
        s.AutoConnect = _autoConnect.Value;
        s.LogTraffic = _logTraffic.Value;
        s.Save();
        Gcs.ConnectGimbal();
    }

    private void ApplyVideo()
    {
        var s = Gcs.Settings;
        s.VideoInput = (VideoInputKind)_videoInput.Value;
        s.VideoPort = _videoPort.Int;
        s.VideoMulticastGroup = _multicast.Value;
        s.VideoRtspUrl = _rtspUrl.Value;
        s.NetworkCachingMs = _caching.Int;
        s.Save();
        bool restream = Gcs.Restreamer.State != RestreamState.Stopped;
        if (restream) Gcs.StopRestream();
        Gcs.Video.Start(restartReceiver: true);
        if (restream) Gcs.StartRestream();
    }

    private void SendVideoOutput()
    {
        Send(Cmd.EthernetDisplayParameters(_outProtocol.Value, _pad.Value, _outIp.Value, _outPort.Int));
        AppLog.Write($"Gimbal video -> {_outIp.Value}:{_outPort.Int} protocol {_outProtocol.Value}");
    }

    private void SendNetwork()
    {
        if (!Confirm("Apply new network settings to the gimbal?")) return;
        Send(Cmd.NetworkSettings(_netMode.Value == 1, _netIp.Value, _netMask.Value, _netGw.Value,
            _netIn.Int, _netDest.Int, _netDestIp.Value));
    }

    public override void OnSetting(Packet p)
    {
        var d = p.Data;
        switch (p.Id)
        {
            case MessageId.EthernetDisplayParameters when d.Length >= 7:
                _outProtocol.Value = d[0] & 0x0F;
                _pad.Value = (d[0] & 0x40) != 0;
                _outIp.Value = ByteReader.Ip(d, 1);
                _outPort.Value = ByteReader.U16(d, 5);
                break;
            case MessageId.NetworkSettings when d.Length >= 21:
                _netMode.Value = d[0];
                _netIp.Value = ByteReader.Ip(d, 1);
                _netMask.Value = ByteReader.Ip(d, 5);
                _netGw.Value = ByteReader.Ip(d, 9);
                _netIn.Value = ByteReader.U16(d, 13);
                _netDest.Value = ByteReader.U16(d, 15);
                _netDestIp.Value = ByteReader.Ip(d, 17);
                break;
        }
    }
}
