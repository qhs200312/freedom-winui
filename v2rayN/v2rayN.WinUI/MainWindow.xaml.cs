using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Text;
using System.Windows.Input;
using DynamicData.Binding;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Models.Entities;
using ServiceLib.ViewModels;
using v2rayN.WinUI.Services;
using v2rayN.WinUI.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace v2rayN.WinUI;

public sealed partial class MainWindow : Window
{
    private const int MaxPendingLogChars = 100_000;
    private const int MaxVisibleLogChars = 200_000;

    private readonly WinUIPlatformService _platform;
    private readonly MainWindowViewModel _mainViewModel;
    private readonly ProfilesViewModel _profilesViewModel;
    private readonly StatusBarViewModel _statusViewModel;
    private readonly MsgViewModel _msgViewModel;
    private readonly Dictionary<string, UIElement> _moduleCache = [];
    private readonly HashSet<string> _moduleCreationPending = [];
    private readonly Queue<long> _uploadHistory = [];
    private readonly Queue<long> _downloadHistory = [];
    private readonly object _pendingLogLock = new();
    private readonly StringBuilder _pendingLogText = new();
    private DispatcherQueueTimer? _logFlushTimer;
    private TextBox? _logTextBox;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly TrayIconService _trayIcon;
    private readonly OutboundIpService _outboundIpService = new();
    private readonly DispatcherTimer _dashboardTimer;
    private readonly DispatcherTimer _trafficTimer;
    private readonly DispatcherTimer _messageTimer;
    private List<RoutingItem> _visibleRoutingItems = [];
    private string _routingSignature = string.Empty;
    private int _trayMenuSignature;
    private bool _trayMenuInitialized;
    private readonly AppWindow _appWindow;
    private bool _updatingControls;
    private bool _shuttingDown;
    private bool _allowClose;
    private long _currentUpload;
    private long _currentDownload;
    private long _sessionUpload;
    private long _sessionDownload;
    private ESysProxyType _lastTrayProxyMode = (ESysProxyType)(-1);
    private ECoreType _lastRunningCoreType = (ECoreType)(-1);
    private bool? _lastCoreRunning;
    private bool _checkingOutboundIp;
    private bool _checkingNodeLatency;
    private string _lastMessage = string.Empty;
    private DateTime _lastMessageAt;
    private string? _currentNavTag;
    private bool _initializing = true;
    private bool _syncingNavSelection;

