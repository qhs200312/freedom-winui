using AwesomeAssertions;
using ServiceLib.Services.Privacy;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class UdpInterceptionRoutingTests
{
    [Theory]
    [InlineData(ECoreType.Xray, false, false)]
    [InlineData(ECoreType.Xray, false, true)]
    [InlineData(ECoreType.Xray, true, false)]
    [InlineData(ECoreType.Xray, true, true)]
    [InlineData(ECoreType.sing_box, false, false)]
    [InlineData(ECoreType.sing_box, false, true)]
    [InlineData(ECoreType.sing_box, true, false)]
    [InlineData(ECoreType.sing_box, true, true)]
    public async Task DedicatedInbound_ShouldBypassDirectRulesWithoutDependingOnStunOrSniffing(
        ECoreType coreType, bool stun, bool sniffing)
    {
        var context = CreateContext(coreType);
        var config = context.AppConfig;
        config.GuiItem.ProxyStunTraffic = stun;
        config.Inbound[0].SniffingEnabled = sniffing;
        config.Inbound[0].UdpEnabled = false;
        config.Inbound[0].AllowLANConn = true;
        config.Inbound[0].NewPort4LAN = true;
        config.Inbound[0].User = "local-user";
        config.Inbound[0].Pass = "local-password";

        var json = Generate(context);
        var expectedPort = config.Inbound[0].LocalPort + (int)EInboundProtocol.udpInterception;
        var interception = JsonNode.Parse(UdpInterceptionConfig.Build("chrome.exe", expectedPort))!;
        interception["proxies"]![0]!["socks5ProxyEndpoint"]!.GetValue<string>()
            .Should().Be($"127.0.0.1:{expectedPort}");
        if (coreType == ECoreType.Xray)
        {
            var core = JsonUtils.Deserialize<V2rayConfig>(json)!;
            var inbound = core.inbounds.Single(i => i.tag == UdpInterceptionConfig.InboundTag);
            inbound.listen.Should().Be(Global.Loopback);
            inbound.port.Should().Be(expectedPort);
            inbound.protocol.Should().Be("socks");
            inbound.settings.udp.Should().BeTrue();
            inbound.settings.auth.Should().Be("noauth");
            inbound.settings.accounts.Should().BeNull();
            core.inbounds.Single(i => i.tag == "socks").settings.udp.Should().BeFalse();
            core.inbounds.Single(i => i.tag == "socks3").settings.auth.Should().Be("password");
            var rule = core.routing.rules[0];
            rule.inboundTag.Should().Equal(UdpInterceptionConfig.InboundTag);
            rule.outboundTag.Should().Be(Global.ProxyTag);
            rule.network.Should().BeNull();
            rule.port.Should().BeNull();
            rule.ip.Should().BeNull();
            rule.domain.Should().BeNull();
            core.routing.rules.Should().Contain(r => r.outboundTag == Global.DirectTag && r.port == "0-65535");
        }
        else
        {
            var core = JsonUtils.Deserialize<SingboxConfig>(json)!;
            var inbound = core.inbounds.Single(i => i.tag == UdpInterceptionConfig.InboundTag);
            inbound.listen.Should().Be(Global.Loopback);
            inbound.listen_port.Should().Be(expectedPort);
            inbound.type.Should().Be("socks");
            inbound.users.Should().BeNull();
            core.inbounds.Single(i => i.tag == "socks3").users.Should().ContainSingle();
            var rule = core.route.rules[0];
            rule.inbound.Should().Equal(UdpInterceptionConfig.InboundTag);
            rule.outbound.Should().Be(Global.ProxyTag);
            rule.network.Should().BeNull();
            rule.protocol.Should().BeNull();
            rule.port.Should().BeNull();
            rule.port_range.Should().BeNull();
            rule.ip_cidr.Should().BeNull();
            rule.domain.Should().BeNull();
            core.route.rules.Should().Contain(r => r.outbound == Global.DirectTag && r.port_range != null);
        }
        await PrivacyRoutingTests.CheckCoreIfConfigured(json,
            coreType == ECoreType.Xray ? "XRAY_VALIDATION_EXE" : "SINGBOX_VALIDATION_EXE",
            coreType == ECoreType.Xray ? ["run", "-test", "-c"] : ["check", "-c"]);
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void DedicatedInbound_ShouldNotBeGeneratedWhenDisabledOrTunIsEnabled(ECoreType coreType)
    {
        var context = CreateContext(coreType);
        context.AppConfig.GuiItem.EnableUdpInterception = false;
        Generate(context).Should().NotContain(UdpInterceptionConfig.InboundTag);

        context.AppConfig.GuiItem.EnableUdpInterception = true;
        context.AppConfig.TunModeItem.EnableTun = true;
        // The main context also has IsTunEnabled=false for legacy pre-socks TUN.
        Generate(context).Should().NotContain(UdpInterceptionConfig.InboundTag);
        UdpInterceptionConfig.ShouldCreateInbound(context with { IsTunEnabled = true }).Should().BeFalse();
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void DedicatedInbound_ShouldNotBeUsedForUnsupportedConfigurations(ECoreType coreType)
    {
        var context = CreateContext(coreType);
        UdpInterceptionConfig.ShouldCreateInbound(context).Should().BeTrue();
        UdpInterceptionConfig.ShouldCreateInbound(context with { IsWindows = false }).Should().BeFalse();
        UdpInterceptionConfig.ShouldCreateInbound(context with
        {
            FullConfigTemplate = new FullConfigTemplateItem { Enabled = true }
        }).Should().BeFalse();
        UdpInterceptionConfig.ShouldCreateInbound(context with { RunCoreType = ECoreType.mihomo }).Should().BeFalse();
        context.Node.ConfigType = EConfigType.Custom;
        UdpInterceptionConfig.ShouldCreateInbound(context).Should().BeFalse();
    }

    [Fact]
    public void Xray_DedicatedInbound_ShouldFollowSelectedBalancer()
    {
        var context = CreateContext(ECoreType.Xray);
        var first = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "first");
        var second = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "second");
        var group = CoreConfigTestFactory.CreatePolicyGroupNode(ECoreType.Xray, "group", "group",
            [first.IndexId, second.IndexId]);
        context = context with { Node = group };
        context.AllProxiesMap[first.IndexId] = first;
        context.AllProxiesMap[second.IndexId] = second;
        var core = JsonUtils.Deserialize<V2rayConfig>(Generate(context))!;
        var rule = core.routing.rules[0];
        rule.inboundTag.Should().Equal(UdpInterceptionConfig.InboundTag);
        rule.outboundTag.Should().BeNull();
        rule.balancerTag.Should().Be(Global.ProxyTag + Global.BalancerTagSuffix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalPortSelection_ShouldReserveDedicatedTcpAndUdpEvenWhenMixedUdpIsDisabled(bool udp)
    {
        using var occupiedUdp = udp ? new UdpClient() { ExclusiveAddressUse = true } : null;
        using var occupiedTcp = !udp ? new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true } : null;
        occupiedUdp?.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupiedTcp?.Start();
        var occupiedPort = udp
            ? ((IPEndPoint)occupiedUdp!.Client.LocalEndPoint!).Port
            : ((IPEndPoint)occupiedTcp!.LocalEndpoint).Port;
        var config = CoreConfigTestFactory.CreateConfig();
        config.GuiItem.EnableUdpInterception = true;
        config.Inbound[0].UdpEnabled = false;
        config.Inbound[0].LocalPort = occupiedPort - (int)EInboundProtocol.udpInterception;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        AppManager.Instance.EnsureLocalPortsAvailable(includeUdpInterception: true).Should().BeTrue();
        AppManager.Instance.GetLocalPort(EInboundProtocol.udpInterception).Should().NotBe(occupiedPort);
    }

    internal static CoreConfigContext CreateContext(ECoreType coreType)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.GuiItem.EnableUdpInterception = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(coreType), coreType) with { IsWindows = true };
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { OutboundTag = Global.DirectTag, Network = "udp", Port = "0-65535" }
        });
        return context;
    }

    internal static string Generate(CoreConfigContext context)
    {
        CoreConfigTestFactory.BindAppManagerConfig(context.AppConfig);
        var result = context.RunCoreType == ECoreType.Xray
            ? new CoreConfigV2rayService(context).GenerateClientConfigContent()
            : new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue(result.Msg);
        return result.Data!.ToString()!;
    }
}

// AppManager's singleton is also used by the existing configuration tests.
[CollectionDefinition("UDP interception config", DisableParallelization = true)]
public class UdpInterceptionConfigCollection
{
}
