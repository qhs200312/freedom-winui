using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServiceLib.Enums;
using ServiceLib.Manager;
using ServiceLib.Services.Diagnostics;
using v2rayN.WinUI.Controls;
using v2rayN.WinUI.Services;

namespace v2rayN.WinUI.Views;

public sealed class ServiceAvailabilityView : UserControl
{
    private readonly Dictionary<AvailabilityService, ResultCard> _rows = [];
    private readonly Grid _cards = new() { ColumnSpacing = 12, RowSpacing = 10 };
    private readonly Button _testAll = new();
    private readonly Button _cancel = new() { Content = new SymbolIcon(Symbol.Cancel), Visibility = Visibility.Collapsed };
    private readonly InfoBar _message = new() { IsOpen = false, IsClosable = true };
    private readonly Func<ServiceAvailabilityService> _factory;
    private readonly Func<string> _routeIdentity;
    private readonly DispatcherTimer _routeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _run;
    private string _identity;
    private int _generation;

    public ServiceAvailabilityView(Func<ServiceAvailabilityService>? factory = null, Func<string>? routeIdentity = null)
    {
        _factory = factory ?? CreateService;
        _routeIdentity = routeIdentity ?? (() => NetworkDiagnosticsRoute.Identity);
        _identity = _routeIdentity();
        var root = new Grid { RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        var allContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        allContent.Children.Add(new SymbolIcon(Symbol.Refresh));
        allContent.Children.Add(new TextBlock { Text = "测试全部" });
        _testAll.Content = allContent;
        _testAll.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ToolTipService.SetToolTip(_testAll, "测试全部服务");
        ToolTipService.SetToolTip(_cancel, "取消测试");
        _testAll.Click += async (_, _) => await RunAsync(null);
        _cancel.Click += (_, _) => _run?.Cancel();
        actions.Children.Add(_testAll);
        actions.Children.Add(_cancel);
        root.Children.Add(actions);
        Grid.SetRow(_message, 1);
        root.Children.Add(_message);
        AvailabilityService[] order =
        [
            AvailabilityService.BilibiliMainland, AvailabilityService.BilibiliHongKongTaiwan, AvailabilityService.Bahamut,
            AvailabilityService.ChatGPT, AvailabilityService.Claude, AvailabilityService.DisneyPlus,
            AvailabilityService.Gemini, AvailabilityService.Netflix, AvailabilityService.PrimeVideo,
            AvailabilityService.Spotify, AvailabilityService.TikTok, AvailabilityService.YouTubePremium
        ];
        foreach (var definition in ServiceAvailabilityService.ServiceDefinitions.OrderBy(item => Array.IndexOf(order, item.Id)))
        {
            var row = new ResultCard(definition.Name);
            row.Refresh.Click += async (_, _) => await RunAsync(definition.Id);
            _rows[definition.Id] = row;
            _cards.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _cards.SizeChanged += (_, args) => ArrangeCards(args.NewSize.Width);
        ArrangeCards(0);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;
        _routeTimer.Tick += (_, _) =>
        {
            if (_identity != _routeIdentity()) Invalidate();
        };
        Loaded += (_, _) =>
        {
            if (_identity != _routeIdentity()) Invalidate();
            _routeTimer.Start();
        };
        Unloaded += (_, _) => { _routeTimer.Stop(); _run?.Cancel(); };
    }

    private void ArrangeCards(double width)
    {
        var columns = width >= 720 ? 3 : width >= 470 ? 2 : 1;
        if (_cards.ColumnDefinitions.Count == columns) return;
        _cards.ColumnDefinitions.Clear();
        _cards.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (_cards.Children.Count + columns - 1) / columns; row++)
            _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < _cards.Children.Count; index++)
        {
            Grid.SetColumn((FrameworkElement)_cards.Children[index], index % columns);
            Grid.SetRow((FrameworkElement)_cards.Children[index], index / columns);
        }
    }

    private static ServiceAvailabilityService CreateService()
    {
        if (!Enum.IsDefined(AppManager.Instance.RunningCoreType))
            throw new InvalidOperationException("请先启动内核，再测试当前代理路由。");
        return new ServiceAvailabilityService(ServiceLib.Handler.ConnectionHandler.GetLocalWebProxy());
    }

