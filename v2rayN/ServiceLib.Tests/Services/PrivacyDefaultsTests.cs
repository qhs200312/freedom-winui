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
    }

    [Fact]
    public void OldSettings_ShouldEnableProtectionsOnce()
    {
        var config = JsonUtils.Deserialize<Config>(
            """{"GuiItem":{"ProxyStunTraffic":false}}""")!;
        ConfigHandler.ApplyPrivacyDefaults(config);
        config.PrivacyDefaultsVersion.Should().Be(1);
        config.GuiItem.ProxyStunTraffic.Should().BeTrue();

        config.GuiItem.ProxyStunTraffic = false;
        var reloaded = JsonUtils.Deserialize<Config>(JsonUtils.Serialize(config))!;
        ConfigHandler.ApplyPrivacyDefaults(reloaded);
        reloaded.GuiItem.ProxyStunTraffic.Should().BeFalse();
    }

    [Fact]
    public void MissingSettings_ShouldGetEnabledDefaults()
    {
        var config = new Config();
        ConfigHandler.ApplyPrivacyDefaults(config);
        config.GuiItem.ProxyStunTraffic.Should().BeTrue();
    }
}
