using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class UpdateSettingsTests
{
    [Fact]
    public async Task ExternallyManagedCoreChecks_ShouldSkipLegacyBackgroundChecksWhenAppChecksAreDisabled()
    {
        var manager = TaskManager.Instance;
        var appChecks = manager.IncludeAppUpdates;
        var externalChecks = manager.CoreUpdateChecksManagedExternally;
        try
        {
            manager.IncludeAppUpdates = false;
            manager.CoreUpdateChecksManagedExternally = true;
            var routine = typeof(TaskManager).GetMethod("UpdateTaskRunCheckUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            // No scheduler configuration is needed: ownership must be checked before any network work.
            await (Task)routine.Invoke(manager, null)!;
        }
        finally
        {
            manager.IncludeAppUpdates = appChecks;
            manager.CoreUpdateChecksManagedExternally = externalChecks;
        }
    }

    [Fact]
    public void LegacyUpdateSettings_ShouldLeaveAutomaticCoreInstallationDisabled()
    {
        var config = JsonUtils.Deserialize<Config>(
            """{"CheckUpdateItem":{"CheckPreReleaseUpdate":true,"SelectedCoreTypes":["Xray"]}}""")!;
        config.CheckUpdateItem.AutoUpdateCores.Should().BeFalse();
        config.CheckUpdateItem.CheckPreReleaseUpdate.Should().BeTrue();
        config.CheckUpdateItem.SelectedCoreTypes.Should().Equal("Xray");
        new CheckUpdateItem().AutoUpdateCores.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticCoreSetting_ShouldPersistWithExistingUpdatePreferences(bool enabled)
    {
        var config = new Config
        {
            CheckUpdateItem = new CheckUpdateItem
            {
                AutoUpdateCores = enabled, CheckPreReleaseUpdate = true,
                SelectedCoreTypes = ["v2rayN", "Xray", "sing_box"]
            }
        };
        var restored = JsonUtils.Deserialize<Config>(JsonUtils.Serialize(config))!;
        restored.CheckUpdateItem.AutoUpdateCores.Should().Be(enabled);
        restored.CheckUpdateItem.CheckPreReleaseUpdate.Should().BeTrue();
        restored.CheckUpdateItem.SelectedCoreTypes.Should().Equal("v2rayN", "Xray", "sing_box");
    }
}
