using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class RoutingNodeSelectionTests
{
    static RoutingNodeSelectionTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }

    [Fact]
    public void Choices_ShouldIncludeLocalAndSubscriptionNodesWithDistinctStableReferences()
    {
        var local = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "local", "same-name");
        var subscribed = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "subscribed", "same-name");
        subscribed.Subid = "subscription";
        var custom = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, "custom");
        custom.ConfigType = EConfigType.Custom;
        var choices = RoutingNodeSelection.BuildChoices([local, subscribed, custom],
            new Dictionary<string, string> { ["subscription"] = "airport" });
        choices.Should().HaveCount(5);
        choices.Should().Contain(choice => choice.Tag == "node:local" && choice.DisplayName.StartsWith("本地"));
        choices.Should().Contain(choice => choice.Tag == "node:subscribed" && choice.DisplayName.StartsWith("airport"));
        choices.Should().NotContain(choice => choice.Tag == "node:custom");
        subscribed.Remarks = "renamed";
        RoutingNodeSelection.BuildChoices([subscribed])[3].Tag.Should().Be("node:subscribed");
        RoutingNodeSelection.MapKey("old-remarks").Should().Be("remark:old-remarks");
    }

    [Theory]
    [InlineData(ECoreType.Xray, false)]
    [InlineData(ECoreType.Xray, true)]
    [InlineData(ECoreType.sing_box, false)]
    [InlineData(ECoreType.sing_box, true)]
    public async Task GeneratedRules_ShouldRouteWebsitesAppsAndDefaultToSelectedNodes(ECoreType coreType, bool tun)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.TunModeItem.EnableTun = tun;
        config.TunModeItem.AutoRoute = false;
        config.TunModeItem.Stack = "system";
        config.GuiItem.EnableUdpInterception = false;
        config.GuiItem.ProxyStunTraffic = true;
        var main = CoreConfigTestFactory.CreateSocksNode(coreType, "main", "same");
        var website = CoreConfigTestFactory.CreateSocksNode(coreType, "website", "same");
        website.Port = 11001;
        website.Subid = "airport";
        var app = CoreConfigTestFactory.CreateSocksNode(coreType, "app", "same");
        app.Port = 11002;
        var context = CoreConfigTestFactory.CreateContext(config, main, coreType);
        context.AllProxiesMap[RoutingNodeSelection.Reference(website.IndexId)] = website;
        context.AllProxiesMap[RoutingNodeSelection.Reference(app.IndexId)] = app;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Domain = ["domain:service.test"], OutboundTag = "node:website" },
            new()
            {
                Process = ["chrome.exe", "C:/Apps/test.exe"], Domain = ["domain:mixed.test"],
                Ip = ["198.51.100.2/32"], OutboundTag = "node:app"
            },
            new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
        });
        // SQLite retains node identity through changes to names and subscriptions.
        var path = Path.Combine(Path.GetTempPath(), $"routing-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new SQLiteConnection(path, false);
            db.CreateTable<RoutingItem>();
            db.Insert(context.RoutingItem);
            context = context with { RoutingItem = db.Table<RoutingItem>().First() };
        }
        finally { File.Delete(path); }
        var json = UdpInterceptionRoutingTests.Generate(context);
        var websiteTag = $"{website.IndexId}-{Global.ProxyTag}-{website.Remarks}";
        var appTag = $"{app.IndexId}-{Global.ProxyTag}-{app.Remarks}";
        if (coreType == ECoreType.Xray)
        {
            var core = JsonUtils.Deserialize<V2rayConfig>(json)!;
            core.routing.rules.Should().Contain(rule => rule.domain != null
                && rule.domain.Contains("domain:service.test") && rule.outboundTag == websiteTag);
            var appRule = core.routing.rules.Single(rule => rule.process != null && rule.process.Contains("chrome.exe"));
            appRule.outboundTag.Should().Be(appTag);
            appRule.process.Should().Contain("C:/Apps/test.exe");
            if (tun) core.routing.rules.IndexOf(appRule).Should().BeLessThan(
                core.routing.rules.FindIndex(rule => rule.domain != null && rule.domain.Contains("full:stun.cloudflare.com")));
            core.routing.rules.FindIndex(rule => rule.domain?.Contains("domain:mixed.test") == true)
                .Should().BeGreaterThan(core.routing.rules.FindIndex(
                    rule => rule.domain?.Contains("domain:service.test") == true));
            core.routing.rules.Should().Contain(rule => rule.network == "tcp,udp" && rule.outboundTag == Global.ProxyTag);
            core.outbounds.Single(outbound => outbound.tag == websiteTag).settings.servers![0].port.Should().Be(11001);
            core.outbounds.Single(outbound => outbound.tag == appTag).settings.servers![0].port.Should().Be(11002);
        }
        else
        {
            var core = JsonUtils.Deserialize<SingboxConfig>(json)!;
            core.route.rules.Should().Contain(rule => rule.domain_suffix != null
                && rule.domain_suffix.Contains("service.test") && rule.outbound == websiteTag);
            var appRule = core.route.rules.Single(rule => rule.process_name != null && rule.process_name.Contains("chrome.exe"));
            appRule.outbound.Should().Be(appTag);
            core.route.rules.Should().Contain(rule => rule.process_path != null
                && rule.process_path.Contains("C:\\Apps\\test.exe") && rule.outbound == appTag);
            if (tun) core.route.rules.IndexOf(appRule).Should().BeLessThan(
                core.route.rules.FindIndex(rule => rule.type == "logical" && rule.outbound == Global.ProxyTag));
            core.route.rules.FindIndex(rule => rule.domain_suffix?.Contains("mixed.test") == true)
                .Should().BeGreaterThan(core.route.rules.FindIndex(
                    rule => rule.domain_suffix?.Contains("service.test") == true));
            core.route.rules.Should().Contain(rule => rule.network != null
                && rule.network.SequenceEqual(new[] { "tcp", "udp" }) && rule.outbound == Global.ProxyTag);
            core.outbounds.Single(outbound => outbound.tag == websiteTag).server_port.Should().Be(11001);
            core.outbounds.Single(outbound => outbound.tag == appTag).server_port.Should().Be(11002);
        }
        await PrivacyRoutingTests.CheckCoreIfConfigured(json,
            coreType == ECoreType.Xray ? "XRAY_VALIDATION_EXE" : "SINGBOX_VALIDATION_EXE",
            coreType == ECoreType.Xray ? ["run", "-test", "-c"] : ["check", "-c"]);
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public void DeletedPinnedNode_ShouldFailGenerationInsteadOfUsingAnotherExit(ECoreType coreType)
    {
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Domain = ["domain:service.test"], OutboundTag = "node:deleted" }
        });
        var generated = coreType == ECoreType.Xray
            ? new CoreConfigV2rayService(context).GenerateClientConfigContent()
            : new CoreConfigSingboxService(context).GenerateClientConfigContent();
        generated.Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    public async Task PinnedDefault_ShouldAlsoRouteInterceptedUdpAndStunToDefaultNode(ECoreType coreType)
    {
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.AppConfig.GuiItem.ProxyStunTraffic = true;
        var node = CoreConfigTestFactory.CreateSocksNode(coreType, "default-node", "default");
        context.AllProxiesMap["node:default-node"] = node;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Network = "tcp,udp", OutboundTag = "node:default-node" }
        });
        var json = UdpInterceptionRoutingTests.Generate(context);
        var expected = "default-node-proxy-default";
        if (coreType == ECoreType.Xray)
        {
            var rules = JsonUtils.Deserialize<V2rayConfig>(json)!.routing.rules;
            rules[0].inboundTag.Should().Equal("udp-interception");
            rules[0].outboundTag.Should().Be(expected);
            rules.Should().Contain(rule => rule.domain != null
                && rule.domain.Contains("full:stun.cloudflare.com") && rule.outboundTag == expected);
            rules.Should().Contain(rule => rule.network == "tcp,udp" && rule.outboundTag == expected);
        }
        else
        {
            var rules = JsonUtils.Deserialize<SingboxConfig>(json)!.route.rules;
            rules[0].inbound.Should().Equal("udp-interception");
            rules[0].outbound.Should().Be(expected);
            rules.Should().Contain(rule => rule.type == "logical" && rule.outbound == expected);
            rules.Should().Contain(rule => rule.network != null
                && rule.network.SequenceEqual(new[] { "tcp", "udp" }) && rule.outbound == expected);
        }
        await PrivacyRoutingTests.CheckCoreIfConfigured(json,
            coreType == ECoreType.Xray ? "XRAY_VALIDATION_EXE" : "SINGBOX_VALIDATION_EXE",
            coreType == ECoreType.Xray ? ["run", "-test", "-c"] : ["check", "-c"]);
    }

    [Theory]
    [InlineData(ECoreType.Xray, false)]
    [InlineData(ECoreType.Xray, true)]
    [InlineData(ECoreType.sing_box, false)]
    [InlineData(ECoreType.sing_box, true)]
    public async Task Builder_ShouldResolveDatabaseNodeByIdOrLegacyRemarks(ECoreType coreType, bool legacy)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<SubItem>();
        var originalActive = await SQLiteHelper.Instance.TableAsync<RoutingItem>()
            .Where(item => item.IsActive).ToListAsync();
        var main = CoreConfigTestFactory.CreateSocksNode(coreType, $"main-{Guid.NewGuid():N}");
        var selected = CoreConfigTestFactory.CreateSocksNode(coreType, $"selected-{Guid.NewGuid():N}", $"saved-{Guid.NewGuid():N}");
        var reference = legacy ? selected.Remarks : RoutingNodeSelection.Reference(selected.IndexId);
        var routing = new RoutingItem
        {
            Id = $"routing-{Guid.NewGuid():N}", IsActive = true, Remarks = "routing-test",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Domain = ["domain:service.test"], OutboundTag = reference },
                new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
            })
        };
        try
        {
            foreach (var item in originalActive)
            {
                item.IsActive = false;
                await SQLiteHelper.Instance.UpdateAsync(item);
            }
            await SQLiteHelper.Instance.InsertAsync(selected);
            await SQLiteHelper.Instance.InsertAsync(routing);
            if (!legacy)
            {
                selected.Remarks = "renamed-after-save";
                await SQLiteHelper.Instance.UpdateAsync(selected);
            }
            var result = await CoreConfigContextBuilder.BuildAll(config, main);
            result.Success.Should().BeTrue();
            result.MainResult.Context.AllProxiesMap[RoutingNodeSelection.MapKey(reference)]
                .IndexId.Should().Be(selected.IndexId);
            var json = UdpInterceptionRoutingTests.Generate(result.MainResult.Context);
            json.Should().Contain($"{selected.IndexId}-proxy-{selected.Remarks}");
            await SQLiteHelper.Instance.DeleteAsync(selected);
            var missing = await CoreConfigContextBuilder.BuildAll(config, main);
            missing.Success.Should().Be(legacy);
            (legacy ? missing.MainResult.ValidatorResult.Warnings : missing.MainResult.ValidatorResult.Errors)
                .Should().NotBeEmpty();
        }
        finally
        {
            await SQLiteHelper.Instance.DeleteAsync(selected);
            await SQLiteHelper.Instance.DeleteAsync(routing);
            foreach (var item in originalActive)
            {
                item.IsActive = true;
                await SQLiteHelper.Instance.UpdateAsync(item);
            }
        }
    }

    [Theory]
    [InlineData("unchanged", true)]
    [InlineData("renamed", true)]
    [InlineData("endpoint-changed", true)]
    [InlineData("removed", false)]
    [InlineData("ambiguous", false)]
    public async Task SubscriptionRefresh_ShouldRemapPinnedRulesOnlyForUniqueMatches(string update, bool remapped)
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<SubItem>();
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        var subid = $"sub-{Guid.NewGuid():N}";
        var originalUri = "socks://user:pass@127.0.0.1:12001#same-name";
        var original = SocksFmt.Resolve(originalUri, out _)!;
        original.IndexId = $"old-{Guid.NewGuid():N}";
        original.Subid = subid;
        var local = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray, $"local-{Guid.NewGuid():N}");
        var reference = RoutingNodeSelection.Reference(original.IndexId);
        config.RoutingBasicItem.CustomRules =
        [
            new() { Domain = ["domain:custom.test"], OutboundTag = reference }
        ];
        var routing = new RoutingItem
        {
            Id = $"subscription-routing-{Guid.NewGuid():N}", Remarks = "refresh-test",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Domain = ["domain:service.test"], OutboundTag = reference },
                new() { Process = ["player.exe"], OutboundTag = reference, Enabled = false },
                new() { Network = "tcp,udp", OutboundTag = reference },
                new() { Domain = ["domain:local.test"], OutboundTag = RoutingNodeSelection.Reference(local.IndexId) },
                new() { Domain = ["domain:legacy.test"], OutboundTag = "same-name" }
            })
        };
        try
        {
            await SQLiteHelper.Instance.InsertAsync(original);
            await SQLiteHelper.Instance.InsertAsync(local);
            await SQLiteHelper.Instance.InsertAsync(routing);
            var refreshedUris = update switch
            {
                "renamed" => originalUri.Replace("same-name", "new-name"),
                "endpoint-changed" => originalUri.Replace("12001", "12002"),
                "removed" => originalUri.Replace("12001", "12002").Replace("same-name", "other-node"),
                "ambiguous" => originalUri.Replace("12001", "12002") + Environment.NewLine
                    + originalUri.Replace("12001", "12003"),
                _ => originalUri
            };
            (await ConfigHandler.AddBatchServers(config, refreshedUris, subid, true)).Should().BeGreaterThan(0);
            var nodes = await AppManager.Instance.ProfileItems(subid);
            var saved = await SQLiteHelper.Instance.TableAsync<RoutingItem>().FirstAsync(item => item.Id == routing.Id);
            var rules = JsonUtils.Deserialize<List<RulesItem>>(saved.RuleSet)!;
            var expected = remapped ? RoutingNodeSelection.Reference(nodes!.Single().IndexId) : reference;
            config.RoutingBasicItem.CustomRules.Single().OutboundTag.Should().Be(expected);
            rules.Take(3).Should().OnlyContain(rule => rule.OutboundTag == expected);
            rules[1].Enabled.Should().BeFalse();
            rules[3].OutboundTag.Should().Be(RoutingNodeSelection.Reference(local.IndexId));
            rules[4].OutboundTag.Should().Be("same-name");
            (await AppManager.Instance.GetProfileItem(local.IndexId)).Should().NotBeNull();
        }
        finally
        {
            await ConfigHandler.RemoveServersViaSubid(config, subid, true);
            await SQLiteHelper.Instance.DeleteAsync(local);
            await SQLiteHelper.Instance.DeleteAsync(routing);
        }
    }

    [Fact]
    public async Task DefaultNodeEditor_ShouldReplaceCatchAllAndRetainSpecificRules()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var routing = new RoutingItem
        {
            Id = "default-test",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Id = "website", Domain = ["domain:service.test"], OutboundTag = "node:local" },
                new() { Id = "app", Process = ["chrome.exe"], OutboundTag = "node:airport" },
                new() { Id = "old-default", Port = "0-65535", OutboundTag = Global.DirectTag }
            })
        };
        var viewModel = new RoutingRuleSettingViewModel(routing, (action, value) =>
        {
            action.Should().Be(EViewAction.RoutingDefaultNodeWindow);
            ((RulesItem)value!).OutboundTag = Global.ProxyTag;
            return Task.FromResult(true);
        });
        await viewModel.DefaultNodeCmd.Execute();
        viewModel.RulesItems.Should().HaveCount(3);
        viewModel.RulesItems[0].Id.Should().Be("website");
        viewModel.RulesItems[1].Id.Should().Be("app");
        viewModel.RulesItems[2].OutboundTag.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public async Task CancellingDefaultOrRuleEdit_ShouldPreserveOriginalRules()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var routing = new RoutingItem
        {
            Id = "cancel-test",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Id = "default", Network = "tcp,udp", OutboundTag = Global.ProxyTag }
            })
        };
        var viewModel = new RoutingRuleSettingViewModel(routing, (_, value) =>
        {
            ((RulesItem)value!).OutboundTag = Global.DirectTag;
            return Task.FromResult(false);
        });
        await viewModel.DefaultNodeCmd.Execute();
        viewModel.SelectedSource = viewModel.RulesItems[0];
        await viewModel.RuleEditAsync(false);
        viewModel.RulesItems.Should().ContainSingle().Which.OutboundTag.Should().Be(Global.ProxyTag);
    }

    [Fact]
    public void ApplicationPicker_ShouldAppendNormalizedPathWithoutDuplicates()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var viewModel = new RoutingRuleDetailsViewModel(new RulesItem(), null);
        viewModel.Process = "chrome.exe";
        viewModel.AddApplication("C:\\Apps\\Player.exe");
        viewModel.AddApplication("c:\\apps\\PLAYER.exe");
        Utils.String2List(Utils.ParseProcess(viewModel.Process)).Should().Equal("chrome.exe", "C:/Apps/Player.exe");
    }

    [Theory]
    [InlineData(ECoreType.Xray, "direct")]
    [InlineData(ECoreType.Xray, "block")]
    [InlineData(ECoreType.Xray, "node:local")]
    [InlineData(ECoreType.sing_box, "direct")]
    [InlineData(ECoreType.sing_box, "block")]
    [InlineData(ECoreType.sing_box, "node:local")]
    public async Task SimpleWebsite_ShouldSavePlainDomainsAndGenerateSelectedAction(ECoreType coreType, string target)
    {
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.AppConfig.GuiItem.EnableUdpInterception = false;
        var local = CoreConfigTestFactory.CreateSocksNode(coreType, "local", "local");
        context.AllProxiesMap["node:local"] = local;
        var closed = false;
        var rule = new RulesItem();
        var viewModel = new RoutingRuleDetailsViewModel(rule, (action, _) =>
        {
            action.Should().Be(EViewAction.CloseWindow);
            closed = true;
            return Task.FromResult(true);
        }, websiteOnly: true);
        viewModel.Domain = "EXAMPLE.COM\n*.cdn.example.com，https://example.com/page\n例子.测试";
        rule.OutboundTag = target;
        await viewModel.SaveCmd.Execute();
        closed.Should().BeTrue();
        viewModel.ValidationError.Should().BeNull();
        rule.Domain.Should().Equal("domain:example.com", "domain:cdn.example.com", "domain:xn--fsqu00a.xn--0zwm56d");
        rule.OutboundTag.Should().Be(target);
        rule.Remarks.Should().Be("example.com");
        rule.Port.Should().BeNull();
        rule.Network.Should().BeNull();
        rule.Process.Should().BeNull();
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            rule, new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
        });
        var json = UdpInterceptionRoutingTests.Generate(context);
        var expected = target == "node:local" ? "local-proxy-local" : target;
        if (coreType == ECoreType.Xray)
        {
            var generatedRule = JsonUtils.Deserialize<V2rayConfig>(json)!.routing.rules
                .Single(item => item.domain?.Contains("domain:example.com") == true);
            generatedRule.outboundTag.Should().Be(expected);
            generatedRule.domain.Should().Equal(rule.Domain);
        }
        else
        {
            var generatedRule = JsonUtils.Deserialize<SingboxConfig>(json)!.route.rules
                .Single(item => item.domain_suffix?.Contains("example.com") == true);
            if (target == "block")
            {
                generatedRule.action.Should().Be("reject");
                generatedRule.outbound.Should().BeNull();
            }
            else generatedRule.outbound.Should().Be(expected);
        }
        await PrivacyRoutingTests.CheckCoreIfConfigured(json,
            coreType == ECoreType.Xray ? "XRAY_VALIDATION_EXE" : "SINGBOX_VALIDATION_EXE",
            coreType == ECoreType.Xray ? ["run", "-test", "-c"] : ["check", "-c"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n，")]
    [InlineData("127.0.0.1")]
    [InlineData("example..com")]
    [InlineData("-example.com")]
    [InlineData("domain:")]
    [InlineData("example.com:443")]
    [InlineData("regexp:.*")]
    [InlineData("example.com\nbad/path")]
    public async Task SimpleWebsite_InvalidInput_ShouldKeepOriginalAndRemainOpen(string input)
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var rule = new RulesItem { Id = "saved", Domain = ["domain:original.test"], OutboundTag = Global.DirectTag };
        var closed = false;
        var viewModel = new RoutingRuleDetailsViewModel(rule, (_, _) =>
        {
            closed = true;
            return Task.FromResult(true);
        }, websiteOnly: true) { Domain = input };
        await viewModel.SaveCmd.Execute();
        closed.Should().BeFalse();
        viewModel.ValidationError.Should().NotBeNullOrEmpty();
        rule.Domain.Should().Equal("domain:original.test");
        rule.OutboundTag.Should().Be(Global.DirectTag);
    }

    [Fact]
    public async Task SimpleWebsite_Edit_ShouldHidePrefixAndPreserveEnabledState()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var rule = new RulesItem
        {
            Id = "saved", Domain = ["domain:original.test"], OutboundTag = Global.DirectTag,
            Enabled = false, Remarks = "saved-remark", RuleType = ERuleType.Routing, Network = "tcp,udp"
        };
        var viewModel = new RoutingRuleDetailsViewModel(rule, (_, _) => Task.FromResult(true), websiteOnly: true);
        viewModel.Domain.Should().Be("original.test");
        viewModel.Domain = "updated.test";
        await viewModel.SaveCmd.Execute();
        rule.Domain.Should().Equal("domain:updated.test");
        rule.Enabled.Should().BeFalse();
        rule.Remarks.Should().Be("saved-remark");
        rule.RuleType.Should().Be(ERuleType.Routing);
        rule.Network.Should().Be("tcp,udp");
    }

    [Fact]
    public async Task WebsiteEntryAndEdit_ShouldUseSimpleFormAndRetainAdvancedRules()
    {
        CoreConfigTestFactory.BindAppManagerConfig(CoreConfigTestFactory.CreateConfig());
        var routing = new RoutingItem
        {
            Id = "forms",
            RuleSet = JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Id = "simple", Domain = ["domain:example.com"], OutboundTag = Global.DirectTag },
                new() { Id = "keyword", Domain = ["example.com"], OutboundTag = Global.DirectTag },
                new() { Id = "regex", Domain = ["regexp:.*"], OutboundTag = Global.DirectTag },
                new() { Id = "limited", Domain = ["domain:example.com"], Port = "443", OutboundTag = Global.DirectTag },
                new() { Id = "app", Process = ["app.exe"], OutboundTag = Global.ProxyTag }
            })
        };
        var actions = new List<EViewAction>();
        var viewModel = new RoutingRuleSettingViewModel(routing, (action, _) =>
        {
            actions.Add(action);
            return Task.FromResult(false);
        });
        await viewModel.WebsiteAddCmd.Execute();
        foreach (var item in viewModel.RulesItems.ToList())
        {
            viewModel.SelectedSource = item;
            await viewModel.RuleEditAsync(false);
        }
        actions.Should().Equal(EViewAction.RoutingWebsiteRuleWindow, EViewAction.RoutingWebsiteRuleWindow,
            EViewAction.RoutingRuleDetailsWindow, EViewAction.RoutingRuleDetailsWindow,
            EViewAction.RoutingRuleDetailsWindow, EViewAction.RoutingRuleDetailsWindow);
        viewModel.RulesItems[0].Domains.Should().Be("example.com");
        viewModel.RulesItems.Should().HaveCount(5);
    }
}
