namespace ServiceLib.Services;

public record RoutingNodeChoice(string Tag, string DisplayName);

public static class RoutingNodeSelection
{
    private const string Prefix = "node:";

    public static string Reference(string indexId) => Prefix + indexId;

    public static bool TryGetIndexId(string? reference, out string indexId)
    {
        indexId = reference?.StartsWith(Prefix, StringComparison.Ordinal) == true
            ? reference[Prefix.Length..] : string.Empty;
        return indexId.Length > 0;
    }

    public static string MapKey(string reference) =>
        TryGetIndexId(reference, out _) ? reference : $"remark:{reference}";

    public static List<RoutingNodeChoice> BuildChoices(IEnumerable<ProfileItem> nodes,
        IReadOnlyDictionary<string, string>? subscriptions = null)
    {
        var choices = new List<RoutingNodeChoice>
        {
            new(Global.ProxyTag, "当前选中节点"),
            new(Global.DirectTag, "直连"),
            new("block", "阻断")
        };
        foreach (var node in nodes.Where(node => !node.IndexId.IsNullOrEmpty()
                     && node.ConfigType != EConfigType.Custom)
                     .OrderBy(node => node.Subid).ThenBy(node => node.Remarks))
        {
            var source = node.Subid.IsNullOrEmpty() ? "本地"
                : subscriptions?.GetValueOrDefault(node.Subid) ?? "订阅";
            var suffix = node.IndexId.Length > 8 ? node.IndexId[..8] : node.IndexId;
            choices.Add(new(Reference(node.IndexId),
                $"{source} · {node.Remarks} · {node.ConfigType} [{suffix}]"));
        }
        return choices;
    }

    public static async Task<List<RoutingNodeChoice>> LoadChoicesAsync(string? selectedTag = null)
    {
        var nodes = await AppManager.Instance.ProfileItems(string.Empty) ?? [];
        var subscriptions = (await AppManager.Instance.SubItems() ?? [])
            .ToDictionary(sub => sub.Id, sub => sub.Remarks);
        var choices = BuildChoices(nodes, subscriptions);
        if (selectedTag.IsNotEmpty() && choices.All(choice => choice.Tag != selectedTag))
        {
            choices.Add(new(selectedTag, $"已有规则：{selectedTag}"));
        }
        return choices;
    }

    public static bool IsDefaultRule(RulesItem rule) =>
        rule.Enabled && rule.RuleType != ERuleType.DNS
        && !(rule.Domain?.Count > 0 || rule.Ip?.Count > 0 || rule.Process?.Count > 0
            || rule.Protocol?.Count > 0 || rule.InboundTag?.Count > 0)
        && (rule.Port.IsNullOrEmpty() || rule.Port is "0-65535" or "1-65535")
        && (rule.Network.IsNullOrEmpty() || rule.Network is "tcp,udp" or "udp,tcp");

    public static string DefaultProxyReference(CoreConfigContext context)
    {
        var defaultRule = (JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [])
            .FirstOrDefault(IsDefaultRule);
        return TryGetIndexId(defaultRule?.OutboundTag, out _) ? defaultRule.OutboundTag! : Global.ProxyTag;
    }

    public static void ValidateReferences(CoreConfigContext context)
    {
        foreach (var rule in JsonUtils.Deserialize<List<RulesItem>>(context.RoutingItem?.RuleSet) ?? [])
        {
            if (!rule.Enabled || rule.RuleType == ERuleType.DNS
                || !TryGetIndexId(rule.OutboundTag, out _)) continue;
            var node = context.AllProxiesMap.GetValueOrDefault(MapKey(rule.OutboundTag!));
            var supported = context.RunCoreType == ECoreType.sing_box
                ? Global.SingboxSupportConfigType : Global.XraySupportConfigType;
            if (node is null || (!supported.Contains(node.ConfigType) && !node.ConfigType.IsGroupType()))
                throw new InvalidOperationException($"分流节点不存在或当前内核不支持：{rule.Remarks} ({rule.OutboundTag})");
        }
    }
}