    public void Invalidate()
    {
        _generation++;
        _identity = _routeIdentity();
        _run?.Cancel();
        _message.IsOpen = false;
        foreach (var row in _rows.Values) row.Reset();
    }

    public Task TestAllAsync() => RunAsync(null);

    private async Task RunAsync(AvailabilityService? selected)
    {
        if (_run != null) return;
        if (_identity != _routeIdentity()) Invalidate();
        using var run = new CancellationTokenSource();
        _run = run;
        var generation = _generation;
        var identity = _identity;
        var targets = selected is { } service ? new[] { service } : _rows.Keys.ToArray();
        SetBusy(true);
        _message.IsOpen = false;
        foreach (var target in targets) _rows[target].SetPending("检测中");
        try
        {
            using var checker = _factory();
            await Task.WhenAll(targets.Select(async target =>
            {
                var result = await checker.CheckAsync(target, run.Token);
                if (run.IsCancellationRequested || generation != _generation || identity != _routeIdentity()) return;
                _rows[target].Show(result);
            }));
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation)
                foreach (var target in targets.Where(target => _rows[target].IsPending))
                    _rows[target].SetPending("已取消");
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            _message.Title = "检测失败";
            _message.Message = ex.Message;
            _message.Severity = InfoBarSeverity.Warning;
            _message.IsOpen = true;
            foreach (var target in targets.Where(target => _rows[target].IsPending))
                _rows[target].SetPending("检测失败");
        }
        finally
        {
            _run = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _testAll.IsEnabled = !busy;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in _rows.Values) row.Refresh.IsEnabled = !busy;
    }

    private sealed class ResultCard : UserControl
    {
        private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        private readonly FontIcon _statusIcon = new() { Width = 14, Height = 14, FontSize = 12,
            FontFamily = new FontFamily("Segoe MDL2 Assets") };
        private readonly Border _badge = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 210 };
        private readonly Border _surface = new() { CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(3, 1, 1, 1), Padding = new Thickness(11, 10, 10, 8) };
        private readonly Border _divider = new() { Height = 1, Opacity = 0.6 };
        private readonly Border _regionBadge = new() { CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left };
        private readonly TextBlock _region = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _time = new() { FontSize = 11, Opacity = 0.65, Text = "--",
            TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly CountryFlag _flag = new();
        public Button Refresh { get; } = new() { Content = new SymbolIcon(Symbol.Refresh), Width = 32, Height = 32,
            Padding = new Thickness(5), CornerRadius = new CornerRadius(16) };
        public bool IsPending { get; private set; }

        public ResultCard(string name)
        {
            MinHeight = 112;
            var grid = new Grid { RowSpacing = 7 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(Refresh, 1);
            header.Children.Add(Refresh);
            grid.Children.Add(header);
            ToolTipService.SetToolTip(Refresh, $"重新检测 {name}");

            var status = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Top };
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new Grid { ColumnSpacing = 4 };
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            label.Children.Add(_statusIcon);
            Grid.SetColumn(_status, 1);
            label.Children.Add(_status);
            _badge.Child = label;
            status.Children.Add(_badge);
            var region = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            region.Children.Add(new Viewbox { Child = _flag, Width = 18, Height = 14 });
            region.Children.Add(_region);
            _regionBadge.Child = region;
            Grid.SetColumn(_regionBadge, 1);
            status.Children.Add(_regionBadge);
            Grid.SetRow(status, 1);
            grid.Children.Add(status);
            var footer = new Grid { RowSpacing = 4 };
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.Children.Add(_divider);
            Grid.SetRow(_time, 1);
            footer.Children.Add(_time);
            Grid.SetRow(footer, 2);
            grid.Children.Add(footer);
            _surface.Child = grid;
            Content = _surface;
            ActualThemeChanged += (_, _) => ApplyTheme();
            ApplyTheme();
            Reset();
        }

        private void ApplyTheme()
        {
            var dark = ActualTheme == ElementTheme.Dark;
            _surface.Background = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(255, 23, 33, 36) : Colors.White);
            var stroke = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(255, 44, 60, 65) : ColorHelper.FromArgb(255, 217, 227, 231));
            _surface.BorderBrush = stroke;
            _divider.Background = stroke;
            var accent = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(255, 118, 210, 215) : ColorHelper.FromArgb(255, 8, 127, 134));
            Refresh.Foreground = accent;
            Refresh.BorderBrush = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(100, 118, 210, 215) : ColorHelper.FromArgb(100, 8, 127, 134));
            _regionBadge.BorderBrush = Refresh.BorderBrush;
            _region.Foreground = accent;
        }

        private void SetBadge(Windows.UI.Color color, Symbol icon)
        {
            _badge.Background = new SolidColorBrush(color);
            _status.Foreground = new SolidColorBrush(Colors.White);
            _statusIcon.Foreground = _status.Foreground;
            _statusIcon.Glyph = char.ConvertFromUtf32((int)icon);
        }

        public void Reset()
        {
            SetPending("待检测");
        }

        public void SetPending(string label)
        {
            IsPending = label == "检测中";
            _status.Text = label;
            SetBadge(ColorHelper.FromArgb(255, 100, 116, 122), Symbol.Clock);
            _region.Text = "";
            _flag.SetCountry(null);
            _regionBadge.Visibility = Visibility.Collapsed;
            _time.Text = "--";
            ToolTipService.SetToolTip(_surface, null);
            ToolTipService.SetToolTip(_badge, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, label);
        }

        public void Show(ServiceAvailabilityResult result)
        {
            IsPending = false;
            _status.Text = result.Reason switch
            {
                AvailabilityReason.UnsupportedRegion => "地区不支持",
                AvailabilityReason.IpBanned => "IP 受限",
                AvailabilityReason.OriginalsOnly => "仅自制内容",
                AvailabilityReason.ComingSoon => "暂未开放",
                AvailabilityReason.Timeout => "检测超时",
                AvailabilityReason.PageReachable => "网页可达",
                AvailabilityReason.CatalogAvailable => "片库可用",
                AvailabilityReason.AuthenticationRequired => "需登录",
                AvailabilityReason.HumanVerification => "人机验证",
                AvailabilityReason.AccessDenied => "访问受限",
                AvailabilityReason.SampleUnavailable => "样本失效",
                AvailabilityReason.ServiceBusy => "服务繁忙",
                _ => result.State switch
                {
                    AvailabilityState.Available => "支持",
                    AvailabilityState.Unavailable => "不支持",
                    AvailabilityState.Limited => "部分支持",
                    AvailabilityState.Failed => "检测失败",
                    _ => "无法确认"
                }
            };
            SetBadge(result.Reason is AvailabilityReason.AuthenticationRequired or AvailabilityReason.HumanVerification
                ? ColorHelper.FromArgb(255, 157, 100, 24) : result.State switch
            {
                AvailabilityState.Available => ColorHelper.FromArgb(255, 16, 139, 106),
                AvailabilityState.Unavailable or AvailabilityState.Failed => ColorHelper.FromArgb(255, 207, 65, 65),
                AvailabilityState.Limited => ColorHelper.FromArgb(255, 157, 100, 24),
                _ => ColorHelper.FromArgb(255, 100, 116, 122)
            }, result.State switch
            {
                AvailabilityState.Available => Symbol.Accept,
                AvailabilityState.Unavailable => Symbol.Cancel,
                AvailabilityState.Limited => Symbol.Important,
                _ => Symbol.Help
            });
            _region.Text = result.RegionCode ?? "";
            _flag.SetCountry(result.RegionCode);
            _regionBadge.Visibility = string.IsNullOrWhiteSpace(result.RegionCode) ? Visibility.Collapsed : Visibility.Visible;
            _time.Text = $"{result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            var detail = Describe(result);
            var tooltip = $"{_status.Text}\n{detail}\n{result.Detail}\n{result.EvidenceUrl}".Trim();
            ToolTipService.SetToolTip(_surface, tooltip);
            ToolTipService.SetToolTip(_badge, tooltip);
            ToolTipService.SetToolTip(_regionBadge, result.RegionEvidenceUrl ?? "地区未知");
            ToolTipService.SetToolTip(_time, $"{_time.Text} · 耗时 {result.Elapsed.TotalSeconds:0.0}s");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(this, tooltip);
        }

        private static string Describe(ServiceAvailabilityResult result)
        {
            if (result.Reason == AvailabilityReason.CatalogAvailable)
                return "Netflix 影片元数据明确标记该样本可用，尚未验证会员播放权限。";
            if (result.Reason == AvailabilityReason.AuthenticationRequired)
                return "服务接口要求登录，当前检测使用匿名请求。";
            if (result.Reason == AvailabilityReason.SampleUnavailable)
                return "两个检测样本均未找到，不能据此判断整个服务或自制片库是否可用。";
            if (result.Reason == AvailabilityReason.ServiceBusy)
                return "服务返回临时网关错误或请求限制，请稍后重试。";
            if (result.Reason == AvailabilityReason.PageReachable)
                return result.Service switch
                {
                    AvailabilityService.ChatGPT => "匿名模型列表接口可达，尚未验证登录账号和实际模型回复。",
                    AvailabilityService.Claude => "应用初始化接口可达，尚未验证登录账号和实际模型回复。",
                    AvailabilityService.Gemini => "Gemini 应用页面和初始化数据可达，尚未验证登录和实际模型回复。",
                    AvailabilityService.Spotify => "Web Player 页面可达，地区为播放器报告的市场，尚未验证账号和音乐播放。",
                    AvailabilityService.TikTok => "应用页面和初始化数据可达，尚未验证视频流和账号权限。",
                    AvailabilityService.PrimeVideo => "未登录首页及页面数据可达，尚未验证会员播放权限。",
                    _ => "应用页面可达，尚未验证账号权限。"
                };
            if (result.Detail?.Contains("Anti-bot", StringComparison.OrdinalIgnoreCase) == true)
                return "服务要求人机验证，暂时无法确认可用性。";
            if (result.Detail?.StartsWith("HTTP ", StringComparison.Ordinal) == true)
                return result.Detail.StartsWith("HTTP 403", StringComparison.Ordinal)
                    ? "HTTP 403：无法区分地区限制、身份验证或人机验证。" : result.Detail;
            if (result.State == AvailabilityState.Failed)
                return result.Reason switch
                {
                    AvailabilityReason.NetworkError => "当前代理路由无法完成请求。",
                    AvailabilityReason.Timeout => "服务未在限定时间内响应。",
                    AvailabilityReason.InvalidResponse => "服务响应格式无法识别。",
                    AvailabilityReason.ResponseTooLarge => "服务响应超出检测限制。",
                    _ => "请求未完成，请稍后重试。"
                };
            if (result.State == AvailabilityState.Unknown)
                return result.Service switch
                {
                    AvailabilityService.ChatGPT => "地区或接口可达性已检测，账户与模型访问尚未验证。",
                    AvailabilityService.Claude or AvailabilityService.Gemini => result.RegionCode != null
                        ? "已检测地区，实际模型访问尚未验证。" : "没有可靠的地区或可用性信息。",
                    AvailabilityService.Netflix => "已检测接口或 CDN 地区，片库与播放权限尚未验证。",
                    AvailabilityService.Spotify => "公开地区列表不足以确认用户地区或播放可用性。",
                    AvailabilityService.TikTok => "已检测页面或地区，实际服务访问尚未验证。",
                    _ => "响应未提供足够的可用性信息。"
                };
            if (result.State == AvailabilityState.Available && result.Detail != null)
                return result.Service switch
                {
                    AvailabilityService.BilibiliMainland or AvailabilityService.BilibiliHongKongTaiwan => "指定节目接口允许访问。",
                    AvailabilityService.Bahamut => "指定节目接口返回访问凭据。",
                    AvailabilityService.DisneyPlus => "服务确认当前地区受支持。",
                    AvailabilityService.YouTubePremium => "页面显示 Premium 服务，订阅与播放权限尚未验证。",
                    AvailabilityService.PrimeVideo => "页面确认地区不受限制，账户与播放权限尚未验证。",
                    _ => ""
                };
            return "";
        }
    }
}
