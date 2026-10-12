using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using ServiceLib.Services;
using ServiceLib.Services.Diagnostics;

namespace v2rayN.WinUI.Services;

internal sealed class OutboundIpService
{
    public async Task<OutboundIpResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var running = Enum.IsDefined(AppManager.Instance.RunningCoreType);
        using var client = NetworkDiagnosticsRoute.CreateClient(requireProxy: running);
        var info = await new DetailedIpLookupService(client).DetectAsync(
            IpLocationLocalization.LocalizeApiUrl(AppManager.Instance.Config.SpeedTestItem.IPAPIUrl ?? ""), cancellationToken);
        var route = running ? "当前代理路由" : "直连出口";
        var location = IpLocationLocalization.Format(new IpInfoResult(
            info.CountryCode ?? "unknown", info.Ip, info.CountryName, info.Region, info.City, info.Organization));
        return new(info.Ip, route, string.IsNullOrWhiteSpace(location) ? "地区未知" : location,
            info, DateTimeOffset.Now);
    }
}

internal sealed record OutboundIpResult(string Ip, string Route, string LocationText,
    DetailedIpInfoResult Details, DateTimeOffset CheckedAt);