    public MainWindow()
    {
        InitializeComponent();
        _initializing = false;
        Title = "freedom";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(windowHandle));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 980;
            presenter.PreferredMinimumHeight = 680;
        }

        _platform = new WinUIPlatformService(this)
        {
            FocusProfiles = () => ProfilesList.Focus(FocusState.Programmatic),
            ShowMessage = message => DispatcherQueue.TryEnqueue(() => ShowMessage(message))
        };

        _msgViewModel = new MsgViewModel(_platform.HandleAsync);
        _mainViewModel = new MainWindowViewModel(_platform.HandleAsync, forceRealtimeSpeed: true);
        _profilesViewModel = new ProfilesViewModel(_platform.HandleAsync);
        _statusViewModel = new StatusBarViewModel(_platform.HandleAsync);
        _platform.MainViewModel = _mainViewModel;
        _platform.ProfilesViewModel = _profilesViewModel;
        ApplySavedTheme();

        _trayIcon = new TrayIconService(windowHandle, DispatcherQueue, AppManager.Instance.Config);
        _trayIcon.ShowRequested += ShowMainWindow;
        _trayIcon.ExitRequested += ExitApplication;
        _trayIcon.MenuCommandRequested += HandleTrayCommand;
        _trayIcon.RoutingRequested += routingId =>
        {
            var routing = _statusViewModel.RoutingItems.FirstOrDefault(item => item.Id == routingId);
            if (routing is not null)
            {
                _statusViewModel.SelectedRouting = routing;
            }
        };
        _trayIcon.ServerRequested += serverId =>
        {
            var server = _statusViewModel.Servers.FirstOrDefault(item => item.ID == serverId);
            if (server is not null)
            {
                _statusViewModel.SelectedServer = server;
            }
        };
        _trayIcon.HotkeyRequested += HandleGlobalHotkey;
        _trayIcon.SessionEndingChanged += HandleSessionEndingChanged;
        if (App.ProgramStarted is not null)
        {
            ThreadPool.RegisterWaitForSingleObject(App.ProgramStarted, (_, _) => DispatcherQueue.TryEnqueue(ShowMainWindow), null, Timeout.Infinite, false);
        }
        _appWindow.Closing += AppWindow_Closing;

        ProfilesList.ItemsSource = _profilesViewModel.ProfileItems;
        ProfileGroupCombo.ItemsSource = _profilesViewModel.SubItems;
        _profilesViewModel.SubItems.CollectionChanged += (_, _) => RefreshMoveToGroupMenu();
        RoutingCombo.DisplayMemberPath = "DisplayRemarks";
        AppManager.Instance.ShowInTaskbar = true;
        if (Content is UIElement rootElement)
        {
            rootElement.KeyDown += Root_KeyDown;
        }

        SubscribeToApplicationEvents();
        _trafficTimer = InitializeTrafficChart();
        Closed += MainWindow_Closed;
        SelectNavItem("dashboard");
        NavigateToTag("dashboard");
        _ = RefreshOutboundIpAsync();

        if (AppManager.Instance.Config.UiItem.AutoHideStartup)
        {
            _trayIcon.HideWindow();
            AppManager.Instance.ShowInTaskbar = false;
        }

        _dashboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _dashboardTimer.Tick += (_, _) => RefreshDashboard();
        _dashboardTimer.Start();
        _messageTimer = new DispatcherTimer();
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            MessageBar.IsOpen = false;
        };
    }

    private void SubscribeToApplicationEvents()
    {
        _subscriptions.Add(AppEvents.DispatcherStatisticsRequested.AsObservable().Subscribe(speed =>
            DispatcherQueue.TryEnqueue(() => UpdateSpeed(speed))));
        _subscriptions.Add(AppEvents.SendSnackMsgRequested.AsObservable().Subscribe(message =>
            DispatcherQueue.TryEnqueue(() => ShowMessage(message))));
        _subscriptions.Add(AppEvents.ReloadRequested.AsObservable().Subscribe(_ =>
            DispatcherQueue.TryEnqueue(ResetSessionTraffic)));
        _subscriptions.Add(AppEvents.ShutdownRequested.AsObservable().Subscribe(_ =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _shuttingDown = true;
                _allowClose = true;
                Close();
            })));
        _subscriptions.Add(AppEvents.AppExitRequested.AsObservable().Subscribe(_ => _shuttingDown = true));
    }

    private DispatcherTimer InitializeTrafficChart()
    {
        for (var index = 0; index < 60; index++)
        {
            _uploadHistory.Enqueue(0);
            _downloadHistory.Enqueue(0);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            _uploadHistory.Dequeue();
            _downloadHistory.Dequeue();
            _uploadHistory.Enqueue(Math.Max(0, _currentUpload));
            _downloadHistory.Enqueue(Math.Max(0, _currentDownload));
            RenderTrafficChart();
            RefreshProcessMemory();
        };
        timer.Start();
        RefreshProcessMemory();
        return timer;
    }

    private void RefreshDashboard()
    {
        _updatingControls = true;
        var active = _profilesViewModel.ProfileItems.FirstOrDefault(item => item.IndexId == AppManager.Instance.Config.IndexId);
        ActiveServerText.Text = active?.Remarks ?? "未选择服务器";
        ActiveServerHeaderText.Text = active?.GetSummary() ?? "未选择服务器";
        ActiveServerDetailText.Text = active is null ? "请在配置项中选择活动服务器" : $"[{active.ConfigType}] {active.Address}:{active.Port}";
        var runningCoreType = AppManager.Instance.RunningCoreType;
        var coreRunning = Enum.IsDefined(runningCoreType);
        if (!_checkingNodeLatency)
        {
            NodeLatencyText.Text = coreRunning ? GetNodeLatencyText(active) : "未运行";
        }
        var coreTypeChanged = runningCoreType != _lastRunningCoreType;
        if (coreTypeChanged)
        {
            _lastRunningCoreType = runningCoreType;
            ResetSessionTraffic();
        }
        CoreStatusText.Text = GetCoreStatusText();
        UdpInterceptionStatusText.Text = AppManager.Instance.Config.GuiItem.EnableUdpInterception
            ? $"UDP 接管：{CoreManager.Instance.UdpInterceptionStatus}"
            : "UDP 接管：关闭";
        StopCoreButton.IsEnabled = coreRunning;
        ReloadCoreButtonText.Text = coreRunning ? "重启" : "启动";
        ToolTipService.SetToolTip(ReloadCoreButton, coreRunning ? "重载核心" : "启动核心");
        var coreStateChanged = _lastCoreRunning is null || coreRunning != _lastCoreRunning.Value;
        if (coreStateChanged)
        {
            _lastCoreRunning = coreRunning;
            _ = SyncSystemProxyWithCoreStateAsync(coreRunning);
        }
        if (coreRunning && (coreTypeChanged || coreStateChanged))
        {
            _ = RefreshNetworkStatusAfterCoreStartAsync();
        }
        else if (!coreRunning && coreStateChanged)
        {
            _ = RefreshOutboundIpAsync();
        }
        UploadTotalText.Text = $"本次 {Utils.HumanFy(_sessionUpload)}";
        DownloadTotalText.Text = $"本次 {Utils.HumanFy(_sessionDownload)}";
        TotalTrafficText.Text = Utils.HumanFy(_sessionUpload + _sessionDownload);
        TotalTrafficDetailText.Text = $"上传 {Utils.HumanFy(_sessionUpload)} · 下载 {Utils.HumanFy(_sessionDownload)}";
        ProxyModeCombo.SelectedIndex = _statusViewModel.SystemProxySelected;
        var proxyMode = (ESysProxyType)_statusViewModel.SystemProxySelected;
        if (_lastTrayProxyMode != proxyMode)
        {
            _lastTrayProxyMode = proxyMode;
            _trayIcon.UpdateIcon(proxyMode);
        }
        TunToggle.IsOn = _statusViewModel.EnableTun;
        RefreshVisibleRoutings();
        var selectedRouting = _statusViewModel.SelectedRouting;
        if (selectedRouting is null || _visibleRoutingItems.All(item => item.Id != selectedRouting.Id))
        {
            selectedRouting = _visibleRoutingItems.FirstOrDefault();
            if (selectedRouting is not null)
            {
                _statusViewModel.SelectedRouting = selectedRouting;
            }
        }
        if (RoutingCombo.SelectedItem != selectedRouting)
        {
            RoutingCombo.SelectedItem = selectedRouting;
        }
        var trayMenuSignature = CalculateTrayMenuSignature(selectedRouting?.Id, _statusViewModel.SelectedServer?.ID);
        if (!_trayMenuInitialized || trayMenuSignature != _trayMenuSignature)
        {
            _trayMenuInitialized = true;
            _trayMenuSignature = trayMenuSignature;
            _trayIcon.UpdateMenuItems(
                _visibleRoutingItems.Select(item => new TrayMenuEntry(item.Id, item.DisplayRemarks)),
                selectedRouting?.Id,
                _statusViewModel.Servers.Select(item => new TrayMenuEntry(item.ID ?? string.Empty, item.Text ?? string.Empty)),
                _statusViewModel.SelectedServer?.ID);
        }
        _trayIcon.UpdateToolTip(active?.GetSummary() ?? "freedom");
        _updatingControls = false;
    }

    private int CalculateTrayMenuSignature(string? selectedRoutingId, string? selectedServerId)
    {
        var hash = new HashCode();
        hash.Add(selectedRoutingId, StringComparer.Ordinal);
        hash.Add(selectedServerId, StringComparer.Ordinal);
        foreach (var item in _visibleRoutingItems)
        {
            hash.Add(item.Id, StringComparer.Ordinal);
            hash.Add(item.DisplayRemarks, StringComparer.Ordinal);
        }
        foreach (var item in _statusViewModel.Servers)
        {
            hash.Add(item.ID, StringComparer.Ordinal);
            hash.Add(item.Text, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    private static async Task SyncSystemProxyWithCoreStateAsync(bool coreRunning)
    {
        await SysProxyHandler.UpdateSysProxy(AppManager.Instance.Config, forceDisable: !coreRunning);
    }

    private void RefreshVisibleRoutings()
    {
        var configuredLanguage = AppManager.Instance.Config.UiItem.CurrentLanguage;
        var language = configuredLanguage.IsNotEmpty() ? configuredLanguage : CultureInfo.CurrentUICulture.Name;
        IEnumerable<RoutingItem> visible = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? _statusViewModel.RoutingItems.Where(item => item.Remarks.StartsWith("V4-", StringComparison.OrdinalIgnoreCase))
            : language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                ? _statusViewModel.RoutingItems.Where(item => item.Remarks.StartsWith("RUv1-", StringComparison.OrdinalIgnoreCase))
                : _statusViewModel.RoutingItems.Where(item => !item.Remarks.StartsWith("RUv1-", StringComparison.OrdinalIgnoreCase));

        var items = visible.ToList();
        var signature = $"{language}|{string.Join('|', items.Select(item => item.Id))}";
        if (signature == _routingSignature)
        {
            return;
        }

        _routingSignature = signature;
        _visibleRoutingItems = items;
        RoutingCombo.ItemsSource = _visibleRoutingItems;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _syncingNavSelection)
        {
            return;
        }

        var tag = ResolveNavTag(e.AddedItems.FirstOrDefault());
        if (tag is null)
        {
            return;
        }

        if (!_shuttingDown)
        {
            NavigateToTag(tag);
        }
    }

    private static string? ResolveNavTag(object? item)
    {
        switch (item)
        {
            case ListViewItem { Tag: string listTag }:
                return listTag;
            case FrameworkElement element:
                if (element.Tag is string elementTag)
                {
                    return elementTag;
                }

                // Content inside ListViewItem was clicked — walk parents to the container.
                for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                {
                    if (current is ListViewItem { Tag: string parentTag })
                    {
                        return parentTag;
                    }
                }

                return null;
            case string directTag:
                return directTag;
            default:
                return null;
        }
    }

    private void SelectNavItem(string tag)
    {
        ListViewItem? match = null;
        foreach (var item in NavList.Items)
        {
            if (item is ListViewItem listItem && string.Equals(listItem.Tag as string, tag, StringComparison.Ordinal))
            {
                match = listItem;
                break;
            }
        }

        if (match is null || ReferenceEquals(NavList.SelectedItem, match))
        {
            return;
        }

        _syncingNavSelection = true;
        try
        {
            NavList.SelectedItem = match;
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    private void NavigateToTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        // Keep selection chrome in sync even if navigation was requested programmatically.
        SelectNavItem(tag);
        if (string.Equals(_currentNavTag, tag, StringComparison.Ordinal))
        {
            return;
        }

        _currentNavTag = tag;

        DashboardPage.Visibility = tag == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = tag == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ModulePage.Visibility = tag is not ("dashboard" or "profiles") ? Visibility.Visible : Visibility.Collapsed;

        // Always show the target page fully opaque — no fade (fade caused blank/stuck frames).
        DashboardPage.Opacity = 1;
        ProfilesPage.Opacity = 1;
        ModulePage.Opacity = 1;

        if (ModulePage.Visibility == Visibility.Visible)
        {
            ShowModule(tag);
        }

    }

    private void ShowModule(string tag)
    {
        var definition = tag switch
        {
            "subscriptions" => ("订阅管理", "添加、编辑、删除、分享和更新订阅"),
            "logs" => ("运行日志", "实时核心日志、自动刷新和正则筛选"),
            "updates" => ("组件更新", "检查或更新 GUI、核心和地理数据"),
            "settings" => ("设置", "网络、核心、数据维护和界面选项"),
            _ => ("功能", string.Empty)
        };
        ModuleTitleText.Text = definition.Item1;
        ModuleSubtitleText.Text = definition.Item2;
        if (_moduleCache.TryGetValue(tag, out var module))
        {
            ModuleContent.Content = module;
            return;
        }

        // Keep rapid navigation responsive: expensive view trees are built only
        // after the current navigation settles, and stale requests are skipped.
        ModuleContent.Content = new TextBlock
        {
            Text = "正在加载...",
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        QueueModuleCreation(tag);
    }

    private void QueueModuleCreation(string tag)
    {
        if (_shuttingDown || _moduleCache.ContainsKey(tag) || !_moduleCreationPending.Add(tag))
        {
            return;
        }

        void LoadModule()
        {
            try
            {
                if (_shuttingDown || _currentNavTag != tag)
                {
                    return;
                }

                var created = CreateModule(tag);
                _moduleCache[tag] = created;
                if (_currentNavTag == tag)
                {
                    ModuleContent.Content = created;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to create module '{tag}': {ex}");
                if (_currentNavTag == tag)
                {
                    ModuleContent.Content = CreateLoadErrorContent("功能加载失败，请重试。", () => QueueModuleCreation(tag));
                }
            }
            finally
            {
                _moduleCreationPending.Remove(tag);
            }
        }

        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, LoadModule))
        {
            LoadModule();
        }
    }

    private UIElement CreateModule(string tag)
    {
        return tag switch
        {
            "subscriptions" => CreateSubscriptionModule(),
            "logs" => CreateLogsModule(),
            "updates" => _platform.CreateUpdateView(),
            "settings" => CreateSettingsModule(),
            _ => new TextBlock { Text = "没有可用内容" }
        };
    }

    private UIElement CreateSubscriptionModule()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var updateBar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsOpen = false, IsDynamicOverflowEnabled = false };
        updateBar.PrimaryCommands.Add(CreateCommandButton("更新全部", Symbol.Sync, _mainViewModel.SubUpdateCmd));
        updateBar.PrimaryCommands.Add(CreateCommandButton("通过代理更新全部", Symbol.Sync, _mainViewModel.SubUpdateViaProxyCmd));
        updateBar.PrimaryCommands.Add(CreateCommandButton("更新当前订阅", Symbol.Refresh, _mainViewModel.SubGroupUpdateCmd));
        updateBar.PrimaryCommands.Add(CreateCommandButton("通过代理更新当前订阅", Symbol.Refresh, _mainViewModel.SubGroupUpdateViaProxyCmd));
        root.Children.Add(updateBar);
        var subscriptions = _platform.CreateSubscriptionView();
        Grid.SetRow(subscriptions, 1);
        root.Children.Add(subscriptions);
        return root;
    }

    private UIElement CreateProxyModule()
    {
        var form = new DynamicFormView(_statusViewModel);
        return form;
    }

    private UIElement CreateClashProxiesModule()
    {
        var viewModel = new ClashProxiesViewModel(_platform.HandleAsync);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.Children.Add(new DynamicCollectionView(viewModel, "ProxyGroups", "SelectedGroup", "Name"));
        var details = new DynamicCollectionView(viewModel, "ProxyDetails", "SelectedDetail", "Name");
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);
        return grid;
    }

    private UIElement CreateConnectionsModule()
    {
        var viewModel = new ClashConnectionsViewModel(_platform.HandleAsync);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var form = new DynamicFormView(viewModel, false);
        grid.Children.Add(form);
        var list = new DynamicCollectionView(viewModel, "ConnectionItems", "SelectedSource", "Host");
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        return grid;
    }

    private UIElement CreateLogsModule()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var filter = new TextBox { Header = "正则筛选", Width = 320 };
        filter.TextChanged += (_, _) => _msgViewModel.MsgFilter = filter.Text;
        var autoRefresh = new ToggleSwitch { Header = "自动刷新", IsOn = _msgViewModel.AutoRefresh };
        autoRefresh.Toggled += (_, _) => _msgViewModel.AutoRefresh = autoRefresh.IsOn;
        var clear = new Button { Content = "清空" };
        var logBox = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono") };
        _logTextBox = logBox;
        _logFlushTimer = DispatcherQueue.CreateTimer();
        _logFlushTimer.Interval = TimeSpan.FromMilliseconds(120);
        _logFlushTimer.IsRepeating = true;
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs();
        _logFlushTimer.Start();
        clear.Click += (_, _) =>
        {
            lock (_pendingLogLock)
            {
                _pendingLogText.Clear();
            }
            logBox.Text = string.Empty;
        };
        controls.Children.Add(filter);
        controls.Children.Add(autoRefresh);
        controls.Children.Add(clear);
        root.Children.Add(controls);
        Grid.SetRow(logBox, 1);
        root.Children.Add(logBox);
        _platform.LogSink = text =>
        {
            lock (_pendingLogLock)
            {
                _pendingLogText.Append(text);
                if (_pendingLogText.Length > MaxPendingLogChars)
                {
                    _pendingLogText.Remove(0, _pendingLogText.Length - MaxPendingLogChars);
                }
            }
        };
        return root;
    }

    private void FlushPendingLogs()
    {
        if (_logTextBox is null || _currentNavTag != "logs")
        {
            return;
        }

        string pending;
        lock (_pendingLogLock)
        {
            if (_pendingLogText.Length == 0)
            {
                return;
            }

            pending = _pendingLogText.ToString();
            _pendingLogText.Clear();
        }

        var text = _logTextBox.Text + pending;
        if (text.Length > MaxVisibleLogChars)
        {
            text = text[^MaxVisibleLogChars..];
        }
        _logTextBox.Text = text;
        _logTextBox.SelectionStart = _logTextBox.Text.Length;
    }

    private UIElement CreateSettingsModule()
    {
        var tabs = new TabView { IsAddTabButtonVisible = false };
        var factories = new (string Title, Func<UIElement> Factory)[]
        {
            ("参数", _platform.CreateSettingsView),
            ("路由", _platform.CreateRoutingView),
            ("DNS", _platform.CreateDnsView),
            ("配置模板", _platform.CreateTemplateView),
            ("热键", () => new HotkeyEditorView(new GlobalHotkeySettingViewModel(_platform.HandleAsync), () => _trayIcon.ReloadGlobalHotkeys(AppManager.Instance.Config))),
            ("备份还原", _platform.CreateBackupView),
            ("程序操作", CreateApplicationActionsPanel),
            ("外观", CreateThemePanel)
        };

        var pendingFactories = new Dictionary<TabViewItem, Func<UIElement>>();
        var contentHosts = new Dictionary<TabViewItem, ContentControl>();
        var loadingTabs = new HashSet<TabViewItem>();

        foreach (var (title, factory) in factories)
        {
            // TabView caches the selected content in its presenter. Keep that
            // object stable so deferred loading updates the visible tree.
            var host = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new ProgressRing
                {
                    IsActive = true,
                    Width = 24,
                    Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var tab = new TabViewItem
            {
                Header = title,
                IsClosable = false,
                Content = host
            };
            pendingFactories[tab] = factory;
            contentHosts[tab] = host;
            host.Loaded += (_, _) => QueueLoadTab(tab);
            tabs.TabItems.Add(tab);
        }

        void QueueLoadTab(TabViewItem tab)
        {
            if (!pendingFactories.ContainsKey(tab) || !loadingTabs.Add(tab))
            {
                return;
            }

            void LoadTab()
            {
                loadingTabs.Remove(tab);
                if (_shuttingDown || !pendingFactories.TryGetValue(tab, out var factory))
                {
                    return;
                }

                try
                {
                    contentHosts[tab].Content = factory();
                    pendingFactories.Remove(tab);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to create settings tab '{tab.Header}': {ex}");
                    contentHosts[tab].Content = CreateLoadErrorContent("此页面加载失败，请重试。", () => QueueLoadTab(tab));
                }
            }

            if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, LoadTab))
            {
                LoadTab();
            }
        }

        tabs.SelectionChanged += (_, args) =>
        {
            if (args.AddedItems.OfType<TabViewItem>().FirstOrDefault() is { } tab)
            {
                QueueLoadTab(tab);
            }
        };
        tabs.Loaded += (_, _) =>
        {
            if (tabs.SelectedItem is TabViewItem selected)
            {
                QueueLoadTab(selected);
            }
        };
        tabs.SelectedIndex = 0;
        return tabs;
    }

    private static UIElement CreateLoadErrorContent(string message, Action retryAction)
    {
        var retry = new Button
        {
            Content = "重新加载",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        retry.Click += (_, _) => retryAction();

        var error = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4
        };
        error.Children.Add(new TextBlock
        {
            Text = message,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        error.Children.Add(retry);
        return error;
    }

    private UIElement CreateApplicationActionsPanel()
    {
        var panel = new StackPanel { Spacing = 18, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "程序与数据", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        var appActions = new Grid { RowSpacing = 10, ColumnSpacing = 10 };
        appActions.ColumnDefinitions.Add(new ColumnDefinition());
        appActions.ColumnDefinitions.Add(new ColumnDefinition());
        appActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        appActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        appActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddSettingsAction(appActions, 0, 0, "以管理员身份重启", Symbol.Sync, _mainViewModel.RebootAsAdminCmd);
        AddSettingsAction(appActions, 0, 1, "解除 UWP 回环限制", Symbol.World, _mainViewModel.SetUwpLoopbackCmd);
        AddSettingsAction(appActions, 1, 0, "清空流量统计", Symbol.Delete, _mainViewModel.ClearServerStatisticsCmd);
        AddSettingsAction(appActions, 1, 1, "打开存储目录", Symbol.OpenFile, _mainViewModel.OpenTheFileLocationCmd);
        AddSettingsAction(appActions, 2, 0, "更新 Geo/路由规则", Symbol.Refresh, _mainViewModel.UpdateGeoFilesCmd);
        panel.Children.Add(appActions);

        panel.Children.Add(new TextBlock { Text = "区域预设", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        presets.Children.Add(new Button { Content = "默认预设", Command = _mainViewModel.RegionalPresetDefaultCmd });
        presets.Children.Add(new Button { Content = "俄罗斯预设", Command = _mainViewModel.RegionalPresetRussiaCmd });
        presets.Children.Add(new Button { Content = "伊朗预设", Command = _mainViewModel.RegionalPresetIranCmd });
        panel.Children.Add(presets);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void AddSettingsAction(Grid grid, int row, int column, string label, Symbol symbol, ICommand command)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        content.Children.Add(new SymbolIcon(symbol));
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Command = command,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private UIElement CreateThemePanel()
    {
        var config = AppManager.Instance.Config;
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "界面主题", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var themeValues = Utils.GetEnumNames<ETheme>().Take(3).ToList();
        var theme = new ComboBox { Header = "颜色模式", ItemsSource = themeValues, SelectedItem = config.UiItem.CurrentTheme, Width = 260 };
        theme.SelectionChanged += async (_, _) =>
        {
            if (Content is FrameworkElement root)
            {
                root.RequestedTheme = theme.SelectedItem?.ToString() switch
                {
                    nameof(ETheme.Light) => ElementTheme.Light,
                    nameof(ETheme.Dark) => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }
            config.UiItem.CurrentTheme = theme.SelectedItem?.ToString() ?? nameof(ETheme.FollowSystem);
            await ConfigHandler.SaveConfig(config);
        };
        panel.Children.Add(theme);
        var accentItems = new[]
        {
            new AccentChoice("Teal", Windows.UI.Color.FromArgb(255, 8, 127, 134)),
            new AccentChoice("Blue", Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            new AccentChoice("Green", Windows.UI.Color.FromArgb(255, 16, 137, 98)),
            new AccentChoice("Orange", Windows.UI.Color.FromArgb(255, 202, 80, 16)),
            new AccentChoice("Red", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            new AccentChoice("Purple", Windows.UI.Color.FromArgb(255, 116, 77, 169))
        };
        var accent = new ComboBox { Header = "强调色", ItemsSource = accentItems, DisplayMemberPath = "Name", SelectedItem = accentItems.FirstOrDefault(item => item.Name == config.UiItem.ColorPrimaryName) ?? accentItems[0], Width = 260 };
        accent.SelectionChanged += async (_, _) =>
        {
            if (accent.SelectedItem is not AccentChoice choice)
            {
                return;
            }

            ApplyAccentColor(choice.Color);

            config.UiItem.ColorPrimaryName = choice.Name;
            await ConfigHandler.SaveConfig(config);
        };
        panel.Children.Add(accent);
        var fontSizes = Enumerable.Range(Global.MinFontSize, Global.MinFontSizeCount).ToList();
        var fontSize = new ComboBox { Header = "字体大小", ItemsSource = fontSizes, SelectedItem = config.UiItem.CurrentFontSize, Width = 260 };
        fontSize.SelectionChanged += async (_, _) =>
        {
            if (fontSize.SelectedItem is int size)
            {
                config.UiItem.CurrentFontSize = size;
            }

            await ConfigHandler.SaveConfig(config);
            ShowMessage("字体大小将在重启后完整应用");
        };
        panel.Children.Add(fontSize);
        var language = new ComboBox { Header = "语言", ItemsSource = Global.Languages, SelectedItem = config.UiItem.CurrentLanguage, Width = 260 };
        language.SelectionChanged += async (_, _) =>
        {
            config.UiItem.CurrentLanguage = language.SelectedItem?.ToString() ?? config.UiItem.CurrentLanguage;
            await ConfigHandler.SaveConfig(config);
            ShowMessage("语言将在重启后应用");
        };
        panel.Children.Add(language);
        return panel;
    }

    private void ApplySavedTheme()
    {
        var config = AppManager.Instance.Config;
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = config.UiItem.CurrentTheme switch
            {
                nameof(ETheme.Light) => ElementTheme.Light,
                nameof(ETheme.Dark) => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
        var accent = config.UiItem.ColorPrimaryName switch
        {
            "Blue" => Windows.UI.Color.FromArgb(255, 0, 120, 212),
            "Green" => Windows.UI.Color.FromArgb(255, 16, 137, 98),
            "Orange" => Windows.UI.Color.FromArgb(255, 202, 80, 16),
            "Red" => Windows.UI.Color.FromArgb(255, 196, 43, 28),
            "Purple" => Windows.UI.Color.FromArgb(255, 116, 77, 169),
            _ => Windows.UI.Color.FromArgb(255, 8, 127, 134)
        };
        ApplyAccentColor(accent);
    }

    private static void ApplyAccentColor(Windows.UI.Color accent)
    {
        var resources = Application.Current.Resources;
        if (resources["BrandBrush"] is SolidColorBrush brandBrush)
        {
            brandBrush.Color = accent;
        }

        foreach (var themeName in new[] { "Light", "Dark" })
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var value)
                && value is ResourceDictionary themeResources
                && themeResources["AccentSoftBrush"] is SolidColorBrush softBrush)
            {
                softBrush.Color = accent;
            }
        }
    }

    private static TabViewItem CreateTab(string title, UIElement content)
    {
        return new() { Header = title, Content = content, IsClosable = false };
    }

    private static AppBarButton CreateCommandButton(string label, Symbol symbol, ICommand command)
    {
        return new()
        {
            Label = label,
            Icon = new SymbolIcon(symbol),
            Command = command
        };
    }

    private async void ReloadCore_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(_mainViewModel.Reload);
    }

    private async void StopCore_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(CoreManager.Instance.CoreStop);
    }

    private void ProxyModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && ProxyModeCombo.SelectedIndex >= 0)
        {
            _statusViewModel.SystemProxySelected = ProxyModeCombo.SelectedIndex;
        }
    }

    private void TunToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls)
        {
            _statusViewModel.EnableTun = TunToggle.IsOn;
        }
    }

    private void RoutingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && RoutingCombo.SelectedItem is ServiceLib.Models.Entities.RoutingItem routing)
        {
            _statusViewModel.SelectedRouting = routing;
        }
    }

    private async void RefreshOutboundIp_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOutboundIpAsync();
    }

    private async Task RefreshOutboundIpAsync()
    {
        if (_checkingOutboundIp)
        {
            return;
        }

        _checkingOutboundIp = true;
        OutboundIpProgress.IsActive = true;
        RefreshOutboundIpButton.IsEnabled = false;
        CopyOutboundIpButton.IsEnabled = false;
        OutboundIpText.Text = "检测中...";
        OutboundIpDetailText.Text = "正在通过当前网络路径查询";
        try
        {
            var result = await _outboundIpService.DetectAsync();
            OutboundIpText.Text = result.Ip;
            OutboundIpDetailText.Text = result.LocationText;
            CopyOutboundIpButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            OutboundIpText.Text = "检测失败";
            OutboundIpDetailText.Text = "检查网络或 IP API 后重试";
            ShowMessage(ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _checkingOutboundIp = false;
            OutboundIpProgress.IsActive = false;
            RefreshOutboundIpButton.IsEnabled = true;
        }
    }

    private void CopyOutboundIp_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(OutboundIpText.Text);
        Clipboard.SetContent(package);
        ShowMessage("出口 IP 已复制");
    }

    private async void RefreshNodeLatency_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNodeLatencyAsync(true);
    }

    private async Task RefreshNetworkStatusAfterCoreStartAsync()
    {
        await Task.Delay(1000);
        if (!Enum.IsDefined(AppManager.Instance.RunningCoreType))
        {
            return;
        }

        await Task.WhenAll(RefreshOutboundIpAsync(), RefreshNodeLatencyAsync(false));
    }

    private async Task RefreshNodeLatencyAsync(bool showWarnings)
    {
        if (_checkingNodeLatency)
        {
            return;
        }

        var indexId = AppManager.Instance.Config.IndexId;
        if (indexId.IsNullOrEmpty())
        {
            NodeLatencyText.Text = "未选择";
            if (showWarnings)
            {
                ShowMessage("请先选择活动节点", InfoBarSeverity.Warning);
            }
            return;
        }
        if (!Enum.IsDefined(AppManager.Instance.RunningCoreType))
        {
            NodeLatencyText.Text = "未运行";
            if (showWarnings)
            {
                ShowMessage("请先启动内核", InfoBarSeverity.Warning);
            }
            return;
        }

        _checkingNodeLatency = true;
        RefreshNodeLatencyButton.IsEnabled = false;
        NodeLatencyText.Text = "检测中...";
        try
        {
            var delay = await _profilesViewModel.TestServerLatencyAsync(indexId);
            if (delay is null)
            {
                NodeLatencyText.Text = "不可用";
                if (showWarnings)
                {
                    ShowMessage("当前活动节点不可用", InfoBarSeverity.Warning);
                }
            }
            else
            {
                NodeLatencyText.Text = delay >= 0 ? $"{delay} ms" : "超时";
            }
        }
        catch (Exception ex)
        {
            NodeLatencyText.Text = "检测失败";
            if (showWarnings)
            {
                ShowMessage(ex.Message, InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _checkingNodeLatency = false;
            RefreshNodeLatencyButton.IsEnabled = true;
        }
    }

    private void ProfileSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _profilesViewModel.ServerFilter = ProfileSearchBox.Text;
    }

    private void ProfileGroupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileGroupCombo.SelectedItem is ServiceLib.Models.Entities.SubItem group)
        {
            _profilesViewModel.SelectedSub = group;
        }
    }

    private void MoveToGroupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubItem group })
        {
            _profilesViewModel.MoveToGroupCmd.Execute(group).Subscribe();
        }
    }

    private async void SortColumnMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string column })
        {
            await _profilesViewModel.SortServer(column);
        }
    }

    private void RefreshMoveToGroupMenu()
    {
        MoveToGroupMenu.Items.Clear();
        foreach (var group in _profilesViewModel.SubItems.Where(item => !string.IsNullOrEmpty(item.Id)))
        {
            var menuItem = new MenuFlyoutItem
            {
                Text = group.Remarks,
                Tag = group
            };
            menuItem.Click += MoveToGroupMenuItem_Click;
            MoveToGroupMenu.Items.Add(menuItem);
        }

        if (MoveToGroupMenu.Items.Count == 0)
        {
            MoveToGroupMenu.Items.Add(new MenuFlyoutItem
            {
                Text = "无可用分组",
                IsEnabled = false
            });
        }
    }

    private void ProfilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProfileItemModel profile)
        {
            _profilesViewModel.SelectedProfile = profile;
        }

        _profilesViewModel.SelectedProfiles = ProfilesList.SelectedItems.Cast<ProfileItemModel>().ToList();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _profilesViewModel.SelectedProfile = ProfilesList.SelectedItem as ProfileItemModel ?? new ProfileItemModel();
        _profilesViewModel.SelectedProfiles = ProfilesList.SelectedItems.Cast<ProfileItemModel>().ToList();
    }

    private void ProfileRow_ContextRequested(object sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileItemModel profile })
        {
            return;
        }

        if (!ProfilesList.SelectedItems.Contains(profile))
        {
            ProfilesList.SelectedItems.Clear();
            ProfilesList.SelectedItem = profile;
        }

        _profilesViewModel.SelectedProfile = profile;
        _profilesViewModel.SelectedProfiles = ProfilesList.SelectedItems.Cast<ProfileItemModel>().ToList();
    }

    private void ProfilesList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || ProfilesList.SelectedItem is not ProfileItemModel)
        {
            return;
        }

        ((ICommand)_profilesViewModel.SetDefaultServerCmd).Execute(null);
        e.Handled = true;
    }

    private void ProfilesList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        var command = AppManager.Instance.Config.UiItem.DoubleClick2Activate
            ? _profilesViewModel.SetDefaultServerCmd
            : _profilesViewModel.EditServerCmd;
        ((ICommand)command).Execute(null);
    }

    private void Root_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            ((ICommand)_mainViewModel.ReloadCmd).Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.V && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement((Content as FrameworkElement)?.XamlRoot) is not TextBox)
        {
            ((ICommand)_mainViewModel.AddServerViaClipboardCmd).Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Windows.System.VirtualKey.A && ProfilesPage.Visibility == Visibility.Visible)
        {
            ProfilesList.SelectAll();
            e.Handled = true;
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_allowClose && !_shuttingDown)
        {
            args.Cancel = true;
            _trayIcon.HideWindow();
            AppManager.Instance.ShowInTaskbar = false;
        }
    }

    private void ShowMainWindow()
    {
        _trayIcon.ShowWindow();
        AppManager.Instance.ShowInTaskbar = true;
    }

    private async void ExitApplication()
    {
        _shuttingDown = true;
        _allowClose = true;
        await SysProxyHandler.UpdateSysProxy(AppManager.Instance.Config, true);
        await AppManager.Instance.AppExitAsync(false);
        Close();
    }

    private void HandleSessionEndingChanged(bool isEnding)
    {
        _shuttingDown = isEnding;
        _allowClose = isEnding;
        if (isEnding)
        {
            App.ClearManagedSystemProxy();
            return;
        }

        _ = SysProxyHandler.UpdateSysProxy(AppManager.Instance.Config, false);
    }

    private void HandleTrayCommand(int command)
    {
        ICommand? action = command switch
        {
            201 => _statusViewModel.SystemProxyClearCmd,
            202 => _statusViewModel.SystemProxySetCmd,
            203 => _statusViewModel.SystemProxyNothingCmd,
            204 => _statusViewModel.SystemProxyPacCmd,
            301 => _mainViewModel.AddServerViaClipboardCmd,
            302 => _mainViewModel.SubUpdateCmd,
            303 => _mainViewModel.SubUpdateViaProxyCmd,
            304 => _mainViewModel.ReloadCmd,
            305 => _statusViewModel.CopyProxyCmdToClipboardCmd,
            _ => null
        };
        if (command == 205)
        {
            _statusViewModel.EnableTun = !_statusViewModel.EnableTun;
        }
        else
        {
            action?.Execute(null);
        }
    }

    private void HandleGlobalHotkey(EGlobalHotkey hotkey)
    {
        switch (hotkey)
        {
            case EGlobalHotkey.ShowForm:
                ShowMainWindow();
                break;
            case EGlobalHotkey.SystemProxyClear:
            case EGlobalHotkey.SystemProxySet:
            case EGlobalHotkey.SystemProxyUnchanged:
            case EGlobalHotkey.SystemProxyPac:
                _statusViewModel.SystemProxySelected = (int)hotkey - 1;
                break;
        }
    }

    private void AddProtocol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        ICommand? command = key switch
        {
            "vmess" => _mainViewModel.AddVmessServerCmd,
            "vless" => _mainViewModel.AddVlessServerCmd,
            "shadowsocks" => _mainViewModel.AddShadowsocksServerCmd,
            "socks" => _mainViewModel.AddSocksServerCmd,
            "http" => _mainViewModel.AddHttpServerCmd,
            "trojan" => _mainViewModel.AddTrojanServerCmd,
            "hysteria2" => _mainViewModel.AddHysteria2ServerCmd,
            "tuic" => _mainViewModel.AddTuicServerCmd,
            "wireguard" => _mainViewModel.AddWireguardServerCmd,
            "anytls" => _mainViewModel.AddAnytlsServerCmd,
            "naive" => _mainViewModel.AddNaiveServerCmd,
            "custom" => _mainViewModel.AddCustomServerCmd,
            "policy" => _mainViewModel.AddPolicyGroupServerCmd,
            "chain" => _mainViewModel.AddProxyChainServerCmd,
            _ => null
        };
        command?.Execute(null);
    }

    private void FeatureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        ICommand? command = key switch
        {
            "add-clipboard" => _mainViewModel.AddServerViaClipboardCmd,
            "add-image" => _mainViewModel.AddServerViaImageCmd,
            "edit-server" => _profilesViewModel.EditServerCmd,
            "remove-server" => _profilesViewModel.RemoveServerCmd,
            "profile-activate" => _profilesViewModel.SetDefaultServerCmd,
            "tcp-ping" => _profilesViewModel.TcpingServerCmd,
            "real-ping" => _profilesViewModel.RealPingServerCmd,
            "udp-test" => _profilesViewModel.UdpTestServerCmd,
            "speed-test" => _profilesViewModel.SpeedServerCmd,
            "export" => _profilesViewModel.ShareServerCmd,
            "copy-server" => _profilesViewModel.CopyServerCmd,
            "deduplicate" => _profilesViewModel.RemoveDuplicateServerCmd,
            "remove-invalid" => _profilesViewModel.RemoveInvalidServerResultCmd,
            "mixed-test" => _profilesViewModel.MixedTestServerCmd,
            "fast-ping" => _profilesViewModel.FastRealPingCmd,
            "sort-results" => _profilesViewModel.SortServerResultCmd,
            "move-top" => _profilesViewModel.MoveTopCmd,
            "move-up" => _profilesViewModel.MoveUpCmd,
            "move-down" => _profilesViewModel.MoveDownCmd,
            "move-bottom" => _profilesViewModel.MoveBottomCmd,
            "export-client" => _profilesViewModel.Export2ClientConfigCmd,
            "export-client-clipboard" => _profilesViewModel.Export2ClientConfigClipboardCmd,
            "export-url" => _profilesViewModel.Export2ShareUrlCmd,
            "export-base64" => _profilesViewModel.Export2ShareUrlBase64Cmd,
            "export-inner" => _profilesViewModel.Export2InnerUriCmd,
            "generate-all" => _profilesViewModel.GenGroupAllServerCmd,
            "generate-region" => _profilesViewModel.GenGroupRegionServerCmd,
            _ => null
        };
        command?.Execute(null);
    }

    private void UpdateSpeed(ServerSpeedItem speed)
    {
        var coreType = AppManager.Instance.RunningCoreType;
        if (coreType is not ECoreType.Xray and not ECoreType.sing_box)
        {
            return;
        }

        _currentUpload = speed.ProxyUp;
        _currentDownload = speed.ProxyDown;
        _sessionUpload += Math.Max(0, speed.ProxyUp);
        _sessionDownload += Math.Max(0, speed.ProxyDown);
        UploadSpeedText.Text = $"{Utils.HumanFy(speed.ProxyUp)}/s";
        DownloadSpeedText.Text = $"{Utils.HumanFy(speed.ProxyDown)}/s";
    }

    private void ResetSessionTraffic()
    {
        _currentUpload = 0;
        _currentDownload = 0;
        _sessionUpload = 0;
        _sessionDownload = 0;
        _uploadHistory.Clear();
        _downloadHistory.Clear();
        for (var index = 0; index < 60; index++)
        {
            _uploadHistory.Enqueue(0);
            _downloadHistory.Enqueue(0);
        }
    }

    private void TrafficCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderTrafficChart();
    }

    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DashboardContentGrid.Width = Math.Max(0, Math.Min(1600, e.NewSize.Width - 2));
        ApplyDashboardGridLayout(e.NewSize.Width < 1120);
    }

    private void ApplyDashboardGridLayout(bool compact)
    {
        MetricColumn3.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        MetricColumn4.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ControlColumn3.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ControlColumn4.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(TotalTrafficCard, compact ? 1 : 0);
        Grid.SetColumn(TotalTrafficCard, compact ? 0 : 2);
        Grid.SetRow(ActiveServerCard, compact ? 1 : 0);
        Grid.SetColumn(ActiveServerCard, compact ? 1 : 3);
        Grid.SetRow(RoutingCard, compact ? 1 : 0);
        Grid.SetColumn(RoutingCard, compact ? 0 : 2);
        Grid.SetRow(OutboundIpCard, compact ? 1 : 0);
        Grid.SetColumn(OutboundIpCard, compact ? 1 : 3);
    }

    private void RenderTrafficChart()
    {
        var width = TrafficCanvas.ActualWidth;
        var height = TrafficCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        // Leave a little vertical padding so top labels/lines are not clipped.
        const double topPadding = 2;
        const double bottomPadding = 2;
        var plotHeight = Math.Max(1, height - topPadding - bottomPadding);

        AxisBottom.X1 = AxisMiddle.X1 = AxisTop.X1 = 0;
        AxisBottom.X2 = AxisMiddle.X2 = AxisTop.X2 = width;
        AxisBottom.Y1 = AxisBottom.Y2 = height - bottomPadding;
        AxisMiddle.Y1 = AxisMiddle.Y2 = topPadding + plotHeight / 2;
        AxisTop.Y1 = AxisTop.Y2 = topPadding;

        var maximum = Math.Max(1024, Math.Max(_uploadHistory.Max(), _downloadHistory.Max()));
        var niceMaximum = NiceCeiling(maximum);

        YAxisTopLabel.Text = $"{Utils.HumanFy(niceMaximum)}/s";
        YAxisMidLabel.Text = $"{Utils.HumanFy(niceMaximum / 2)}/s";
        YAxisBottomLabel.Text = "0 B/s";
        XAxisLeftLabel.Text = "-60s";
        XAxisMidLabel.Text = "-30s";
        XAxisRightLabel.Text = "现在";

        UploadLine.Points = BuildPoints(_uploadHistory, width, height, niceMaximum, topPadding, bottomPadding);
        DownloadLine.Points = BuildPoints(_downloadHistory, width, height, niceMaximum, topPadding, bottomPadding);
    }

    private static long NiceCeiling(long value)
    {
        if (value <= 1024)
        {
            return 1024;
        }
        // Round up to a clean magnitude so axis labels stay readable.
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (long)Math.Ceiling(nice * magnitude);
    }

    private static PointCollection BuildPoints(
        IEnumerable<long> values,
        double width,
        double height,
        long maximum,
        double topPadding,
        double bottomPadding)
    {
        var items = values.ToArray();
        var points = new PointCollection();
        if (items.Length == 0 || maximum <= 0)
        {
            return points;
        }

        var plotHeight = Math.Max(1, height - topPadding - bottomPadding);
        var lastIndex = Math.Max(1, items.Length - 1);
        for (var index = 0; index < items.Length; index++)
        {
            var x = width * index / lastIndex;
            var y = height - bottomPadding - plotHeight * Math.Clamp(items[index] / (double)maximum, 0, 1);
            points.Add(new Point(x, y));
        }
        return points;
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        var now = DateTime.UtcNow;
        if (MessageBar.IsOpen && message == _lastMessage && now - _lastMessageAt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastMessage = message;
        _lastMessageAt = now;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
        _messageTimer.Stop();
        _messageTimer.Interval = severity switch
        {
            InfoBarSeverity.Error => TimeSpan.FromSeconds(7),
            InfoBarSeverity.Warning => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(3)
        };
        _messageTimer.Start();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _dashboardTimer.Stop();
        _trafficTimer.Stop();
        _messageTimer.Stop();
        _logFlushTimer?.Stop();
        _trayIcon.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        if (!_shuttingDown)
        {
            _shuttingDown = true;
            await AppManager.Instance.AppExitAsync(false);
        }
    }

    private static string GetCoreStatusText()
    {
        var core = AppManager.Instance.RunningCoreType;
        return Enum.IsDefined(core) ? core.ToString() : "未运行";
    }

    private void RefreshProcessMemory()
    {
        try
        {
            using var appProcess = Process.GetCurrentProcess();
            appProcess.Refresh();
            AppMemoryText.Text = FormatWorkingSet(appProcess.WorkingSet64);
        }
        catch
        {
            AppMemoryText.Text = "-- MB";
        }

        var coreType = AppManager.Instance.RunningCoreType;
        CoreMemoryNameText.Text = coreType switch
        {
            ECoreType.Xray => "xray",
            ECoreType.v2fly => "v2fly",
            ECoreType.v2fly_v5 => "v2fly v5",
            ECoreType.mihomo => "mihomo",
            _ => "sing-box"
        };
        var coreWorkingSet = CoreManager.Instance.WorkingSet64;
        CoreMemoryText.Text = coreWorkingSet > 0 ? FormatWorkingSet(coreWorkingSet) : "-- MB";
    }

    private static string FormatWorkingSet(long bytes)
    {
        return $"{bytes / 1024d / 1024d:0.0} MB";
    }

    private static string GetNodeLatencyText(ProfileItemModel? profile)
    {
        if (profile is null || profile.DelayVal.IsNullOrEmpty())
        {
            return "未测试";
        }

        if (int.TryParse(profile.DelayVal, out var delay))
        {
            return delay >= 0 ? $"{delay} ms" : "超时";
        }

        return profile.DelayVal;
    }

    private sealed record AccentChoice(string Name, Windows.UI.Color Color);
}
