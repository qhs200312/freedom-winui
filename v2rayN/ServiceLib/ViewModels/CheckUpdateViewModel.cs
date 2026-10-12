namespace ServiceLib.ViewModels;

public class CheckUpdateViewModel : MyReactiveObject
{
    private const string _geo = "GeoFiles";
    private readonly ECoreType _v2rayN = ECoreType.v2rayN;
    private List<CheckUpdateModel> _lstUpdated = [];
    private static readonly string _tag = "CheckUpdateViewModel";
    private readonly bool _includeAppUpdates;
    private static readonly SemaphoreSlim CoreUpdateGate = new(1, 1);
    private static readonly CancellationTokenSource UpdateLifetime = new();
    public static bool IsUpdating => CoreUpdateGate.CurrentCount == 0;
    private bool _reloadAfterUpdate = true;
    private bool _automaticRun;
    private CancellationToken _updateToken;
    private readonly Func<Task>? _reloadCore;

    public IObservableCollection<CheckUpdateModel> CheckUpdateModels { get; } = new ObservableCollectionExtended<CheckUpdateModel>();
    public ReactiveCommand<Unit, Unit> CheckUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> CheckOnlyCmd { get; }
    [Reactive] public bool EnableCheckPreReleaseUpdate { get; set; }

    public CheckUpdateViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
        : this(updateView, includeAppUpdates: true) { }

    public CheckUpdateViewModel(Func<EViewAction, object?, Task<bool>>? updateView, bool includeAppUpdates)
        : this(updateView, includeAppUpdates, null) { }

