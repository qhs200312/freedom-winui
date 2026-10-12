using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServiceLib;
using ServiceLib.Enums;
using ServiceLib.Models.Dto;

namespace v2rayN.WinUI.Views;

public sealed class UpdateNotificationView : UserControl
{
    private readonly InfoBar _bar = new() { IsOpen = true, IsClosable = true, MaxWidth = 400 };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 96, FontSize = 12 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    public event EventHandler? Dismissed;
    public event EventHandler? InstallRequested;
    public event EventHandler? ApplyOnExitRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? ViewRequested;
    public UpdateStage Stage { get; private set; }

    public UpdateNotificationView()
    {
        var body = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(_detail);
        body.Children.Add(_progress);
        body.Children.Add(_actions);
        _bar.Content = body;
        _bar.Closed += (_, _) => Dismissed?.Invoke(this, EventArgs.Empty);
        Content = _bar;
    }

    public void Update(UpdateStatusSnapshot status, bool applyOnExit)
    {
        Stage = status.Stage;
        var app = status.CoreType == ECoreType.v2rayN;
        var name = app ? Global.AppName : status.CoreType == ECoreType.sing_box ? "sing-box" : status.CoreType.ToString();
        var busy = status.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Verifying or UpdateStage.Installing;
        _bar.IsOpen = true;
        _bar.IsClosable = true;
        _bar.Severity = status.Stage switch
        {
            UpdateStage.Failed => InfoBarSeverity.Error,
            UpdateStage.Ready or UpdateStage.Installed or UpdateStage.UpToDate => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
        _bar.Title = name + (status.Stage switch
        {
            UpdateStage.Checking => " · 检查更新",
            UpdateStage.Available => " · 有新版本",
            UpdateStage.Downloading => " · 下载更新",
            UpdateStage.Verifying => " · 校验更新",
            UpdateStage.Ready => " · 下载完成",
            UpdateStage.Installing => " · 安装更新",
            UpdateStage.Installed => " · 更新完成",
            UpdateStage.HandedOff => " · 正在退出",
            UpdateStage.UpToDate => " · 已是最新版本",
            UpdateStage.Cancelled => " · 下载已取消",
            UpdateStage.Unsupported => " · 当前安装不支持自更新",
            UpdateStage.Failed => " · 更新失败",
            _ => string.Empty
        });
        _detail.Text = status.Stage switch
        {
            UpdateStage.Downloading => FormatProgress(status),
            UpdateStage.Ready when applyOnExit => $"{status.Version} · 退出应用后自动安装",
            UpdateStage.Ready => $"{status.Version} · 可以安装",
            UpdateStage.Available => status.Version ?? string.Empty,
            UpdateStage.Failed => status.Error ?? "无法完成更新，请稍后重试。",
            _ => string.Empty
        };
        _detail.Visibility = string.IsNullOrEmpty(_detail.Text) ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy && (status.Stage != UpdateStage.Downloading || status.Percentage is null);
        _progress.Value = Math.Clamp(status.Percentage ?? 0, 0, 100);
        _actions.Children.Clear();
        if (app && status.Stage == UpdateStage.Ready)
        {
            AddAction("安装并重启", Symbol.Download, () => InstallRequested?.Invoke(this, EventArgs.Empty));
            if (!applyOnExit)
                AddAction("退出后安装", Symbol.Clock, () => ApplyOnExitRequested?.Invoke(this, EventArgs.Empty));
        }
        else if (app && status.Stage == UpdateStage.Downloading)
        {
            AddAction("取消", Symbol.Cancel, () => CancelRequested?.Invoke(this, EventArgs.Empty));
        }
        else if (status.Stage is UpdateStage.Available or UpdateStage.Failed or UpdateStage.Cancelled)
        {
            AddAction(app && status.Stage == UpdateStage.Cancelled ? "重试下载" : app ? "查看更新" : "查看组件",
                Symbol.Refresh, () => ViewRequested?.Invoke(this, EventArgs.Empty));
        }
        _actions.Visibility = _actions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddAction(string label, Symbol symbol, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new SymbolIcon(symbol));
        content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content, Padding = new Thickness(10, 5, 10, 5) };
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) =>
        {
            foreach (var item in _actions.Children.OfType<Button>()) item.IsEnabled = false;
            action();
        };
        _actions.Children.Add(button);
    }

    private static string FormatProgress(UpdateStatusSnapshot status)
    {
        if (status.BytesReceived == 0 && status.TotalBytes is null)
            return status.Percentage is { } knownPercent ? $"{knownPercent:0}%" : "正在下载";
        var downloaded = FormatBytes(status.BytesReceived);
        var total = status.TotalBytes is { } bytes ? $" / {FormatBytes(bytes)}" : string.Empty;
        var percent = status.Percentage is { } percentage ? $" · {percentage:0}%" : string.Empty;
        return downloaded + total + percent;
    }

    private static string FormatBytes(long amount)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        double size = Math.Max(0, amount);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.0} {units[unit]}";
    }
}
