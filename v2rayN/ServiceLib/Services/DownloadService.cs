using System.Net.Http.Headers;

namespace ServiceLib.Services;

/// <summary>
/// Download
/// </summary>
public class DownloadService
{
    public event EventHandler<UpdateResult>? UpdateCompleted;

    public event ErrorEventHandler? Error;
    public event EventHandler<DownloadProgressSnapshot>? ProgressChanged;

    private static readonly string _tag = "DownloadService";

    /// <summary>
    /// Downloads data with the specified proxy and reports progress messages.
    /// </summary>
    public async Task<int> DownloadDataAsync(string url, IWebProxy webProxy, int downloadTimeout, Func<bool, string, Task> updateFunc)
    {
        try
        {
            var progress = new Progress<string>();
            progress.ProgressChanged += (sender, value) => updateFunc?.Invoke(false, $"{value}");

            await DownloaderHelper.Instance.DownloadDataAsync4Speed(webProxy,
                  url,
                  progress,
                  downloadTimeout);
        }
        catch (Exception ex)
        {
            await updateFunc?.Invoke(false, ex.Message);
            if (ex.InnerException != null)
            {
                await updateFunc?.Invoke(false, ex.InnerException.Message);
            }
        }
        return 0;
    }

    /// <summary>
    /// Downloads a file and reports progress through events.
    /// </summary>
    public async Task DownloadFileAsync(string url, string fileName, bool blProxy, int downloadTimeout)
    {
        await DownloadFileWithResultAsync(url, fileName, blProxy, downloadTimeout);
    }

    public async Task<bool> DownloadFileWithResultAsync(string url, string fileName, bool blProxy, int downloadTimeout)
    {
        try
        {
            UpdateCompleted?.Invoke(this, new UpdateResult(false, $"{ResUI.Downloading}   {url}"));

            var progress = new InlineProgress<double>(value =>
            {
                if (value <= 100)
                {
                    ProgressChanged?.Invoke(this, new(0, null, value));
                    UpdateCompleted?.Invoke(this, new UpdateResult(false, $"...{value}%"));
                }
            });

            var webProxy = await GetWebProxy(blProxy, url);
            await DownloaderHelper.Instance.DownloadFileAsync(webProxy,
                url,
                fileName,
                progress,
                downloadTimeout);
            if (!File.Exists(fileName) || new FileInfo(fileName).Length == 0)
                throw new IOException("Downloaded file is missing or empty.");
            UpdateCompleted?.Invoke(this, new UpdateResult(true, fileName));
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);

            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
        return false;
    }

    /// <summary>Cancelable streamed update download with numeric progress and an idle timeout.</summary>
    public async Task DownloadFileWithProgressAsync(string url, string fileName, bool useProxy,
        IProgress<DownloadProgressSnapshot> progress, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = await CreateUpdateClientAsync(url, useProxy);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        long received = 0;
        Report();
        var buffer = new byte[81920];
        while (true)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            received += count;
            Report();
        }
        if (received == 0 || (total.HasValue && received != total))
            throw new IOException("The update download is empty or incomplete.");
        await output.FlushAsync(timeout.Token);

