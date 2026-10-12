using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models;
using SQLite;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.V2ray;

public class Hysteria2CompatibilityTests
{
    [Theory]
    [InlineData("hy2.example.test", "brutal")]
    [InlineData("127.0.0.1", "force-brutal")]
    [InlineData("::1", "brutal")]
    [InlineData("hy2.example.test", "bbr")]
    public async Task ImportStoreGenerate_ShouldPreserveTemplate(string address, string congestion)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        var source = CreateNode(address, congestion);
        var uri = Hysteria2Fmt.ToUri(source)!;
        var imported = Hysteria2Fmt.Resolve(uri, out var message)!;
        message.Should().BeEmpty();
        imported.CoreType = ECoreType.Xray;
        (await ConfigHandler.AddHysteria2Server(config, imported, false)).Should().Be(0);
        imported.Alpn.Should().Be("h3");
        imported.CoreType.Should().Be(ECoreType.Xray);

        var stored = StoreAndReload(imported);
        stored.ProtoExtra.Should().Be(imported.ProtoExtra);
        var root = Generate(config, stored);
        var proxy = root["outbounds"]!.AsArray().First(o => o!["tag"]!.GetValue<string>() == Global.ProxyTag)!;
        proxy["protocol"]!.GetValue<string>().Should().Be("hysteria");
        proxy["settings"]!["version"]!.GetValue<int>().Should().Be(2);
        proxy["settings"]!["address"]!.GetValue<string>().Trim('[', ']').Should().Be(address);
        proxy["settings"]!["port"]!.GetValue<int>().Should().Be(56923);
        var stream = proxy["streamSettings"]!;
        stream["network"]!.GetValue<string>().Should().Be("hysteria");
        stream["security"]!.GetValue<string>().Should().Be("tls");
        stream["tlsSettings"]!["serverName"]!.GetValue<string>().Should().Be("hy2.example.test");
        stream["tlsSettings"]!["alpn"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("h3");
        stream["hysteriaSettings"]!["auth"]!.GetValue<string>().Should().Be("test-auth");
        var mask = stream["finalmask"]!;
        mask["quicParams"]!["congestion"]!.GetValue<string>().Should().Be(congestion);
        mask["quicParams"]!["brutalUp"]!.GetValue<string>().Should().Be("100mbps");
        mask["quicParams"]!["brutalDown"]!.GetValue<string>().Should().Be("100mbps");
        mask["quicParams"]!["disableChromeParrot"]!.GetValue<bool>().Should().BeTrue();
        mask["quicParams"]!["udpHop"].Should().BeNull();
        var udp = mask["udp"]!.AsArray();
        udp.Select(m => m!["type"]!.GetValue<string>()).Should().Equal("salamander", "udphop");
        udp[1]!["settings"]!["mode"]!.GetValue<string>().Should().Be("intervalLocal,intervalRemote");
        udp[1]!["settings"]!["remotePorts"]!.GetValue<string>().Should().Be("30000-40000");
        udp[1]!["settings"]!["interval"]!.GetValue<string>().Should().Be("30");
        udp[0]!["settings"]!["password"]!.GetValue<string>().Should().Be("test-obfs");
        WriteArtifact($"{congestion}-{(address == "::1" ? "ipv6" : address == "127.0.0.1" ? "ipv4" : "domain")}.json", root);
    }

    [Theory]
    [InlineData("30000-40000", "30000-40000")]
    [InlineData("30000:40000", "30000-40000")]
    [InlineData("30000,30001", "30000,30001")]
    [InlineData("30000", "30000")]
    public void Mport_ShouldGenerateFinalMaskIncludingSinglePort(string ports, string expected)
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.SetProtocolExtra(node.GetProtocolExtra() with { Ports = ports });
        var mask = ProxyMask(Generate(CoreConfigTestFactory.CreateConfig(), node));
        mask["udp"]![1]!["settings"]!["remotePorts"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void LegacyNode_ShouldUseGlobalBandwidthAndDefaultAlpnWithoutReimport()
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.Alpn = string.Empty;
        node.SetProtocolExtra(new ProtocolExtraItem { Ports = "30000:40000", SalamanderPass = "test-obfs" });
        var root = Generate(CoreConfigTestFactory.CreateConfig(), StoreAndReload(node));
        var mask = ProxyMask(root);
        mask["quicParams"]!["congestion"]!.GetValue<string>().Should().Be("brutal");
        mask["quicParams"]!["brutalUp"]!.GetValue<string>().Should().Be("100mbps");
        mask["udp"]![1]!["settings"]!["interval"]!.GetValue<string>().Should().Be("30");
        root["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!["alpn"]![0]!.GetValue<string>().Should().Be("h3");
        node.Alpn.Should().BeEmpty();
        WriteArtifact("legacy-node.json", root);
    }

    [Fact]
    public void ZeroBandwidth_ShouldUseBbrRatherThanInheritGlobalBandwidth()
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.SetProtocolExtra(node.GetProtocolExtra() with { UpMbps = 0, DownMbps = 0, CongestionControl = null });
        var root = Generate(CoreConfigTestFactory.CreateConfig(), node);
        var quic = ProxyMask(root)["quicParams"]!;
        quic["congestion"]!.GetValue<string>().Should().Be("bbr");
        quic["brutalUp"].Should().BeNull();
        quic["brutalDown"].Should().BeNull();
        WriteArtifact("bbr-zero.json", root);
    }

