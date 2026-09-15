using System.Web;

namespace ServiceLib.Helper;

public static class IpLocationLocalization
{
    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "美国", ["CN"] = "中国", ["HK"] = "香港", ["TW"] = "台湾", ["MO"] = "澳门",
        ["JP"] = "日本", ["KR"] = "韩国", ["SG"] = "新加坡", ["MY"] = "马来西亚", ["TH"] = "泰国",
        ["VN"] = "越南", ["ID"] = "印度尼西亚", ["PH"] = "菲律宾", ["IN"] = "印度",
        ["GB"] = "英国", ["DE"] = "德国", ["FR"] = "法国", ["NL"] = "荷兰", ["CA"] = "加拿大",
        ["AU"] = "澳大利亚", ["NZ"] = "新西兰", ["RU"] = "俄罗斯", ["UA"] = "乌克兰",
        ["CH"] = "瑞士", ["SE"] = "瑞典", ["NO"] = "挪威", ["FI"] = "芬兰", ["DK"] = "丹麦",
        ["IT"] = "意大利", ["ES"] = "西班牙", ["PT"] = "葡萄牙", ["PL"] = "波兰",
        ["IE"] = "爱尔兰", ["AT"] = "奥地利", ["BE"] = "比利时", ["CZ"] = "捷克",
        ["TR"] = "土耳其", ["AE"] = "阿联酋", ["IL"] = "以色列", ["BR"] = "巴西",
        ["AR"] = "阿根廷", ["MX"] = "墨西哥", ["ZA"] = "南非"
    };
    private static readonly Dictionary<string, string> Places = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US|California"] = "加利福尼亚州", ["US|Los Angeles"] = "洛杉矶",
        ["US|San Jose"] = "圣何塞", ["US|San Francisco"] = "旧金山",
        ["US|Washington"] = "华盛顿州", ["US|Seattle"] = "西雅图",
        ["US|New York"] = "纽约", ["US|Virginia"] = "弗吉尼亚州", ["US|Ashburn"] = "阿什本",
        ["US|Texas"] = "得克萨斯州", ["US|Dallas"] = "达拉斯", ["US|Oregon"] = "俄勒冈州",
        ["US|Florida"] = "佛罗里达州", ["US|Miami"] = "迈阿密", ["US|Illinois"] = "伊利诺伊州",
        ["US|Chicago"] = "芝加哥", ["US|New Jersey"] = "新泽西州",
        ["JP|Tokyo"] = "东京", ["JP|Osaka"] = "大阪", ["JP|Tokyo Prefecture"] = "东京都",
        ["KR|Seoul"] = "首尔", ["SG|Singapore"] = "新加坡", ["HK|Hong Kong"] = "香港",
        ["TW|Taipei"] = "台北", ["GB|London"] = "伦敦", ["GB|England"] = "英格兰",
        ["DE|Frankfurt am Main"] = "美因河畔法兰克福", ["DE|Hesse"] = "黑森州",
        ["FR|Paris"] = "巴黎", ["NL|Amsterdam"] = "阿姆斯特丹",
        ["CA|Toronto"] = "多伦多", ["CA|Ontario"] = "安大略省",
        ["CA|Vancouver"] = "温哥华", ["CA|British Columbia"] = "不列颠哥伦比亚省",
        ["AU|Sydney"] = "悉尼", ["AU|New South Wales"] = "新南威尔士州"
    };

    public static bool HasChineseNames(IpInfoResult result) =>
        ContainsChinese(result.CountryName) || ContainsChinese(result.Region) || ContainsChinese(result.City);

    public static string LocalizeApiUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Host.Equals("ipwho.is", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("ip-api.com", StringComparison.OrdinalIgnoreCase)))
            return url;
        var builder = new UriBuilder(uri);
        var query = HttpUtility.ParseQueryString(builder.Query);
        query["lang"] = "zh-CN";
        builder.Query = query.ToString();
        return builder.Uri.AbsoluteUri;
    }

    public static string Format(IpInfoResult result)
    {
        var country = ContainsChinese(result.CountryName) ? result.CountryName
            : Countries.GetValueOrDefault(result.Country ?? string.Empty) ?? result.CountryName ?? result.Country;
        var names = new[] { country, TranslatePlace(result.Country, result.Region), TranslatePlace(result.Country, result.City) };
        return string.Join(" · ", names.Where(value => !string.IsNullOrWhiteSpace(value)
            && !value.Equals("unknown", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? TranslatePlace(string country, string? value) => value is null
        ? null : Places.GetValueOrDefault($"{country}|{value}", value);
    private static bool ContainsChinese(string? value) => value?.Any(c => c is >= '\u3400' and <= '\u9fff') == true;
}
