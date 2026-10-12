using System.Reactive.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Services;
using ServiceLib.ViewModels;
using v2rayN.WinUI.Views;

namespace v2rayN.WinUI.Services;

public sealed class WinUIUpdateController : IDisposable
{
    private readonly Window _window;
    private readonly StackPanel _host;
    private readonly AppUpdateService _app;
    private readonly Func<bool> _canShowDialog;
    private readonly Func<Task> _exit;
    private readonly Action _showComponents;
    private readonly Func<Task>? _reloadCore;
    private readonly Dictionary<ECoreType, UpdateNotificationView> _notifications = [];
    private readonly Dictionary<ECoreType, DispatcherTimer> _dismissTimers = [];
    private readonly Dictionary<ECoreType, UpdateStage> _dismissedStages = [];
    private readonly Dictionary<ECoreType, UpdateStatusSnapshot> _latestStatuses = [];
    private readonly Dictionary<ECoreType, UpdateStatusSnapshot> _pendingStatuses = [];
    private readonly object _statusLock = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _subscription;
    private bool _dialogOpen;
    private bool _checkingApp;
    private bool _checkingCores;
    private bool _lastAutoUpdate;
    private DateTimeOffset _lastCoreCheck;
    private readonly DispatcherTimer _coreCheckTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _disposed;
    private bool _exiting;
    private bool _statusFlushQueued;
    private Task? _exitPreparation;
    private Task? _coreInstallTask;
    public UpdateStatusSnapshot ApplicationStatus => _app.Status;
    public IReadOnlyList<UpdateStatusSnapshot> ComponentStatuses => _latestStatuses.Values.ToArray();

    public WinUIUpdateController(Window window, StackPanel host, AppUpdateService app,
        Func<bool> canShowDialog, Func<Task> exit, Action showComponents, Func<Task>? reloadCore = null)
    {
        _window = window;
        _host = host;
        _app = app;
        _canShowDialog = canShowDialog;
        _exit = exit;
        _showComponents = showComponents;
        _reloadCore = reloadCore;
        TaskManager.Instance.CoreUpdateChecksManagedExternally = true;
        _statusTimer.Tick += (_, _) => FlushStatuses();
        _subscription = AppEvents.UpdateStatusChanged.AsObservable().Subscribe(QueueStatus);
        _coreCheckTimer.Tick += async (_, _) =>
        {
            var automatic = AppManager.Instance.Config.CheckUpdateItem.AutoUpdateCores;
            var justEnabled = automatic && !_lastAutoUpdate;
            _lastAutoUpdate = automatic;
            if (justEnabled || DateTimeOffset.UtcNow - _lastCoreCheck >= TimeSpan.FromDays(1))
                await CheckCoreUpdatesAsync();
        };
        _coreCheckTimer.Start();
    }

