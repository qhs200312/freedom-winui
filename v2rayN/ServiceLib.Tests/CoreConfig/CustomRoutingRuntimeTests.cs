using AwesomeAssertions;
using System.Buffers.Binary;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public class CustomRoutingRuntimeTests
{
    [Theory]
    [InlineData(ECoreType.Xray, "XRAY_VALIDATION_EXE")]
    [InlineData(ECoreType.sing_box, "SINGBOX_VALIDATION_EXE")]
    public async Task ActualCore_ShouldConnectCustomWebsiteDirectlyBeforeProxyCatchAll(
        ECoreType coreType, string executableVariable)
    {
        var executable = Environment.GetEnvironmentVariable(executableVariable);
        if (string.IsNullOrEmpty(executable)) Assert.Skip($"Set {executableVariable} to run the actual core.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = (ushort)((IPEndPoint)target.LocalEndpoint).Port;
        await using var proxy = new UdpInterceptionRuntimeTests.EchoSocksProxy();
        var context = UdpInterceptionRoutingTests.CreateContext(coreType);
        context.AppConfig.GuiItem.EnableUdpInterception = false;
        context.AppConfig.GuiItem.ProxyStunTraffic = false;
        context.AppConfig.Inbound[0].SniffingEnabled = false;
        context.AppConfig.Inbound[0].LocalPort = Utils.GetFreePort();
        context.AppConfig.SimpleDNSItem.Hosts = "direct.test 127.0.0.1";
        context.AppConfig.SimpleDNSItem.Strategy4Freedom = "UseIPv4";
        context.AppConfig.RoutingBasicItem.CustomRules =
        [
            new() { Domain = ["domain:direct.test"], OutboundTag = Global.DirectTag }
        ];
        context.Node.Port = proxy.Port;
        context.Node.Username = string.Empty;
        context.Node.Password = string.Empty;
        context.RoutingItem!.RuleSet = JsonUtils.Serialize(new List<RulesItem>
        {
            new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
        });
        AppManager.Instance.Reset();
        var json = UdpInterceptionRoutingTests.Generate(context);
        var directory = Path.Combine(Path.GetTempPath(), $"custom-direct-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? core = null;
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, json, timeout.Token);
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory
            };
            foreach (var argument in new[] { "run", "-c", path }) start.ArgumentList.Add(argument);
            core = Process.Start(start)!;
            output = core.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = core.StandardError.ReadToEndAsync(CancellationToken.None);
            var inboundPort = context.AppConfig.Inbound[0].LocalPort;
            (await ServiceLib.Services.Privacy.UdpInterceptionService.ProbeLocalUdpAsync(inboundPort)).Should().BeTrue();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, inboundPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var method = new byte[2];
            await stream.ReadExactlyAsync(method, timeout.Token);
            method.Should().Equal(5, 0);
            var domain = Encoding.ASCII.GetBytes("direct.test");
            var request = new byte[domain.Length + 7];
            request[0] = 5;
            request[1] = 1;
            request[3] = 3;
            request[4] = (byte)domain.Length;
            domain.CopyTo(request, 5);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(request.Length - 2), targetPort);
            await stream.WriteAsync(request, timeout.Token);
            using var directConnection = await target.AcceptTcpClientAsync(timeout.Token);
            var reply = new byte[4];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            reply[1].Should().Be(0);
            var addressLength = reply[3] == 1 ? 4 : 16;
            await stream.ReadExactlyAsync(new byte[addressLength + 2], timeout.Token);
            var payload = Encoding.ASCII.GetBytes("custom-website-direct");
            await stream.WriteAsync(payload, timeout.Token);
            var received = new byte[payload.Length];
            await directConnection.GetStream().ReadExactlyAsync(received, timeout.Token);
            received.Should().Equal(payload);
            proxy.ObservedTcp.Should().BeEmpty();
        }
        finally
        {
            if (core is not null)
            {
                if (!core.HasExited) core.Kill(true);
                await core.WaitForExitAsync(CancellationToken.None);
                var log = await output! + Environment.NewLine + await error!;
                var artifacts = Environment.GetEnvironmentVariable("ROUTING_AUDIT_ARTIFACTS");
                if (!string.IsNullOrEmpty(artifacts))
                {
                    Directory.CreateDirectory(artifacts);
                    await File.WriteAllTextAsync(Path.Combine(artifacts, $"{coreType}-custom-direct.json"), json, CancellationToken.None);
                    await File.WriteAllTextAsync(Path.Combine(artifacts, $"{coreType}-custom-direct.log"), log, CancellationToken.None);
                }
                core.Dispose();
            }
            Directory.Delete(directory, true);
        }
    }
}
