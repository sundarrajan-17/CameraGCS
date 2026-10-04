using System.Globalization;

namespace Epsilon.Core.Maps;

/// <summary>Cache statistics.</summary>
public readonly record struct TileCacheStats(long Tiles, long Bytes);

/// <summary>
/// On-disk tile cache: one file per tile, <c>root/&lt;providerId&gt;/&lt;z&gt;/&lt;x&gt;/&lt;y&gt;.&lt;ext&gt;</c>
/// (ext = png / jpg / webp / gif, detected from the image bytes). The provider id is part of the path, so the same
/// z/x/y of different providers never collide. No database: the directory structure is the index.
///
/// Expiry: <see cref="MaxAgeDays"/> (0 = never) applies to every tile by file age. Tiles of providers that require
/// HTTP cache rules (Google) get a "y.exp" side file holding the expiry time from Cache-Control: max-age.
/// Writes go to a temporary file first and are then moved into place, so a crash never leaves half a tile.
/// Thread-safe for concurrent readers and writers.
/// </summary>
public sealed class TileCache
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".webp", ".gif" };

    public TileCache(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    /// <summary>Tiles older than this many days are treated as expired (re-downloaded when online). 0 = never.</summary>
    public int MaxAgeDays { get; set; }

    public string ProviderDirectory(string providerId) => Path.Combine(Root, providerId);

    private string TileBase(string providerId, int z, int x, int y) =>
        Path.Combine(Root, providerId, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture),
                     y.ToString(CultureInfo.InvariantCulture));

    /// <summary>Existing cached file of a tile, or null.</summary>
    public string FindFile(string providerId, int z, int x, int y)
    {
        string b = TileBase(providerId, z, x, y);
        foreach (var ext in Extensions)
            if (File.Exists(b + ext)) return b + ext;
        return null;
    }

    public bool Contains(string providerId, int z, int x, int y, bool allowExpired = false) =>
        TryRead(providerId, z, x, y, allowExpired, out _, out _);

    /// <summary>
    /// Reads a cached tile. Corrupt files (not an image) are deleted and reported as missing.
    /// <paramref name="expired"/> is true when the tile is past its expiry (only returned with <paramref name="allowExpired"/>).
    /// </summary>
    public bool TryRead(string providerId, int z, int x, int y, bool allowExpired, out byte[] data, out bool expired)
    {
        data = null;
        expired = false;
        string file = FindFile(providerId, z, x, y);
        if (file == null) return false;
        try
        {
            expired = IsExpired(file);
            if (expired && !allowExpired) return false;
            var bytes = File.ReadAllBytes(file);
            if (!LooksLikeImage(bytes))
            {
                Delete(file);
                return false;
            }
            data = bytes;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool IsExpired(string file)
    {
        string exp = Path.ChangeExtension(file, ".exp");
        if (File.Exists(exp))
        {
            if (long.TryParse(File.ReadAllText(exp).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                return DateTime.UtcNow.Ticks > ticks;
            return true;
        }
        return MaxAgeDays > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(MaxAgeDays);
    }

    /// <summary>
    /// Stores a tile after checking that it is an image. <paramref name="expiresUtc"/> = hard expiry from HTTP headers
    /// (null = only <see cref="MaxAgeDays"/> applies). Returns false for invalid data.
    /// </summary>
    public bool Write(string providerId, int z, int x, int y, byte[] data, DateTime? expiresUtc = null)
    {
        if (!LooksLikeImage(data)) return false;
        string ext = ImageExtension(data);
        string b = TileBase(providerId, z, x, y);
        string file = b + ext;
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        string tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, file, overwrite: true);
            foreach (var other in Extensions)
                if (other != ext) Delete(b + other);   // the server changed format
            string exp = b + ".exp";
            if (expiresUtc.HasValue) File.WriteAllText(exp, expiresUtc.Value.Ticks.ToString(CultureInfo.InvariantCulture));
            else Delete(exp);
            return true;
        }
        catch (IOException) { Delete(tmp); return false; }
        catch (UnauthorizedAccessException) { Delete(tmp); return false; }
    }

    /// <summary>Removes one tile (e.g. after it failed to decode).</summary>
    public void Invalidate(string providerId, int z, int x, int y)
    {
        string b = TileBase(providerId, z, x, y);
        foreach (var ext in Extensions) Delete(b + ext);
        Delete(b + ".exp");
    }

    /// <summary>Counts tiles and bytes (whole cache or one provider). Can take a while: call off the UI thread.</summary>
    public TileCacheStats GetStats(string providerId = null, CancellationToken ct = default)
    {
        string dir = providerId == null ? Root : ProviderDirectory(providerId);
        if (!Directory.Exists(dir)) return new TileCacheStats(0, 0);
        long tiles = 0, bytes = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (Array.IndexOf(Extensions, f.Extension.ToLowerInvariant()) < 0) continue;
            tiles++;
            bytes += f.Length;
        }
        return new TileCacheStats(tiles, bytes);
    }

    /// <summary>Deletes all cached tiles of one provider. Only on explicit operator request.</summary>
    public void ClearProvider(string providerId)
    {
        if (!CustomMapDefinition.IsSafeId(providerId)) throw new ArgumentException("Invalid provider id.");
        string dir = ProviderDirectory(providerId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Deletes the whole cache. Only on explicit operator request.</summary>
    public void ClearAll()
    {
        foreach (var d in Directory.EnumerateDirectories(Root)) Directory.Delete(d, recursive: true);
    }

    /// <summary>
    /// One-time move of tiles cached by earlier versions (root/&lt;layer folder&gt;/z/x/y.png) into this cache.
    /// Existing tiles in the new place win. Returns the number of tiles moved.
    /// </summary>
    public int MigrateLegacy(string legacyRoot, IReadOnlyDictionary<string, string> legacyFolderToProviderId)
    {
        if (string.IsNullOrEmpty(legacyRoot) || !Directory.Exists(legacyRoot)) return 0;
        int moved = 0;
        foreach (var (folder, providerId) in legacyFolderToProviderId)
        {
            string src = Path.Combine(legacyRoot, folder);
            if (!Directory.Exists(src)) continue;
            foreach (var file in Directory.EnumerateFiles(src, "*.png", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (rel.Length != 3 || !int.TryParse(rel[0], out int z) || !int.TryParse(rel[1], out int x) ||
                    !int.TryParse(Path.GetFileNameWithoutExtension(rel[2]), out int y)) continue;
                try
                {
                    if (FindFile(providerId, z, x, y) == null)
                    {
                        var data = File.ReadAllBytes(file);
                        if (Write(providerId, z, x, y, data)) moved++;
                    }
                    File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(src, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return moved;
    }

    // ------------------------------------------------------------------ image validation

    /// <summary>File extension for PNG / JPEG / WebP / GIF data, or null when the bytes are not one of these images.</summary>
    public static string ImageExtension(byte[] d)
    {
        if (d == null || d.Length < 12) return null;
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return ".png";
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
        if (d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ".webp";
        if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8') return ".gif";
        return null;
    }

    public static bool LooksLikeImage(byte[] d)
    {
        string ext = ImageExtension(d);
        if (ext == null) return false;
        // Truncated downloads: PNG must end with the IEND chunk, JPEG with the EOI marker.
        if (ext == ".png") return d.Length >= 20 && d[^8] == 'I' && d[^7] == 'E' && d[^6] == 'N' && d[^5] == 'D';
        if (ext == ".jpg")
        {
            for (int i = d.Length - 2; i >= Math.Max(2, d.Length - 32); i--)   // EOI, allowing a little padding
                if (d[i] == 0xFF && d[i + 1] == 0xD9) return true;
            return false;
        }
        return true;
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
