namespace ServiceLib.Services.Privacy;

public sealed class UdpInterceptionReadiness
{
    private readonly HashSet<string> _remaining;
    private readonly string _endpoint;
    private readonly string? _logPath;
    private long _logOffset;
    private string _pending = "";
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private bool _running;
    private readonly object _sync = new();

    public UdpInterceptionReadiness(IEnumerable<string> applications, int port, string? logPath = null)
    {
        _remaining = new(applications, StringComparer.OrdinalIgnoreCase);
        _endpoint = $"127.0.0.1:{port}";
        _logPath = logPath;
        _logOffset = logPath is not null && File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
    }

    public string PendingDescription
    {
        get
        {
            lock (_sync)
            {
                return $"驱动就绪消息：{(_running ? "已收到" : "未收到")}；应用关联："
                    + (_remaining.Count == 0 ? "已全部确认" : string.Join(", ", _remaining));
            }
        }
    }

    public async Task<bool> ObserveFileAsync(CancellationToken cancellationToken = default)
    {
        if (_logPath is not null)
        {
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _logOffset)
                {
                    _logOffset = 0;
                    _pending = "";
                    _decoder.Reset();
                }
                stream.Position = _logOffset;
                var bytes = new byte[16 * 1024];
                var count = await stream.ReadAsync(bytes, cancellationToken);
                _logOffset += count;
                var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
                var length = _decoder.GetChars(bytes, 0, count, chars, 0, false);
                var lines = (_pending + new string(chars, 0, length)).Split('\n');
                _pending = lines[^1];
                if (_pending.Length > 16 * 1024) _pending = "";
                foreach (var line in lines.SkipLast(1)) Observe(line.TrimEnd('\r'));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }
        lock (_sync) return _running && _remaining.Count == 0;
    }

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
