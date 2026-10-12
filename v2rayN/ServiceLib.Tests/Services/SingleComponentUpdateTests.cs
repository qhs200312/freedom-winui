using AwesomeAssertions;
using ReactiveUI.Builder;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;
using Xunit;

namespace ServiceLib.Tests.Services;

[Collection("Subscription management database")]
public sealed class SingleComponentUpdateTests : IDisposable
{
    private readonly Config _originalConfig = AppManager.Instance.Config;
    private readonly object? _originalViewModelConfig = typeof(MyReactiveObject)
        .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
    private readonly Config _config = CoreConfigTestFactory.CreateConfig();
    private readonly CheckUpdateViewModel _model;

    public SingleComponentUpdateTests()
    {
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
        CoreConfigTestFactory.BindAppManagerConfig(_config);
        _config.CheckUpdateItem.SelectedCoreTypes = ["v2rayN", "Xray", "GeoFiles"];
        _model = new(null, includeAppUpdates: false);
        _model.CheckUpdateModels.Clear();
        foreach (var type in new[] { ECoreType.Xray, ECoreType.mihomo, ECoreType.sing_box })
            _model.CheckUpdateModels.Add(new() { CoreType = type, IsSelected = false });
        _model.CheckUpdateModels.Add(new() { IsGeoFile = true, IsSelected = false });
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.mihomo)]
    [InlineData(ECoreType.sing_box)]
    public void SingleUpdate_ShouldIncludeOnlyRequestedCoreEvenWhenUnchecked(ECoreType type)
    {
        var target = _model.CheckUpdateModels.Single(item => item.CoreType == type);
        foreach (var item in _model.CheckUpdateModels.Where(item => item != target)) item.IsSelected = true;
        var selection = _model.CheckUpdateModels.Select(item => item.IsSelected).ToArray();

        Items(target).Should().Equal(target);

        _model.CheckUpdateModels.Select(item => item.IsSelected).Should().Equal(selection);
        _config.CheckUpdateItem.SelectedCoreTypes.Should().Equal("v2rayN", "Xray", "GeoFiles");
    }

    [Fact]
    public void SingleGeoUpdate_ShouldExcludeAllCheckedCores()
    {
        foreach (var item in _model.CheckUpdateModels) item.IsSelected = !item.IsGeoFile;
        var target = _model.CheckUpdateModels.Single(item => item.IsGeoFile);

        Items(target).Should().Equal(target);
        target.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void BatchUpdate_ShouldContinueUsingOnlyCheckedRows()
    {
        var xray = _model.CheckUpdateModels.Single(item => item.CoreType == ECoreType.Xray);
        var geo = _model.CheckUpdateModels.Single(item => item.IsGeoFile);
        xray.IsSelected = true;
        geo.IsSelected = true;

        Items(null).Should().Equal(xray, geo);
    }

    [Fact]
    public async Task DetachedRow_ShouldNotStartAnUpdateOrChangeBatchPreferences()
    {
        var detached = new CheckUpdateModel { CoreType = ECoreType.Xray, IsSelected = true };

        Items(detached).Should().BeEmpty();
        await _model.UpdateSingleAsync(detached);

        CheckUpdateViewModel.IsUpdating.Should().BeFalse();
        _config.CheckUpdateItem.SelectedCoreTypes.Should().Equal("v2rayN", "Xray", "GeoFiles");
    }

    [Fact]
    public void SeparateAppCoordinator_ShouldKeepAppOutOfComponentUpdates()
    {
        var app = new CheckUpdateModel { CoreType = ECoreType.v2rayN, IsSelected = true };
        _model.CheckUpdateModels.Add(app);

        Items(app).Should().BeEmpty();
        Items(null).Should().BeEmpty();
    }

    private List<CheckUpdateModel> Items(CheckUpdateModel? target)
        => (List<CheckUpdateModel>)typeof(CheckUpdateViewModel)
            .GetMethod("GetUpdateItems", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_model, [target])!;

    public void Dispose()
    {
        CoreConfigTestFactory.BindAppManagerConfig(_originalConfig);
        typeof(MyReactiveObject).GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, _originalViewModelConfig);
    }
}
