using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Epsilon.Core.Protocol;

namespace Epsilon.Simulator;

/// <summary>
/// Minimal Epsilon gimbal simulator for testing the GCS without hardware.
///   EpsilonSimulator [--port 4001] [--reply-port 4002] [--video] [--video-host 127.0.0.1] [--video-port 15004] [--ffmpeg path]
/// - Listens for protocol packets on UDP --port and replies to the sender's IP on --reply-port.
/// - Integrates RATE_CONTROL speeds, keeps control mode / flags, flies a slow orbit and computes the geo target.
/// - With --video, starts FFmpeg to send a 1280x720 H.264 MPEG-TS test pattern to the GCS video port.
/// </summary>
public static class Program
{
    private static readonly object Lock = new();
    private static UdpClient _udp;
    private static IPEndPoint _client;
    private static int _replyPort = 4002;

    // gimbal state
    private static double _pan, _tilt = -30, _zoom, _focus = 128;
    private static int _panSpeed, _tiltSpeed, _zoomSpeed, _focusStep;
    private static ControlMode _mode = ControlMode.Rate;
    private static bool _stab, _stabTrack, _rec, _osi = true, _manualFocus, _ir, _laser;
    private static DateTime _gyroBiasUntil = DateTime.MinValue;
    private static double? _lockLat, _lockLon;
    private static double _stowPan, _stowTilt = -90;
    private static readonly Dictionary<MessageId, byte[]> Stored = new();

    // platform (orbit)
    private const double OrbitLat = 35.2828, OrbitLon = -120.6596, OrbitRadiusM = 800, OrbitPeriodS = 180;
    private const double AltMsl = 450, GroundMsl = 100;
    private static double _lat = OrbitLat, _lon = OrbitLon, _yaw;

    public static int Main(string[] args)
    {
        int port = IntArg(args, "--port", 4001);
        _replyPort = IntArg(args, "--reply-port", 4002);
        bool video = args.Contains("--video");
        string videoHost = StrArg(args, "--video-host", "127.0.0.1");
        int videoPort = IntArg(args, "--video-port", 15004);

        Console.WriteLine("Epsilon gimbal simulator");
        Console.WriteLine($"  protocol: UDP {port} (replies to sender:{_replyPort})");

        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        if (OperatingSystem.IsWindows())
        {
            try { _udp.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
        }

        Process ffmpeg = null;
        if (video) ffmpeg = StartVideo(StrArg(args, "--ffmpeg", null), videoHost, videoPort);

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var physics = Task.Run(() => PhysicsLoop(cts.Token));
        var receive = Task.Run(() => ReceiveLoop(cts.Token));
        Console.WriteLine("Running. Press Ctrl+C to stop.");
        try { Task.WaitAll(physics, receive); } catch { }

        try { ffmpeg?.Kill(true); } catch { }
        return 0;
    }

    // ------------------------------------------------------------------ networking

    private static async Task ReceiveLoop(CancellationToken ct)
    {
        var parser = new PacketParser();
        parser.PacketReceived += Handle;
        parser.ParseError += e => Console.WriteLine("  ! " + e);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var ep = new IPEndPoint(r.RemoteEndPoint.Address, _replyPort);
            if (_client == null || !_client.Equals(ep))
            {
                _client = ep;
                Console.WriteLine($"  GCS at {ep}");
            }
            parser.Feed(r.Buffer, 0, r.Buffer.Length);
        }
    }

    private static void Reply(Packet p)
    {
        if (_client == null) return;
        var bytes = p.Encode();
        try { _udp.Send(bytes, bytes.Length, _client); } catch { }
    }

