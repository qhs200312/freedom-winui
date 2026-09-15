using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.Configs;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.Resx;
using ServiceLib.ViewModels;
using v2rayN.WinUI.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace v2rayN.WinUI.Services;

public sealed class WinUIPlatformService
{
    private readonly Window _window;
    private readonly Stack<DialogContext> _dialogs = [];
    private readonly object _logLock = new();
    private readonly StringBuilder _logBuffer = new();
    private Action<string>? _logSink;

    public MainWindowViewModel? MainViewModel { get; set; }
    public ProfilesViewModel? ProfilesViewModel { get; set; }
    public Action? FocusProfiles { get; set; }
    public Action<string>? ShowMessage { get; set; }
    public Action<string>? LogSink
    {
        get => _logSink;
        set
        {
            string buffered;
            lock (_logLock)
            {
                _logSink = value;
                buffered = _logBuffer.ToString();
            }
            if (value is not null && buffered.Length > 0)
            {
                value(buffered);
            }
        }
    }

    public WinUIPlatformService(Window window)
    {
        _window = window;
    }

    public async Task<bool> HandleAsync(EViewAction action, object? value)
    {
        switch (action)
        {
            case EViewAction.CloseWindow:
                if (_dialogs.TryPeek(out var closeContext))
                {
                    if (closeContext.CompleteInline(true))
                    {
                        return true;
                    }
                    closeContext.Saved = true;
                    closeContext.Dialog.Hide();
                }
                return true;

            case EViewAction.ShowYesNo:
                return await ConfirmAsync(ResUI.RemoveServer);

            case EViewAction.AddBatchRoutingRulesYesNo:
                return await ConfirmAsync(ResUI.AddBatchRoutingRulesYesNo);

            case EViewAction.SetClipboardData:
                SetClipboardText(value?.ToString());
                return true;

            case EViewAction.AddServerViaClipboard:
                if (MainViewModel is not null && await GetClipboardTextAsync() is { Length: > 0 } clipboardServer)
                {
                    await MainViewModel.AddServerViaClipboardAsync(clipboardServer);
                }
                return true;

            case EViewAction.ScanScreenTask:
                if (MainViewModel is not null)
                {
                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                    ShowWindowNative(handle, 0);
                    await Task.Delay(300);
                    var capture = CaptureDesktop();
                    ShowWindowNative(handle, 9);
                    await MainViewModel.ScanScreenResult(capture);
                }
                return true;

            case EViewAction.ScanImageTask:
                if (MainViewModel is not null && await PickOpenFileAsync([".png", ".jpg", ".jpeg", ".bmp"]) is { } imagePath)
                {
                    await MainViewModel.ScanImageResult(imagePath);
                }
                return true;

            case EViewAction.SaveFileDialog:
                if (ProfilesViewModel is not null && value is ProfileItem profile && await PickSaveFileAsync("config.json", ".json") is { } configPath)
                {
                    await ProfilesViewModel.Export2ClientConfigResult(configPath, profile);
                    return true;
                }
                return false;

            case EViewAction.ProfilesFocus:
                FocusProfiles?.Invoke();
                return true;

            case EViewAction.DispatcherRefreshServersBiz:
                FocusProfiles?.Invoke();
                return true;

            case EViewAction.ShareServer:
            case EViewAction.ShareSub:
                await ShowQrCodeAsync(value?.ToString());
                return true;

            case EViewAction.AddServerWindow when value is ProfileItem server:
                return await ShowFormDialogAsync(GetServerTitle(server), new AddServerViewModel(server, HandleAsync));

            case EViewAction.AddServer2Window when value is ProfileItem customServer:
                return await ShowFormDialogAsync("自定义配置", new AddServer2ViewModel(customServer, HandleAsync));

            case EViewAction.AddGroupServerWindow when value is ProfileItem groupServer:
                return await ShowGroupDialogAsync(groupServer);

            case EViewAction.SubEditWindow when value is SubItem subscription:
                return await ShowFormDialogAsync("订阅编辑", new SubEditViewModel(subscription, HandleAsync));

            case EViewAction.SubSettingWindow:
                return await ShowCollectionDialogAsync("订阅设置", new SubSettingViewModel(HandleAsync), "SubItems", "SelectedSource");

            case EViewAction.OptionSettingWindow:
                return await ShowFormDialogAsync("参数设置", new OptionSettingViewModel(HandleAsync), 1120);

            case EViewAction.DNSSettingWindow:
                return await ShowFormDialogAsync("DNS 设置", new DNSSettingViewModel(HandleAsync), 1080);

            case EViewAction.FullConfigTemplateWindow:
                return await ShowFormDialogAsync("完整配置模板", new FullConfigTemplateViewModel(HandleAsync), 1120);

            case EViewAction.GlobalHotkeySettingWindow:
                return await ShowFormDialogAsync("全局热键", new GlobalHotkeySettingViewModel(HandleAsync), 920);

            case EViewAction.RoutingSettingWindow:
                return await ShowCollectionDialogAsync("路由设置", new RoutingSettingViewModel(HandleAsync), "RoutingItems", "SelectedSource", 1080);

            case EViewAction.RoutingRuleSettingWindow when value is RoutingItem routing:
                return await ShowRoutingRuleDialogAsync(routing);

            case EViewAction.RoutingRuleDetailsWindow when value is RulesItem rule:
                if (_dialogs.TryPeek(out var routingContext) && routingContext.ViewModel is RoutingRuleSettingViewModel)
                {
                    return await ShowInlineRoutingRuleDetailsAsync(routingContext, rule);
                }
                return await ShowFormDialogAsync("路由规则详情", new RoutingRuleDetailsViewModel(rule, HandleAsync));

            case EViewAction.ImportRulesFromFile:
                if (_dialogs.TryPeek(out var ruleContext) && ruleContext.ViewModel is RoutingRuleSettingViewModel ruleVm &&
                    await PickOpenFileAsync([".json"]) is { } rulesPath)
                {
                    await ruleVm.ImportRulesFromFileAsync(rulesPath);
                }
                return true;

            case EViewAction.ImportRulesFromClipboard:
                if (_dialogs.TryPeek(out var clipboardContext) && clipboardContext.ViewModel is RoutingRuleSettingViewModel clipboardVm &&
                    GetClipboardText() is { Length: > 0 } rulesText)
                {
                    await clipboardVm.ImportRulesFromClipboardAsync(rulesText);
                }
                return true;

            case EViewAction.BrowseServer:
                if (_dialogs.TryPeek(out var browseContext) && browseContext.ViewModel is AddServer2ViewModel customVm &&
                    await PickOpenFileAsync([".json", ".yaml", ".yml"]) is { } customPath)
                {
                    customVm.SelectedSource.Address = customPath;
                }
                return true;

            case EViewAction.DispatcherShowMsg:
                if (value is string logText)
                {
                    AppendLog(logText);
                }

                return true;

            case EViewAction.InitSettingFont:
            case EViewAction.DispatcherRefreshIcon:
                return true;
        }

        return true;
    }

