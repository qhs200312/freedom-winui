using System.Buffers.Binary;
using AwesomeAssertions;
using ServiceLib.UdpTest;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class UdpInterceptionRuntimeTests
{
    [Theory]
    [InlineData(ECoreType.Xray, "XRAY_VALIDATION_EXE")]
    [InlineData(ECoreType.sing_box, "SINGBOX_VALIDATION_EXE")]
    public async Task ActualCore_ShouldProxyUnknownUdpAndNeverFallBackToDirect(
        ECoreType coreType, string executableVariable)
    {
        var executable = Environment.GetEnvironmentVariable(executableVariable);
        if (string.IsNullOrEmpty(executable)) Assert.Skip($"Set {executableVariable} to run the actual Core.");
        await using var upstream = new EchoSocksProxy();
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.Node.Port = upstream.Port;
        context.Node.Username = string.Empty;
        context.Node.Password = string.Empty;
        context.AppConfig.Inbound[0].LocalPort = Utils.GetFreePort();
        context.AppConfig.Inbound[0].SniffingEnabled = false;
        context.AppConfig.GuiItem.ProxyStunTraffic = false;
        CoreConfigTestFactory.BindAppManagerConfig(context.AppConfig);
        // Re-select controller ports for this runtime rather than reuse a TUN fixture's cache.
        AppManager.Instance.Reset();
        AppManager.Instance.EnsureLocalPortsAvailable(includeUdpInterception: true).Should().BeTrue();
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.udpInterception);
        var json = UdpInterceptionRoutingTests.Generate(context);
        var directory = Path.Combine(Path.GetTempPath(), $"udp-interception-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? process = null;
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory
            };
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(path);
            process = Process.Start(start)!;
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            (await ServiceLib.Services.Privacy.UdpInterceptionService.ProbeLocalUdpAsync(port))
                .Should().BeTrue("the dedicated SOCKS inbound must be usable");

            using var channel = new Socks5UdpChannel(Global.Loopback, port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            (await channel.EstablishUdpAssociationAsync(timeout.Token)).Should().BeTrue();
            var destinations = new (string Host, ushort Port)[]
            {
                ("1.1.1.1", 3478), ("198.51.100.17", 54321), ("2001:db8::17", 55000)
            };
            foreach (var destination in destinations)
            {
                var payload = Encoding.ASCII.GetBytes($"udp-proxy-{destination.Host}:{destination.Port}");
                await channel.SendAsync(destination.Host, destination.Port, payload);
                var (_, response) = await channel.ReceiveAsync(timeout.Token);
                response.Should().Equal(payload);
                upstream.Observed.Should().Contain(destination);
            }

            // A direct rule would deliver this datagram to the local witness.
            // After the upstream dies, the dedicated route must fail, not bypass.
            await upstream.StopAsync();
            using var witness = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await channel.SendAsync(Global.Loopback, (ushort)((IPEndPoint)witness.Client.LocalEndPoint!).Port,
                Encoding.ASCII.GetBytes("must-not-go-direct"));
            using var noDirect = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            noDirect.CancelAfter(TimeSpan.FromMilliseconds(800));
            var directReceive = async () => await witness.ReceiveAsync(noDirect.Token);
            await directReceive.Should().ThrowAsync<OperationCanceledException>();
            WriteArtifact(coreType, "json", json);
            WriteArtifact(coreType, "results.json", JsonUtils.Serialize(new
            {
                observedProxyDestinations = upstream.Observed.Select(destination =>
                    new { host = destination.Host, port = destination.Port }),
                directFallbackAfterUpstreamFailure = false,
                normalRouting = "all UDP direct",
                stunRouting = false,
                usesRealCore = true,
                usesPacketInterceptionDriver = false
            }));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                WriteArtifact(coreType, "log", (await output!) + (await error!));
                process.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }

    private static void WriteArtifact(ECoreType coreType, string extension, string content)
    {
        var directory = Environment.GetEnvironmentVariable("UDP_INTERCEPTION_AUDIT_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{coreType}-runtime.{extension}"), content);
    }

    internal sealed class EchoSocksProxy : IAsyncDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _clients = [];
        private readonly Task _accept;
        private readonly Task _echo;
        private bool _stopped;
        public ConcurrentQueue<(string Host, ushort Port)> Observed { get; } = new();
        public ConcurrentQueue<(string Host, ushort Port)> ObservedTcp { get; } = new();
        public int Port => ((IPEndPoint)_tcp.LocalEndpoint).Port;

        public EchoSocksProxy()
        {
            _tcp.Start();
            _accept = AcceptAsync();
            _echo = EchoAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true) _clients.Add(HandleAsync(await _tcp.AcceptTcpClientAsync(_stop.Token)));
            }
            catch (OperationCanceledException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var greeting = new byte[2];
                    await stream.ReadExactlyAsync(greeting, _stop.Token);
                    var methods = new byte[greeting[1]];
                    await stream.ReadExactlyAsync(methods, _stop.Token);
                    await stream.WriteAsync(new byte[] { 5, 0 }, _stop.Token);
                    var request = new byte[4];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    if (request[1] is not (1 or 3)) throw new InvalidDataException("Unexpected SOCKS command.");
                    var addressLength = request[3] switch
                    {
                        1 => 4, 4 => 16,
                        3 => await ReadDomainLength(stream),
                        _ => throw new InvalidDataException("Unexpected SOCKS address type.")
                    };
                    var destination = new byte[addressLength + 2];
                    await stream.ReadExactlyAsync(destination, _stop.Token);
                    var reply = new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 };
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8),
                        (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port);
                    await stream.WriteAsync(reply, _stop.Token);
                    if (request[1] == 1)
                    {
                        var host = request[3] == 3
                            ? Encoding.ASCII.GetString(destination, 0, addressLength)
                            : new IPAddress(destination.AsSpan(0, addressLength)).ToString();
                        ObservedTcp.Enqueue((host, BinaryPrimitives.ReadUInt16BigEndian(destination.AsSpan(addressLength))));
                        var buffer = new byte[4096];
                        int length;
                        while ((length = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                            await stream.WriteAsync(buffer.AsMemory(0, length), _stop.Token);
                        return;
                    }
                    while (await stream.ReadAsync(new byte[1], _stop.Token) != 0) { }
                }
                catch (OperationCanceledException) { }
                catch (EndOfStreamException) { }
                catch (IOException) when (_stop.IsCancellationRequested) { }
                catch (IOException ex) when (ex.InnerException is SocketException
                    { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
            }
        }

        private async Task<int> ReadDomainLength(NetworkStream stream)
        {
            var length = new byte[1];
            await stream.ReadExactlyAsync(length, _stop.Token);
            return length[0];
        }

        private async Task EchoAsync()
        {
            try
            {
                while (true)
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    var offset = 4;
                    var length = packet.Buffer[3] switch
                    {
                        1 => 4, 4 => 16,
                        3 => packet.Buffer[offset++],
                        _ => throw new InvalidDataException("Unexpected SOCKS UDP address type.")
                    };
                    var address = packet.Buffer[3] == 3
                        ? Encoding.ASCII.GetString(packet.Buffer, offset, length)
                        : new IPAddress(packet.Buffer.AsSpan(offset, length)).ToString();
                    var port = BinaryPrimitives.ReadUInt16BigEndian(packet.Buffer.AsSpan(offset + length));
                    Observed.Enqueue((address, port));
                    await _udp.SendAsync(packet.Buffer, packet.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            _stop.Cancel();
            await Task.WhenAll(_accept, _echo);
            await Task.WhenAll(_clients);
            _tcp.Stop();
            _udp.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stop.Dispose();
        }
    }
}
