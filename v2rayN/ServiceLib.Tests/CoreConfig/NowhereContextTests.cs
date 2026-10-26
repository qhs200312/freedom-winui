using AwesomeAssertions;
using SQLite;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public sealed class NowhereContextTests
{
    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task ImportStoreResolveGenerate_ShouldUseAuthenticatedLoopbackWithoutMutatingStoredNode(ECoreType coreType)
    {
        var originalConfig = AppManager.Instance.Config;
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var path = Path.Combine(Path.GetTempPath(), $"nowhere-node-{Guid.NewGuid():N}.db");
        try
        {
            var node = FmtHandler.ResolveConfig(
                "nowhere://0123456789abcdef0123456789abcdef@relay.example:2000?net=udp&morph=0&rate=100&etar=200#Saved", out _)!;
            (await ConfigHandler.AddNowhereServer(config, node, false)).Should().Be(0);
            ProfileItem stored;
            using (var database = new SQLiteConnection(path))
            {
                database.CreateTable<ProfileItem>();
                database.Insert(node);
                stored = database.Table<ProfileItem>().Single();
            }
            stored.ConfigType.Should().Be(EConfigType.Nowhere);
            stored.ProtoExtra.Should().Be(node.ProtoExtra);
            var before = JsonUtils.Serialize(stored);
            var context = new CoreConfigContext
            {
                Node = stored, RunCoreType = coreType, AppConfig = config, SimpleDnsItem = config.SimpleDNSItem,
                RoutingItem = new() { RuleSet = "[]" }
            };
            var (resolved, validation) = await CoreConfigContextBuilder.ResolveNodeAsync(context, stored, false);
            validation.Errors.Should().BeEmpty();
            context = context with { Node = resolved };
            resolved.ConfigType.Should().Be(EConfigType.SOCKS);
            resolved.CoreType.Should().Be(coreType);
            resolved.Address.Should().Be(Global.Loopback);
            resolved.Username.Should().NotBeNullOrWhiteSpace();
            resolved.Password.Should().NotBeNullOrWhiteSpace();
            context.ProtectDomainList.Should().Contain(stored.Address);
            JsonUtils.Serialize(stored).Should().Be(before);
            var bridge = context.NowhereBridges.Values.Single();
            bridge.VectorUri.Should().Contain("morph=0").And.Contain("rate=100").And.Contain("etar=200");
            var result = coreType == ECoreType.Xray
                ? new CoreConfigV2rayService(context).GenerateClientConfigContent()
                : new CoreConfigSingboxService(context).GenerateClientConfigContent();
            result.Success.Should().BeTrue(result.Msg);
            result.Data!.ToString().Should().Contain("127.0.0.1").And.NotContain("nowhere://")
                .And.NotContain("vector://").And.NotContain(stored.Password);
            // A second reference to the same saved node shares one owned Vector process.
            (await CoreConfigContextBuilder.ResolveNodeAsync(context, stored, false)).Item1.Port.Should().Be(resolved.Port);
            context.NowhereBridges.Should().ContainSingle();
            var speedTest = await CoreConfigHandler.GenerateClientSpeedtestConfig(config, context, new ServerTestItem(), "");
            speedTest.Success.Should().BeFalse();
            speedTest.Msg.Should().Contain("Nowhere");
        }
        finally
        {
            CoreConfigTestFactory.BindAppManagerConfig(originalConfig);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task MultipleNodes_ShouldHaveDistinctLocalPortsAndCredentials()
    {
        var node = NowhereFmt.Resolve("nowhere://0123456789abcdef0123456789abcdef@example.test:2000", out _)!;
        var context = new CoreConfigContext { Node = node, RunCoreType = ECoreType.Xray, AppConfig = CoreConfigTestFactory.CreateConfig() };
        foreach (var id in new[] { "a", "b" })
        {
            var clone = JsonUtils.DeepCopy(node);
            clone.IndexId = id;
            (await CoreConfigContextBuilder.ResolveNodeAsync(context, clone, false)).Item2.Success.Should().BeTrue();
        }
        context.NowhereBridges.Values.Select(bridge => bridge.SocksNode.Port).Should().OnlyHaveUniqueItems();
        context.NowhereBridges.Values.Select(bridge => bridge.SocksNode.Password).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FullTemplate_ShouldFailBeforePreparingAnyBridge()
    {
        var node = NowhereFmt.Resolve("nowhere://0123456789abcdef0123456789abcdef@example.test:2000", out _)!;
        node.IndexId = "template-fixture";
        var context = new CoreConfigContext
        {
            Node = node, RunCoreType = ECoreType.Xray,
            AppConfig = CoreConfigTestFactory.CreateConfig(),
            FullConfigTemplate = new() { Enabled = true }
        };
        (await CoreConfigContextBuilder.ResolveNodeAsync(context, node, false)).Item2.Success.Should().BeFalse();
        context.NowhereBridges.Should().BeEmpty();
    }
}
