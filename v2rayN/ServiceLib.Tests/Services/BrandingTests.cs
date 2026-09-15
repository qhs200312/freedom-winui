using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public class BrandingTests
{
    [Fact]
    public void UpdateDisplay_ShouldUseFreedomWithoutChangingStoredSelection()
    {
        var model = new CheckUpdateModel { CoreType = ECoreType.v2rayN };
        model.DisplayName.Should().Be("freedom");
        model.CoreTypeForStorage.Should().Be("v2rayN");
        new CheckUpdateModel { CoreType = ECoreType.Xray }.DisplayName.Should().Be("Xray");
        new CheckUpdateModel { IsGeoFile = true }.DisplayName.Should().Be("GeoFiles");
    }

    [Fact]
    public void Branding_ShouldKeepCompatibilityIdentifiersAndBackupLocation()
    {
        Global.AppName.Should().Be("freedom");
        Global.AutoRunName.Should().Be("freedomAutoRun");
        Global.LegacyAppName.Should().Be("v2rayN");
        Global.InnerUriProtocol.Should().Be("v2rayn://");
        var manager = new WebDavManager();
        typeof(WebDavManager).GetField("_webDir", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager).Should().Be("v2rayN_backup");
        ResUI.TbSettingsN.Should().Contain("freedom");
    }
}
