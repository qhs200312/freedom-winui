namespace ServiceLib.Services.Statistics;

public class StatisticsSingboxService
{
    private bool _exitFlag;
    private readonly Func<ServerSpeedItem, Task>? _updateFunc;
    private readonly SingboxProxyTrafficTracker _trafficTracker = new();

    public StatisticsSingboxService(Config config, Func<ServerSpeedItem, Task> updateFunc)
    {
        _updateFunc = updateFunc;
        _exitFlag = false;

        _ = Task.Run(Run);
    }

    public void Close()
    {
        _exitFlag = true;
        _trafficTracker.Reset();
    }

    private async Task Run()
    {
        await Task.Delay(5000);

        while (!_exitFlag)
        {
            await Task.Delay(1000);
            try
            {
                if (!AppManager.Instance.IsRunningCore(ECoreType.sing_box))
                {
                    _trafficTracker.Reset();
                    continue;
                }

                var connections = await ClashApiManager.Instance.GetClashConnectionsAsync();
                if (connections is null)
                {
                    continue;
                }

                await _updateFunc?.Invoke(_trafficTracker.Update(connections));
            }
            catch
            {
                // ignored
            }
        }
    }
}

public sealed class SingboxProxyTrafficTracker
{
    private const ulong BytesPerStorageUnit = 1000;
    private Dictionary<string, ConnectionTrafficSnapshot> _previous = new(StringComparer.Ordinal);
    private bool _hasSnapshot;

    public ServerSpeedItem Update(ClashConnections snapshot)
    {
        var current = CreateSnapshot(snapshot.connections);
        if (!_hasSnapshot)
        {
            _previous = current;
            _hasSnapshot = true;
            return new();
        }

        ulong proxyUp = 0;
        ulong proxyDown = 0;
        foreach (var (id, connection) in current)
        {
            if (!connection.IsProxy)
            {
                continue;
            }

            var previous = _previous.GetValueOrDefault(id);
            proxyUp = AddSaturating(proxyUp, GetDelta(connection.Upload, previous.Upload));
            proxyDown = AddSaturating(proxyDown, GetDelta(connection.Download, previous.Download));
        }

        _previous = current;
        return new()
        {
            ProxyUp = ToStorageUnit(proxyUp),
            ProxyDown = ToStorageUnit(proxyDown)
        };
    }

    public void Reset()
    {
        _previous.Clear();
        _hasSnapshot = false;
    }

    private static Dictionary<string, ConnectionTrafficSnapshot> CreateSnapshot(List<ConnectionItem>? connections)
    {
        var snapshot = new Dictionary<string, ConnectionTrafficSnapshot>(StringComparer.Ordinal);
        foreach (var connection in connections ?? [])
        {
            if (connection.id.IsNullOrEmpty())
            {
                continue;
            }

            snapshot[connection.id!] = new(
                connection.upload,
                connection.download,
                connection.chains?.Any(IsProxyChain) == true);
        }
        return snapshot;
    }

    private static bool IsProxyChain(string? tag)
    {
        if (tag.IsNullOrEmpty())
        {
            return false;
        }

        return tag.Equals(Global.ProxyTag, StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith(Global.ProxyTag, StringComparison.OrdinalIgnoreCase)
            || tag.Contains($"-{Global.ProxyTag}-", StringComparison.OrdinalIgnoreCase)
            || tag.EndsWith($"-{Global.ProxyTag}", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong GetDelta(ulong current, ulong previous)
    {
        return current >= previous ? current - previous : current;
    }

    private static ulong AddSaturating(ulong total, ulong value)
    {
        return ulong.MaxValue - total < value ? ulong.MaxValue : total + value;
    }

    private static long ToStorageUnit(ulong bytes)
    {
        var value = bytes / BytesPerStorageUnit;
        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    private readonly record struct ConnectionTrafficSnapshot(ulong Upload, ulong Download, bool IsProxy);
}