    private static void Handle(Packet p)
    {
        lock (Lock)
        {
            var d = p.Data;
            if (p.Id != MessageId.RateControl)
                Console.WriteLine($"  <- {p}{(d.Length > 0 ? "  [" + Packet.ToHex(d) + "]" : "")}");

            switch (p.Id)
            {
                case MessageId.RateControl:
                    _panSpeed = ByteReader.S8(d, 0);
                    _tiltSpeed = ByteReader.S8(d, 1);
                    _zoomSpeed = ByteReader.S8(d, 4);
                    _focusStep = ByteReader.S8(d, 5);
                    Reply(new Packet(MessageId.GlobalStatus, BuildStatus()));
                    return;

                case MessageId.GetVersion:
                    Reply(new Packet(MessageId.Version, BuildVersion()));
                    return;

                case MessageId.Errors:
                    Reply(new Packet(MessageId.Errors, new byte[16]));
                    return;

                case MessageId.GetImageSize:
                    Reply(new Packet(MessageId.GetImageSize, new ByteWriter().U16(1280).U16(720).ToArray()));
                    return;

                case MessageId.ErrorHandlingReqClr when d.Length >= 3 && d[0] <= 1:
                    Reply(new Packet(MessageId.ErrorHandlingResponse, BuildErrorEntry(d[0])));
                    return;

                case MessageId.DeviceDiagnostic when d.Length >= 2:
                    Reply(new Packet(MessageId.DeviceDiagnostic,
                        Encoding.ASCII.GetBytes($"SIMULATOR device {d[0]} page {d[1]}\nstatus: OK\nuptime: {Environment.TickCount64 / 1000} s")));
                    return;
            }

            if (d.Length == 0)
            {
                // Request of a "Req" setting: answer with the last stored value, otherwise just ACK.
                Reply(Stored.TryGetValue(p.Id, out var stored) ? new Packet(p.Id, stored) : new Packet(p.Id));
                return;
            }

            Stored[p.Id] = d;
            Apply(p.Id, d);
            Reply(new Packet(p.Id)); // ACK
        }
    }

    private static void Apply(MessageId id, byte[] d)
    {
        switch (id)
        {
            case MessageId.GyroBias: _gyroBiasUntil = DateTime.UtcNow.AddSeconds(3); break;
            case MessageId.VideoStabilization: _stab = d[0] != 0; break;
            case MessageId.StabilizeOnTrack: _stabTrack = d[0] != 0; break;
            case MessageId.VideoRecording: if (d[0] == 1) _rec = true; else if (d[0] == 2) _rec = false; break;
            case MessageId.OnScreenInformation: _osi = (ByteReader.U16(d, 0) & 1) != 0; break;
            case MessageId.FocusMode: _manualFocus = (d[0] & 1) != 0; break;
            case MessageId.SetCameraOrder: _ir = d[0] == 1 || d[0] == 3; break;
            case MessageId.LaserPointer: _laser = d[0] != 0; break;
            case MessageId.StowMode:
                _stowPan = ByteReader.U16(d, 0) / 10.0;
                _stowTilt = ByteReader.S16(d, 2) / 10.0;
                break;
            case MessageId.GeoLock:
                _lockLat = Cmd.DecodeLatLon(ByteReader.S32(d, 0));
                _lockLon = Cmd.DecodeLatLon(ByteReader.S32(d, 4));
                _mode = ControlMode.GeoLock;
                break;
            case MessageId.SetControlMode:
                var mode = (ControlMode)d[0];
                if (mode == ControlMode.NoChange) break;
                _mode = mode;
                if (mode is ControlMode.TrackVehicle or ControlMode.TrackScene or ControlMode.TrackStationary
                    or ControlMode.TrackStatic or ControlMode.GeoLock)
                {
                    // Lock on the ground point currently in the line of sight (unless an explicit geo lock point was given).
                    if (mode != ControlMode.GeoLock || !_lockLat.HasValue)
                    {
                        var t = Target();
                        _lockLat = t.Lat;
                        _lockLon = t.Lon;
                    }
                }
                else
                {
                    _lockLat = _lockLon = null;
                }
                break;
        }
    }

    // ------------------------------------------------------------------ physics

