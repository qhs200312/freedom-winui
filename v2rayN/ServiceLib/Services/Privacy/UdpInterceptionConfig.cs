namespace ServiceLib.Services.Privacy;

public static class UdpInterceptionConfig
{
    public const string ComponentVersion = "2.6.1";
    public const string ComponentDirectory = "proxifyre";
    public const string OwnershipMarker = ".freedom-managed";
    public static readonly string[] ExcludedApplications =
    [
        "freedom.exe", "v2rayN.exe", "AmazTool.exe", "ProxiFyre.exe",
        "ProxiFyreUI.exe", "sing-box.exe", "xray.exe", "mihomo.exe",
        "clash.exe", "v2ray.exe", "hysteria.exe", "tuic.exe", "naive.exe"
    ];

    public static List<string> ParseApplications(string? value)
    {
        var names = (value ?? string.Empty)
            .Split([';', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0 || names.Count > 32)
        {
            throw new ArgumentException("请填写 1 至 32 个 UDP 接管进程名。");
        }
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            if (!Regex.IsMatch(name, @"^[\p{L}\p{N}_][\p{L}\p{N}_. -]*$") || name.Length > 128)
            {
                throw new ArgumentException("UDP 接管只接受进程名，不接受路径、通配符或全局匹配。");
            }
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }
            if (ExcludedApplications.Any(excluded => name.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("不能接管代理核心或转发组件本身，避免产生转发循环。");
            }
            names[index] = name;
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Build(string applications, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return JsonUtils.Serialize(new
        {
            logLevel = "Info",
            bypassLan = true,
            proxies = new[]
            {
                new
                {
                    appNames = ParseApplications(applications),
                    socks5ProxyEndpoint = $"127.0.0.1:{port}",
                    socks5Transport = "TCP",
                    supportedProtocols = new[] { "UDP" },
                    supportedAddressFamilies = new[] { "IPv4", "IPv6" }
                }
            },
            excludes = ExcludedApplications
        });
    }

    public static string? GetPrerequisiteError(bool isWindows, bool isAdministrator,
        bool componentAvailable, bool driverInstalled, bool hasAuthentication, bool udpEnabled)
    {
        if (!isWindows) return "UDP 接管仅支持 Windows";
        if (!componentAvailable) return "UDP 接管组件未就绪，请运行原型准备脚本";
        if (!driverInstalled) return "缺少 Windows Packet Filter (NDISRD) 驱动";
        if (!isAdministrator) return "UDP 接管需要以管理员身份运行客户端";
        if (hasAuthentication) return "原型暂不支持带认证的本地 SOCKS5 入口";
        if (!udpEnabled) return "本地代理入口未启用 UDP";
        return null;
    }
}