    private void AppendLog(string text)
    {
        Action<string>? sink;
        lock (_logLock)
        {
            _logBuffer.Append(text);
            if (_logBuffer.Length > 500_000)
            {
                _logBuffer.Remove(0, _logBuffer.Length - 400_000);
            }
            sink = _logSink;
        }
        sink?.Invoke(text);
    }

    public DynamicFormView CreateSettingsView()
    {
        return new(new OptionSettingViewModel(HandleAsync), collapseSections: true);
    }

    public DynamicFormView CreateDnsView()
    {
        return new(new DNSSettingViewModel(HandleAsync), collapseSections: true);
    }

    public DynamicFormView CreateTemplateView()
    {
        return new(new FullConfigTemplateViewModel(HandleAsync), collapseSections: true);
    }

    public DynamicFormView CreateHotkeyView()
    {
        return new(new GlobalHotkeySettingViewModel(HandleAsync), collapseSections: true);
    }

    public UIElement CreateBackupView()
    {
        var viewModel = new BackupAndRestoreViewModel(HandleAsync);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsOpen = false, IsDynamicOverflowEnabled = false };
        var localBackup = new AppBarButton { Label = "本地备份", Icon = new SymbolIcon(Symbol.Save) };
        localBackup.Click += async (_, _) =>
        {
            if (await PickSaveFileAsync("freedom-backup.zip", ".zip") is { } file)
            {
                await viewModel.LocalBackup(file);
            }
        };
        var localRestore = new AppBarButton { Label = "本地还原", Icon = new SymbolIcon(Symbol.OpenFile) };
        localRestore.Click += async (_, _) =>
        {
            if (await PickOpenFileAsync([".zip"]) is { } file)
            {
                await viewModel.LocalRestore(file);
            }
        };
        bar.PrimaryCommands.Add(localBackup);
        bar.PrimaryCommands.Add(localRestore);
        root.Children.Add(bar);
        var form = new DynamicFormView(viewModel, collapseSections: true);
        Grid.SetRow(form, 1);
        root.Children.Add(form);
        return root;
    }
    public DynamicCollectionView CreateSubscriptionView()
    {
        return new(new SubSettingViewModel(HandleAsync), "SubItems", "SelectedSource");
    }

    public DynamicCollectionView CreateRoutingView()
    {
        return new(new RoutingSettingViewModel(HandleAsync), "RoutingItems", "SelectedSource");
    }

    public UpdateManagerView CreateUpdateView()
    {
        return new(new CheckUpdateViewModel(HandleAsync));
    }

    private async Task<bool> ShowFormDialogAsync(string title, object viewModel, double width = 920)
    {
        return await ShowDialogAsync(title, viewModel, new DynamicFormView(viewModel), width);
    }

    private async Task<bool> ShowCollectionDialogAsync(string title, object viewModel, string collection, string selected, double width = 980)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var list = new DynamicCollectionView(viewModel, collection, selected);
        content.Children.Add(list);
        var form = new DynamicFormView(viewModel, false);
        Grid.SetRow(form, 1);
        content.Children.Add(form);
        return await ShowDialogAsync(title, viewModel, content, width);
    }

    private async Task<bool> ShowRoutingRuleDialogAsync(RoutingItem routing)
    {
        var viewModel = new RoutingRuleSettingViewModel(routing, HandleAsync);
        return await ShowDialogAsync("路由规则", viewModel, new RoutingRuleEditorView(viewModel), 1120);
    }

    private async Task<bool> ShowInlineRoutingRuleDetailsAsync(DialogContext context, RulesItem rule)
    {
        var viewModel = new RoutingRuleDetailsViewModel(rule, HandleAsync);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!context.BeginInline("路由规则详情", new RoutingRuleDetailsView(viewModel), completion))
        {
            return false;
        }
        return await completion.Task;
    }

    private async Task<bool> ShowGroupDialogAsync(ProfileItem profile)
    {
        var viewModel = new AddGroupServerViewModel(profile, HandleAsync);
        return await ShowDialogAsync("策略组 / 代理链", viewModel, new GroupServerEditorView(viewModel), 1120);
    }

    private async Task<bool> ShowDialogAsync(string title, object viewModel, UIElement content, double width)
    {
        var contentHost = new Border
        {
            Width = width,
            Height = 650,
            MaxWidth = width,
            Child = content
        };
        var dialog = new ContentDialog
        {
            XamlRoot = (_window.Content as FrameworkElement)?.XamlRoot,
            Title = title,
            Content = contentHost,
            CloseButtonText = "关闭"
        };
        dialog.Resources["ContentDialogMinWidth"] = width + 48;
        dialog.Resources["ContentDialogMaxWidth"] = width + 48;
        var context = new DialogContext(dialog, contentHost, viewModel);
        dialog.CloseButtonClick += (_, args) =>
        {
            if (context.CompleteInline(false))
            {
                args.Cancel = true;
            }
        };
        _dialogs.Push(context);
        await dialog.ShowAsync();
        _dialogs.Pop();
        return context.Saved;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = (_window.Content as FrameworkElement)?.XamlRoot,
            Title = "确认操作",
            Content = message,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowQrCodeAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var bytes = QRCodeUtils.GenQRCode(text);
        var image = new Microsoft.UI.Xaml.Controls.Image { Width = 280, Height = 280, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        if (bytes is not null)
        {
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            image.Source = bitmap;
        }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(image);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = (_window.Content as FrameworkElement)?.XamlRoot,
            Title = "分享二维码",
            Content = panel,
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    private async Task<string?> PickOpenFileAsync(IReadOnlyList<string> extensions)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        picker.FileTypeChoices.Add("文件", [extension]);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static void SetClipboardText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string? GetClipboardText()
    {
        var view = Clipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? view.GetTextAsync().AsTask().GetAwaiter().GetResult() : null;
    }

    private static async Task<string?> GetClipboardTextAsync()
    {
        var view = Clipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null;
    }

    private static byte[]? CaptureDesktop()
    {
        try
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string GetServerTitle(ProfileItem profile)
    {
        return $"服务器编辑 · {profile.ConfigType}";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(nint window, int command);

    private sealed class DialogContext(ContentDialog dialog, Border contentHost, object viewModel)
    {
        private readonly object? _rootTitle = dialog.Title;
        private readonly string _rootCloseButtonText = dialog.CloseButtonText;
        private readonly UIElement? _rootContent = contentHost.Child;
        private TaskCompletionSource<bool>? _inlineCompletion;

        public ContentDialog Dialog { get; } = dialog;
        public Border ContentHost { get; } = contentHost;
        public object ViewModel { get; } = viewModel;
        public bool Saved { get; set; }

        public bool BeginInline(string title, UIElement content, TaskCompletionSource<bool> completion)
        {
            if (_inlineCompletion is not null)
            {
                return false;
            }

            _inlineCompletion = completion;
            Dialog.Title = title;
            Dialog.CloseButtonText = "返回";
            ContentHost.Child = content;
            return true;
        }

        public bool CompleteInline(bool saved)
        {
            if (_inlineCompletion is null)
            {
                return false;
            }

            var completion = _inlineCompletion;
            _inlineCompletion = null;
            Dialog.Title = _rootTitle;
            Dialog.CloseButtonText = _rootCloseButtonText;
            ContentHost.Child = _rootContent;
            completion.TrySetResult(saved);
            return true;
        }
    }
}
