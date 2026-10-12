using System.Globalization;

namespace ServiceLib.Services.Diagnostics;

internal static class AvailabilityEvidence
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CountryCodes = new(() =>
    {
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.TwoLetterISORegionName.Length != 2 || !region.TwoLetterISORegionName.All(char.IsAsciiLetter))
                    continue;
                codes.TryAdd(region.TwoLetterISORegionName, region.TwoLetterISORegionName);
                codes.TryAdd(region.ThreeLetterISORegionName, region.TwoLetterISORegionName);
            }
            catch (ArgumentException) { }
        }
        codes["UK"] = "GB";
        codes["EL"] = "GR";
        codes["USA"] = "US";
        codes["CHN"] = "CN";
        codes["HKG"] = "HK";
        codes["MAC"] = "MO";
        codes["TWN"] = "TW";
        codes["KOR"] = "KR";
        codes["PRK"] = "KP";
        codes["XK"] = "XK";
        codes["XKX"] = "XK";
        return codes;
    });

    public static string? Region(string? value)
    {
        value = value?.Trim().ToUpperInvariant();
        return value != null && CountryCodes.Value.TryGetValue(value, out var alpha2) ? alpha2 : null;
    }

    public static string? TraceRegion(string body) => Region(body.Split('\n')
        .FirstOrDefault(line => line.StartsWith("loc=", StringComparison.Ordinal))?[4..]);

    public static string? QuotedField(string body, string key)
    {
        var match = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?:\\\\.|[^\"\\\\])*\")",
            RegexOptions.CultureInvariant, RegexTimeout);
        if (!match.Success) return null;
        try { return JsonSerializer.Deserialize<string>(match.Groups[1].Value); }
        catch (JsonException) { return null; }
    }

    public static string? Attribute(string body, string key)
    {
        var match = Regex.Match(body, Regex.Escape(key) + "\\s*=\\s*[\"']([^\"']*)[\"']",
            RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool? BooleanField(string body, string key)
    {
        var match = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)(?=\\s*[,}\\]])",
            RegexOptions.CultureInvariant, RegexTimeout);
        return match.Success ? match.Groups[1].Value == "true" : null;
    }

    public static bool Contains(string body, params string[] markers) =>
        markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static JsonElement? Find(JsonElement root, string key)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(key, out var value)) return value;
            foreach (var child in root.EnumerateObject())
                if (Find(child.Value, key) is { } found) return found;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in root.EnumerateArray())
                if (Find(child, key) is { } found) return found;
        }
        return null;
    }

    public static bool IsChallenge(string body) => Contains(body,
        "<title>Just a moment", "cf-chl-", "g-recaptcha", "hcaptcha", "captcha challenge",
        "verify you are human", "checking your browser", "challenge-platform",
        "<title>Attention Required", "<title>Access Denied");
}
