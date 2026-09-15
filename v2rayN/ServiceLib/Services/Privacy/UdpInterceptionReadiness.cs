namespace ServiceLib.Services.Privacy;

public sealed class UdpInterceptionReadiness(IEnumerable<string> applications, int port)
{
    private readonly HashSet<string> _remaining = new(applications, StringComparer.OrdinalIgnoreCase);
    private readonly string _endpoint = $"127.0.0.1:{port}";
    private bool _running;
    private readonly object _sync = new();

    public bool Observe(string line)
    {
        lock (_sync)
        {
            foreach (var app in _remaining.ToArray())
            {
                if (line.Contains($"Successfully associated {app} to {_endpoint} SOCKS5 proxy with protocols UDP ",
                    StringComparison.OrdinalIgnoreCase))
                {
                    _remaining.Remove(app);
                }
            }
            _running |= line.Contains("ProxiFyre Service is running...", StringComparison.Ordinal);
            return _running && _remaining.Count == 0;
        }
    }
}
