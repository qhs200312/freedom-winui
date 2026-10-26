using AwesomeAssertions;
using ServiceLib.Services.Privacy;
using Xunit;

namespace ServiceLib.Tests.Services;

public class UdpInterceptionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("C:\\browser.exe")]
    [InlineData("xray.exe")]
    [InlineData("sing-box.exe")]
    [InlineData("nowhere.exe")]
    public void InvalidOrRecursiveApplications_ShouldBeRejected(string names)
    {
        var action = () => UdpInterceptionConfig.ParseApplications(names);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Configuration_ShouldOnlyCaptureSelectedUdpWithBothFamilies()
    {
        var root = JsonNode.Parse(UdpInterceptionConfig.Build("chrome;CHROME.exe;Discord.exe", 10809))!;
        var proxy = root["proxies"]![0]!;
        proxy["appNames"]!.AsArray().Should().HaveCount(2);
        proxy["supportedProtocols"]!.ToJsonString().Should().Be("""["UDP"]""");
        proxy["supportedAddressFamilies"]!.AsArray().Should().HaveCount(2);
        proxy["socks5ProxyEndpoint"]!.GetValue<string>().Should().Be("127.0.0.1:10809");
        root["bypassLan"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Readiness_RequiresAllAssociationsAndDriverReadyMessage()
    {
        var ready = new UdpInterceptionReadiness(["chrome.exe", "Discord.exe"], 10808);
        ready.Observe("ProxiFyre Service is running...").Should().BeFalse();
        ready.Observe("Successfully associated chrome.exe to 127.0.0.1:10808 SOCKS5 proxy with protocols UDP and address families IPv4, IPv6!").Should().BeFalse();
        ready.Observe("Successfully associated Discord.exe to 127.0.0.1:10809 SOCKS5 proxy with protocols UDP and address families IPv4, IPv6!").Should().BeFalse();
        ready.Observe("Successfully associated Discord.exe to 127.0.0.1:10808 SOCKS5 proxy with protocols UDP and address families IPv4, IPv6!").Should().BeTrue();
    }

    [Fact]
    public async Task FileReadiness_ShouldIgnorePreviousRunAndWaitForCompleteNewLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "udp-readiness-" + Guid.NewGuid() + ".log");
        const string associated = "Successfully associated chrome.exe to 127.0.0.1:10808 SOCKS5 proxy with protocols UDP and address families IPv4, IPv6!";
        try
        {
            await File.WriteAllTextAsync(path, associated + "\nProxiFyre Service is running...\n",
                TestContext.Current.CancellationToken);
            var readiness = new UdpInterceptionReadiness(["chrome.exe"], 10808, path);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
            await File.AppendAllTextAsync(path, associated + "\nProxiFyre Service is run",
                TestContext.Current.CancellationToken);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
            readiness.PendingDescription.Should().Contain("未收到").And.Contain("已全部确认");
            await File.AppendAllTextAsync(path, "ning...\n", TestContext.Current.CancellationToken);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FileReadiness_MissingOrTruncatedLog_ShouldKeepRealDriverRequirement()
    {
        var path = Path.Combine(Path.GetTempPath(), "udp-readiness-" + Guid.NewGuid() + ".log");
        try
        {
            var readiness = new UdpInterceptionReadiness(["chrome.exe"], 10808, path);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
            await File.WriteAllTextAsync(path, new string(' ', 1024) + "\n", TestContext.Current.CancellationToken);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
            await File.WriteAllTextAsync(path, "ProxiFyre Service is running...\n", TestContext.Current.CancellationToken);
            (await readiness.ObserveFileAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
            readiness.PendingDescription.Should().Contain("已收到").And.Contain("chrome.exe");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingDriverOrPrivileges_ShouldNotReportReady()
    {
        UdpInterceptionConfig.GetPrerequisiteError(true, true, true, false).Should().NotBeNull();
        UdpInterceptionConfig.GetPrerequisiteError(true, false, true, true).Should().NotBeNull();
        UdpInterceptionConfig.GetPrerequisiteError(true, true, true, true).Should().BeNull();
    }

    [Fact]
    public async Task ExternalProbe_ShouldAcceptFirstWorkingStunServer()
    {
        var targets = new List<string>();
        var result = await UdpInterceptionService.ProbeExternalStunAsync(10808, (target, port, _) =>
        {
            targets.Add(target);
            port.Should().Be(10808);
            return target.StartsWith("stun.cloudflare.com", StringComparison.Ordinal)
                ? Task.FromResult(TimeSpan.FromMilliseconds(80))
                : Task.FromException<TimeSpan>(new TimeoutException());
        });
        result.Should().BeTrue();
        targets.Should().Equal("stun.l.google.com:19302", "stun.cloudflare.com:3478");
    }

    [Fact]
    public async Task ExternalProbe_ShouldFailClosedWhenNoStunServerResponds()
    {
        var result = await UdpInterceptionService.ProbeExternalStunAsync(10808,
            (_, _, _) => Task.FromException<TimeSpan>(new SocketException((int)SocketError.HostUnreachable)));
        result.Should().BeFalse();
    }
}
