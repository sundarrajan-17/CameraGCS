using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Epsilon.Core.Transport;

/// <summary>Raw byte link to the gimbal (UDP or RS-232).</summary>
public interface IGimbalTransport : IDisposable
{
    string Description { get; }
    bool IsOpen { get; }
    event Action<byte[], int> DataReceived;
    event Action<Exception> Faulted;
    void Open();
    void Send(byte[] data);
    void Close();
}

/// <summary>
/// UDP link. The gimbal listens on its input port (default 4001) and sends replies to its
/// destination IP/port (default 4002), see SET_NETWORK_SETTINGS 0x12.
/// </summary>
public sealed class UdpTransport : IGimbalTransport
{
    private readonly IPEndPoint _remote;
    private readonly int _localPort;
    private UdpClient _udp;
    private CancellationTokenSource _cts;

    public UdpTransport(string gimbalIp, int gimbalPort, int localPort)
    {
        _remote = new IPEndPoint(IPAddress.Parse(gimbalIp), gimbalPort);
        _localPort = localPort;
    }

    public string Description => $"UDP {_remote} (listening on {_localPort})";
    public bool IsOpen => _udp != null;

    public event Action<byte[], int> DataReceived;
    public event Action<Exception> Faulted;

    public void Open()
    {
        if (_udp != null) return;
        var udp = new UdpClient(AddressFamily.InterNetwork);
        // Exclusive: if another program (e.g. Epsilon Control) also listened on this port, Windows would hand each
        // reply to only one of the two sockets and the link would drop in and out. Fail clearly instead.
        udp.ExclusiveAddressUse = true;
        udp.Client.ReceiveBufferSize = 1 << 20;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _localPort));
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            udp.Dispose();
            throw new InvalidOperationException(
                $"UDP port {_localPort} is already used by another program (for example Epsilon Control or a second " +
                "Epsilon GCS). Close it, or change the GCS listen port and the gimbal destination port (0x12).", ex);
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Stop Windows reporting ICMP "port unreachable" as a socket error (WSAECONNRESET).
            const int SIO_UDP_CONNRESET = -1744830452;
            try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { /* ignore */ }
        }
        _udp = udp;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(udp, token));
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
                DataReceived?.Invoke(result.Buffer, result.Buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Local IPv4 address on the same subnet as <paramref name="remote"/>, or null when no active adapter is in
    /// that subnet (the most common reason for "no reply": the PC has no address in the gimbal network,
    /// e.g. only a 169.254.x.x link-local address on the cable to the gimbal).
    /// </summary>
    public static string LocalAddressInSubnetOf(IPAddress remote)
    {
        var rb = remote.GetAddressBytes();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var lb = ua.Address.GetAddressBytes();
                var mb = ua.IPv4Mask.GetAddressBytes();
                bool same = true;
                for (int i = 0; i < 4; i++) same &= (lb[i] & mb[i]) == (rb[i] & mb[i]);
                if (same) return ua.Address.ToString();
            }
        }
        return null;
    }

    public IPEndPoint Remote => _remote;

    public void Send(byte[] data)
    {
        var udp = _udp;
        if (udp == null) return;
        try { udp.Send(data, data.Length, _remote); }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    public void Close()
    {
        _cts?.Cancel();
        _cts = null;
        var udp = _udp;
        _udp = null;
        udp?.Dispose();
    }

    public void Dispose() => Close();
}

/// <summary>RS-232 link. The protocol document does not state the baud rate; it is configurable (default 115200 8N1).</summary>
public sealed class SerialTransport : IGimbalTransport
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort _port;

    public SerialTransport(string portName, int baudRate)
    {
        _portName = portName;
        _baud = baudRate;
    }

    public string Description => $"Serial {_portName} @ {_baud}";
    public bool IsOpen => _port?.IsOpen == true;

    public event Action<byte[], int> DataReceived;
    public event Action<Exception> Faulted;

    public static string[] AvailablePorts() => SerialPort.GetPortNames();

    public void Open()
    {
        if (_port != null) return;
        _port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            Handshake = Handshake.None,
        };
        _port.DataReceived += OnData;
        _port.ErrorReceived += (_, e) => Faulted?.Invoke(new IOException("Serial error: " + e.EventType));
        _port.Open();
    }

    private void OnData(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var port = _port;
            if (port == null || !port.IsOpen) return;
            int n = port.BytesToRead;
            if (n <= 0) return;
            var buf = new byte[n];
            int read = port.Read(buf, 0, n);
            if (read > 0) DataReceived?.Invoke(buf, read);
        }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    public void Send(byte[] data)
    {
        try { if (_port?.IsOpen == true) _port.Write(data, 0, data.Length); }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    public void Close()
    {
        var p = _port;
        _port = null;
        if (p == null) return;
        try { p.DataReceived -= OnData; p.Close(); } catch { /* ignore */ }
        p.Dispose();
    }

    public void Dispose() => Close();
}
