using System.Net;
using System.Reflection;
using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Services;
using Xunit;

namespace ServiceLib.Tests.Services;

public class DownloadServiceTests
{
    [Fact]
    public void GetSystemProxy_ConfiguredProxy_ShouldReturnSystemProxy()
    {
        var method = typeof(DownloadService).GetMethod("GetSystemProxy", BindingFlags.Static | BindingFlags.NonPublic);
        var proxy = new TestProxy(new Uri("http://127.0.0.1:10809"), isBypassed: false);

        var result = method?.Invoke(null, ["https://github.com/qhs200312/v2rayN", proxy]);

        result.Should().BeSameAs(proxy);
    }

    [Fact]
    public void GetSystemProxy_BypassedDestination_ShouldReturnNull()
    {
        var method = typeof(DownloadService).GetMethod("GetSystemProxy", BindingFlags.Static | BindingFlags.NonPublic);
        var proxy = new TestProxy(new Uri("http://127.0.0.1:10809"), isBypassed: true);

        var result = method?.Invoke(null, ["https://github.com/qhs200312/v2rayN", proxy]);

        result.Should().BeNull();
    }

    [Fact]
    public void CoreInfo_AppUpdate_ShouldUseUserRepository()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(ECoreType.v2rayN);

        coreInfo.Should().NotBeNull();
        coreInfo!.Url.Should().Be("https://github.com/qhs200312/freedom-winui/releases");
        coreInfo.ReleaseApiUrl.Should().Be("https://api.github.com/repos/qhs200312/freedom-winui/releases");
        coreInfo.DownloadUrlWin64.Should().EndWith("/freedom-windows-64.zip");
        coreInfo.DownloadUrlWinArm64.Should().EndWith("/freedom-windows-arm64.zip");
        string.Format(coreInfo.DownloadUrlWin64!, new SemanticVersion("v7.23.9"))
            .Should().Be("https://github.com/qhs200312/freedom-winui/releases/download/v7.23.9/freedom-windows-64.zip");
    }

    [Fact]
    public async Task UpdateDownload_ShouldReportNumericProgressAndSaveExactHttpPayload()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var payload = Encoding.UTF8.GetBytes(new string('a', 16 * 1024));
        var file = Path.Combine(Path.GetTempPath(), "freedom-http-test-" + Guid.NewGuid().ToString("N") + ".zip");
        var server = ServeAsync();
        try
        {
            var progress = new List<DownloadProgressSnapshot>();
            var downloader = new DownloadService();
            downloader.ProgressChanged += (_, value) => progress.Add(value);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await downloader.DownloadFileWithProgressAsync($"http://127.0.0.1:{port}/package.zip",
                file, false, new Progress<DownloadProgressSnapshot>(), timeout.Token);
            await server;
            File.ReadAllBytes(file).Should().Equal(payload);
            progress.First().BytesReceived.Should().Be(0);
            progress.Last().BytesReceived.Should().Be(payload.Length);
            progress.Last().TotalBytes.Should().Be(payload.Length);
            progress.Last().Percentage.Should().Be(100);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
            if (File.Exists(file)) File.Delete(file);
        }

        async Task ServeAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1024];
            while (!header.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) throw new IOException("Incomplete fixture request.");
                header.Append(Encoding.ASCII.GetString(buffer, 0, count));
            }
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.WriteAsync(payload, timeout.Token);
        }
    }

    private sealed class TestProxy(Uri proxyUri, bool isBypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxyUri;

        public bool IsBypassed(Uri host) => isBypassed;
    }
}