    private void QueueStatus(UpdateStatusSnapshot status)
    {
        if (status.Stage == UpdateStage.Checking)
        {
            _window.DispatcherQueue.TryEnqueue(() => _dismissedStages.Remove(status.CoreType));
            return;
        }
        lock (_statusLock)
        {
            if (_disposed) return;
            _pendingStatuses[status.CoreType] = status;
            if (_statusFlushQueued) return;
            _statusFlushQueued = true;
        }
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) _statusTimer.Start();
        });
    }

    private void FlushStatuses()
    {
        _statusTimer.Stop();
        UpdateStatusSnapshot[] statuses;
        lock (_statusLock)
        {
            statuses = _pendingStatuses.Values.ToArray();
            _pendingStatuses.Clear();
            _statusFlushQueued = false;
        }
        foreach (var status in statuses) HandleStatus(status);
    }

    public async Task CheckOnStartupAsync()
    {
        await Task.WhenAll(CheckForAppUpdateAsync(false), CheckCoreUpdatesAsync());
    }

    public async Task CheckForAppUpdateAsync(bool manual = true)
    {
        if (_disposed || _exiting || _checkingApp ||
            _app.Status.Stage is UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.HandedOff) return;
        _checkingApp = true;
        try
        {
            if (manual && _app.AvailableRelease != null &&
                _app.Status.Stage is UpdateStage.Ready or UpdateStage.Available or UpdateStage.Cancelled or UpdateStage.Failed)
            {
                await ShowAppDialogAsync();
                return;
            }
            var release = await _app.CheckAsync(AppManager.Instance.Config.CheckUpdateItem.CheckPreReleaseUpdate, _lifetime.Token);
            if (_disposed || _exiting) return;
            if (release != null && _app.Status.Stage is UpdateStage.Available or UpdateStage.Ready)
                await ShowAppDialogAsync();
            else if (manual)
                ShowNotification(_app.Status, force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logging.SaveLog("WinUI update check", ex);
            ShowNotification(new(ECoreType.v2rayN, UpdateStage.Failed, Error: ex.Message), force: true);
        }
        finally { _checkingApp = false; }
    }

    private async Task CheckCoreUpdatesAsync()
    {
        if (_checkingCores || _disposed || _exiting || CheckUpdateViewModel.IsUpdating) return;
        _checkingCores = true;
        var config = AppManager.Instance.Config;
        var available = new List<ECoreType>();
        try
        {
            _lastAutoUpdate = config.CheckUpdateItem.AutoUpdateCores;
            _lastCoreCheck = DateTimeOffset.UtcNow;
            foreach (var type in CoreInfoManager.Instance.GetCheckUpdateCoreTypes().Where(type => type != ECoreType.v2rayN))
            {
                if (_disposed || _exiting) return;
                if (!(config.CheckUpdateItem.SelectedCoreTypes?.Contains(type.ToString()) ?? true)) continue;
                try
                {
                    var result = await new UpdateService(config, (_, _) => Task.CompletedTask)
                        .CheckHasUpdateOnly(type, config.CheckUpdateItem.CheckPreReleaseUpdate);
                    if (result.Success) available.Add(type);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("WinUI core update check", ex);
                    ShowNotification(new(type, UpdateStage.Failed, Error: ex.Message));
                }
            }
            if (!_disposed && !_exiting && config.CheckUpdateItem.AutoUpdateCores && available.Count > 0)
            {
                _coreInstallTask = new CheckUpdateViewModel(null, includeAppUpdates: false, reloadCore: _reloadCore)
                    .AutoUpdateCoresAsync(available, _lifetime.Token);
                await _coreInstallTask;
            }
        }
        catch (Exception ex) { Logging.SaveLog("WinUI automatic core update", ex); }
        finally { _checkingCores = false; _coreInstallTask = null; }
    }

    private async Task ShowAppDialogAsync()
    {
        if (_dialogOpen || _disposed || _exiting || _app.AvailableRelease is not { } release) return;
        if (!_canShowDialog() || _window.Content.XamlRoot is not { } root)
        {
            ShowNotification(_app.Status);
            return;
        }
        _dialogOpen = true;
        var ready = _app.PendingUpdate is { } pending && pending.Release.Version == release.Version
            && pending.Release.Url == release.Url;
        ContentDialogResult result;
        try
        {
            var dialog = AppUpdateDialog.Create(root, Utils.GetVersionInfo(), release, ready);
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WinUI update dialog", ex);
            ShowNotification(_app.Status);
            return;
        }
        finally { _dialogOpen = false; }
        if (_disposed || _exiting) return;
        if (result == ContentDialogResult.Primary)
        {
            if (ready) await RunActionAsync(InstallNowAsync);
            else await RunActionAsync(() => DownloadAsync(false));
        }
        else if (result == ContentDialogResult.Secondary)
        {
            if (ready) await RunActionAsync(EnableApplyOnExitAsync);
            else await RunActionAsync(() => DownloadAsync(true));
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logging.SaveLog("WinUI update action", ex);
            ShowNotification(new(ECoreType.v2rayN, UpdateStage.Failed, Error: ex.Message));
        }
    }

    private async Task DownloadAsync(bool background)
    {
        var pending = await _app.DownloadAsync(applyOnExit: background, cancellationToken: _lifetime.Token);
        if (pending == null || _disposed || _exiting) return;
        ShowNotification(_app.Status);
        if (!background) await ShowAppDialogAsync();
    }

    private async Task EnableApplyOnExitAsync()
    {
        if (await _app.SetApplyOnExitAsync(true, _lifetime.Token))
            ShowNotification(_app.Status, force: true);
    }

    private async Task InstallNowAsync()
    {
        if (await _app.HandoffAsync(true, _lifetime.Token))
            await _exit();
    }

    public Task PrepareForExitAsync()
    {
        if (_disposed) return Task.CompletedTask;
        return _exitPreparation ??= PrepareForExitCoreAsync();
    }

    private async Task PrepareForExitCoreAsync()
    {
        _exiting = true;
        _app.Cancel();
        _lifetime.Cancel();
        var stopping = CheckUpdateViewModel.StopUpdatesAsync();
        if (_coreInstallTask != null)
        {
            try { await _coreInstallTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logging.SaveLog("WinUI core update during exit", ex); }
        }
        await stopping;
        if (_app.PendingUpdate?.ApplyOnExit == true)
            await _app.HandoffAsync(false);
    }

    private void HandleStatus(UpdateStatusSnapshot status)
    {
        if (_disposed || _exiting) return;
        _latestStatuses[status.CoreType] = status;
        if (status.Stage is UpdateStage.Idle or UpdateStage.Checking or UpdateStage.UpToDate) return;
        if (status.CoreType == ECoreType.v2rayN &&
            status.Stage is UpdateStage.Idle or UpdateStage.Checking or UpdateStage.Available or UpdateStage.UpToDate or UpdateStage.Unsupported) return;
        ShowNotification(status);
    }

    private void ShowNotification(UpdateStatusSnapshot status, bool force = false)
    {
        if (_disposed || _exiting || status.Stage is UpdateStage.Idle or UpdateStage.Checking) return;
        var type = status.CoreType;
        if (!force && _dismissedStages.GetValueOrDefault(type) == status.Stage) return;
        _dismissedStages.Remove(type);
        var newStage = !_notifications.TryGetValue(type, out var existing) || existing.Stage != status.Stage;
        if (!_notifications.TryGetValue(type, out var view))
        {
            view = new UpdateNotificationView();
            view.Dismissed += (_, _) => RemoveNotification(type);
            view.InstallRequested += async (_, _) => await RunActionAsync(InstallNowAsync);
            view.ApplyOnExitRequested += async (_, _) => await RunActionAsync(EnableApplyOnExitAsync);
            view.CancelRequested += (_, _) => _app.Cancel();
            view.ViewRequested += async (_, _) =>
            {
                if (type == ECoreType.v2rayN) await CheckForAppUpdateAsync();
                else _showComponents();
            };
            _notifications[type] = view;
            _host.Children.Add(view);
        }
        view.Update(status, _app.PendingUpdate?.ApplyOnExit == true);
        if (newStage || force)
        {
            if (_dismissTimers.Remove(type, out var oldTimer)) oldTimer.Stop();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) => RemoveNotification(type);
            _dismissTimers[type] = timer;
            timer.Start();
        }
    }

    private void RemoveNotification(ECoreType type)
    {
        if (_dismissTimers.Remove(type, out var timer)) timer.Stop();
        if (_notifications.Remove(type, out var view))
        {
            _dismissedStages[type] = view.Stage;
            _host.Children.Remove(view);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.Cancel();
        _lifetime.Cancel();
        _subscription.Dispose();
        _statusTimer.Stop();
        _coreCheckTimer.Stop();
        foreach (var timer in _dismissTimers.Values) timer.Stop();
        _lifetime.Dispose();
    }
}
