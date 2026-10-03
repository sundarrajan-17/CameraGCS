using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Epsilon.Core.Protocol;
using Epsilon.Core.Transport;

namespace Epsilon.Core.Client;

/// <summary>
/// Live joystick / button state sent continuously in RATE_CONTROL (0x05).
/// The gimbal zeroes all rates if it receives no RATE_CONTROL for 1 second.
/// </summary>
public sealed class RateControlState
{
    private int _pan, _tilt, _zoom, _focus, _nudgeCol, _nudgeRow, _dted;

    public int PanSpeed { get => Volatile.Read(ref _pan); set => Volatile.Write(ref _pan, Math.Clamp(value, -100, 100)); }
    public int TiltSpeed { get => Volatile.Read(ref _tilt); set => Volatile.Write(ref _tilt, Math.Clamp(value, -100, 100)); }
    public int ZoomSpeed { get => Volatile.Read(ref _zoom); set => Volatile.Write(ref _zoom, Math.Clamp(value, -8, 8)); }
    /// <summary>Applied by the gimbal on every packet while non-zero (manual focus only).</summary>
    public int FocusStep { get => Volatile.Read(ref _focus); set => Volatile.Write(ref _focus, Math.Clamp(value, -128, 127)); }
    /// <summary>Track-box nudge in pixels, applied on every packet while non-zero.</summary>
    public int NudgeColumn { get => Volatile.Read(ref _nudgeCol); set => Volatile.Write(ref _nudgeCol, Math.Clamp(value, -128, 127)); }
    public int NudgeRow { get => Volatile.Read(ref _nudgeRow); set => Volatile.Write(ref _nudgeRow, Math.Clamp(value, -128, 127)); }
    /// <summary>Height above EGM in metres (GEO_DTED).</summary>
    public int DtedHeight { get => Volatile.Read(ref _dted); set => Volatile.Write(ref _dted, value); }

    public void StopAll()
    {
        PanSpeed = 0; TiltSpeed = 0; ZoomSpeed = 0; FocusStep = 0; NudgeColumn = 0; NudgeRow = 0;
    }

    public Packet BuildPacket() =>
        Cmd.RateControl(PanSpeed, TiltSpeed, NudgeColumn, NudgeRow, ZoomSpeed, FocusStep, DtedHeight);
}

/// <summary>
/// High-level gimbal connection.
/// - Serialises all outgoing packets with the mandatory >= 50 ms gap (section 4.1 note).
/// - Sends RATE_CONTROL periodically (keeps control alive and polls EPSILON_GLOBAL_STATUS 0x80).
/// - Parses replies and raises typed events. Events fire on a background thread.
/// </summary>
public sealed class GimbalClient : IDisposable
{
    private readonly PacketParser _parser = new();
    private readonly ConcurrentQueue<Packet> _queue = new();
    private readonly object _rxLock = new();
    private IGimbalTransport _transport;
    private CancellationTokenSource _cts;
    private Timer _watchdog;
    private long _lastRxTicks;
    private long _lastReopenTicks;
    private volatile bool _connected;
    private readonly object _linkLock = new();

    public RateControlState Rate { get; } = new();

    /// <summary>Minimum delay between packets. Protocol requires at least 50 ms.</summary>
    public int MinPacketGapMs { get; set; } = 50;

    /// <summary>RATE_CONTROL period. Must be well below 1000 ms.</summary>
    public int RateControlPeriodMs { get; set; } = 200;

    public bool RateControlEnabled { get; set; } = true;

    /// <summary>Link considered lost after this long without any valid packet.</summary>
    public int LinkTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// When nothing valid has been received for this long, the link (socket / COM port) is closed and opened
    /// again automatically, every <see cref="ReopenIntervalMs"/>. Recovers from a re-plugged cable or USB-serial
    /// adapter, a network adapter that was reset, or a gimbal reboot. 0 disables.
    /// </summary>
    public int ReopenIntervalMs { get; set; } = 5000;

