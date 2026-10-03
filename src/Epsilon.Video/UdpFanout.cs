using System.Net;
using System.Net.Sockets;

namespace Epsilon.Video;

/// <summary>
/// Receives the gimbal's UDP video stream (MPEG-TS, default port 15004) once and copies every
/// datagram to local loopback ports. This lets the video display and the RTSP restreamer consume
/// the same stream without fighting over the UDP port, and without re-encoding.
/// </summary>
public sealed class UdpFanout : IDisposable
{
    private readonly object _lock = new();
    private readonly List<IPEndPoint> _targets = new();
    private UdpClient _in;
    private UdpClient _out;
    private CancellationTokenSource _cts;
    private long _packets, _bytes;
    private long _lastBytes, _lastTicks;

    public int InputPort { get; private set; }
    public bool IsRunning => _in != null;
    public long Packets => Interlocked.Read(ref _packets);
    public long Bytes => Interlocked.Read(ref _bytes);
    public DateTime LastPacketUtc { get; private set; }

    /// <summary>Finds the KLV track in the MPEG-TS stream and counts KLV packets.</summary>
    public TsInspector Inspector { get; } = new();

    public event Action<string> Log;

    public void Start(int inputPort, string multicastGroup = null)
    {
        Stop();
        InputPort = inputPort;
        Inspector.Reset();
        var udp = new UdpClient(AddressFamily.InterNetwork);
        // Exclusive bind: with a shared port (e.g. Epsilon Control also running) Windows splits the datagrams
        // between the two programs and the picture breaks up / freezes. Report it instead.
        udp.ExclusiveAddressUse = string.IsNullOrWhiteSpace(multicastGroup);
        if (!udp.ExclusiveAddressUse)
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.ReceiveBufferSize = 8 * 1024 * 1024;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, inputPort));
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            udp.Dispose();
            throw new InvalidOperationException(
                $"Video UDP port {inputPort} is already used by another program (for example Epsilon Control or VLC). Close it and press LIVE.", ex);
        }
        if (!string.IsNullOrWhiteSpace(multicastGroup))
            udp.JoinMulticastGroup(IPAddress.Parse(multicastGroup));
        _in = udp;
        _out = new UdpClient(AddressFamily.InterNetwork);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var output = _out;
        _ = Task.Run(() => Loop(udp, output, token));
        Log?.Invoke($"Video receiver listening on UDP {inputPort}");
    }

    public void AddTarget(int loopbackPort)
    {
        lock (_lock)
        {
            if (_targets.Any(t => t.Port == loopbackPort)) return;
            _targets.Add(new IPEndPoint(IPAddress.Loopback, loopbackPort));
        }
    }

    public void RemoveTarget(int loopbackPort)
    {
        lock (_lock) _targets.RemoveAll(t => t.Port == loopbackPort);
    }

    /// <summary>Bitrate in Mbit/s since the previous call.</summary>
    public double SampleBitrateMbps()
    {
        long now = Environment.TickCount64;
        long bytes = Bytes;
        double seconds = (now - _lastTicks) / 1000.0;
        double mbps = seconds > 0 && _lastTicks != 0 ? (bytes - _lastBytes) * 8 / seconds / 1_000_000 : 0;
        _lastTicks = now;
        _lastBytes = bytes;
        return Math.Max(0, mbps);
    }

    private async Task Loop(UdpClient input, UdpClient output, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await input.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            Interlocked.Increment(ref _packets);
            Interlocked.Add(ref _bytes, r.Buffer.Length);
            LastPacketUtc = DateTime.UtcNow;
            try { Inspector.Feed(r.Buffer, r.Buffer.Length); } catch { /* malformed TS: ignore */ }

            IPEndPoint[] targets;
            lock (_lock) targets = _targets.ToArray();
            foreach (var t in targets)
            {
                try { output.Send(r.Buffer, r.Buffer.Length, t); }
                catch (SocketException) { /* receiver not running yet */ }
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _in?.Dispose();
        _out?.Dispose();
        _in = null;
        _out = null;
    }

    public void Dispose() => Stop();
}
