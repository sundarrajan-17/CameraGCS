namespace Epsilon.Video;

/// <summary>
/// Lightweight MPEG-TS inspector used for the "Klv Packets" counter under the video.
/// Reads PAT -> PMT to find the KLV metadata elementary stream
/// (stream_type 0x15, or 0x06 with a "KLVA" registration descriptor) and counts KLV PES packets.
/// Works on raw TS datagrams and on RTP-wrapped TS (the RTP header is skipped).
/// Called from the single UDP receive thread only.
/// </summary>
public sealed class TsInspector
{
    private const int PacketSize = 188;
    private const byte SyncByte = 0x47;

    private int _pmtPid = -1;
    private readonly HashSet<int> _klvPids = new();
    private int _videoPid = -1;
    private long _klvPackets;

    public long KlvPackets => Interlocked.Read(ref _klvPackets);
    public bool HasKlvTrack { get; private set; }
    public string VideoCodec { get; private set; } = "";

    public void Reset()
    {
        _pmtPid = -1;
        _klvPids.Clear();
        _videoPid = -1;
        HasKlvTrack = false;
        VideoCodec = "";
        Interlocked.Exchange(ref _klvPackets, 0);
    }

    public void Feed(byte[] buf, int len)
    {
        int start = FindStart(buf, len);
        if (start < 0) return;

        for (int off = start; off + PacketSize <= len; off += PacketSize)
        {
            if (buf[off] != SyncByte) return; // lost alignment, wait for the next datagram

            int pid = ((buf[off + 1] & 0x1F) << 8) | buf[off + 2];
            bool unitStart = (buf[off + 1] & 0x40) != 0;
            int adaptation = (buf[off + 3] >> 4) & 0x03;
            int p = off + 4;
            if (adaptation == 2 || adaptation == 0) continue;           // no payload
            if (adaptation == 3) p += 1 + buf[off + 4];                  // skip adaptation field
            int end = off + PacketSize;
            if (p >= end) continue;

            if (pid == 0 && unitStart) ParsePat(buf, p, end);
            else if (pid == _pmtPid && unitStart) ParsePmt(buf, p, end);
            else if (unitStart && _klvPids.Contains(pid)) Interlocked.Increment(ref _klvPackets);
        }
    }

    private static int FindStart(byte[] buf, int len)
    {
        if (len >= PacketSize && buf[0] == SyncByte) return 0;
        int rem = len % PacketSize;                       // e.g. 12-byte RTP header + 7 TS packets
        if (rem > 0 && rem < len && buf[rem] == SyncByte) return rem;
        return -1;
    }

    private void ParsePat(byte[] b, int p, int end)
    {
        p += 1 + b[p];                                      // pointer_field
        if (p + 8 > end || b[p] != 0x00) return;            // table_id PAT
        int sectionLength = ((b[p + 1] & 0x0F) << 8) | b[p + 2];
        int last = Math.Min(p + 3 + sectionLength - 4, end); // exclude CRC
        for (int i = p + 8; i + 4 <= last; i += 4)
        {
            int program = (b[i] << 8) | b[i + 1];
            int pid = ((b[i + 2] & 0x1F) << 8) | b[i + 3];
            if (program != 0) { _pmtPid = pid; return; }
        }
    }

    private void ParsePmt(byte[] b, int p, int end)
    {
        p += 1 + b[p];
        if (p + 12 > end || b[p] != 0x02) return;           // table_id PMT
        int sectionLength = ((b[p + 1] & 0x0F) << 8) | b[p + 2];
        int programInfoLength = ((b[p + 10] & 0x0F) << 8) | b[p + 11];
        int last = Math.Min(p + 3 + sectionLength - 4, end);
        int i = p + 12 + programInfoLength;
        while (i + 5 <= last)
        {
            int streamType = b[i];
            int pid = ((b[i + 1] & 0x1F) << 8) | b[i + 2];
            int esInfoLength = ((b[i + 3] & 0x0F) << 8) | b[i + 4];
            int descStart = i + 5, descEnd = Math.Min(descStart + esInfoLength, last);

            if (streamType == 0x15 || (streamType == 0x06 && HasKlvaRegistration(b, descStart, descEnd)))
            {
                _klvPids.Add(pid);
                HasKlvTrack = true;
            }
            else if (_videoPid < 0 && (streamType == 0x1B || streamType == 0x24 || streamType == 0x02))
            {
                _videoPid = pid;
                VideoCodec = streamType == 0x1B ? "H.264" : streamType == 0x24 ? "H.265" : "MPEG-2";
            }
            i = descStart + esInfoLength;
        }
    }

    private static bool HasKlvaRegistration(byte[] b, int i, int end)
    {
        while (i + 2 <= end)
        {
            int tag = b[i], len = b[i + 1];
            if (tag == 0x05 && len >= 4 && i + 6 <= end &&
                b[i + 2] == 'K' && b[i + 3] == 'L' && b[i + 4] == 'V' && b[i + 5] == 'A')
                return true;
            i += 2 + len;
        }
        return false;
    }
}