    public long Reconnects { get; private set; }

    public bool IsOpen => _transport?.IsOpen == true;
    public bool IsConnected => _connected;
    public string TransportDescription => _transport?.Description ?? "Not connected";
    private GlobalStatus _lastStatus;

    /// <summary>
    /// Latest EPSILON_GLOBAL_STATUS (0x80), written by the receive thread. <see cref="GlobalStatus"/> is immutable and
    /// the reference is published atomically, so any thread can read a consistent snapshot without locking.
    /// </summary>
    public GlobalStatus LastStatus
    {
        get => Volatile.Read(ref _lastStatus);
        private set => Volatile.Write(ref _lastStatus, value);
    }
    public VersionInfo Version { get; private set; }
    public long PacketsSent { get; private set; }
    public long PacketsReceived => _parser.GoodPackets;
    public long BadPackets => _parser.BadPackets;
    public bool LogTraffic { get; set; }

    public event Action<Packet> PacketReceived;
    public event Action<GlobalStatus> StatusReceived;
    public event Action<VersionInfo> VersionReceived;
    public event Action<LegacyErrors> LegacyErrorsReceived;
    public event Action<ErrorReport> ErrorReportReceived;
    public event Action<int, int> ImageSizeReceived;
    public event Action<string> DiagnosticReceived;
    public event Action<bool> ConnectionChanged;
    public event Action<string> Log;

    public GimbalClient()
    {
        _parser.PacketReceived += OnPacket;
        _parser.ParseError += msg => Log?.Invoke(msg);
    }

    public void Connect(IGimbalTransport transport)
    {
        Disconnect();
        transport.DataReceived += OnData;
        transport.Faulted += ex => Log?.Invoke("Link error: " + ex.Message);
        try
        {
            transport.Open();
        }
        catch
        {
            transport.DataReceived -= OnData;
            transport.Dispose();
            throw;
        }
        _transport = transport;
        lock (_rxLock) _parser.Reset();
        Interlocked.Exchange(ref _lastRxTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _lastReopenTicks, Environment.TickCount64);

        if (transport is UdpTransport udp &&
            !System.Net.IPAddress.IsLoopback(udp.Remote.Address) &&
            UdpTransport.LocalAddressInSubnetOf(udp.Remote.Address) == null)
        {
            Log?.Invoke($"WARNING: this PC has no network address in the subnet of the gimbal {udp.Remote.Address}. " +
                        "Give the Ethernet adapter connected to the gimbal a static IP in that subnet (e.g. 192.168.1.10 / 255.255.255.0), " +
                        "otherwise replies may not arrive.");
        }
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SenderLoop(_cts.Token));
        _watchdog = new Timer(_ => CheckLink(), null, 500, 500);
        Log?.Invoke("Opened " + _transport.Description);

