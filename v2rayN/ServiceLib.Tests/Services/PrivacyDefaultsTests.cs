using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public class PrivacyDefaultsTests
{
    [Fact]
    public void NewSettings_ShouldEnableBothProtections()
    {
        var settings = new GUIItem();
        settings.ProxyStunTraffic.Should().BeTrue();
        settings.EnableUdpInterception.Should().BeTrue();
    }

    [Fact]
    public void OldSettings_ShouldEnableProtectionsOnce()
    {
        var config = JsonUtils.Deserialize<Config>(
            """{"GuiItem":{"ProxyStunTraffic":false}}""")!;
        ConfigHandler.ApplyPrivacyDefaults(config);
        config.PrivacyDefaultsVersion.Should().Be(2);
        config.GuiItem.ProxyStunTraffic.Should().BeTrue();
        config.GuiItem.EnableUdpInterception.Should().BeTrue();

        config.GuiItem.ProxyStunTraffic = false;
        config.GuiItem.EnableUdpInterception = false;
        var reloaded = JsonUtils.Deserialize<Config>(JsonUtils.Serialize(config))!;
        ConfigHandler.ApplyPrivacyDefaults(reloaded);
        reloaded.GuiItem.ProxyStunTraffic.Should().BeFalse();
        reloaded.GuiItem.EnableUdpInterception.Should().BeFalse();
    }

    [Fact]
    public void MissingSettings_ShouldGetEnabledDefaults()
    {
        var config = new Config();
        ConfigHandler.ApplyPrivacyDefaults(config);
        config.GuiItem.ProxyStunTraffic.Should().BeTrue();
        config.GuiItem.EnableUdpInterception.Should().BeTrue();
    }
}
