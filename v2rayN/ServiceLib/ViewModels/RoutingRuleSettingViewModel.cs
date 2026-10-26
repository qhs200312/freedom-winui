namespace ServiceLib.ViewModels;

public class RoutingRuleSettingViewModel : MyReactiveObject
{
    private List<RulesItem> _rules;
    private Dictionary<string, string> _nodeNames = [];
    public bool IndependentRules { get; }

    public async Task LoadNodeNamesAsync()
    {
        _nodeNames = (await RoutingNodeSelection.LoadChoicesAsync())
            .ToDictionary(choice => choice.Tag, choice => choice.DisplayName);
        RefreshRulesItems();
    }

    [Reactive]
    public RoutingItem SelectedRouting { get; set; }

    public IObservableCollection<RulesItemModel> RulesItems { get; } = new ObservableCollectionExtended<RulesItemModel>();

    [Reactive]
    public RulesItemModel SelectedSource { get; set; }

    public IList<RulesItemModel> SelectedSources { get; set; }

    public ReactiveCommand<Unit, Unit> RuleAddCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleEditCmd { get; }
    public ReactiveCommand<Unit, Unit> WebsiteAddCmd { get; }
    public ReactiveCommand<Unit, Unit> ApplicationAddCmd { get; }
    public ReactiveCommand<Unit, Unit> DefaultNodeCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromFileCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportRulesFromUrlCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleRemoveCmd { get; }
    public ReactiveCommand<Unit, Unit> RuleExportSelectedCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveTopCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCmd { get; }
    public ReactiveCommand<Unit, Unit> MoveBottomCmd { get; }

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }

    public RoutingRuleSettingViewModel(RoutingItem routingItem, Func<EViewAction, object?, Task<bool>>? updateView,
        bool independentRules = false)
    {
        IndependentRules = independentRules;
        _config = AppManager.Instance.Config;
        _updateView = updateView;

        var canEditRemove = this.WhenAnyValue(
            x => x.SelectedSource,
            selectedSource => selectedSource != null && !selectedSource.OutboundTag.IsNullOrEmpty());

        RuleAddCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleEditAsync(true);
        });
        RuleEditCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleEditAsync(false);
        }, canEditRemove);
        WebsiteAddCmd = ReactiveCommand.CreateFromTask(() => AddTargetRuleAsync("网站分流", EViewAction.RoutingWebsiteRuleWindow));
        ApplicationAddCmd = ReactiveCommand.CreateFromTask(() => AddTargetRuleAsync("应用分流"));
        DefaultNodeCmd = ReactiveCommand.CreateFromTask(EditDefaultNodeAsync);
        ImportRulesFromFileCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await _updateView?.Invoke(EViewAction.ImportRulesFromFile, null);
        });
        ImportRulesFromClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ImportRulesFromClipboardAsync(null);
        });
        ImportRulesFromUrlCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ImportRulesFromUrl();
        });

        RuleRemoveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleRemoveAsync();
        }, canEditRemove);
        RuleExportSelectedCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RuleExportSelectedAsync();
        }, canEditRemove);

        MoveTopCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Top);
        }, canEditRemove);
        MoveUpCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Up);
        }, canEditRemove);
        MoveDownCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Down);
        }, canEditRemove);
        MoveBottomCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await MoveRule(EMove.Bottom);
        }, canEditRemove);

        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveRoutingAsync();
        });

        SelectedSource = new();
        SelectedRouting = routingItem;
        _rules = IndependentRules
            ? JsonUtils.DeepCopy(_config.RoutingBasicItem.CustomRules ?? [])
            : routingItem.Id.IsNullOrEmpty() ? [] : JsonUtils.Deserialize<List<RulesItem>>(SelectedRouting.RuleSet) ?? [];

        RefreshRulesItems();
    }

    public void RefreshRulesItems()
    {
        var selectedId = SelectedSource?.Id;
        SelectedSources = [];
        SelectedSource = null;
        RulesItems.Clear();

        var models = new List<RulesItemModel>();
        foreach (var item in _rules)
        {
            var it = new RulesItemModel()
            {
                Id = item.Id,
                RuleTypeName = item.RuleType?.ToString(),
                OutboundTag = item.OutboundTag,
                OutboundDisplayName = item.OutboundTag is { } tag ? _nodeNames.GetValueOrDefault(tag, tag) : null,
                Port = item.Port,
                Network = item.Network,
                Protocols = Utils.List2String(item.Protocol),
                InboundTags = Utils.List2String(item.InboundTag),
                Domains = Utils.List2String((item.Domain ?? [])
                    .Select(domain => RoutingRuleDetailsViewModel.IsSimpleWebsiteRule(item) ? domain[7..] : domain)
                    .Concat(item.Ip ?? []).Concat(item.Process ?? []).ToList()),
                Enabled = item.Enabled,
                Remarks = item.Remarks,
            };
            models.Add(it);
        }
        RulesItems.AddRange(models);
        SelectedSource = models.FirstOrDefault(model => model.Id == selectedId);
    }

    public async Task RuleEditAsync(bool blNew)
    {
        RulesItem? item;
        if (blNew)
        {
            item = new();
        }
        else
        {
            item = JsonUtils.DeepCopy(_rules.FirstOrDefault(t => t.Id == SelectedSource?.Id));
            if (item is null)
            {
                return;
            }
        }
        var action = !blNew && RoutingRuleDetailsViewModel.IsSimpleWebsiteRule(item)
            ? EViewAction.RoutingWebsiteRuleWindow
            : !blNew && RoutingNodeSelection.IsDefaultRule(item)
                ? EViewAction.RoutingDefaultNodeWindow : EViewAction.RoutingRuleDetailsWindow;
        if (await _updateView?.Invoke(action, item) == true)
        {
            if (blNew)
            {
                _rules.Insert(0, item);
            }
            else
            {
                _rules[_rules.FindIndex(rule => rule.Id == item.Id)] = item;
            }
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
        }
    }

    private async Task AddTargetRuleAsync(string remarks, EViewAction action = EViewAction.RoutingRuleDetailsWindow)
    {
        var rule = new RulesItem { Remarks = remarks };
        if (await _updateView?.Invoke(action, rule) == true)
        {
            _rules.Insert(0, rule);
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
        }
    }

    private async Task EditDefaultNodeAsync()
    {
        var current = _rules.FirstOrDefault(RoutingNodeSelection.IsDefaultRule);
        var rule = current is null
            ? new RulesItem { Remarks = "默认流量", Network = "tcp,udp" }
            : JsonUtils.DeepCopy(current);
        if (await _updateView?.Invoke(EViewAction.RoutingDefaultNodeWindow, rule) != true) return;
        // Replace existing catch-all rules only when the user saves this choice.
        _rules.RemoveAll(RoutingNodeSelection.IsDefaultRule);
        _rules.Add(rule);
        RefreshRulesItems();
        await SaveIndependentRulesAsync();
    }

    public async Task RuleRemoveAsync()
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }
        var selectedIds = (SelectedSources?.Count > 0 ? SelectedSources : [SelectedSource])
            .Select(item => item.Id).ToHashSet();
        if (await _updateView?.Invoke(EViewAction.ShowYesNo, "确定删除选中的分流规则？") != true)
        {
            return;
        }
        _rules.RemoveAll(item => selectedIds.Contains(item.Id));
        RefreshRulesItems();
        await SaveIndependentRulesAsync();
    }

    public async Task RuleExportSelectedAsync()
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }

        var lst = new List<RulesItem>();
        var sources = SelectedSources ?? [SelectedSource];
        foreach (var it in _rules)
        {
            if (sources.Any(t => t.Id == it?.Id))
            {
                var item2 = JsonUtils.DeepCopy(it);
                item2.Id = null;
                lst.Add(item2 ?? new());
            }
        }
        if (lst.Count > 0)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            await _updateView?.Invoke(EViewAction.SetClipboardData, JsonUtils.Serialize(lst, options));
        }
    }

    public async Task MoveRule(EMove eMove)
    {
        if (SelectedSource is null || SelectedSource.OutboundTag.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseSelectRules);
            return;
        }

        var item = _rules.FirstOrDefault(t => t.Id == SelectedSource?.Id);
        if (item == null)
        {
            return;
        }
        var index = _rules.IndexOf(item);
        if (await ConfigHandler.MoveRoutingRule(_rules, index, eMove) == 0)
        {
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
        }
    }

    private async Task SaveIndependentRulesAsync()
    {
        if (!IndependentRules) return;
        var previous = _config.RoutingBasicItem.CustomRules;
        _config.RoutingBasicItem.CustomRules = JsonUtils.DeepCopy(_rules);
        if (await ConfigHandler.SaveConfig(_config) != 0)
        {
            _config.RoutingBasicItem.CustomRules = previous;
            _rules = JsonUtils.DeepCopy(previous ?? []);
            RefreshRulesItems();
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            return;
        }
        AppEvents.ReloadRequested.Publish();
    }

    private async Task SaveRoutingAsync()
    {
        if (IndependentRules)
        {
            await SaveIndependentRulesAsync();
            return;
        }
        var remarks = SelectedRouting.Remarks;
        if (remarks.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.PleaseFillRemarks);
            return;
        }
        var item = SelectedRouting;
        foreach (var it in _rules)
        {
            it.Id = Utils.GetGuid(false);
        }
        item.RuleNum = _rules.Count;
        item.RuleSet = JsonUtils.Serialize(_rules, false);

        if (await ConfigHandler.SaveRoutingItem(_config, item) == 0)
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            AppEvents.RoutingsMenuRefreshRequested.Publish();
            if (item.IsActive) AppEvents.ReloadRequested.Publish();
            await _updateView?.Invoke(EViewAction.CloseWindow, null);
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    #region Import rules

    public async Task ImportRulesFromFileAsync(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        var result = EmbedUtils.LoadResource(fileName);
        if (result.IsNullOrEmpty())
        {
            return;
        }
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, result);
        if (ret == 0)
        {
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    public async Task ImportRulesFromClipboardAsync(string? clipboardData)
    {
        if (clipboardData == null)
        {
            await _updateView?.Invoke(EViewAction.ImportRulesFromClipboard, null);
            return;
        }
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, clipboardData);
        if (ret == 0)
        {
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    private async Task ImportRulesFromUrl()
    {
        var url = SelectedRouting.Url;
        if (url.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.MsgNeedUrl);
            return;
        }

        var downloadHandle = new DownloadService();
        var result = await downloadHandle.TryDownloadString(url, true, "");
        var ret = await AddBatchRoutingRulesAsync(SelectedRouting, result);
        if (ret == 0)
        {
            RefreshRulesItems();
            await SaveIndependentRulesAsync();
            NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
        }
    }

    private async Task<int> AddBatchRoutingRulesAsync(RoutingItem routingItem, string? clipboardData)
    {
        var blReplace = false;
        if (await _updateView?.Invoke(EViewAction.AddBatchRoutingRulesYesNo, null) == false)
        {
            blReplace = true;
        }
        if (clipboardData.IsNullOrEmpty())
        {
            return -1;
        }
        var lstRules = JsonUtils.Deserialize<List<RulesItem>>(clipboardData);
        if (lstRules == null)
        {
            return -1;
        }
        foreach (var rule in lstRules)
        {
            rule.Id = Utils.GetGuid(false);
        }

        if (blReplace)
        {
            _rules = lstRules;
        }
        else
        {
            _rules.AddRange(lstRules);
        }
        return 0;
    }

    #endregion Import rules
}
