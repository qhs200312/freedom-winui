namespace ServiceLib.Services;

/// <summary>Owns only the Vector processes created for one active native-core context.</summary>
public sealed class NowhereBridgeService
{
    private readonly List<ProcessService> _processes = [];
    private readonly object _processLock = new();
    public long WorkingSet64
    {
        get
        {
            lock (_processLock)
            {
                return _processes.Sum(process => process.WorkingSet64);
            }
        }
    }

    public async Task StartAsync(IEnumerable<NowhereBridge> bridges, string executable,
        Func<bool, string, Task>? update, Action<nint>? attachProcess = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var bridge in bridges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var process = new ProcessService(executable, bridge.VectorUri.AppendQuotes(),
                    Path.GetDirectoryName(executable)!, true, false, null, update);
                try { await process.StartAsync(); }
                catch
                {
                    try { await process.StopAsync(); } catch { }
                    process.Dispose();
                    throw;
                }
                lock (_processLock) _processes.Add(process);
                attachProcess?.Invoke(process.Handle);
                await WaitReadyAsync(process, bridge.SocksNode, cancellationToken);
            }
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        ProcessService[] processes;
        lock (_processLock)
        {
            processes = _processes.ToArray();
            _processes.Clear();
        }
        foreach (var process in processes)
        {
            try { await process.StopAsync(); }
            catch (Exception ex) { Logging.SaveLog("Nowhere shutdown", ex); }
            finally { process.Dispose(); }
        }
    }

    private static async Task WaitReadyAsync(ProcessService process, ProfileItem socks, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException("Nowhere Vector exited before its SOCKS listener became ready.");
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(200));
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, socks.Port, attempt.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 2 }, attempt.Token);
                var reply = new byte[2];
                await stream.ReadExactlyAsync(reply, attempt.Token);
                if (!reply.SequenceEqual(new byte[] { 5, 2 })) throw new IOException("Unexpected Nowhere SOCKS authentication method.");
                var user = Encoding.UTF8.GetBytes(socks.Username);
                var password = Encoding.UTF8.GetBytes(socks.Password);
                await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user)
                    .Append((byte)password.Length).Concat(password).ToArray(), attempt.Token);
                await stream.ReadExactlyAsync(reply, attempt.Token);
                if (!reply.SequenceEqual(new byte[] { 1, 0 })) throw new IOException("Nowhere SOCKS listener identity check failed.");
                // Complete a local-only request so Vector does not log an abandoned handshake.
                await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, attempt.Token);
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, attempt.Token);
                if (header[0] != 5 || header[1] != 0 || header[2] != 0 || header[3] is not (1 or 4))
                    throw new IOException("Nowhere SOCKS UDP listener check failed.");
                var endpoint = new byte[(header[3] == 1 ? 4 : 16) + 2];
                await stream.ReadExactlyAsync(endpoint, attempt.Token);
                if (endpoint[^1] == 0 && endpoint[^2] == 0)
                    throw new IOException("Nowhere SOCKS UDP listener returned an invalid port.");
                return;
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            await Task.Delay(50, timeout.Token);
        }
    }
}
