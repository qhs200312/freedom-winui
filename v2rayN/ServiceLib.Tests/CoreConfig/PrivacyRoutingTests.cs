using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

public class PrivacyRoutingTests
{
    [Theory]
    [InlineData(false, false, false, "UseIPv4")]
    [InlineData(true, false, false, "UseIPv4")]
    [InlineData(true, true, false, "UseIPv4")]
    [InlineData(true, true, true, "UseIPv4")]
    [InlineData(true, true, true, "ForceIPv4")]
    [InlineData(true, true, true, "ForceIPv6")]
    public async Task Singbox_ShouldGenerateCompatibleDnsAndScopedStunRules(bool protect, bool tun, bool fakeIp, string strategy)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.GuiItem.ProxyStunTraffic = protect;
        config.TunModeItem.EnableTun = tun;
        config.TunModeItem.AutoRoute = false;
        config.TunModeItem.Stack = "system";
        config.Inbound[0].SniffingEnabled = false;
        config.SimpleDNSItem.AddCommonHosts = false;
        config.SimpleDNSItem.UseSystemHosts = false;
        config.SimpleDNSItem.Hosts = "unit-test.example 192.0.2.1";
        config.SimpleDNSItem.FakeIP = fakeIp;
        config.SimpleDNSItem.GlobalFakeIp = false;
        config.SimpleDNSItem.Strategy4Proxy = strategy;
        config.SimpleDNSItem.Strategy4Freedom = strategy;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box);
        context.RoutingItem.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { OutboundTag = Global.DirectTag, Network = "udp", Port = "0-65535" }
        });

        var generated = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        generated.Success.Should().BeTrue(generated.Msg);
        var json = generated.Data!.ToString()!;
        var core = JsonUtils.Deserialize<SingboxConfig>(json)!;
        core.dns.independent_cache.Should().BeNull();
        core.dns.rules.Should().NotContain(rule => rule.ip_accept_any != null);
        core.dns.rules.Should().OnlyContain(rule => rule.strategy == null);
        core.dns.strategy.Should().Be(Utils.DomainStrategy4Sbox(strategy));
        if (strategy.StartsWith("Force", StringComparison.Ordinal))
        {
            var rejectedType = strategy == "ForceIPv4" ? 28 : 1;
            core.dns.rules.Should().Contain(rule => rule.action == "predefined"
                && rule.query_type != null && rule.query_type.Contains(rejectedType));
        }
        var hostRule = core.dns.rules.Single(rule => rule.server == Global.SingboxHostsDNSTag);
        hostRule.domain.Should().Contain("unit-test.example");
        if (fakeIp)
        {
            core.dns.rules.Should().Contain(rule => rule.server == Global.SingboxFakeDNSTag && rule.query_type != null);
        }

        var stun = core.route.rules.FirstOrDefault(rule => rule.type == "logical" && rule.outbound == Global.ProxyTag);
        if (protect)
        {
            stun.Should().NotBeNull();
            core.route.rules.Should().Contain(rule => rule.action == "sniff"
                && rule.network!.SequenceEqual(new[] { "udp" })
                && rule.sniffer!.SequenceEqual(new[] { "stun" }));
            stun!.rules.Should().Contain(rule => rule.network != null && rule.network.SequenceEqual(new[] { "udp" }));
            stun.rules.Should().Contain(rule => rule.invert == true && rule.ip_cidr!.Contains("fc00::/7"));
            var selectors = stun.rules.Single(rule => rule.mode == "or").rules!;
            selectors.Should().OnlyContain(rule => rule.port == null && rule.port_range == null);
            selectors.Should().Contain(rule => rule.protocol != null && rule.protocol.Contains("stun"));
            selectors.Should().Contain(rule => rule.domain != null && rule.domain.Contains("stun.cloudflare.com"));
            var dnsRule = core.dns.rules.Single(rule => rule.domain != null
                && rule.domain.Contains("stun.cloudflare.com"));
            dnsRule.server.Should().Be(Global.SingboxRemoteDNSTag);
            core.route.rules.IndexOf(stun).Should().BeLessThan(
                core.route.rules.FindLastIndex(rule => rule.outbound == Global.DirectTag));
        }
        else
        {
            stun.Should().BeNull();
            core.route.rules.Should().NotContain(rule => rule.action == "sniff");
        }
        await CheckCoreIfConfigured(json, "SINGBOX_VALIDATION_EXE", ["check", "-c"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Xray_ShouldPlaceScopedStunRulesBeforeUserDirectRules(bool enabled, bool addressesResolved)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.GuiItem.ProxyStunTraffic = enabled;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray);
        context = context with
        {
            StunServerAddresses = addressesResolved
                ? ["203.0.113.9", "2001:db8::9", "192.168.1.1", "fe80::1", "::ffff:192.168.1.1"]
                : []
        };
        context.RoutingItem.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { OutboundTag = Global.DirectTag, Network = "udp", Port = "0-65535" }
        });
        var generated = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        generated.Success.Should().BeTrue(generated.Msg);
        var core = JsonUtils.Deserialize<V2rayConfig>(generated.Data!.ToString())!;
        var rules = core.routing.rules.Where(rule => rule.port == WebRtcRoutingPolicy.Ports).ToList();
        if (enabled && addressesResolved)
        {
            rules.Should().ContainSingle();
            rules[0].outboundTag.Should().Be(Global.ProxyTag);
            rules[0].ip.Should().Equal("203.0.113.9", "2001:db8::9");
            rules.Should().OnlyContain(rule => rule.network == "udp");
            core.routing.rules.IndexOf(rules[0]).Should().BeLessThan(
                core.routing.rules.FindIndex(rule => rule.port == "0-65535"));
        }
        else
        {
            rules.Should().BeEmpty();
        }
        core.routing.rules.Where(rule => rule.outboundTag == Global.ProxyTag && rule.network == "udp")
            .Should().OnlyContain(rule => (rule.ip != null && rule.ip.Count > 0)
                || (rule.domain != null && rule.domain.Count > 0));
        await CheckCoreIfConfigured(generated.Data!.ToString()!, "XRAY_VALIDATION_EXE", ["run", "-test", "-c"]);
    }

    private static async Task CheckCoreIfConfigured(string json, string environmentVariable, string[] arguments)
    {
        var executable = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrEmpty(executable)) return;
        var directory = Path.Combine(Path.GetTempPath(), $"v2rayN-dns-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, json);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
                throw;
            }
            process.ExitCode.Should().Be(0, (await output) + (await error));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
