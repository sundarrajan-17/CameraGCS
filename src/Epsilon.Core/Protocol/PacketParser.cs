namespace Epsilon.Core.Protocol;

/// <summary>
/// Byte-stream state machine that extracts validated packets from serial/UDP data.
/// Handles packets split across reads. After a bad header/data checksum or terminator it
/// rewinds to the byte after the false 0xAA and scans again, so a good packet that was
/// swallowed by a corrupt or truncated one is not lost (important on lossy serial / UDP links).
/// Not thread-safe: feed it from one thread.
/// </summary>
public sealed class PacketParser
{
    private enum State { Header1, Header2, Device, Message, Length, HeaderCrc, Data, DataCrc, Terminator }

    private State _state = State.Header1;
    private byte _device, _message, _length;
    private byte[] _data = Array.Empty<byte>();
    private int _index;

    // Unconsumed input. _pos = next byte to read, _frameStart = index of the 0xAA of the packet being assembled.
    private readonly List<byte> _buf = new(1024);
    private int _pos, _frameStart;

    public long GoodPackets { get; private set; }
    public long BadPackets { get; private set; }

    public event Action<Packet> PacketReceived;
    public event Action<string> ParseError;

    public void Reset()
    {
        _state = State.Header1;
        _buf.Clear();
        _pos = _frameStart = 0;
    }

    public void Feed(byte[] bytes, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++) _buf.Add(bytes[i]);
        while (_pos < _buf.Count)
            Step(_buf[_pos++]);

        // Keep only the bytes of an unfinished packet.
        int keepFrom = _state == State.Header1 ? _buf.Count : _frameStart;
        if (keepFrom > 0)
        {
            _buf.RemoveRange(0, keepFrom);
            _pos -= keepFrom;
            _frameStart -= keepFrom;
        }
    }

    public void Feed(byte[] bytes) => Feed(bytes, 0, bytes.Length);

    private void Step(byte b)
    {
        switch (_state)
        {
            case State.Header1:
                if (b == Packet.Header1)
                {
                    _frameStart = _pos - 1;
                    _state = State.Header2;
                }
                break;

            case State.Header2:
                if (b == Packet.Header2) _state = State.Device;
                else if (b == Packet.Header1) _frameStart = _pos - 1;
                else _state = State.Header1;
                break;

            case State.Device:
                _device = b;
                _state = State.Message;
                break;

            case State.Message:
                _message = b;
                _state = State.Length;
                break;

            case State.Length:
                _length = b;
                _state = State.HeaderCrc;
                break;

            case State.HeaderCrc:
                if (Crc8.Compute(new[] { _device, _message, _length }) != b)
                {
                    Fail($"header checksum (msg 0x{_message:X2})");
                    return;
                }
                _data = new byte[_length];
                _index = 0;
                _state = _length == 0 ? State.Terminator : State.Data;
                break;

            case State.Data:
                _data[_index++] = b;
                if (_index >= _length) _state = State.DataCrc;
                break;

            case State.DataCrc:
                if (Crc8.Compute(_data) != b)
                {
                    Fail($"data checksum (msg 0x{_message:X2})");
                    return;
                }
                _state = State.Terminator;
                break;

            case State.Terminator:
                if (b != Packet.Terminator)
                {
                    Fail($"terminator (msg 0x{_message:X2})");
                    return;
                }
                _state = State.Header1;
                GoodPackets++;
                PacketReceived?.Invoke(new Packet((MessageId)_message, _data, _device));
                break;
        }
    }

    private void Fail(string reason)
    {
        BadPackets++;
        _state = State.Header1;
        _pos = _frameStart + 1; // rescan from the byte after the false 0xAA
        ParseError?.Invoke("Bad packet: " + reason);
    }
}