    public CheckUpdateViewModel(Func<EViewAction, object?, Task<bool>>? updateView, bool includeAppUpdates,
        Func<Task>? reloadCore)
    {
        _reloadCore = reloadCore;
        _includeAppUpdates = includeAppUpdates;
        _config = AppManager.Instance.Config;
        _updateView = updateView;

        CheckUpdateCmd = ReactiveCommand.CreateFromTask(CheckUpdate);
        CheckUpdateCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
            _ = UpdateView(_v2rayN, ex.Message);
        });

        CheckOnlyCmd = ReactiveCommand.CreateFromTask(CheckOnly);
        CheckOnlyCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
            _ = UpdateView(_v2rayN, ex.Message);
        });

        EnableCheckPreReleaseUpdate = _config.CheckUpdateItem.CheckPreReleaseUpdate;

        this.WhenAnyValue(
        x => x.EnableCheckPreReleaseUpdate,
        y => y == true)
            .Subscribe(c => _ = OnCheckPreReleaseUpdateChanged());

        RefreshCheckUpdateItems();
    }

    private void RefreshCheckUpdateItems()
    {
        var models = CoreInfoManager.Instance.GetCheckUpdateCoreTypes()
                        .Where(t => _includeAppUpdates || t != _v2rayN)
                        .Select(t => GetCheckUpdateModel(t))
                        .ToList();

        models.Add(GetGeoFileCheckUpdateModel());

        CheckUpdateModels.Clear();
        CheckUpdateModels.AddRange(models);
    }

    private CheckUpdateModel GetCheckUpdateModel(ECoreType coreType)
    {
        if (coreType == _v2rayN && Utils.IsPackagedInstall())
        {
            return new()
            {
                IsSelected = false,
                CoreType = coreType,
                IsGeoFile = false,
                Remarks = ResUI.menuCheckUpdate + $" ({ResUI.MsgNotSupport})",
            };
        }

        AppManager.Instance.LastCheckUpdateResults.TryGetValue(coreType, out var lastResult);
        return new()
        {
            IsSelected = _config.CheckUpdateItem.SelectedCoreTypes?.Contains(coreType.ToString()) ?? true,
            CoreType = coreType,
            IsGeoFile = false,
            Remarks = lastResult ?? ResUI.menuCheckUpdate,
        };
    }

    private CheckUpdateModel GetGeoFileCheckUpdateModel()
    {
        return new()
        {
            IsSelected = _config.CheckUpdateItem.SelectedCoreTypes?.Contains(_geo) ?? true,
            CoreType = null,
            IsGeoFile = true,
            Remarks = ResUI.menuCheckUpdate,
        };
    }

    private async Task OnCheckPreReleaseUpdateChanged()
    {
        if (_config.CheckUpdateItem.CheckPreReleaseUpdate == EnableCheckPreReleaseUpdate)
        {
            return;
        }
        _config.CheckUpdateItem.CheckPreReleaseUpdate = EnableCheckPreReleaseUpdate;
        await SaveSelectedCoreTypes();
    }

    private async Task SaveSelectedCoreTypes()
    {
        var preserveAppSelection = !_includeAppUpdates
            && (_config.CheckUpdateItem.SelectedCoreTypes?.Contains(_v2rayN.ToString()) ?? true);
        _config.CheckUpdateItem.SelectedCoreTypes =
            CheckUpdateModels.Where(t => t.IsSelected == true)
                            .Select(t => t.CoreTypeForStorage)
                            .ToList();
        if (preserveAppSelection) _config.CheckUpdateItem.SelectedCoreTypes.Add(_v2rayN.ToString());

        await ConfigHandler.SaveConfig(_config);
    }

    private async Task CheckOnly()
    {
        await Task.Run(CheckOnlyTask);
    }

    private async Task CheckUpdate()
    {
        await CoreUpdateGate.WaitAsync(UpdateLifetime.Token);
        try
        {
            _automaticRun = false;
            _updateToken = UpdateLifetime.Token;
            _reloadAfterUpdate = true;
            await Task.Run(CheckUpdateTask);
        }
        finally { CoreUpdateGate.Release(); }
    }

    public async Task AutoUpdateCoresAsync(IEnumerable<ECoreType> availableTypes, CancellationToken cancellationToken = default)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, UpdateLifetime.Token);
        await CoreUpdateGate.WaitAsync(operation.Token);
        try
        {
            _automaticRun = true;
            _updateToken = operation.Token;
            if (!_config.CheckUpdateItem.AutoUpdateCores) return;
            var selected = availableTypes.Distinct()
                .Where(type => type != _v2rayN && CoreInfoManager.Instance.IsCheckUpdateSupported(type))
                .Where(type => _config.CheckUpdateItem.SelectedCoreTypes?.Contains(type.ToString()) ?? true)
                .Select(type => new CheckUpdateModel { CoreType = type }).ToList();
            if (selected.Count == 0) return;
            _lstUpdated = selected;
            foreach (var item in selected)
            {
                _updateToken.ThrowIfCancellationRequested();
                await CheckUpdateCore(item, CoreInfoManager.Instance.GetCheckPreRelease(
                    item.CoreType!.Value, _config.CheckUpdateItem.CheckPreReleaseUpdate));
            }
            _updateToken.ThrowIfCancellationRequested();
            await UpdateFinished();
        }
        finally { CoreUpdateGate.Release(); }
    }

    public static async Task StopUpdatesAsync()
    {
        UpdateLifetime.Cancel();
        await CoreUpdateGate.WaitAsync();
        CoreUpdateGate.Release();
    }

    private async Task CheckOnlyTask()
    {
        await SaveSelectedCoreTypes();

        for (var k = CheckUpdateModels.Count - 1; k >= 0; k--)
        {
            var item = CheckUpdateModels[k];
            if (item.IsSelected != true || (!_includeAppUpdates && item.CoreType == _v2rayN))
            {
                continue;
            }

            await UpdateView(item.CoreType, "...");

            if (item.IsGeoFile || item.CoreType == null)
            {
                await UpdateView(item.CoreType, ResUI.menuCheckOnly + $" ({ResUI.MsgNotSupport})");
                continue;
            }

            if (item.CoreType == null)
            {
                await UpdateView(item.CoreType, ResUI.MsgNotSupport);
                continue;
            }

            var updateService = new UpdateService(_config, async (success, msg) => await Task.CompletedTask);
            var result = await updateService.CheckHasUpdateOnly(item.CoreType.Value, EnableCheckPreReleaseUpdate);
            if (result.Success && result.Version != null)
            {
                await UpdateView(item.CoreType, string.Format(ResUI.MsgCheckUpdateHasNewVersion, item.DisplayName, result.Version));
            }
            else
            {
                await UpdateView(item.CoreType, result.Msg);
            }
        }
    }

    private async Task CheckUpdateTask()
    {
        _lstUpdated.Clear();
        _lstUpdated = CheckUpdateModels
            .Where(x => x.IsSelected == true && (_includeAppUpdates || x.CoreType != _v2rayN))
            .Select(x => new CheckUpdateModel()
            {
                CoreType = x.CoreType,
                IsGeoFile = x.IsGeoFile
            })
            .ToList();
        await SaveSelectedCoreTypes();

        for (var k = CheckUpdateModels.Count - 1; k >= 0; k--)
        {
            _updateToken.ThrowIfCancellationRequested();
            var item = CheckUpdateModels[k];
            if (item.IsSelected != true || (!_includeAppUpdates && item.CoreType == _v2rayN))
            {
                continue;
            }

            await UpdateView(item.CoreType, "...");

            if (item.IsGeoFile)
            {
                await CheckUpdateGeo();
            }
            else if (item.CoreType == _v2rayN)
            {
                if (Utils.IsPackagedInstall())
                {
                    await UpdateView(_v2rayN, ResUI.MsgNotSupport);
                    continue;
                }
                await CheckUpdateN(EnableCheckPreReleaseUpdate);
            }
            else if (item.CoreType == ECoreType.Xray)
            {
                await CheckUpdateCore(item, EnableCheckPreReleaseUpdate);
            }
            else if (item.CoreType.HasValue)
            {
                await CheckUpdateCore(item, false);
            }
        }

        await UpdateFinished();
    }

    private void UpdatedPlusPlus(ECoreType? coreType, string fileName)
    {
        var item = _lstUpdated.FirstOrDefault(x => x.CoreType == coreType);
        if (item == null)
        {
            return;
        }
        item.IsFinished = true;
        if (!fileName.IsNullOrEmpty())
        {
            item.FileName = fileName;
        }
    }

    private async Task CheckUpdateGeo()
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(null, msg);
            if (success)
            {
                UpdatedPlusPlus(null, "");
            }
        }
        await new UpdateService(_config, _updateUI).UpdateGeoFileAll()
            .ContinueWith(t => UpdatedPlusPlus(null, ""));
    }

    private async Task CheckUpdateN(bool preRelease)
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(_v2rayN, msg);
            if (success)
            {
                await UpdateView(_v2rayN, ResUI.OperationSuccess);
                UpdatedPlusPlus(_v2rayN, msg);
            }
        }
        await new UpdateService(_config, _updateUI).CheckUpdateGuiN(preRelease)
            .ContinueWith(t => UpdatedPlusPlus(_v2rayN, ""));
    }

    private async Task CheckUpdateCore(CheckUpdateModel model, bool preRelease)
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(model.CoreType, msg);
            if (success)
            {
                await UpdateView(model.CoreType, ResUI.MsgUpdateV2rayCoreSuccessfullyMore);
                UpdatedPlusPlus(model.CoreType, msg);
            }
        }

        if (model.CoreType.HasValue)
        {
            await new UpdateService(_config, _updateUI).CheckUpdateCore(model.CoreType.Value, preRelease, _updateToken);
            UpdatedPlusPlus(model.CoreType, "");
        }
    }

    private async Task UpdateFinished()
    {
        if (_lstUpdated.Count > 0 && _lstUpdated.Count(x => x.IsFinished == true) == _lstUpdated.Count)
        {
            if (!_lstUpdated.Any(item => item.IsGeoFile || !string.IsNullOrEmpty(item.FileName)))
                return;
            var prepared = new List<(CheckUpdateModel Item, CoreArchiveInstaller.PreparedUpdate Archive)>();
            var stopped = false;
            CheckUpdateModel? preparing = null;
            try
            {
                foreach (var item in _lstUpdated.Where(item => !item.IsGeoFile && item.CoreType != _v2rayN && !string.IsNullOrEmpty(item.FileName)))
                {
                    preparing = item;
                    _updateToken.ThrowIfCancellationRequested();
                    var core = CoreInfoManager.Instance.GetCoreInfo(item.CoreType!.Value);
                    var names = core.CoreExes!.Select(name => Utils.GetExeName(name)).ToArray();
                    var target = Utils.GetBinPath("", item.CoreType.Value.ToString());
                    var archive = await Task.Run(() => CoreArchiveInstaller.Prepare(item.FileName!, target, names), _updateToken);
                    prepared.Add((item, archive));
                    preparing = null;
                }
                _updateToken.ThrowIfCancellationRequested();
                if (_automaticRun && !_config.CheckUpdateItem.AutoUpdateCores) return;
                // Evaluate running intent after downloads, immediately before stopping the core.
                _reloadAfterUpdate = !_automaticRun || Enum.IsDefined(AppManager.Instance.RunningCoreType);
                if (prepared.Count > 0 || _lstUpdated.Any(item => item.IsGeoFile))
                {
                    await UpdateFinishedResult(false);
                    stopped = true;
                }
                foreach (var (item, archive) in prepared)
                {
                    AppEvents.UpdateStatusChanged.Publish(new(item.CoreType!.Value, UpdateStage.Installing));
                    try
                    {
                        archive.Apply();
                        if (Utils.IsNonWindows())
                            foreach (var name in CoreInfoManager.Instance.GetCoreInfo(item.CoreType.Value).CoreExes!)
                            {
                                var file = Utils.GetBinPath(Utils.GetExeName(name), item.CoreType.Value.ToString());
                                if (File.Exists(file)) await Utils.SetLinuxChmod(file);
                            }
                        await UpdateView(item.CoreType, ResUI.MsgUpdateV2rayCoreSuccessfully);
                        try { File.Delete(item.FileName!); }
                        catch (IOException ex) { Logging.SaveLog("Core update package cleanup", ex); }
                        AppEvents.UpdateStatusChanged.Publish(new(item.CoreType.Value, UpdateStage.Installed));
                    }
                    catch (Exception ex)
                    {
                        if (ex is CoreArchiveRollbackException) _reloadAfterUpdate = false;
                        AppEvents.UpdateStatusChanged.Publish(new(item.CoreType.Value, UpdateStage.Failed, Error: ex.Message));
                        throw;
                    }
                }
                if (_lstUpdated.Any(x => x.CoreType == _v2rayN && !string.IsNullOrEmpty(x.FileName)))
                {
                    _updateToken.ThrowIfCancellationRequested();
                    if (await UpgradeN()) { stopped = false; return; }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (preparing?.CoreType is { } failedType)
                    AppEvents.UpdateStatusChanged.Publish(new(failedType, UpdateStage.Failed, Error: ex.Message));
                throw;
            }
            finally
            {
                foreach (var (_, archive) in prepared) archive.Dispose();
                if (stopped && _reloadAfterUpdate && !UpdateLifetime.IsCancellationRequested && !_updateToken.IsCancellationRequested)
                    await UpdateFinishedSub(true);
            }
        }
    }

    private async Task UpdateFinishedSub(bool blReload)
    {
        if (blReload && _reloadCore != null)
        {
            await _reloadCore();
            return;
        }
        RxSchedulers.MainThreadScheduler.Schedule(blReload, (scheduler, blReload) =>
        {
            _ = UpdateFinishedResult(blReload);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task UpdateFinishedResult(bool blReload)
    {
        if (blReload)
        {
            AppEvents.ReloadRequested.Publish();
        }
        else
        {
            await CoreManager.Instance.CoreStop();
        }
    }

    private async Task<bool> UpgradeN()
    {
        try
        {
            var fileName = _lstUpdated.FirstOrDefault(x => x.CoreType == _v2rayN)?.FileName;
            if (fileName.IsNullOrEmpty())
            {
                return false;
            }
            if (!Utils.UpgradeAppExists(out var upgradeFileName))
            {
                await UpdateView(_v2rayN, ResUI.UpgradeAppNotExistTip);
                NoticeManager.Instance.SendMessageAndEnqueue(ResUI.UpgradeAppNotExistTip);
                Logging.SaveLog("UpgradeApp does not exist");
                return false;
            }

            var helper = AppUpdatePreparation.PrepareHelper(upgradeFileName, Utils.GetTempPath());
            var arguments = new List<string>
            {
                "upgrade", fileName, Environment.ProcessId.ToString(), Utils.GetBaseDirectory()
            };
            if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
                arguments.Add(Global.UseLocalAppDataArgument);
            var id = ProcUtils.ProcessStart(helper, arguments, Path.GetDirectoryName(helper));
            if (id > 0)
            {
                await AppManager.Instance.AppExitAsync(true);
                return true;
            }
        }
        catch (Exception ex)
        {
            await UpdateView(_v2rayN, ex.Message);
        }
        return false;
    }

    private async Task UpdateView(ECoreType? coreType, string msg)
    {
        var item = new CheckUpdateModel()
        {
            CoreType = coreType,
            IsGeoFile = coreType == null,
            Remarks = msg,
        };

        RxSchedulers.MainThreadScheduler.Schedule(item, (scheduler, model) =>
        {
            _ = UpdateViewResult(model);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task UpdateViewResult(CheckUpdateModel model)
    {
        var found = CheckUpdateModels.FirstOrDefault(t => t.CoreType == model.CoreType && t.IsGeoFile == model.IsGeoFile);
        if (found == null)
        {
            return;
        }
        found.Remarks = model.Remarks;
        await Task.CompletedTask;
    }
}
