using AwesomeAssertions;
using ServiceLib.Models;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Services;

[Collection("UDP interception config")]
public class OutboundModeSelectionTests
{
    [Fact]
    public void MainlandBypass_ShouldSelectWhitelistInsteadOfTheLastCustomRulePlan()
    {
        var custom = new RoutingItem { Id = "custom", Remarks = "My rules", IsActive = true };
        var white = new RoutingItem { Id = "white", Remarks = "V4-绕过大陆(Whitelist)" };
        OutboundModeSelection.FindMainlandBypass([custom, white]).Should().BeSameAs(white);
        OutboundModeSelection.IsMainlandBypass(custom).Should().BeFalse();
        white.Enabled = false;
        OutboundModeSelection.FindMainlandBypass([custom, white]).Should().BeNull();
        var created = OutboundModeSelection.CreateRouting(OutboundMode.Rules, 1);
        OutboundModeSelection.IsMainlandBypass(created).Should().BeTrue();
    }

    [Fact]
    public void ReturningToRules_ShouldRestoreTheRememberedCustomPlan()
    {
        var custom = new RoutingItem { Id = "custom", Remarks = "My rules" };
        var white = new RoutingItem { Id = "white", Remarks = "V4-绕过大陆(Whitelist)" };
        var global = OutboundModeSelection.CreateRouting(OutboundMode.Global, 3);
        global.IsActive = true;
        OutboundModeSelection.FindRouting([white, custom, global], OutboundMode.Rules, custom.Id)
            .Should().BeSameAs(custom);
    }

    [Fact]
    public void MissingOrDisabledRememberedPlan_ShouldFallBackToAnEnabledRulePlan()
    {
        var disabled = new RoutingItem { Id = "disabled", Remarks = "Old rules", Enabled = false };
        var white = new RoutingItem { Id = "white", Remarks = "V4-绕过大陆(Whitelist)" };
        var direct = OutboundModeSelection.CreateRouting(OutboundMode.Direct, 3);
        OutboundModeSelection.FindRouting([disabled, direct, white], OutboundMode.Rules, disabled.Id)
            .Should().BeSameAs(white);
        OutboundModeSelection.FindRouting([disabled, direct], OutboundMode.Rules, "deleted").Should().BeNull();
    }

    [Theory]
    [InlineData("V4-全局(Global)")]
    [InlineData("RUv1-Всё")]
    public void ExistingBuiltinGlobalPlan_ShouldBeReused(string remarks)
    {
        var global = new RoutingItem { Id = "existing-global", Remarks = remarks };
        OutboundModeSelection.GetMode(global).Should().Be(OutboundMode.Global);
        OutboundModeSelection.FindRouting([global], OutboundMode.Global, null).Should().BeSameAs(global);
    }

    [Fact]
    public void ACustomPlanNamedGlobal_ShouldRemainARulePlan()
    {
        OutboundModeSelection.GetMode(new RoutingItem { Id = "custom", Remarks = "全局" })
            .Should().Be(OutboundMode.Rules);
    }

    [Theory]
    [InlineData(OutboundMode.Rules)]
    [InlineData(OutboundMode.Global)]
    [InlineData(OutboundMode.Direct)]
    public void CreatedPlan_ShouldRoundTripAndRemainInItsMode(OutboundMode mode)
    {
        var created = OutboundModeSelection.CreateRouting(mode, 7);
        var restored = JsonUtils.Deserialize<RoutingItem>(JsonUtils.Serialize(created))!;
        OutboundModeSelection.GetMode(restored).Should().Be(mode);
        restored.Sort.Should().Be(7);
        JsonUtils.Deserialize<List<RulesItem>>(restored.RuleSet).Should().NotBeEmpty();
        OutboundModeSelection.FindRouting([restored], mode, restored.Id).Should().BeSameAs(restored);
    }

    [Fact]
    public void RememberedRulePlan_ShouldPersistWithoutChangingCustomRules()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.RoutingBasicItem.LastRuleRoutingId = "my-custom-plan";
        config.RoutingBasicItem.CustomRules = [new() { Domain = ["domain:example.test"], OutboundTag = Global.DirectTag }];
        var restored = JsonUtils.Deserialize<Config>(JsonUtils.Serialize(config))!;
        restored.RoutingBasicItem.LastRuleRoutingId.Should().Be("my-custom-plan");
        restored.RoutingBasicItem.CustomRules.Single().Domain.Should().Equal("domain:example.test");
    }

    [Theory]
    [InlineData(ECoreType.Xray, OutboundMode.Direct, "direct")]
    [InlineData(ECoreType.sing_box, OutboundMode.Direct, "direct")]
    [InlineData(ECoreType.Xray, OutboundMode.Global, "proxy")]
    [InlineData(ECoreType.sing_box, OutboundMode.Global, "proxy")]
    public void Mode_ShouldGenerateTheExpectedCoreCatchAll(ECoreType core, OutboundMode mode, string outbound)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        config.GuiItem.EnableUdpInterception = false;
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(core), core)
            with { RoutingItem = OutboundModeSelection.CreateRouting(mode, 1) };
        var json = UdpInterceptionRoutingTests.Generate(context);
        if (core == ECoreType.Xray)
        {
            JsonUtils.Deserialize<V2rayConfig>(json)!.routing.rules
                .Last(rule => rule.port == "0-65535").outboundTag.Should().Be(outbound);
        }
        else
        {
            JsonUtils.Deserialize<SingboxConfig>(json)!.route.rules
                .Last(rule => rule.port_range?.Contains("0:65535") == true).outbound.Should().Be(outbound);
        }
    }

    [Theory]
    [InlineData(ERuleMode.Global, false, "global")]
    [InlineData(ERuleMode.Direct, false, "direct")]
    [InlineData(ERuleMode.Rule, true, "rule")]
    [InlineData(ERuleMode.Unchanged, true, "direct")]
    [InlineData(ERuleMode.Unchanged, false, "rule")]
    public async Task MihomoMode_ShouldApplyEvenWhenTheProfileOmitsMode(ERuleMode mode, bool hasMode, string expected)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.ClashUIItem.RuleMode = mode;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var source = Path.GetTempFileName();
        var output = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(source,
                (hasMode ? "mode: direct\n" : "") + "proxies: []\nproxy-groups: []\nrules: []\n",
                TestContext.Current.CancellationToken);
            var result = await new CoreConfigClashService(config).GenerateClientCustomConfig(
                new ProfileItem { Address = source, Remarks = "outbound-mode-fixture" }, output);
            result.Success.Should().BeTrue(result.Msg);
            var generated = YamlUtils.FromYaml<Dictionary<string, object>>(
                await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))!;
            generated["mode"].ToString().Should().Be(expected);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }
}
