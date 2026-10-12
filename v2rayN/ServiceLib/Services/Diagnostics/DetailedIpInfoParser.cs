using System.Globalization;

namespace ServiceLib.Services.Diagnostics;

public sealed record DetailedIpInfoResult(
    string Ip,
    string? CountryCode,
    string? CountryName,
    string? City,
    string? Region,
    long? Asn,
    string? Provider,
    string? Organization,
    string? Timezone,
    double? Latitude,
    double? Longitude);

/// <summary>Parses ipwho.is, ipinfo and the shipped IP card's provider schemas without making network requests.</summary>
public static class DetailedIpInfoParser
{
    public static DetailedIpInfoResult? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Get(root, "success")?.ValueKind == JsonValueKind.False ||
                string.Equals(Text(root, "status"), "fail", StringComparison.OrdinalIgnoreCase) ||
                Get(root, "error") is { ValueKind: not (JsonValueKind.False or JsonValueKind.Null) })
                return null;
            var ip = First(Text(root, "ip"), Text(root, "clientIp"), Text(root, "ip_addr"), Text(root, "query"));
            if (!IPAddress.TryParse(ip, out var address) ||
                (address.AddressFamily == AddressFamily.InterNetwork &&
                 (ip!.Split('.').Length != 4 || ip.Split('.').Any(part => part.Length == 0 ||
                     !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0'))))) return null;
            var connection = Get(root, "connection");
            var location = Get(root, "location") ?? Get(root, "geo");
            var asnObject = Get(root, "asn");
            var company = Get(root, "company");
            var countryValue = Text(root, "country");
            var countryCode = CountryCode(First(Text(root, "country_code"), Text(root, "countryCode"),
                Text(location, "country_code"), CountryCode(countryValue)));
            var countryName = First(Text(root, "country_name"), Text(location, "country"),
                CountryCode(countryValue) == null ? countryValue : null);
            if (countryName == null && countryCode != null)
            {
                try { countryName = new RegionInfo(countryCode).EnglishName; }
                catch (ArgumentException) { }
            }
            var rawOrg = First(Text(root, "org"), Text(root, "organization"), Text(root, "organization_name"), Text(root, "asOrg"), Text(root, "as_name"));
            var orgAsn = ParseOrganizationAsn(rawOrg, out var orgName);
            var asn = Number(Get(connection, "asn")) ?? Number(Get(asnObject, "asn")) ??
                Number(asnObject) ?? orgAsn;
            var provider = First(Text(connection, "isp"), Text(root, "isp"), Text(company, "name"),
                Text(asnObject, "name"), orgName);
            var organization = First(Text(connection, "org"), Text(asnObject, "org"),
                Text(root, "asn_organization"), orgName, Text(company, "name"), provider);
            var latitude = Coordinate(Get(root, "latitude") ?? Get(root, "lat") ?? Get(location, "latitude"), 90);
            var longitude = Coordinate(Get(root, "longitude") ?? Get(root, "lon") ?? Get(location, "longitude"), 180);
            var loc = Text(root, "loc")?.Split(',');
            if (loc is { Length: 2 })
            {
                latitude ??= Coordinate(loc[0], 90);
                longitude ??= Coordinate(loc[1], 180);
            }
            return new(address.ToString(), countryCode, countryName,
                First(Text(root, "city"), Text(location, "city")),
                First(Text(root, "region"), Text(root, "region_name"), Text(root, "regionName"), Text(location, "state"), Text(location, "region")),
                asn, provider, organization,
                First(Text(Get(root, "timezone"), "id"), Text(root, "timezone"), Text(location, "timezone")),
                latitude, longitude);
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Get(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var field) ? field : null;
    private static string? Text(JsonElement? root, string name) =>
        Get(root, name) is { ValueKind: JsonValueKind.String } field ? Clean(field.GetString()) : null;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    private static string? CountryCode(string? value)
    {
        return AvailabilityEvidence.Region(value);
    }
    private static long? Number(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var numeric))
            return numeric is >= 1 and <= uint.MaxValue ? numeric : null;
        if (value is not { ValueKind: JsonValueKind.String } text) return null;
        var raw = text.GetString()?.Trim();
        if (raw?.StartsWith("AS", StringComparison.OrdinalIgnoreCase) == true) raw = raw[2..];
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var result) &&
            result is >= 1 and <= uint.MaxValue ? result : null;
    }
    private static long? ParseOrganizationAsn(string? organization, out string? name)
    {
        name = organization;
        if (organization?.StartsWith("AS", StringComparison.OrdinalIgnoreCase) != true) return null;
        var parts = organization.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (!long.TryParse(parts[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
            number is < 1 or > uint.MaxValue) return null;
        name = parts.Length == 2 ? Clean(parts[1]) : null;
        return number;
    }
    private static double? Coordinate(JsonElement? value, double limit)
    {
        if (value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var numeric))
            return double.IsFinite(numeric) && Math.Abs(numeric) <= limit ? numeric : null;
        return value is { ValueKind: JsonValueKind.String } text ? Coordinate(text.GetString(), limit) : null;
    }
    private static double? Coordinate(string? value, double limit) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number) && Math.Abs(number) <= limit ? number : null;
}