        Send(Cmd.GetVersion());
        Send(Cmd.GetImageSize());
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _cts = null;
        _watchdog?.Dispose();
        _watchdog = null;
        while (_queue.TryDequeue(out _)) { }
        if (_transport != null)
        {
            _transport.DataReceived -= OnData;
            _transport.Dispose();
            Log?.Invoke("Closed " + _transport.Description);
            _transport = null;
        }
        SetConnected(false);
    }

    /// <summary>Queues a packet. Packets are sent in order with the minimum gap.</summary>
    public void Send(Packet packet)
    {
        if (packet == null || _transport == null) return;
        _queue.Enqueue(packet);
    }

    /// <summary>Requests the current value of a "Req" setting (zero-length packet).</summary>
    public void Request(MessageId id) => Send(Cmd.Request(id));

    private async Task SenderLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastRate = -100000;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(MinPacketGapMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var transport = _transport;
            if (transport == null) continue;

            long now = sw.ElapsedMilliseconds;
            long sinceRate = now - lastRate;
            bool rateDue = RateControlEnabled && sinceRate >= RateControlPeriodMs;
            bool rateOverdue = RateControlEnabled && sinceRate >= RateControlPeriodMs * 2;

            Packet packet;
            if (rateDue && (rateOverdue || _queue.IsEmpty))
            {
                packet = Rate.BuildPacket();
                lastRate = now;
            }
            else if (!_queue.TryDequeue(out packet))
            {
                continue;
            }

            transport.Send(packet.Encode());
            PacketsSent++;
            if (LogTraffic && packet.Id != MessageId.RateControl)
                Log?.Invoke($"TX {packet} [{Packet.ToHex(packet.Encode())}]");
        }
    }

    private void OnData(byte[] buffer, int count)
    {
        lock (_rxLock) _parser.Feed(buffer, 0, count);
    }

    private void OnPacket(Packet p)
    {
        Interlocked.Exchange(ref _lastRxTicks, Environment.TickCount64);
        SetConnected(true);

        if (LogTraffic && p.Id != MessageId.GlobalStatus)
            Log?.Invoke($"RX {p} [{Packet.ToHex(p.Data)}]");

        try
        {
            switch (p.Id)
            {
                case MessageId.GlobalStatus when p.Length >= 4:
                    var status = GlobalStatus.Parse(p.Data);
                    LastStatus = status;
                    StatusReceived?.Invoke(status);
                    break;
                case MessageId.Version when p.Length >= 12:
                    Version = VersionInfo.Parse(p.Data);
                    VersionReceived?.Invoke(Version);
                    break;
                case MessageId.Errors when p.Length >= 12:
                    LegacyErrorsReceived?.Invoke(LegacyErrors.Parse(p.Data));
                    break;
                case MessageId.ErrorHandlingResponse when p.Length >= 8:
                    ErrorReportReceived?.Invoke(ErrorReport.Parse(p.Data));
                    break;
                case MessageId.GetImageSize when p.Length >= 4:
                    ImageSizeReceived?.Invoke(ByteReader.U16(p.Data, 0), ByteReader.U16(p.Data, 2));
                    break;
                case MessageId.DeviceDiagnostic when p.Length > 0:
                    DiagnosticReceived?.Invoke(Encoding.ASCII.GetString(p.Data).TrimEnd('\0'));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Failed to decode {p}: {ex.Message}");
        }

        PacketReceived?.Invoke(p);
    }

    private void CheckLink()
    {
        long now = Environment.TickCount64;
        long silent = now - Interlocked.Read(ref _lastRxTicks);
        if (_connected && silent > LinkTimeoutMs)
            SetConnected(false);

        // No reply for a while: re-open the socket / COM port and ask for version + image size again.
        if (!_connected && ReopenIntervalMs > 0 && silent > ReopenIntervalMs &&
            now - Interlocked.Read(ref _lastReopenTicks) > ReopenIntervalMs)
        {
            Interlocked.Exchange(ref _lastReopenTicks, now);
            Reopen();
        }
    }

    private void Reopen()
    {
        var t = _transport;
        if (t == null) return;
        try
        {
            t.Close();
            if (_transport != t) return;        // disconnected meanwhile
            t.Open();
            if (_transport != t) { t.Close(); return; }
            lock (_rxLock) _parser.Reset();
            Reconnects++;
            Log?.Invoke($"No reply from gimbal - link re-opened ({t.Description})");
            Send(Cmd.GetVersion());
            Send(Cmd.GetImageSize());
        }
        catch (Exception ex)
        {
            Log?.Invoke("Re-open failed (will retry): " + ex.Message);
        }
    }

    private void SetConnected(bool value)
    {
        lock (_linkLock)
        {
            if (_connected == value) return;
            _connected = value;
        }
        Log?.Invoke(value ? "Gimbal link established" : "Gimbal link lost");
        if (value)
        {
            // (Re)synchronise state that is only sent on request: version/features and the video image size.
            Send(Cmd.GetVersion());
            Send(Cmd.GetImageSize());
        }
        ConnectionChanged?.Invoke(value);
    }

    public void Dispose() => Disconnect();
}
