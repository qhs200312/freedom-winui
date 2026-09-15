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
    public void MissingDriverOrPrivileges_ShouldNotReportReady()
    {
        UdpInterceptionConfig.GetPrerequisiteError(true, true, true, false, false, true).Should().NotBeNull();
        UdpInterceptionConfig.GetPrerequisiteError(true, false, true, true, false, true).Should().NotBeNull();
        UdpInterceptionConfig.GetPrerequisiteError(true, true, true, true, false, true).Should().BeNull();
    }
}
