using ServiceLib.Services.Diagnostics;

namespace ServiceLib.Services;

/// <summary>All providers use the supplied route. A failed proxy request never creates a direct client.</summary>
public sealed class DetailedIpLookupService(HttpClient client)
{
    public async Task<DetailedIpInfoResult> DetectAsync(string? configuredUrl = null, CancellationToken cancellationToken = default)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(24));
        var urls = new[] { configuredUrl, "https://ipwho.is/?lang=zh-CN", "https://ipinfo.io/json", "https://api.ipify.org?format=json" }
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        DetailedIpInfoResult? ipOnly = null;
        foreach (var url in urls)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 128 * 1024) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, attempt.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (buffer.Length + count > 128 * 1024) throw new InvalidDataException("IP response exceeds its size limit.");
                    buffer.Write(chunk, 0, count);
                }
                var info = DetailedIpInfoParser.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
                if (info == null) continue;
                ipOnly ??= info;
                if (info.CountryCode != null && (info.Asn != null || info.Organization != null || info.City != null))
                    return info;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (total.IsCancellationRequested) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ipOnly ?? throw new HttpRequestException("无法通过当前网络路径获取出口 IP，请检查内核或 IP API。");
    }
}
