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

    private sealed class TestProxy(Uri proxyUri, bool isBypassed) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxyUri;

        public bool IsBypassed(Uri host) => isBypassed;
    }
}
