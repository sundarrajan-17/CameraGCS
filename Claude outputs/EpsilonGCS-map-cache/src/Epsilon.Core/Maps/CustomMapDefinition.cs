using System.Text.Json;
using System.Text.Json.Serialization;

namespace Epsilon.Core.Maps;

/// <summary>
/// User-defined XYZ (or TMS) tile map. Saved in the user's configuration folder and importable / exportable as JSON:
/// <code>
/// {
///   "name": "My Survey Map",
///   "urlTemplate": "https://example.com/tiles/{z}/{x}/{y}.png",
///   "minZoom": 10,
///   "maxZoom": 18,
///   "attribution": "My Map Provider",
///   "scheme": "xyz"
/// }
/// </code>
/// The template may also be a local folder: "file:///C:/Maps/MyMap/{z}/{x}/{y}.png" or "C:\Maps\MyMap\{z}\{x}\{y}.png".
/// </summary>
public sealed class CustomMapDefinition
{
    /// <summary>Stable id (cache folder). Generated when missing.</summary>
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("urlTemplate")] public string UrlTemplate { get; set; }
    [JsonPropertyName("minZoom")] public int MinZoom { get; set; } = 0;
    [JsonPropertyName("maxZoom")] public int MaxZoom { get; set; } = 19;
    [JsonPropertyName("attribution")] public string Attribution { get; set; } = "";
    /// <summary>"xyz" (default) or "tms".</summary>
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "xyz";

    [JsonIgnore]
    public TileScheme TileScheme => string.Equals(Scheme, "tms", StringComparison.OrdinalIgnoreCase) ? TileScheme.Tms : TileScheme.Xyz;

    [JsonIgnore]
    public bool IsLocal => XyzTileProvider.IsLocalTemplate(UrlTemplate);

    /// <summary>Returns null when valid, otherwise the problem.</summary>
    public string Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Enter a name.";
        if (string.IsNullOrWhiteSpace(UrlTemplate)) return "Enter the tile URL template.";
        foreach (var p in new[] { "{z}", "{x}", "{y}" })
            if (!UrlTemplate.Contains(p, StringComparison.OrdinalIgnoreCase)) return $"The URL template must contain {p}.";
        if (!IsLocal)
        {
            if (!Uri.TryCreate(XyzTileProvider.Fill(UrlTemplate, 0, 0, 0), UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                return "The URL template must start with http:// or https:// (or be a local folder / file:/// path).";
        }
        if (MinZoom < 0 || MaxZoom > TileMath.MaxSupportedZoom) return $"Zoom must be between 0 and {TileMath.MaxSupportedZoom}.";
        if (MinZoom > MaxZoom) return "Min zoom must not be above max zoom.";
        if (!string.IsNullOrWhiteSpace(Scheme) && !Scheme.Equals("xyz", StringComparison.OrdinalIgnoreCase) &&
            !Scheme.Equals("tms", StringComparison.OrdinalIgnoreCase))
            return "Scheme must be \"xyz\" or \"tms\".";
        return null;
    }

    /// <summary>Gives the definition a unique, filesystem-safe id if it has none.</summary>
    public void EnsureId(IEnumerable<string> existingIds)
    {
        var taken = new HashSet<string>(existingIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Id) && IsSafeId(Id) && !taken.Contains(Id)) return;
        string slug = new string((Name ?? "map").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "map";
        if (slug.Length > 40) slug = slug[..40];
        string id = "custom-" + slug;
        for (int i = 2; taken.Contains(id); i++) id = $"custom-{slug}-{i}";
        Id = id;
    }

    public static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    public CustomMapDefinition Clone() => (CustomMapDefinition)MemberwiseClone();

    // ------------------------------------------------------------------ JSON

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>Reads one definition or an array of definitions.</summary>
    public static List<CustomMapDefinition> ParseJson(string json)
    {
        json = (json ?? "").Trim();
        if (json.StartsWith("[")) return JsonSerializer.Deserialize<List<CustomMapDefinition>>(json, Json) ?? new();
        var one = JsonSerializer.Deserialize<CustomMapDefinition>(json, Json);
        return one == null ? new() : new List<CustomMapDefinition> { one };
    }

    public static string ToJson(IEnumerable<CustomMapDefinition> maps) => JsonSerializer.Serialize(maps.ToList(), Json);

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Loads / saves the custom map list (custom-maps.json in the user's configuration folder).</summary>
public static class CustomMapStore
{
    public static List<CustomMapDefinition> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var list = CustomMapDefinition.ParseJson(File.ReadAllText(path));
            var ids = new List<string>();
            var valid = new List<CustomMapDefinition>();
            foreach (var m in list)
            {
                if (m.Validate() != null) continue;
                m.EnsureId(ids);
                ids.Add(m.Id);
                valid.Add(m);
            }
            return valid;
        }
        catch { return new(); }   // unreadable file: start empty rather than failing the map
    }

    public static void Save(string path, IEnumerable<CustomMapDefinition> maps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, CustomMapDefinition.ToJson(maps));
        File.Move(tmp, path, overwrite: true);
    }
}
