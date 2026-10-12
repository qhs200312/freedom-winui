using System.Net.Http.Headers;

namespace ServiceLib.Services.Diagnostics;

internal sealed record DiagnosticResponse(HttpStatusCode Status, string Body, Uri Url, Uri? Location)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

internal sealed class ResponseLimitException : IOException
{
    public ResponseLimitException() : base("Diagnostic response exceeds the configured size limit.") { }
}

internal sealed class DiagnosticsHttpClient : IDisposable
{
    internal const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ServiceAvailabilityOptions _options;

    public DiagnosticsHttpClient(IWebProxy proxy, ServiceAvailabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _options = options;
        _client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new RequiredProxy(proxy),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = options.CheckTimeout
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }

    public DiagnosticsHttpClient(HttpClient client, ServiceAvailabilityOptions options, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = options;
        _ownsClient = ownsClient;
    }

    public async Task<DiagnosticResponse> SendAsync(string url, CancellationToken cancellationToken,
        HttpMethod? method = null, string? content = null, string? contentType = null,
        string? authorization = null, bool followRedirects = true, bool readBody = true)
    {
        var uri = new Uri(url, UriKind.Absolute);
        method ??= HttpMethod.Get;
        var transientRetries = 0;
        for (var redirects = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("Diagnostic endpoints and redirects must use HTTPS.");
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (authorization != null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (content != null)
            {
                request.Content = new StringContent(content, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
            }
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            if (method == HttpMethod.Get && transientRetries == 0 &&
                (int)response.StatusCode is 502 or 503 or 504)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter == null && response.Headers.RetryAfter?.Date is { } date)
                    retryAfter = date - DateTimeOffset.UtcNow;
                var delay = retryAfter ?? TimeSpan.FromMilliseconds(200);
                if (delay <= TimeSpan.FromSeconds(1))
                {
                    transientRetries++;
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            var location = response.Headers.Location;
            if (location != null && !location.IsAbsoluteUri)
                location = new Uri(finalUri, location);
            if (followRedirects && location != null && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= _options.MaxRedirects)
                    throw new HttpRequestException("Diagnostic redirect limit exceeded.");
                if (response.StatusCode == HttpStatusCode.SeeOther ||
                    (method == HttpMethod.Post && (int)response.StatusCode is 301 or 302))
                {
                    method = HttpMethod.Get;
                    content = null;
                }
                if (!string.Equals(finalUri.Scheme, location.Scheme, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(finalUri.Authority, location.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    // Redirects must not forward device assertions or refresh tokens to another origin.
                    if (authorization != null && content != null)
                        throw new HttpRequestException("Authenticated diagnostic bodies cannot redirect to another origin.");
                    authorization = null;
                }
                uri = location;
                redirects++;
                continue;
            }
            var body = readBody ? await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false) : "";
            return new(response.StatusCode, body, finalUri, location);
        }
    }

    private async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaxResponseBytes)
            throw new ResponseLimitException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, _options.MaxResponseBytes + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(0,
                Math.Min(chunk.Length, _options.MaxResponseBytes - (int)buffer.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > _options.MaxResponseBytes)
                throw new ResponseLimitException();
            buffer.Write(chunk, 0, count);
        }
        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private sealed class RequiredProxy(IWebProxy supplied) : IWebProxy
    {
        public ICredentials? Credentials { get => supplied.Credentials; set => supplied.Credentials = value; }
        public bool IsBypassed(Uri host) => false;
        public Uri GetProxy(Uri destination)
        {
            var proxy = supplied.GetProxy(destination);
            if (proxy == null || proxy == destination ||
                proxy.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
                throw new HttpRequestException("A concrete proxy endpoint is required for diagnostics.");
            return proxy;
        }
    }
}
