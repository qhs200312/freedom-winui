namespace ServiceLib.Services.Diagnostics;

internal sealed record AvailabilityPageSignal(bool Identified, string? Region = null, bool? CatalogAvailable = null);

internal static class AvailabilityPageEvidence
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(150);

    public static AvailabilityPageSignal Read(AvailabilityService service, string body, Uri url)
    {
        if (service == AvailabilityService.Netflix && IsHost(url, "netflix.com"))
        {
            using var context = AssignedObject(body, "netflix.reactContext");
            if (context == null) return new(false);
            var root = context.RootElement;
            var meta = At(root, "models", "nmTitleGQL", "data", "metaData");
            var videoId = meta is { } data ? At(data, "videoId") : null;
            var expectedId = url.AbsolutePath.Split('/').LastOrDefault();
            if (videoId?.ToString() != expectedId) return new(false);
            var country = At(root, "models", "geo", "data", "requestCountry", "id");
            var region = country is { ValueKind: JsonValueKind.String } countryValue
                ? AvailabilityEvidence.Region(countryValue.GetString()) : null;
            var available = meta is { } metadata ? At(metadata, "isAvailable") : null;
            var supported = At(root, "models", "geo", "data", "supportedCountry");
            if (supported is { ValueKind: JsonValueKind.String } policy && policy.GetString() == "DENY")
                return new(true, region, false);
            return new(true, region, available?.ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false, _ => null
            });
        }
        if (service == AvailabilityService.Spotify && IsHost(url, "spotify.com"))
        {
            using var config = ScriptObject(body, "appServerConfig", base64: true);
            if (config == null) return new(false);
            var root = config.RootElement;
            var app = At(root, "appName");
            var anonymous = At(root, "isAnonymous");
            if (app is not { ValueKind: JsonValueKind.String } ||
                app.Value.GetString()?.StartsWith("web_player", StringComparison.Ordinal) != true ||
                anonymous?.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(false);
            var market = At(root, "market");
            return new(true, market is { ValueKind: JsonValueKind.String } value ? AvailabilityEvidence.Region(value.GetString()) : null);
        }
        if (service == AvailabilityService.TikTok && IsHost(url, "tiktok.com"))
        {
            using var environment = ScriptObject(body, "tiktok-environment-webapp");
            using var serviceRegion = ScriptObject(body, "service-region");
            using var hydration = ScriptObject(body, "__UNIVERSAL_DATA_FOR_REHYDRATION__");
            var identified = environment?.RootElement.ValueKind == JsonValueKind.Object &&
                At(environment.RootElement, "regionInfo") is { ValueKind: JsonValueKind.Object };
            identified |= hydration != null && At(hydration.RootElement, "__DEFAULT_SCOPE__", "webapp.app-context") is { ValueKind: JsonValueKind.Object };
            var country = serviceRegion == null ? null : At(serviceRegion.RootElement, "vgeo");
            var raw = country is { ValueKind: JsonValueKind.String } value ? value.GetString()?.Replace("VGeo-", "", StringComparison.OrdinalIgnoreCase) : null;
            if (raw == null && hydration != null &&
                At(hydration.RootElement, "__DEFAULT_SCOPE__", "webapp.app-context", "region") is { ValueKind: JsonValueKind.String } hydrationRegion)
                raw = hydrationRegion.GetString();
            return new(identified, AvailabilityEvidence.Region(raw));
        }
        if (service == AvailabilityService.Gemini && IsHost(url, "gemini.google.com"))
        {
            using var globals = AssignedObject(body, "WIZ_global_data");
            return new(globals is { RootElement.ValueKind: JsonValueKind.Object } &&
                AvailabilityEvidence.Contains(body, "https://gemini.google.com", "<title>Gemini"));
        }
        if (service == AvailabilityService.PrimeVideo && IsHost(url, "primevideo.com"))
        {
            var identified = url.AbsolutePath.StartsWith("/offers/nonprimehomepage", StringComparison.Ordinal) &&
                AvailabilityEvidence.Contains(body, "data-a-state");
            return new(identified, AvailabilityEvidence.Region(
                AvailabilityEvidence.QuotedField(WebUtility.HtmlDecode(body), "currentTerritory")));
        }
        return new(false);
    }

    public static bool IsHost(Uri uri, string host) =>
        uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    public static string VisibleText(string body)
    {
        if (body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')) return "";
        var text = Regex.Replace(body, @"<(script|style)\b[^>]*>[\s\S]*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        text = Regex.Replace(text, @"<[^>]+>", " ", RegexOptions.CultureInvariant, Timeout);
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ", RegexOptions.CultureInvariant, Timeout);
    }

    private static JsonElement? At(JsonElement root, params string[] path)
    {
        foreach (var part in path)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(part, out root)) return null;
        }
        return root;
    }

    private static JsonDocument? ScriptObject(string body, string id, bool base64 = false)
    {
        foreach (Match match in Regex.Matches(body, @"<script\b(?<attrs>[^>]*)>(?<body>[\s\S]*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout))
        {
            var attribute = Regex.Match(match.Groups["attrs"].Value, @"(?:^|\s)id\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
            if (!attribute.Success || attribute.Groups[1].Value != id) continue;
            var json = match.Groups["body"].Value.Trim();
            try
            {
                if (base64 && !json.StartsWith('{')) json = Encoding.UTF8.GetString(Convert.FromBase64String(json));
                return JsonDocument.Parse(json);
            }
            catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
        }
        return null;
    }

    private static JsonDocument? AssignedObject(string body, string name)
    {
        var match = Regex.Match(body, Regex.Escape(name) + @"\s*=\s*(?=\{)",
            RegexOptions.CultureInvariant, Timeout);
        if (!match.Success) return null;
        var json = body[(match.Index + match.Length)..];
        // Netflix emits JSON data with JavaScript hex escapes; parse inert data only.
        json = Regex.Replace(json, @"\\x([0-9a-fA-F]{2})", @"\u00$1", RegexOptions.CultureInvariant, Timeout);
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 128 });
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException) { return null; }
    }
}