        void Report()
        {
            var snapshot = new DownloadProgressSnapshot(received, total,
                total > 0 ? Math.Clamp(received * 100d / total.Value, 0, 100) : null);
            progress.Report(snapshot);
            ProgressChanged?.Invoke(this, snapshot);
        }
    }

    public async Task<string?> DownloadUpdateChecksumAsync(string url, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = await CreateUpdateClientAsync(url, true);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[16 * 1024 + 1];
        var received = 0;
        while (received < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(received), timeout.Token);
            if (count == 0) return Encoding.UTF8.GetString(buffer, 0, received);
            received += count;
        }
        throw new InvalidDataException("The update checksum response is too large.");
    }

    private async Task<HttpClient> CreateUpdateClientAsync(string url, bool useProxy)
    {
        var proxy = await GetWebProxy(useProxy, url);
        var handler = new SocketsHttpHandler
        {
            Proxy = proxy, UseProxy = proxy != null, ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var policy = CertPemManager.Instance.BuildCertificateChainPolicy();
        if (policy != null) handler.SslOptions.CertificateChainPolicy = policy;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.TryParseAdd(Global.AppName);
        return client;
    }

    /// <summary>
    /// Gets redirect target URL without following redirects automatically.
    /// </summary>
    public async Task<string?> UrlRedirectAsync(string url, bool blProxy)
    {
        var webRequestHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            Proxy = await GetWebProxy(blProxy, url)
        };
        var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
        if (certificateChainPolicy != null)
        {
            webRequestHandler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
            webRequestHandler.SslOptions.RemoteCertificateValidationCallback = null;
        }
        using var client = new HttpClient(webRequestHandler);

        var response = await client.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location is not null)
        {
            return response.Headers.Location.ToString();
        }
        else
        {
            Error?.Invoke(this, new ErrorEventArgs(new Exception("StatusCode error: " + response.StatusCode)));
            Logging.SaveLog("StatusCode error: " + url);
            return null;
        }
    }

    /// <summary>
    /// Tries to download string content using proxy switch setting.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, bool blProxy, string userAgent)
    {
        var webProxy = await GetWebProxy(blProxy, url);
        return await TryDownloadString(url, webProxy, userAgent);
    }

    /// <summary>
    /// Tries to download string content with a specified proxy.
    /// </summary>
    public async Task<string?> TryDownloadString(string url, IWebProxy? webProxy, string userAgent)
    {
        var timeout = 15;
        try
        {
            var result1 = await DownloadStringAsync(url, webProxy, userAgent, timeout);
            if (result1.IsNotEmpty())
            {
                return result1;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }

        try
        {
            var result2 = await DownloadStringViaDownloader(url, webProxy, userAgent, timeout);
            if (result2.IsNotEmpty())
            {
                return result2;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads string content via HttpClient.
    /// </summary>
    private async Task<string?> DownloadStringAsync(string url, IWebProxy? webProxy, string userAgent, int timeout)
    {
        try
        {
            var connectTimeout = Math.Clamp(timeout / 5, 2, 5);
            var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                ConnectTimeout = TimeSpan.FromSeconds(connectTimeout)
            };
            var certificateChainPolicy = CertPemManager.Instance.BuildCertificateChainPolicy();
            if (certificateChainPolicy != null)
            {
                handler.SslOptions.CertificateChainPolicy = certificateChainPolicy;
                handler.SslOptions.RemoteCertificateValidationCallback = null;
            }

            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent);

            Uri uri = new(url);
            //Authorization Header
            if (uri.UserInfo.IsNotEmpty())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Utils.Base64Encode(uri.UserInfo));
            }

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            return await client.GetStringAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }

        return null;
    }

    /// <summary>
    /// Downloads string content via DownloaderHelper.
    /// </summary>
    private async Task<string?> DownloadStringViaDownloader(string url, IWebProxy? webProxy, string userAgent, int timeout)
    {
        try
        {
            if (userAgent.IsNullOrEmpty())
            {
                userAgent = Utils.GetVersion(false);
            }
            var result = await DownloaderHelper.Instance.DownloadStringAsync(webProxy, url, userAgent, timeout);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            Error?.Invoke(this, new ErrorEventArgs(ex));
            if (ex.InnerException != null)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex.InnerException));
            }
        }
        return null;
    }

    /// <summary>
    /// Uses the configured system proxy first, then falls back to the local SOCKS proxy.
    /// </summary>
    private async Task<IWebProxy?> GetWebProxy(bool blProxy, string destinationUrl)
    {
        if (!blProxy)
        {
            return null;
        }

        var systemProxy = GetSystemProxy(destinationUrl, HttpClient.DefaultProxy);
        if (systemProxy != null)
        {
            return systemProxy;
        }

        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (await SocketCheck(Global.Loopback, port) == false)
        {
            return null;
        }

        return new WebProxy($"socks5://{Global.Loopback}:{port}");
    }

    internal static IWebProxy? GetSystemProxy(string destinationUrl, IWebProxy? systemProxy)
    {
        if (systemProxy == null || !Uri.TryCreate(destinationUrl, UriKind.Absolute, out var destination))
        {
            return null;
        }

        try
        {
            var proxyUri = systemProxy.GetProxy(destination);
            if (systemProxy.IsBypassed(destination) || proxyUri == null || proxyUri == destination)
            {
                return null;
            }
            return systemProxy;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    /// <summary>
    /// Checks whether the specified TCP endpoint is reachable.
    /// </summary>
    private async Task<bool> SocketCheck(string ip, int port)
    {
        try
        {
            IPEndPoint point = new(IPAddress.Parse(ip), port);
            using Socket? sock = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(point);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
