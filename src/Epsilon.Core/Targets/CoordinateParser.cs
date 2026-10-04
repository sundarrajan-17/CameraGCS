using System.Globalization;
using System.Text.RegularExpressions;

namespace Epsilon.Core.Targets;

/// <summary>
/// Parses operator-typed coordinates for manual targets / units. Accepted for each of latitude and longitude:
///   decimal degrees        13.454576      -80.226849      13.454576N     80.226849 E
///   degrees decimal-min    13 27.2746 N   13°27.2746'N
///   degrees min sec        13 27 16.47 N  13°27'16.47"N   N 13 27 16.47
/// A hemisphere letter (N/S/E/W, before or after) overrides the sign. Both a dot and a comma are accepted as
/// the decimal separator when the value has no other comma ("13,4545").
/// A latitude box may also contain the whole pair: "13.454576, 80.226849" or "13.454576 80.226849".
/// </summary>
public static class CoordinateParser
{
    private static readonly Regex Hemisphere = new(@"[NSEWnsew]", RegexOptions.Compiled);

    /// <summary>Parses latitude and longitude text. Returns false with a human-readable error.</summary>
    public static bool TryParse(string latText, string lonText, out double lat, out double lon, out string error)
    {
        lat = lon = 0;
        latText = (latText ?? "").Trim();
        lonText = (lonText ?? "").Trim();

        // Whole pair pasted into the latitude box (longitude box empty).
        if (lonText.Length == 0 && TrySplitPair(latText, out var a, out var b))
        {
            latText = a;
            lonText = b;
        }

        if (latText.Length == 0) { error = "Enter a latitude."; return false; }
        if (lonText.Length == 0) { error = "Enter a longitude."; return false; }
        if (!TryParseAngle(latText, isLatitude: true, out lat, out error)) { error = "Latitude: " + error; return false; }
        if (!TryParseAngle(lonText, isLatitude: false, out lon, out error)) { error = "Longitude: " + error; return false; }
        if (lat is < -90 or > 90) { error = $"Latitude {lat:0.######} is outside -90 .. 90."; return false; }
        if (lon is < -180 or > 180) { error = $"Longitude {lon:0.######} is outside -180 .. 180."; return false; }
        if (!TargetStore.IsValidPosition(lat, lon)) { error = "0, 0 is not accepted as a position."; return false; }
        error = null;
        return true;
    }

    /// <summary>"lat, lon" / "lat; lon" / "lat lon" (two decimal numbers, optional hemisphere letters).</summary>
    private static bool TrySplitPair(string text, out string first, out string second)
    {
        first = second = null;
        var parts = text.Split(new[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2) { first = parts[0]; second = parts[1]; return true; }
        var ws = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (ws.Length == 2 && ws.All(p => double.TryParse(p.TrimEnd('N', 'S', 'E', 'W', 'n', 's', 'e', 'w'),
                                                           NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            first = ws[0];
            second = ws[1];
            return true;
        }
        return false;
    }

    /// <summary>Parses one angle in decimal degrees, DDM or DMS, with optional hemisphere letter.</summary>
    public static bool TryParseAngle(string text, bool isLatitude, out double value, out string error)
    {
        value = 0;
        error = null;
        string s = (text ?? "").Trim();
        if (s.Length == 0) { error = "empty."; return false; }

        int sign = 1;
        var hemi = Hemisphere.Matches(s);
        if (hemi.Count > 1) { error = $"\"{text}\" has more than one hemisphere letter."; return false; }
        if (hemi.Count == 1)
        {
            char h = char.ToUpperInvariant(hemi[0].Value[0]);
            if (isLatitude && h is 'E' or 'W') { error = $"\"{h}\" is not a latitude hemisphere (use N or S)."; return false; }
            if (!isLatitude && h is 'N' or 'S') { error = $"\"{h}\" is not a longitude hemisphere (use E or W)."; return false; }
            if (h is 'S' or 'W') sign = -1;
            s = s.Remove(hemi[0].Index, 1).Trim();
        }

        // Single comma used as a decimal separator ("13,4545").
        if (s.Count(c => c == ',') == 1 && !s.Contains('.')) s = s.Replace(',', '.');

        // Separators for D M S: degree, minute, second marks and whitespace.
        var parts = s.Split(new[] { '°', 'º', '\'', '′', '"', '″', ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3) { error = $"\"{text}\" is not a coordinate."; return false; }

        var nums = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i]) || double.IsNaN(nums[i]))
            {
                error = $"\"{text}\" is not a coordinate.";
                return false;
            }
        }

        bool negative = nums[0] < 0 || parts[0].StartsWith("-");
        double deg = Math.Abs(nums[0]);
        double min = parts.Length > 1 ? nums[1] : 0;
        double sec = parts.Length > 2 ? nums[2] : 0;
        if (parts.Length > 1 && deg != Math.Floor(deg)) { error = "degrees must be whole when minutes are given."; return false; }
        if (min is < 0 or >= 60) { error = "minutes must be 0 .. <60."; return false; }
        if (sec is < 0 or >= 60) { error = "seconds must be 0 .. <60."; return false; }
        if (parts.Length > 2 && min != Math.Floor(min)) { error = "minutes must be whole when seconds are given."; return false; }
        if (negative && hemi.Count == 1) { error = "use either a minus sign or a hemisphere letter, not both."; return false; }

        value = (deg + min / 60.0 + sec / 3600.0) * (negative ? -1 : sign);
        return true;
    }
}
