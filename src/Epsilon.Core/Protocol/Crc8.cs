namespace Epsilon.Core.Protocol;

/// <summary>
/// Checksum used by the Epsilon General Communication Protocol v4.0 (section 3.2).
/// Section 3 calls it an "XOR checksum", but the table, pseudo code and worked examples in 3.2
/// are a CRC-8 Dallas/Maxim (reflected polynomial 0x8C) seeded with 0x01.
/// Verified against the document examples:
///   00 07 01 -> 0x9B, data 01 -> 0x00, 00 01 00 -> 0x6F, 00 02 00 -> 0x3A.
/// </summary>
public static class Crc8
{
    public const byte Seed = 0x01;

    /// <summary>Lookup table identical to crc8_Table[] in the protocol document.</summary>
    public static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int b = 0; b < 8; b++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0x8C : c >> 1;
            t[i] = (byte)c;
        }
        return t;
    }

    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte crc = Seed;
        foreach (var b in data)
            crc = Table[crc ^ b];
        return crc;
    }

    public static byte Compute(byte[] data) => Compute(new ReadOnlySpan<byte>(data));
}
