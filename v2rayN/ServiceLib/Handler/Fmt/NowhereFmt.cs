using System.Globalization;

namespace ServiceLib.Handler.Fmt;

public sealed class NowhereFmt : BaseFmt
{
    public static ProfileItem? Resolve(string text, out string message)
    {
        message = "Invalid Nowhere client link.";
        try
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("nowhere" or "vector") || uri.UserInfo.Contains(':'))
                return null;
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                query.TryAdd(Decode(parts[0]), parts.Length == 2 ? Decode(parts[1]) : "");
            }
            var transport = uri.Scheme == "vector" ? "tcp" : query.GetValueOrDefault("net", "tcp");
            var port = uri.Port;
            // System.Uri synthesizes "/" for an authority-only URL and normalizes dot paths.
            var path = Regex.Match(text, @"\A[a-zA-Z]+://[^/?#]+(?<path>/[^?#]*)").Groups["path"].Value;
            if (path.Length > 0)
            {
                if (uri.Scheme != "vector" || port != -1) return null;
                var match = Regex.Match(path, @"\A/(tcp[46]?|udp[46]?):([0-9]+)\z");
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out port)) return null;
                transport = match.Groups[1].Value;
            }
            else if (transport is not ("tcp" or "udp")) return null;
            if (uri.Scheme == "vector" && uri.Fragment.Length > 0) return null;
            var morph = query.GetValueOrDefault("morph", "1");
            if (morph is not ("0" or "1")) return null;
            int? Rate(string name)
            {
                if (!query.TryGetValue(name, out var value)) return null;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate))
                    throw new FormatException("Invalid Nowhere bandwidth.");
                return rate;
            }
            var node = new ProfileItem
            {
                ConfigType = EConfigType.Nowhere,
                Address = uri.IdnHost.Trim('[', ']'),
                Port = port,
                Password = Uri.UnescapeDataString(uri.UserInfo),
                Remarks = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
                Sni = query.GetValueOrDefault("sni", ""),
                CertSha = query.GetValueOrDefault("pin", ""),
                StreamSecurity = Global.StreamSecurity
            };
            if (node.CertSha == "none") node.CertSha = "";
            if (node.Sni == "none") node.Sni = "";
            node.SetProtocolExtra(new()
            {
                NowhereTransport = transport,
                NowhereMorph = morph == "1",
                NowhereUploadMbps = Rate("rate"),
                NowhereDownloadMbps = Rate("etar")
            });
            var error = Validate(node);
            if (error != null) { message = error; return null; }
            message = "";
            return node;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static string? Validate(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        if (node.ConfigType != EConfigType.Nowhere || node.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(node.Address)
            || (!IPAddress.TryParse(node.Address, out _)
                && Uri.CheckHostName(node.Address) != UriHostNameType.Dns)
            || node.Address.Contains('%'))
            return "Invalid Nowhere server address or port.";
        if (!Regex.IsMatch(node.Password ?? "", @"\A[0-9a-f]{32}\z"))
            return "Nowhere requires a shared key of 32 lowercase hexadecimal characters.";
        if (node.CertSha.IsNotEmpty() && !Regex.IsMatch(node.CertSha, @"\A[0-9a-f]{64}\z"))
            return "Nowhere certificate pin must contain 64 lowercase hexadecimal characters.";
        if (node.Sni.IsNotEmpty() && (node.Sni.Length > 253 || node.Sni.Any(c => c > 127)
            || Uri.CheckHostName(node.Sni) != UriHostNameType.Dns || IPAddress.TryParse(node.Sni, out _)))
            return "Nowhere SNI must be an ASCII DNS name.";
        if ((extra.NowhereTransport ?? "tcp") is not ("tcp" or "udp" or "tcp4" or "tcp6" or "udp4" or "udp6"))
            return "Invalid Nowhere transport.";
        if (IPAddress.TryParse(node.Address, out var address))
        {
            var transport = extra.NowhereTransport ?? "tcp";
            if ((transport.EndsWith('4') && address.AddressFamily != AddressFamily.InterNetwork)
                || (transport.EndsWith('6') && address.AddressFamily != AddressFamily.InterNetworkV6))
                return "Nowhere address does not match the selected transport family.";
        }
        if (extra.NowhereUploadMbps < 0 || extra.NowhereDownloadMbps < 0)
            return "Nowhere bandwidth cannot be negative.";
        if (node.GetAllowInsecure())
            return "Nowhere requires certificate verification; use a trusted certificate pin.";
        if (node.CoreType is not (null or ECoreType.Xray or ECoreType.sing_box))
            return "Nowhere bridging requires Xray or sing-box.";
        return null;
    }

    public static string ToUri(ProfileItem node)
    {
        var error = Validate(node);
        if (error != null) throw new ArgumentException(error);
        var extra = node.GetProtocolExtra();
        var transport = extra.NowhereTransport ?? "tcp";
        var query = Options(node);
        var endpoint = $"{GetIpv6(node.Address)}:{node.Port}";
        var role = "nowhere";
        if (transport.Length > 3)
        {
            role = "vector";
            endpoint = $"{GetIpv6(node.Address)}/{transport}:{node.Port}";
        }
        else query.Insert(0, $"net={transport}");
        var fragment = role == "nowhere" && node.Remarks.IsNotEmpty()
            ? "#" + Uri.EscapeDataString(node.Remarks) : "";
        return $"{role}://{node.Password}@{endpoint}?{string.Join("&", query)}{fragment}";
    }

    public static string ToVectorUri(ProfileItem node, int port, string username, string password)
    {
        var error = Validate(node);
        if (error != null) throw new ArgumentException(error);
        if (port is < 1 or > 65535 || username.IsNullOrEmpty() || password.IsNullOrEmpty())
            throw new ArgumentException("Invalid local Nowhere SOCKS listener.");
        var query = Options(node);
        query.Insert(0, $"in={Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@127.0.0.1:{port}");
        return $"vector://{node.Password}@{GetIpv6(node.Address)}/{node.GetProtocolExtra().NowhereTransport ?? "tcp"}:{node.Port}?{string.Join("&", query)}";
    }

    private static List<string> Options(ProfileItem node)
    {
        var extra = node.GetProtocolExtra();
        var query = new List<string> { "morph=" + (extra.NowhereMorph == false ? "0" : "1") };
        if (node.CertSha.IsNotEmpty()) query.Add("pin=" + Uri.EscapeDataString(node.CertSha));
        if (node.Sni.IsNotEmpty()) query.Add("sni=" + Uri.EscapeDataString(node.Sni));
        if (extra.NowhereUploadMbps is { } up) query.Add("rate=" + up.ToString(CultureInfo.InvariantCulture));
        if (extra.NowhereDownloadMbps is { } down) query.Add("etar=" + down.ToString(CultureInfo.InvariantCulture));
        return query;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " "));
}
