namespace ServiceLib.ViewModels;

public class RoutingSettingViewModel : MyReactiveObject
{
    #region Reactive

    public IObservableCollection<RoutingItemModel> RoutingItems { get; } = new ObservableCollectionExtended<RoutingItemModel>();

    [Reactive]
    public RoutingItemModel SelectedSource { get; set; }

    public IList<RoutingItemModel> SelectedSources { get; set; }

    [Reactive]
    public string DomainStrategy { get; set; }

    [Reactive]
    public string DomainStrategy4Singbox { get; set; }

    public ReactiveCommand<Unit, Unit> RoutingAdvancedAddCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedEditCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedRemoveCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedSetDefaultCmd { get; }
    public ReactiveCommand<Unit, Unit> RoutingAdvancedImportRulesCmd { get; }

    public bool IsModified { get; set; }

    #endregion Reactive

    public RoutingSettingViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;

        var canEditRemove = this.WhenAnyValue(
            x => x.SelectedSource,
            selectedSource => selectedSource != null && !selectedSource.Remarks.IsNullOrEmpty());

        RoutingAdvancedAddCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedEditAsync(true);
        });
        RoutingAdvancedEditCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedEditAsync(false);
        }, canEditRemove);
        RoutingAdvancedRemoveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedRemoveAsync();
        }, canEditRemove);
        RoutingAdvancedSetDefaultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedSetDefault();
        }, canEditRemove);
        RoutingAdvancedImportRulesCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingAdvancedImportRules();
        });

        _ = Init();

        // Auto-save DomainStrategy when changed
        this.WhenAnyValue(
            x => x.DomainStrategy,
            x => x.DomainStrategy4Singbox)
            .Skip(1)
            .DistinctUntilChanged()
            .Subscribe(x =>
            {
                IsModified = true;
                _ = SaveSettingsAsync();
            });
    }

    private async Task Init()
    {
        SelectedSource = new();

        DomainStrategy = _config.RoutingBasicItem.DomainStrategy;
        DomainStrategy4Singbox = _config.RoutingBasicItem.DomainStrategy4Singbox;

        await ConfigHandler.InitBuiltinRouting(_config);
        await RefreshRoutingItems();
    }

    #region Refresh Save

    public async Task RefreshRoutingItems()
    {
        RoutingItems.Clear();
        var models = new List<RoutingItemModel>();

        var routings = await AppManager.Instance.RoutingItems();
        foreach (var item in routings)
        {
            var it = new RoutingItemModel()
            {
                IsActive = item.IsActive,
                RuleNum = item.RuleNum,
                Id = item.Id,
                Remarks = item.Remarks,
                Url = item.Url,
                CustomIcon = item.CustomIcon,
                CustomRulesetPath4Singbox = item.CustomRulesetPath4Singbox,
                Sort = item.Sort,
            };
            models.Add(it);
        }
        RoutingItems.AddRange(models);
    }

    /// <summary>
    /// Save DomainStrategy settings
    /// </summary>
    public async Task SaveSettingsAsync()
    {
        _config.RoutingBasicItem.DomainStrategy = DomainStrategy;
        _config.RoutingBasicItem.DomainStrategy4Singbox = DomainStrategy4Singbox;
        await ConfigHandler.SaveConfig(_config);
    }

    #endregion Refresh Save

    public async Task RoutingAdvancedEditAsync(bool blNew)
    {
        RoutingItem item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = await AppManager.Instance.GetRoutingItem(SelectedSource?.Id);
            if (item is null)
            {
                return;
            }
        }
        if (await _updateView?.Invoke(EViewAction.RoutingRuleSettingWindow, item) == true)
        {
            await RefreshRoutingItems();
            IsModified = true;
        }
    }

    public async Task RoutingAdvancedRemoveAsync()
    {
        if (SelectedSource is null || SelectedSource.Remarks.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        var selectedIds = (SelectedSources?.Count > 0 ? SelectedSources : [SelectedSource])
            .Select(item => item.Id).Distinct().ToList();
        if (await _updateView?.Invoke(EViewAction.ShowYesNo, "确定删除选中的路由方案？") != true)
        {
            return;
        }
        foreach (var id in selectedIds)
        {
            var item = await AppManager.Instance.GetRoutingItem(id);
            if (item != null)
            {
                await ConfigHandler.RemoveRoutingItem(item);
            }
        }

        await ConfigHandler.InitBuiltinRouting(_config);
        await ConfigHandler.GetDefaultRouting(_config);
        await RefreshRoutingItems();
        IsModified = true;
        AppEvents.RoutingsMenuRefreshRequested.Publish();
        AppEvents.ReloadRequested.Publish();
    }

    public async Task RoutingAdvancedSetDefault()
    {
        var item = await AppManager.Instance.GetRoutingItem(SelectedSource?.Id);
        if (item is null)
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }

        if (await ConfigHandler.SetDefaultRouting(_config, item) == 0)
        {
            await RefreshRoutingItems();
            IsModified = true;
            AppEvents.RoutingsMenuRefreshRequested.Publish();
            AppEvents.ReloadRequested.Publish();
        }
    }

    private async Task RoutingAdvancedImportRules()
    {
        if (await ConfigHandler.InitRouting(_config, true) == 0)
        {
            await RefreshRoutingItems();
            IsModified = true;
            AppEvents.RoutingsMenuRefreshRequested.Publish();
            AppEvents.ReloadRequested.Publish();
        }
    }
}
