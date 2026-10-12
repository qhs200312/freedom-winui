using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Reactive.Linq;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Models.Dto;
using ServiceLib.ViewModels;

namespace v2rayN.WinUI.Views;

public sealed partial class UpdateManagerView : UserControl
{
    private readonly CheckUpdateViewModel _viewModel;
    private IDisposable? _statusSubscription;
    private readonly object _progressLock = new();
    private readonly Dictionary<ECoreType, long> _lastProgress = [];
    private readonly Func<Task>? _checkApplicationUpdate;
    private readonly Func<UpdateStatusSnapshot>? _applicationStatus;
    private readonly Func<CheckUpdateModel, Task> _updateComponent;
    private bool _applicationActionRunning;
    private bool _componentActionRunning;

    public UpdateManagerView(CheckUpdateViewModel viewModel, Func<Task>? checkApplicationUpdate = null,
        Func<UpdateStatusSnapshot>? applicationStatus = null,
        Func<IReadOnlyList<UpdateStatusSnapshot>>? componentStatuses = null,
        Func<CheckUpdateModel, Task>? updateComponent = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _checkApplicationUpdate = checkApplicationUpdate;
        _applicationStatus = applicationStatus;
        _updateComponent = updateComponent ?? viewModel.UpdateSingleAsync;
        DataContext = viewModel;
        AppVersionText.Text = Utils.GetVersionInfo();
        if (applicationStatus != null) UpdateAppStatus(applicationStatus());
        Loaded += (_, _) =>
        {
            _statusSubscription?.Dispose();
            if (componentStatuses != null)
                foreach (var status in componentStatuses()) UpdateStatus(status);
            if (applicationStatus != null) UpdateAppStatus(applicationStatus());
            _statusSubscription = AppEvents.UpdateStatusChanged.AsObservable().Subscribe(status =>
            {
                if (status.Stage == UpdateStage.Downloading)
                {
                    lock (_progressLock)
                    {
                        var now = Environment.TickCount64;
                        if (_lastProgress.TryGetValue(status.CoreType, out var previous) && now - previous < 100) return;
                        _lastProgress[status.CoreType] = now;
                    }
                }
                DispatcherQueue.TryEnqueue(() => UpdateStatus(status));
            });
        };
        Unloaded += (_, _) => { _statusSubscription?.Dispose(); _statusSubscription = null; };
        PrereleaseButton.IsChecked = viewModel.EnableCheckPreReleaseUpdate;
        PrereleaseButton.Click += (_, _) => viewModel.EnableCheckPreReleaseUpdate = PrereleaseButton.IsChecked == true;
        CheckApplicationButton.IsEnabled = checkApplicationUpdate != null;
        CheckApplicationButton.Click += async (_, _) => await CheckApplicationAsync();
    }

    private async Task CheckApplicationAsync()
    {
        if (_checkApplicationUpdate == null || _applicationActionRunning || !CheckApplicationButton.IsEnabled) return;
        _applicationActionRunning = true;
        CheckApplicationButton.IsEnabled = false;
        try { await _checkApplicationUpdate(); }
        finally
        {
            _applicationActionRunning = false;
            if (_applicationStatus != null) UpdateAppStatus(_applicationStatus());
            else CheckApplicationButton.IsEnabled = true;
        }
    }

    private async void ApplicationRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsButtonSource(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        await CheckApplicationAsync();
    }

    private async void ComponentRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckUpdateModel model }
            || IsButtonSource(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        if (_componentActionRunning) return;
        _componentActionRunning = true;
        try { await _updateComponent(model); }
        catch (Exception ex)
        {
            await _viewModel.UpdateViewResult(new CheckUpdateModel
            {
                CoreType = model.CoreType, IsGeoFile = model.IsGeoFile, Remarks = ex.Message
            });
        }
        finally { _componentActionRunning = false; }
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        for (var element = source; element != null; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return true;
        return false;
    }

    private void UpdateStatus(UpdateStatusSnapshot status)
    {
        if (status.CoreType == ECoreType.v2rayN) { UpdateAppStatus(status); return; }
        if (status.Stage is UpdateStage.Checking or UpdateStage.Idle) return;
        _ = _viewModel.UpdateViewResult(new CheckUpdateModel
        {
            CoreType = status.CoreType,
            Remarks = status.Stage switch
            {
                UpdateStage.Available => $"有新版本 {status.Version}",
                UpdateStage.Downloading => $"正在下载 {status.Version} {status.Percentage:0}%",
                UpdateStage.Ready => "下载完成",
                UpdateStage.Installing => "安装中",
                UpdateStage.Installed => "更新完成",
                UpdateStage.Failed => status.Error ?? "更新失败",
                UpdateStage.Cancelled => "已取消",
                UpdateStage.UpToDate => "已是最新版本",
                _ => status.Stage.ToString()
            }
        });
    }

    private void UpdateAppStatus(UpdateStatusSnapshot status)
    {
        if (status.Stage == UpdateStage.Checking) return;
        AppUpdateStatusText.Text = status.Stage switch
        {
            UpdateStage.Available => $"新版本 {status.Version}",
            UpdateStage.Downloading => $"正在下载 {status.Version} {status.Percentage:0}%",
            UpdateStage.Verifying => "校验更新包",
            UpdateStage.Ready => $"已下载 {status.Version}，可以安装",
            UpdateStage.UpToDate => "已是最新版本",
            UpdateStage.Failed => status.Error ?? "检查更新失败",
            UpdateStage.HandedOff => "等待退出后安装",
            UpdateStage.Unsupported => "当前安装不支持自更新",
            UpdateStage.Cancelled => "下载已取消",
            _ => "等待检查"
        };
        CheckApplicationButton.IsEnabled = !_applicationActionRunning
            && _checkApplicationUpdate != null
            && status.Stage is not (UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.HandedOff);
        var label = status.Stage == UpdateStage.Ready ? "安装软件更新"
            : status.Stage == UpdateStage.Available ? "查看软件更新" : "检查软件更新";
        CheckApplicationButton.Content = new SymbolIcon(status.Stage == UpdateStage.Ready ? Symbol.Download : Symbol.Refresh);
        ToolTipService.SetToolTip(CheckApplicationButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CheckApplicationButton, label);
    }
}
