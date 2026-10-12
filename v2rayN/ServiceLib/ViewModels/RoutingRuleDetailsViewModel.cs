namespace ServiceLib.ViewModels;

public class RoutingRuleDetailsViewModel : MyReactiveObject
{
    public List<RoutingNodeChoice> OutboundChoices { get; private set; } = [];
    public bool DefaultOnly { get; }
    public bool WebsiteOnly { get; }

    public async Task LoadOutboundChoicesAsync() =>
        OutboundChoices = await RoutingNodeSelection.LoadChoicesAsync(SelectedSource.OutboundTag);

    public static bool IsSimpleWebsiteRule(RulesItem rule) =>
        rule.RuleType != ERuleType.DNS && rule.Domain?.Count > 0
        && rule.Domain.All(domain => domain.StartsWith("domain:", StringComparison.Ordinal))
        && !(rule.Ip?.Count > 0 || rule.Process?.Count > 0
            || rule.Protocol?.Count > 0 || rule.InboundTag?.Count > 0)
        && rule.Port.IsNullOrEmpty()
        && (rule.Network.IsNullOrEmpty() || rule.Network is "tcp,udp" or "udp,tcp");

    [Reactive]
    public string? ValidationError { get; set; }

    public IList<string> ProtocolItems { get; set; }
    public IList<string> InboundTagItems { get; set; }

    [Reactive]
    public RulesItem SelectedSource { get; set; }

    [Reactive]
    public string Domain { get; set; }

    [Reactive]
    public string IP { get; set; }

    [Reactive]
    public string Process { get; set; }

    [Reactive]
    public string? RuleType { get; set; }

    [Reactive]
    public bool AutoSort { get; set; }

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }
    public ReactiveCommand<Unit, Unit> ChooseApplicationCmd { get; }

    public void AddApplication(string path)
    {
        var processes = Utils.String2List(Utils.ParseProcess(Process));
        var normalized = path.Replace('\\', '/');
        if (!processes.Contains(normalized, StringComparer.OrdinalIgnoreCase)) processes.Add(normalized);
        Process = Utils.List2String(processes, true);
    }

    public RoutingRuleDetailsViewModel(RulesItem rulesItem, Func<EViewAction, object?, Task<bool>>? updateView,
        bool defaultOnly = false, bool websiteOnly = false)
    {
        DefaultOnly = defaultOnly;
        WebsiteOnly = websiteOnly;
        _config = AppManager.Instance.Config;
        _updateView = updateView;

        SaveCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SaveRulesAsync();
        });
        ChooseApplicationCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_updateView is not null) await _updateView(EViewAction.ChooseRoutingApplication, this);
        });

        if (rulesItem.Id.IsNullOrEmpty())
        {
            rulesItem.Id = Utils.GetGuid(false);
            rulesItem.OutboundTag = Global.ProxyTag;
            rulesItem.Enabled = true;
            SelectedSource = rulesItem;
        }
        else
        {
            SelectedSource = rulesItem;
        }

        Domain = Utils.List2String(WebsiteOnly
            ? SelectedSource.Domain?.Select(domain => domain.StartsWith("domain:", StringComparison.Ordinal)
                ? domain[7..] : domain).ToList()
            : SelectedSource.Domain, true);
        IP = Utils.List2String(SelectedSource.Ip, true);
        Process = Utils.List2String(SelectedSource.Process, true);
        RuleType = SelectedSource.RuleType?.ToString();
    }

    private async Task SaveRulesAsync()
    {
        if (WebsiteOnly)
        {
            ValidationError = null;
            var domains = new List<string>();
            foreach (var value in (Domain ?? string.Empty).Split(
                         [',', '，', ';', '；', '\r', '\n', '\t', ' '],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var host = value.StartsWith("domain:", StringComparison.Ordinal) ? value[7..] : value;
                if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Uri.TryCreate(host, UriKind.Absolute, out var url))
                    {
                        ValidationError = $"域名格式不正确：{value}";
                        return;
                    }
                    host = url.IdnHost;
                }
                if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
                try
                {
                    host = new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant();
                }
                catch (ArgumentException)
                {
                    ValidationError = $"域名格式不正确：{value}";
                    return;
                }
                if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns
                    || host.Split('.').Any(label => label.Length > 63
                        || label.StartsWith('-') || label.EndsWith('-')))
                {
                    ValidationError = $"域名格式不正确：{value}";
                    return;
                }
                var domain = $"domain:{host}";
                if (!domains.Contains(domain)) domains.Add(domain);
            }
            if (domains.Count == 0)
            {
                ValidationError = "请填写域名";
                return;
            }
            if (SelectedSource.OutboundTag.IsNullOrEmpty())
            {
                ValidationError = "请选择处理方式";
                return;
            }
            SelectedSource.Domain = domains;
            if (SelectedSource.Remarks.IsNullOrEmpty() || SelectedSource.Remarks == "网站分流")
                SelectedSource.Remarks = domains[0][7..];
            await _updateView?.Invoke(EViewAction.CloseWindow, null);
            return;
        }
        if (DefaultOnly)
        {
            SelectedSource.Domain = null;
            SelectedSource.Ip = null;
            SelectedSource.Process = null;
            SelectedSource.Protocol = null;
            SelectedSource.InboundTag = null;
            SelectedSource.Port = null;
            SelectedSource.Network = "tcp,udp";
            SelectedSource.RuleType = null;
            SelectedSource.Enabled = true;
            await _updateView?.Invoke(EViewAction.CloseWindow, null);
            return;
        }
        Domain = Utils.Convert2Comma(Domain);
        IP = Utils.Convert2Comma(IP);
        Process = Utils.ParseProcess(Process);

        if (AutoSort)
        {
            SelectedSource.Domain = Utils.String2ListSorted(Domain);
            SelectedSource.Ip = Utils.String2ListSorted(IP);
            SelectedSource.Process = Utils.String2ListSorted(Process);
        }
        else
        {
            SelectedSource.Domain = Utils.String2List(Domain);
            SelectedSource.Ip = Utils.String2List(IP);
            SelectedSource.Process = Utils.String2List(Process);
        }
        SelectedSource.Protocol = ProtocolItems?.ToList();
        SelectedSource.InboundTag = InboundTagItems?.ToList();
        SelectedSource.RuleType = RuleType.IsNullOrEmpty() ? null : Enum.Parse<ERuleType>(RuleType);

        var hasRule = SelectedSource.Domain?.Count > 0
          || SelectedSource.Ip?.Count > 0
          || SelectedSource.Protocol?.Count > 0
          || SelectedSource.Process?.Count > 0
          || SelectedSource.InboundTag?.Count > 0
          || SelectedSource.Port.IsNotEmpty()
          || SelectedSource.Network.IsNotEmpty();

        if (!hasRule)
        {
            NoticeManager.Instance.Enqueue(string.Format(ResUI.RoutingRuleDetailRequiredTips, "Network/Port/Protocol/Domain/IP/Process"));
            return;
        }
        if (SelectedSource.Process?.Count > 0 && !_config.TunModeItem.EnableTun)
        {
            NoticeManager.Instance.Enqueue("应用分流规则已保存；完整接管应用 TCP/UDP 需要开启 TUN。普通 UDP 接管使用默认代理节点。");
        }
        //NoticeHandler.Instance.Enqueue(ResUI.OperationSuccess);
        await _updateView?.Invoke(EViewAction.CloseWindow, null);
    }
}
