using AwesomeAssertions;
using System.Buffers.Binary;
using ServiceLib.UdpTest;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class RoutingNodeRuntimeTests
{
    [Theory]
    [InlineData(ECoreType.Xray, "XRAY_VALIDATION_EXE")]
    [InlineData(ECoreType.sing_box, "SINGBOX_VALIDATION_EXE")]
    public async Task ActualCore_ShouldUseRuleNodeAndCurrentNodeForUnmatchedTraffic(
        ECoreType coreType, string executableVariable)
    {
        var executable = Environment.GetEnvironmentVariable(executableVariable);
        if (string.IsNullOrEmpty(executable)) Assert.Skip($"Set {executableVariable} to run the actual Core.");
        await using var current = new UdpInterceptionRuntimeTests.EchoSocksProxy();
        await using var selected = new UdpInterceptionRuntimeTests.EchoSocksProxy();
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.AppConfig.GuiItem.EnableUdpInterception = false;
        context.AppConfig.GuiItem.ProxyStunTraffic = false;
        context.AppConfig.Inbound[0].SniffingEnabled = false;
        context.AppConfig.Inbound[0].LocalPort = Utils.GetFreePort();
        context.Node.Port = current.Port;
        context.Node.Username = string.Empty;
        context.Node.Password = string.Empty;
        var ruleNode = CoreConfigTestFactory.CreateSocksNode(coreType, "rule-node", "same");
        ruleNode.Port = selected.Port;
        ruleNode.Username = string.Empty;
        ruleNode.Password = string.Empty;
        context.AllProxiesMap[RoutingNodeSelection.Reference(ruleNode.IndexId)] = ruleNode;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Ip = ["1.1.1.1/32"], Domain = ["full:service.test"], OutboundTag = "node:rule-node" },
            new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
        });
        // Earlier TUN fixtures can leave a cached controller port that is occupied.
        AppManager.Instance.Reset();
        var json = UdpInterceptionRoutingTests.Generate(context);
        var directory = Path.Combine(Path.GetTempPath(), $"routing-runtime-{Guid.NewGuid():N}");
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
            foreach (var argument in new[] { "run", "-c", path }) start.ArgumentList.Add(argument);
            process = Process.Start(start)!;
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var port = context.AppConfig.Inbound[0].LocalPort;
            (await ServiceLib.Services.Privacy.UdpInterceptionService.ProbeLocalUdpAsync(port)).Should().BeTrue();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            foreach (var host in new[] { "1.1.1.1", "198.51.100.17" })
            {
                // Core routing selects an outbound per UDP association/flow.
                using var channel = new Socks5UdpChannel(Global.Loopback, port);
                (await channel.EstablishUdpAssociationAsync(timeout.Token)).Should().BeTrue();
                var payload = Encoding.ASCII.GetBytes(host);
                await channel.SendAsync(host, 54321, payload);
                var (_, reply) = await channel.ReceiveAsync(timeout.Token);
                reply.Should().Equal(payload);
            }
            selected.Observed.Should().Equal(("1.1.1.1", (ushort)54321));
            current.Observed.Should().Equal(("198.51.100.17", (ushort)54321));
            foreach (var host in new[] { "1.1.1.1", "198.51.100.17", "service.test" })
                await CheckTcpAsync(port, host, timeout.Token);
            selected.ObservedTcp.Should().Equal(("1.1.1.1", (ushort)54321), ("service.test", (ushort)54321));
            current.ObservedTcp.Should().Equal(("198.51.100.17", (ushort)54321));
            var artifacts = Environment.GetEnvironmentVariable("ROUTING_AUDIT_ARTIFACTS");
            if (!string.IsNullOrEmpty(artifacts))
            {
                Directory.CreateDirectory(artifacts);
                await File.WriteAllTextAsync(Path.Combine(artifacts, $"{coreType}-runtime.json"),
                    json, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(artifacts, $"{coreType}-results.json"),
                    JsonUtils.Serialize(new
                    {
                        ruleNode = selected.Observed.Select(target => new { target.Host, target.Port }),
                        currentNode = current.Observed.Select(target => new { target.Host, target.Port }),
                        tcpRuleNode = selected.ObservedTcp.Select(target => new { target.Host, target.Port }),
                        tcpCurrentNode = current.ObservedTcp.Select(target => new { target.Host, target.Port }),
                        usesActualCore = true,
                        usesTunDriver = false
                    }), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                var stdout = await output!;
                var stderr = await error!;
                var artifacts = Environment.GetEnvironmentVariable("ROUTING_AUDIT_ARTIFACTS");
                if (!string.IsNullOrEmpty(artifacts))
                {
                    Directory.CreateDirectory(artifacts);
                    await File.WriteAllTextAsync(Path.Combine(artifacts, $"{coreType}-runtime.log"),
                        stdout + Environment.NewLine + stderr, CancellationToken.None);
                }
                process.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }

    private static async Task CheckTcpAsync(int inboundPort, string host, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, inboundPort, token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var method = new byte[2];
        await stream.ReadExactlyAsync(method, token);
        method.Should().Equal(5, 0);
        var address = IPAddress.TryParse(host, out var ip) ? ip.GetAddressBytes()
            : new[] { (byte)host.Length }.Concat(Encoding.ASCII.GetBytes(host)).ToArray();
        var request = new byte[address.Length + 6];
        request[0] = 5;
        request[1] = 1;
        request[3] = ip is null ? (byte)3 : (byte)1;
        address.CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(request.Length - 2), 54321);
        await stream.WriteAsync(request, token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        header[1].Should().Be(0);
        var length = header[3] switch
        {
            1 => 4, 4 => 16,
            3 => await ReadLengthAsync(),
            _ => throw new InvalidDataException("Unexpected SOCKS reply address.")
        };
        await stream.ReadExactlyAsync(new byte[length + 2], token);
        var payload = Encoding.ASCII.GetBytes(host);
        await stream.WriteAsync(payload, token);
        var reply = new byte[payload.Length];
        await stream.ReadExactlyAsync(reply, token);
        reply.Should().Equal(payload);

        async Task<int> ReadLengthAsync()
        {
            var size = new byte[1];
            await stream.ReadExactlyAsync(size, token);
            return size[0];
        }
    }
}
