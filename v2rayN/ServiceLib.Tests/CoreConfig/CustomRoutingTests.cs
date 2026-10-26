using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Events;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class CustomRoutingTests
{
    static CustomRoutingTests() =>
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    public static TheoryData<ECoreType, string, bool> Presets
    {
        get
        {
            var data = new TheoryData<ECoreType, string, bool>();
            foreach (var core in new[] { ECoreType.Xray, ECoreType.sing_box })
            foreach (var preset in new[] { "white", "black", "global", "" })
            foreach (var tun in new[] { false, true })
                data.Add(core, preset, tun);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void CustomWebsite_ShouldOverrideEveryPresetAndSurviveRepeatedGeneration(
        ECoreType coreType, string preset, bool tun)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.TunModeItem.EnableTun = tun;
        config.GuiItem.EnableUdpInterception = false;
        config.RoutingBasicItem.CustomRules =
        [
            new() { Id = "direct", Domain = ["domain:service.test"], OutboundTag = Global.DirectTag },
            new() { Id = "disabled", Domain = ["domain:disabled.test"], OutboundTag = Global.DirectTag, Enabled = false },
            new() { Id = "pinned", Domain = ["domain:pinned.test"], OutboundTag = "node:selected" }
        ];
        // Exercise persistence before generating the effective routing configuration.
        config = JsonUtils.Deserialize<Config>(JsonUtils.Serialize(config))!;
        var node = CoreConfigTestFactory.CreateSocksNode(coreType);
        var context = CoreConfigTestFactory.CreateContext(config, node, coreType);
        context.AllProxiesMap["node:selected"] = CoreConfigTestFactory.CreateSocksNode(coreType, "selected", "selected");
        context = context with
        {
            RoutingItem = preset.Length == 0 ? null : new RoutingItem
            {
                RuleSet = EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + preset)
            }
        };
        var savedRules = JsonUtils.Serialize(config.RoutingBasicItem.CustomRules);
        for (var generation = 0; generation < 2; generation++)
        {
            var json = UdpInterceptionRoutingTests.Generate(context);
            if (coreType == ECoreType.Xray)
            {
                var rules = JsonUtils.Deserialize<V2rayConfig>(json)!.routing.rules;
                var directIndex = rules.FindIndex(rule => rule.domain?.Contains("domain:service.test") == true);
                directIndex.Should().BeGreaterThanOrEqualTo(0);
                rules[directIndex].outboundTag.Should().Be(Global.DirectTag);
                rules.Should().NotContain(rule => rule.domain != null && rule.domain.Contains("domain:disabled.test"));
                var catchAll = rules.FindIndex(rule => rule.port == "0-65535" || rule.network == "tcp,udp");
                if (catchAll >= 0) directIndex.Should().BeLessThan(catchAll);
            }
            else
            {
                var core = JsonUtils.Deserialize<SingboxConfig>(json)!;
                var directIndex = core.route.rules.FindIndex(rule => rule.domain_suffix?.Contains("service.test") == true);
                directIndex.Should().BeGreaterThanOrEqualTo(0);
                core.route.rules[directIndex].outbound.Should().Be(Global.DirectTag);
                core.route.rules.Should().NotContain(rule => rule.domain_suffix != null && rule.domain_suffix.Contains("disabled.test"));
                var catchAll = core.route.rules.FindIndex(rule => rule.port_range?.Contains("0:65535") == true);
                if (catchAll >= 0) directIndex.Should().BeLessThan(catchAll);
                core.dns.rules.Should().Contain(rule => rule.domain_suffix != null
                    && rule.domain_suffix.Contains("service.test") && rule.server == Global.SingboxDirectDNSTag);
            }
            JsonUtils.Serialize(config.RoutingBasicItem.CustomRules).Should().Be(savedRules);
        }
    }

    [Fact]
    public async Task CustomEditor_ShouldSaveAddEditAndDeleteWithoutSavingAPreset()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var reloads = 0;
        using var subscription = AppEvents.ReloadRequested.AsObservable().Subscribe(_ => reloads++);
        RoutingRuleSettingViewModel? viewModel = null;
        viewModel = new RoutingRuleSettingViewModel(new RoutingItem(), (action, value) =>
        {
            if (action == EViewAction.ShowYesNo)
            {
                // Selection can change while the confirmation is being displayed.
                viewModel!.SelectedSource = null;
                viewModel.SelectedSources = [];
            }
            else
            {
                var rule = (RulesItem)value!;
                rule.Id = "custom";
                rule.Domain = ["domain:service.test"];
                rule.OutboundTag = rule.OutboundTag == Global.DirectTag ? Global.ProxyTag : Global.DirectTag;
            }
            return Task.FromResult(true);
        }, independentRules: true);
        await viewModel.WebsiteAddCmd.Execute();
        config.RoutingBasicItem.CustomRules.Should().ContainSingle().Which.OutboundTag.Should().Be(Global.DirectTag);
        viewModel.SelectedSource = viewModel.RulesItems[0];
        await viewModel.RuleEditAsync(false);
        config.RoutingBasicItem.CustomRules.Should().ContainSingle().Which.OutboundTag.Should().Be(Global.ProxyTag);
        viewModel.SelectedSource = viewModel.RulesItems[0];
        viewModel.SelectedSources = [];
        await viewModel.RuleRemoveAsync();
        config.RoutingBasicItem.CustomRules.Should().BeEmpty();
        viewModel.RulesItems.Should().BeEmpty();
        reloads.Should().Be(3);
        new RoutingRuleSettingViewModel(new RoutingItem(), null, independentRules: true)
            .RulesItems.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingRules_ShouldSnapshotMultipleSelectionsBeforeConfirmation()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        RoutingRuleSettingViewModel? viewModel = null;
        viewModel = new RoutingRuleSettingViewModel(new RoutingItem
        {
            Id = "preset",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Id = "one", OutboundTag = Global.DirectTag },
                new() { Id = "two", OutboundTag = Global.ProxyTag },
                new() { Id = "keep", OutboundTag = Global.ProxyTag }
            })
        }, (_, _) =>
        {
            viewModel!.SelectedSources.Clear();
            viewModel.SelectedSource = null;
            return Task.FromResult(true);
        });
        viewModel.SelectedSource = viewModel.RulesItems[0];
        viewModel.SelectedSources = viewModel.RulesItems.Take(2).ToList();
        await viewModel.RuleRemoveAsync();
        viewModel.RulesItems.Should().ContainSingle().Which.Id.Should().Be("keep");
    }
}
