namespace ServiceLib.Services;

public enum OutboundMode
{
    Rules,
    Global,
    Direct
}

public static class OutboundModeSelection
{
    private const string GlobalRoutingId = "freedom-outbound-global";
    private const string DirectRoutingId = "freedom-outbound-direct";

    public static OutboundMode GetMode(RoutingItem? routing) => routing switch
    {
        { Id: DirectRoutingId } => OutboundMode.Direct,
        { Id: GlobalRoutingId } => OutboundMode.Global,
        { Remarks: "V4-全局(Global)" or "RUv1-Всё" } => OutboundMode.Global,
        _ => OutboundMode.Rules
    };

    public static RoutingItem? FindRouting(IEnumerable<RoutingItem> routings, OutboundMode mode, string? lastRuleId)
    {
        var candidates = routings.Where(item => item.Enabled && GetMode(item) == mode).ToList();
        if (mode == OutboundMode.Rules)
        {
            return candidates.FirstOrDefault(item => item.Id == lastRuleId)
                ?? candidates.FirstOrDefault(item => item.IsActive)
                ?? candidates.FirstOrDefault(item => item.Remarks == "V4-绕过大陆(Whitelist)")
                ?? candidates.FirstOrDefault();
        }
        return candidates.FirstOrDefault(item => item.IsActive) ?? candidates.FirstOrDefault();
    }

    public static RoutingItem CreateRouting(OutboundMode mode, int sort)
    {
        var ruleSet = mode switch
        {
            OutboundMode.Global => EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + "global"),
            OutboundMode.Rules => EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + "white"),
            _ => JsonUtils.Serialize(new List<RulesItem>
            {
                new() { Id = Utils.GetGuid(false), Remarks = "默认直连", Port = "0-65535", OutboundTag = Global.DirectTag }
            })
        };
        var rules = JsonUtils.Deserialize<List<RulesItem>>(ruleSet) ?? [];
        return new RoutingItem
        {
            Id = mode switch
            {
                OutboundMode.Global => GlobalRoutingId,
                OutboundMode.Direct => DirectRoutingId,
                _ => Utils.GetGuid(false)
            },
            Remarks = mode switch
            {
                OutboundMode.Global => "全局代理",
                OutboundMode.Direct => "直连模式",
                _ => "规则分流(绕过大陆)"
            },
            RuleSet = ruleSet,
            RuleNum = rules.Count,
            Sort = sort
        };
    }
}
