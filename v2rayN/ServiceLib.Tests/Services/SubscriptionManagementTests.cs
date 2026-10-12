using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.Services;

[Collection("Subscription management database")]
public sealed class SubscriptionManagementTests : IAsyncLifetime
{
    private readonly Config _originalConfig = AppManager.Instance.Config;
    private readonly Config _config = CoreConfigTestFactory.CreateConfig();
    private readonly object? _originalViewModelConfig = typeof(MyReactiveObject)
        .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
    private readonly List<string> _createdIds = [];

    public ValueTask InitializeAsync()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        CoreConfigTestFactory.BindAppManagerConfig(_config);
        SQLiteHelper.Instance.CreateTable<SubItem>();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Refresh_ShouldLoadSavedClipboardSubscriptionIntoAnAlreadyCreatedViewModel()
    {
        var model = new SubSettingViewModel(null);
        await model.RefreshSubItems();
        var url = $"https://subscription.example.test/feed?fixture={Guid.NewGuid():N}&remarks=clipboard-regression";
        await ConfigHandler.AddSubItem(_config, url);
        var saved = await SQLiteHelper.Instance.TableAsync<SubItem>().FirstAsync(item => item.Url == url);
        _createdIds.Add(saved.Id);
        model.SubItems.Should().NotContain(item => item.Id == saved.Id);

        await model.RefreshSubItems();

        var displayed = model.SubItems.Single(item => item.Id == saved.Id);
        displayed.Url.Should().Be(url);
        displayed.Remarks.Should().Be("clipboard-regression");
        displayed.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task RepeatedClipboardSubscription_ShouldKeepItsOriginalIdAndRemainVisible()
    {
        var url = $"https://subscription.example.test/feed?fixture={Guid.NewGuid():N}";
        await ConfigHandler.AddSubItem(_config, url);
        var saved = await SQLiteHelper.Instance.TableAsync<SubItem>().FirstAsync(item => item.Url == url);
        _createdIds.Add(saved.Id);
        var model = new SubSettingViewModel(null);
        await model.RefreshSubItems();
        model.SelectedSource = model.SubItems.Single(item => item.Id == saved.Id);

        await ConfigHandler.AddSubItem(_config, url);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => model.RefreshSubItems()));

        (await SQLiteHelper.Instance.TableAsync<SubItem>().CountAsync(item => item.Url == url)).Should().Be(1);
        model.SubItems.Count(item => item.Id == saved.Id).Should().Be(1);
        model.SelectedSource.Id.Should().Be(saved.Id);
        model.SelectedSource.Url.Should().Be(url);
    }

    [Fact]
    public async Task Refresh_ShouldRetainDisabledSubscriptionsAndLocalGroups()
    {
        var disabled = new SubItem
        {
            Id = Guid.NewGuid().ToString("N"), Remarks = "disabled-fixture", Enabled = false,
            Url = "https://subscription.example.test/disabled", Sort = int.MaxValue - 1
        };
        var local = new SubItem
        {
            Id = Guid.NewGuid().ToString("N"), Remarks = "local-fixture", Url = "", Sort = int.MaxValue
        };
        foreach (var item in new[] { disabled, local })
        {
            await SQLiteHelper.Instance.InsertAsync(item);
            _createdIds.Add(item.Id);
        }
        var model = new SubSettingViewModel(null);
        await model.RefreshSubItems();

        model.SubItems.Should().Contain(item => item.Id == disabled.Id && !item.Enabled);
        model.SubItems.Should().Contain(item => item.Id == local.Id && item.Url == "");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _createdIds)
            await SQLiteHelper.Instance.DeleteAsync(new SubItem { Id = id });
        CoreConfigTestFactory.BindAppManagerConfig(_originalConfig);
        typeof(MyReactiveObject).GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, _originalViewModelConfig);
    }
}

[CollectionDefinition("Subscription management database", DisableParallelization = true)]
public sealed class SubscriptionManagementDatabaseCollection { }
