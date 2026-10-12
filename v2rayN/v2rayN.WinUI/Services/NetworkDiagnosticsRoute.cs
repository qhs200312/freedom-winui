using System.Net;
using ServiceLib.Enums;
using ServiceLib.Manager;

namespace v2rayN.WinUI.Services;

internal static class NetworkDiagnosticsRoute
{
    private static long _generation;
    public static string Identity => $"{AppManager.Instance.RunningCoreType}:{AppManager.Instance.Config.IndexId}:{AppManager.Instance.Config.RoutingBasicItem.RoutingIndexId}:{Interlocked.Read(ref _generation)}";
    public static void Invalidate() => Interlocked.Increment(ref _generation);

    public static HttpClient CreateClient(bool requireProxy = true)
    {
        var running = Enum.IsDefined(AppManager.Instance.RunningCoreType);
        if (requireProxy && !running)
            throw new InvalidOperationException("请先启动内核，再检测当前代理出口。");
        var handler = new SocketsHttpHandler
        {
            UseProxy = running,
            AllowAutoRedirect = false,
            Proxy = running ? new WebProxy($"socks5://127.0.0.1:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 freedom/7");
        return client;
    }
}