    private static async Task PhysicsLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double last = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
            double now = sw.Elapsed.TotalSeconds, dt = now - last;
            last = now;
            lock (Lock) Step(now, dt);
        }
    }

    private static void Step(double t, double dt)
    {
        // platform orbit
        double ang = 2 * Math.PI * t / OrbitPeriodS;
        double north = OrbitRadiusM * Math.Cos(ang), east = OrbitRadiusM * Math.Sin(ang);
        _lat = OrbitLat + north / 111320.0;
        _lon = OrbitLon + east / (111320.0 * Math.Cos(OrbitLat * Math.PI / 180));
        _yaw = Wrap180(ang * 180 / Math.PI + 90);

        double fovScale = 1 - _zoom / 255.0 * 0.95;
        switch (_mode)
        {
            case ControlMode.Rate:
            case ControlMode.RateAid:
                _pan += _panSpeed / 100.0 * 60 * dt * fovScale;
                _tilt -= _tiltSpeed / 100.0 * 60 * dt * fovScale;   // like the hardware: positive TILT_SPEED = down
                break;
            case ControlMode.Stow:
                _pan = Approach(_pan, _stowPan, 60 * dt);
                _tilt = Approach(_tilt, _stowTilt, 60 * dt);
                break;
            case ControlMode.PilotView:
                _pan = Approach(_pan, 0, 60 * dt);
                _tilt = Approach(_tilt, -10, 60 * dt);
                break;
            default:
                if (_lockLat.HasValue)
                {
                    double dist = Distance(_lat, _lon, _lockLat.Value, _lockLon.Value);
                    double brg = Bearing(_lat, _lon, _lockLat.Value, _lockLon.Value);
                    _pan = brg - _yaw;
                    _tilt = -Math.Atan2(AltMsl - GroundMsl, Math.Max(1, dist)) * 180 / Math.PI;
                }
                break;
        }
        _pan = (_pan % 360 + 360) % 360;
        _tilt = Math.Clamp(_tilt, -90, 30);
        _zoom = Math.Clamp(_zoom + _zoomSpeed * 12 * dt, 0, 255);
        if (_manualFocus) _focus = Math.Clamp(_focus + _focusStep * 10 * dt, 0, 255);
    }

    private static (double Lat, double Lon, double Dist, bool Valid) Target()
    {
        double h = AltMsl - GroundMsl;
        if (_tilt > -1) return (0, 0, 0, false);
        double dist = Math.Min(h / Math.Tan(-_tilt * Math.PI / 180), 30000);
        double brg = (_yaw + _pan) * Math.PI / 180;
        double lat = _lat + dist * Math.Cos(brg) / 111320.0;
        double lon = _lon + dist * Math.Sin(brg) / (111320.0 * Math.Cos(_lat * Math.PI / 180));
        return (lat, lon, Math.Sqrt(dist * dist + h * h), true);
    }

    // ------------------------------------------------------------------ packets

    private static byte[] BuildStatus()
    {
        uint flags = 0;
        if (DateTime.UtcNow < _gyroBiasUntil) flags |= (uint)StatusFlags.GyroBiasInProgress;
        if (_laser) flags |= (uint)(StatusFlags.LaserPointerOn | StatusFlags.LaserSwitchOn);
        flags |= (uint)(StatusFlags.VideoProcessorReady | StatusFlags.GimbalInitDone);
        if (_ir) flags |= (uint)StatusFlags.ActiveCameraIr;
        if (_stabTrack) flags |= (uint)StatusFlags.StabilizeOnTrack;
        if (_stab) flags |= (uint)StatusFlags.VideoStabilization;
        if (_manualFocus) flags |= (uint)StatusFlags.ManualFocus;
        if (_rec) flags |= (uint)StatusFlags.VideoRecording;
        if (_osi) flags |= (uint)StatusFlags.OnScreenInfo;
        flags |= (uint)(StatusFlags.GeoGpsCalibrationOk | StatusFlags.GeoGpsFix);
        flags |= (uint)_mode << 16;

        double hfov = (_ir ? 30 : 60) * (1 - _zoom / 255.0 * 0.95);
        var target = Target();
        double az = Wrap180(_yaw + _pan);

        var w = new ByteWriter()
            .U32(flags)
            .U16((int)Math.Round(_pan * 100))
            .S16((int)Math.Round(_tilt * 100))
            .U8((int)_zoom)
            .U8((int)_focus)
            .U16((int)Math.Round(hfov * 9 / 16 * 10))
            .U16((int)Math.Round(hfov * 10))
            .S8(38).S8(41).S8(55)
            .S32(Cmd.EncodeLatLon(_lat))
            .S32(Cmd.EncodeLatLon(_lon))
            .S16((int)AltMsl)
            .S16(150).S16(-200).S16((int)Math.Round(_yaw * 100))
            .S16((int)Math.Round(az * 10))
            .S16((int)Math.Round(_tilt * 10))
            .U16(target.Valid ? (int)target.Dist : 0)
            .S32(target.Valid ? Cmd.EncodeLatLon(target.Lat) : 0)
            .S32(target.Valid ? Cmd.EncodeLatLon(target.Lon) : 0)
            .U8(12).U8(14).U8(11).U8(10)
            .U8(0).U8(0)
            .U8(0x88)
            .U8(5);
        return w.ToArray();
    }

    private static byte[] BuildVersion() =>
        new ByteWriter()
            .U8(10)                           // Epsilon 180
            .U64(VersionInfo.SimulatorUniqueId)  // lets the GCS show a SIMULATOR badge
            .U8(4).U8(0).U8(4).U8(1)          // firmware 4.0.4.1
            .U8(1).U8(2).U8(0).U8(0)          // tilt bootloader
            .U8(1).U8(2).U8(0).U8(0)          // pan bootloader
            .U8(3).U8(3)                      // PCB versions
            .U8(4).U8(0)                      // VP firmware
            .U32(0x05FF)                      // VP features
            .U8(4).U8(5).U8(0)                // Sony 4K, MWIR, default optics
            .U8(1).U8(1).U8(1)                // LRF, laser, GEO
            .U32(0x0007_000F)                 // Epsilon features
            .U8(4).U8(0).U8(4).U8(1)          // pan firmware
            .ToArray();

    private static byte[] BuildErrorEntry(int command)
    {
        string text = command == 0 ? "Simulator: no real hardware connected" : "Simulator log entry";
        var bytes = Encoding.ASCII.GetBytes(text);
        return new ByteWriter()
            .U8(command).U16(1).U16(0).U16(9001).U8(1)
            .U64((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .S32(0).U8(1).U8(0).U8(bytes.Length)
            .Ascii(text, 64)
            .ToArray();
    }

    // ------------------------------------------------------------------ video

    private static Process StartVideo(string ffmpegPath, string host, int port)
    {
        string exe = ffmpegPath ?? FindFfmpeg();
        if (exe == null)
        {
            Console.WriteLine("  ! --video: ffmpeg.exe not found (run tools\\get-tools.ps1 or pass --ffmpeg <path>)");
            return null;
        }
        string args = "-hide_banner -loglevel error -re -f lavfi -i testsrc2=size=1280x720:rate=30 " +
                      "-c:v libx264 -preset ultrafast -tune zerolatency -g 30 -b:v 3M -pix_fmt yuv420p " +
                      $"-f mpegts \"udp://{host}:{port}?pkt_size=1316\"";
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            Console.WriteLine($"  video: test pattern H.264 MPEG-TS -> udp://{host}:{port}");
            return p;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ! could not start ffmpeg: " + ex.Message);
            return null;
        }
    }

    private static string FindFfmpeg()
    {
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(dir, "tools", name),
            Path.Combine(dir, "..", "EpsilonGCS", "tools", name),
            Path.Combine(dir, "..", "tools", name),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var c = Path.Combine(p.Trim(), name);
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static double Approach(double cur, double target, double step)
    {
        double diff = Wrap180(target - cur);
        if (Math.Abs(diff) <= step) return target;
        return cur + Math.Sign(diff) * step;
    }

    private static double Wrap180(double a)
    {
        a %= 360;
        if (a > 180) a -= 360;
        if (a < -180) a += 360;
        return a;
    }

    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        double dn = (lat2 - lat1) * 111320.0;
        double de = (lon2 - lon1) * 111320.0 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(dn * dn + de * de);
    }

    private static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        double dn = (lat2 - lat1) * 111320.0;
        double de = (lon2 - lon1) * 111320.0 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Atan2(de, dn) * 180 / Math.PI;
    }

    private static int IntArg(string[] args, string name, int def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : def;
    }

    private static string StrArg(string[] args, string name, string def)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }
}