    [Fact]
    public void LegacyCustomFinalMask_ShouldUpgradeOnlyRuntimeHopAndPreserveQuicParams()
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.Finalmask = """
            {"quicParams":{"congestion":"force-brutal","brutalUp":"100 mbps","brutalDown":"100 mbps",
            "maxIdleTimeout":60,"udpHop":{"ports":"30000:40000","interval":"30"}},
            "udp":[{"type":"salamander","settings":{"password":"test-obfs"}}]}
            """;
        var original = node.Finalmask;
        var root = Generate(CoreConfigTestFactory.CreateConfig(), StoreAndReload(node));
        var mask = ProxyMask(root);
        mask["quicParams"]!["udpHop"].Should().BeNull();
        mask["quicParams"]!["maxIdleTimeout"]!.GetValue<int>().Should().Be(60);
        mask["quicParams"]!["disableChromeParrot"]!.GetValue<bool>().Should().BeTrue();
        mask["udp"]![1]!["type"]!.GetValue<string>().Should().Be("udphop");
        mask["udp"]![1]!["settings"]!["remotePorts"]!.GetValue<string>().Should().Be("30000-40000");
        node.Finalmask.Should().Be(original);
        WriteArtifact("legacy-finalmask.json", root);
    }

    [Fact]
    public void ModernCustomFinalMask_ShouldRoundTripAllAdvancedFields()
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.Finalmask = """
            {"quicParams":{"congestion":"force-brutal","brutalUp":"100 mbps","brutalDown":"100 mbps",
            "bbrProfile":"standard","initStreamReceiveWindow":8388608,"maxStreamReceiveWindow":8388608,
            "initConnectionReceiveWindow":20971520,"maxConnectionReceiveWindow":20971520,
            "maxIdleTimeout":60,"keepAlivePeriod":15,"disablePathMTUDiscovery":false,"disableChromeParrot":false},
            "udp":[{"type":"salamander","settings":{"password":"test-obfs"}},
            {"type":"udphop","settings":{"mode":"intervalLocal,intervalRemote","interval":30,
            "remotePorts":"30000-40000","remoteIPs":["127.0.0.1/32","::1/128"]}}]}
            """;
        var imported = Hysteria2Fmt.Resolve(Hysteria2Fmt.ToUri(node)!, out _)!;
        imported.StreamSecurity = "tls";
        imported.CoreType = ECoreType.Xray;
        var root = Generate(CoreConfigTestFactory.CreateConfig(), StoreAndReload(imported));
        JsonNode.DeepEquals(ProxyMask(root), JsonNode.Parse(node.Finalmask)).Should().BeTrue();
        WriteArtifact("advanced-finalmask.json", root);
    }

    [Fact]
    public void ModernCustomFinalMask_ShouldMoveHopToLastWithoutChangingSavedNode()
    {
        var node = CreateNode("127.0.0.1", "brutal");
        node.Finalmask = """
            {"udp":[{"type":"udphop","settings":{"mode":"intervalRemote","interval":"30",
            "remotePorts":"30000-40000","remoteIPs":["::1/128"]}},
            {"type":"salamander","settings":{"password":"test-obfs"}}]}
            """;
        var original = node.Finalmask;
        var masks = ProxyMask(Generate(CoreConfigTestFactory.CreateConfig(), node))["udp"]!.AsArray();
        masks.Select(m => m!["type"]!.GetValue<string>()).Should().Equal("salamander", "udphop");
        masks[1]!["settings"]!["remoteIPs"]![0]!.GetValue<string>().Should().Be("::1/128");
        node.Finalmask.Should().Be(original);
    }
    [Fact]
    public async Task OfficialMinimalShare_ShouldDefaultToTlsAndH3()
    {
        var node = Hysteria2Fmt.Resolve("hy2://test-auth@127.0.0.1:56923?mport=30000-40000&obfs=salamander&obfs-password=test-obfs", out _)!;
        var config = CoreConfigTestFactory.CreateConfig();
        (await ConfigHandler.AddHysteria2Server(config, node, false)).Should().Be(0);
        node.StreamSecurity.Should().Be("tls");
        node.Alpn.Should().Be("h3");
        node.GetProtocolExtra().UpMbps.Should().BeNull();
        node.GetProtocolExtra().Ports.Should().Be("30000-40000");
    }

    [Theory]
    [InlineData("ports")]
    [InlineData("interval")]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("congestion")]
    public void Deduplication_ShouldNotCollapseDifferentHy2Parameters(string field)
    {
        var source = CreateNode("127.0.0.1", "brutal");
        var changed = CreateNode("127.0.0.1", "brutal");
        var extra = changed.GetProtocolExtra();
        changed.SetProtocolExtra(field switch
        {
            "ports" => extra with { Ports = "40000-50000" },
            "interval" => extra with { HopInterval = "60" },
            "up" => extra with { UpMbps = 50 },
            "down" => extra with { DownMbps = 50 },
            _ => extra with { CongestionControl = "force-brutal" },
        });
        var compare = typeof(ConfigHandler).GetMethod("CompareProfileItem",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        ((bool)compare.Invoke(null, [source, source, false])!).Should().BeTrue();
        ((bool)compare.Invoke(null, [source, changed, false])!).Should().BeFalse();
    }

    [Fact]
    public async Task Version2DatabaseNode_ShouldMigrateAndGenerateWithoutReimport()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CreateNode("127.0.0.1", "brutal");
        node.IndexId = $"hy2-migration-{Guid.NewGuid():N}";
        node.ConfigVersion = 2;
        node.ProtoExtra = string.Empty;
        node = JsonUtils.DeepCopy(node)!;
        node.Alpn = string.Empty;
#pragma warning disable CS0618
        node.Id = "test-auth";
        node.Path = "test-obfs";
        node.Ports = "30000:40000";
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<ProfileGroupItem>();
#pragma warning restore CS0618
        await SQLiteHelper.Instance.InsertAsync(node);
        try
        {
            await AppManager.Instance.MigrateProfileExtra();
            var migrated = await SQLiteHelper.Instance.TableAsync<ProfileItem>()
                .FirstAsync(p => p.IndexId == node.IndexId);
            migrated.ConfigVersion.Should().Be(4);
            migrated.Password.Should().Be("test-auth");
            migrated.GetProtocolExtra().SalamanderPass.Should().Be("test-obfs");
            migrated.GetProtocolExtra().Ports.Should().Be("30000:40000");
            var root = Generate(config, migrated);
            var mask = ProxyMask(root);
            mask["udp"]![1]!["settings"]!["remotePorts"]!.GetValue<string>().Should().Be("30000-40000");
            mask["quicParams"]!["brutalUp"]!.GetValue<string>().Should().Be("100mbps");
            WriteArtifact("migrated-v2-node.json", root);
        }
        finally
        {
            await SQLiteHelper.Instance.DeleteAsync(node);
        }
    }
    private static ProfileItem CreateNode(string address, string congestion)
    {
        var node = new ProfileItem
        {
            IndexId = "hy2-audit",
            ConfigType = EConfigType.Hysteria2,
            CoreType = ECoreType.Xray,
            Address = address,
            Port = 56923,
            Password = "test-auth",
            Sni = "hy2.example.test",
            StreamSecurity = "tls",
            Alpn = "h3",
            Remarks = "HY2 compatibility audit",
        };
        node.SetProtocolExtra(new ProtocolExtraItem
        {
            Ports = "30000-40000", HopInterval = "30", SalamanderPass = "test-obfs",
            UpMbps = 100, DownMbps = 100, CongestionControl = congestion,
        });
        return node;
    }

    private static ProfileItem StoreAndReload(ProfileItem node)
    {
        // A real SQLite round trip, isolated from the application's database.
        var path = Path.Combine(Path.GetTempPath(), $"hy2-audit-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SQLiteConnection(path, false);
            db.CreateTable<ProfileItem>();
            db.Insert(node);
            return db.Table<ProfileItem>().First();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static JsonNode Generate(Config config, ProfileItem node)
    {
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue();
        return JsonNode.Parse(result.Data!.ToString()!)!;
    }

    private static JsonNode ProxyMask(JsonNode root) => root["outbounds"]![0]!["streamSettings"]!["finalmask"]!;

    private static void WriteArtifact(string name, JsonNode root)
    {
        var directory = Environment.GetEnvironmentVariable("HY2_AUDIT_ARTIFACTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), JsonUtils.Serialize(root));
    }
}
