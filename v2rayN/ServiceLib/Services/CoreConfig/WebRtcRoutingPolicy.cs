namespace ServiceLib.Services.CoreConfig;

public static class WebRtcRoutingPolicy
{
    public const string Ports = "3478,5349,19302-19309";

    public static readonly string[] Domains =
    [
        "stun.l.google.com", "stun1.l.google.com", "stun2.l.google.com",
        "stun3.l.google.com", "stun4.l.google.com", "stun.cloudflare.com",
        "global.stun.twilio.com", "stun.services.mozilla.com"
    ];

    public static readonly string[] LocalNetworks =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "255.255.255.255/32",
        "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8"
    ];

    private static readonly IPNetwork2[] ExcludedAddressRanges = LocalNetworks
        .Concat(["198.18.0.0/15"])
        .Select(IPNetwork2.Parse).ToArray();

    public static List<string> FilterAddresses(IEnumerable<string> addresses)
    {
        return addresses.Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Select(address => address!.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => !ExcludedAddressRanges.Any(range =>
                range.AddressFamily == address.AddressFamily && range.Contains(address)))
            .Select(address => address.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<StunAddressResolution> ResolveAddressesAsync(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolver ??= (host, token) => Dns.GetHostAddressesAsync(host, token);
        var results = await Task.WhenAll(Domains.Select(async domain =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var addresses = await resolver(domain, timeout.Token).WaitAsync(timeout.Token);
                var filtered = FilterAddresses(addresses.Select(address => address.ToString()));
                return (Domain: domain, Addresses: filtered, Failed: filtered.Count == 0);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (Domain: domain, Addresses: new List<string>(), Failed: true);
            }
        }));
        return new(FilterAddresses(results.SelectMany(result => result.Addresses)),
            results.Where(result => result.Failed).Select(result => result.Domain).ToList());
    }
}

public sealed record StunAddressResolution(List<string> Addresses, List<string> FailedDomains);
