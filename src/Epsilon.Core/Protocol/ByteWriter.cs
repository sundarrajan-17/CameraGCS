using System.Text;

namespace Epsilon.Core.Protocol;

/// <summary>Little-endian payload builder (protocol section 3.1: LSB first). Values are clamped to range.</summary>
public sealed class ByteWriter
{
    private readonly List<byte> _bytes = new();

    public int Count => _bytes.Count;

    public ByteWriter U8(int value)
    {
        _bytes.Add((byte)Math.Clamp(value, 0, 255));
        return this;
    }

    public ByteWriter S8(int value)
    {
        int v = Math.Clamp(value, -128, 127);
        _bytes.Add((byte)(v & 0xFF));
        return this;
    }

    public ByteWriter U16(int value)
    {
        int v = Math.Clamp(value, 0, 65535);
        _bytes.Add((byte)(v & 0xFF));
        _bytes.Add((byte)((v >> 8) & 0xFF));
        return this;
    }

    public ByteWriter S16(int value)
    {
        int v = Math.Clamp(value, -32768, 32767);
        _bytes.Add((byte)(v & 0xFF));
        _bytes.Add((byte)((v >> 8) & 0xFF));
        return this;
    }

    public ByteWriter S32(long value)
    {
        long v = Math.Clamp(value, int.MinValue, int.MaxValue);
        int i32 = (int)v;
        for (int i = 0; i < 4; i++)
            _bytes.Add((byte)((i32 >> (8 * i)) & 0xFF));
        return this;
    }

    public ByteWriter U32(uint value)
    {
        for (int i = 0; i < 4; i++)
            _bytes.Add((byte)((value >> (8 * i)) & 0xFF));
        return this;
    }

    public ByteWriter U64(ulong value)
    {
        for (int i = 0; i < 8; i++)
            _bytes.Add((byte)((value >> (8 * i)) & 0xFF));
        return this;
    }

    /// <summary>Writes "a.b.c.d" as 4 bytes in dot order. Invalid text writes 0.0.0.0 ("no change").</summary>
    public ByteWriter Ip(string ip)
    {
        _bytes.AddRange(ParseIp(ip));
        return this;
    }

    public ByteWriter Ascii(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return this;
        var bytes = Encoding.ASCII.GetBytes(text);
        int n = Math.Min(bytes.Length, maxLength);
        for (int i = 0; i < n; i++) _bytes.Add(bytes[i]);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();

    public static byte[] ParseIp(string ip)
    {
        var result = new byte[4];
        if (string.IsNullOrWhiteSpace(ip)) return result;
        var parts = ip.Trim().Split('.');
        if (parts.Length != 4) return result;
        for (int i = 0; i < 4; i++)
            if (!byte.TryParse(parts[i], out result[i])) return new byte[4];
        return result;
    }
}

/// <summary>Little-endian readers that tolerate short payloads (missing bytes read as 0).</summary>
public static class ByteReader
{
    public static byte U8(byte[] d, int o) => o >= 0 && o < d.Length ? d[o] : (byte)0;

    public static int S8(byte[] d, int o)
    {
        int v = U8(d, o);
        return v > 127 ? v - 256 : v;
    }

    public static int U16(byte[] d, int o) => U8(d, o) | (U8(d, o + 1) << 8);

    public static int S16(byte[] d, int o)
    {
        int v = U16(d, o);
        return v > 32767 ? v - 65536 : v;
    }

    public static uint U32(byte[] d, int o) =>
        (uint)U8(d, o) | ((uint)U8(d, o + 1) << 8) | ((uint)U8(d, o + 2) << 16) | ((uint)U8(d, o + 3) << 24);

    public static int S32(byte[] d, int o) => unchecked((int)U32(d, o));

    public static ulong U64(byte[] d, int o) => U32(d, o) | ((ulong)U32(d, o + 4) << 32);

    public static string Ip(byte[] d, int o) => $"{U8(d, o)}.{U8(d, o + 1)}.{U8(d, o + 2)}.{U8(d, o + 3)}";
}
