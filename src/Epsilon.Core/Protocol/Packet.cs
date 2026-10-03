namespace Epsilon.Core.Protocol;

/// <summary>
/// One protocol frame (section 3):
/// 0xAA 0x55 | DeviceID | MessageID | Length | HeaderCRC | Data[Length] | DataCRC | 0xFF
/// Zero-length packets carry no data and no data checksum.
/// </summary>
public sealed class Packet
{
    public const byte Header1 = 0xAA;
    public const byte Header2 = 0x55;
    public const byte Terminator = 0xFF;

    public byte DeviceId { get; }
    public MessageId Id { get; }
    public byte[] Data { get; }
    public int Length => Data.Length;
    public bool IsZeroLength => Data.Length == 0;

    public Packet(MessageId id, byte[] data = null, byte deviceId = 0)
    {
        Id = id;
        Data = data ?? Array.Empty<byte>();
        DeviceId = deviceId;
        if (Data.Length > 255)
            throw new ArgumentException("Packet data cannot exceed 255 bytes.", nameof(data));
    }

    public byte[] Encode()
    {
        int len = Data.Length;
        var buf = new byte[len == 0 ? 7 : 8 + len];
        buf[0] = Header1;
        buf[1] = Header2;
        buf[2] = DeviceId;
        buf[3] = (byte)Id;
        buf[4] = (byte)len;
        buf[5] = Crc8.Compute(new ReadOnlySpan<byte>(buf, 2, 3));
        if (len == 0)
        {
            buf[6] = Terminator;
        }
        else
        {
            Array.Copy(Data, 0, buf, 6, len);
            buf[6 + len] = Crc8.Compute(Data);
            buf[7 + len] = Terminator;
        }
        return buf;
    }

    public static string ToHex(byte[] bytes) =>
        bytes == null ? "" : string.Join(" ", bytes.Select(b => b.ToString("X2")));

    public override string ToString() => $"{Id} (0x{(byte)Id:X2}) len={Length}";
}
