namespace ServiceLib.Handler;

public static class ClipboardSubscriptionImport
{
    public static bool IsSubscriptionUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrEmpty(uri.Host);

    public static HashSet<string> GetUrls(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(IsSubscriptionUrl).ToHashSet(StringComparer.Ordinal);

    public static List<string> SelectIds(HashSet<string> urls, IEnumerable<SubItem> subscriptions) => subscriptions
        .Where(item => item.Enabled && !string.IsNullOrEmpty(item.Id)
            && !string.IsNullOrWhiteSpace(item.Url) && urls.Contains(item.Url.Trim()))
        .Select(item => item.Id).Distinct(StringComparer.Ordinal).ToList();
}
