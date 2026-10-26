using System.Buffers.Binary;
using AwesomeAssertions;
using ServiceLib.UdpTest;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public sealed class NowhereRuntimeTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(ECoreType.Xray, "tcp", true)]
    [InlineData(ECoreType.Xray, "tcp", false)]
    [InlineData(ECoreType.Xray, "udp", true)]
    [InlineData(ECoreType.Xray, "udp", false)]
    [InlineData(ECoreType.sing_box, "tcp", true)]
    [InlineData(ECoreType.sing_box, "udp", true)]
    public async Task OfficialCore_ShouldCarryTcpUdpAndDnsThroughGeneratedNativeConfig(
        ECoreType coreType, string transport, bool morph)
    {
        await using var fixture = await Fixture.StartAsync(coreType, transport, morph);
        await fixture.CheckTrafficAsync();
        fixture.BridgeMessages.Should().NotContain(message =>
            message.Contains("failed to read header: early eof", StringComparison.Ordinal));
        fixture.Original.ConfigType.Should().Be(EConfigType.Nowhere);
        fixture.Context.Node.ConfigType.Should().Be(EConfigType.SOCKS);
        fixture.Context.NowhereBridges.Should().ContainSingle();
        fixture.ConfigJson.Should().NotContain(Key).And.NotContain("vector://");
        fixture.ConfigJson.Should().Contain(fixture.Context.Node.Port.ToString());
        fixture.BridgeService.WorkingSet64.Should().BeGreaterThan(0);
        await fixture.BridgeService.StopAsync();
        Utils.IsTcpPortAvailable(fixture.Context.Node.Port, IPAddress.Loopback).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongPinOrSharedKey_ShouldFailClosed(bool wrongKey)
    {
        await using var fixture = await Fixture.StartAsync(ECoreType.Xray, "tcp", true, wrongKey ? "key" : "pin");
        var attempt = async () => await fixture.CheckTcpAsync();
        await attempt.Should().ThrowAsync<Exception>();
        fixture.TargetConnections.Should().Be(0);
    }

    [Fact]
    public async Task MissingExecutable_ShouldNotLeavePartialBridgeState()
    {
        var node = NowhereFmt.Resolve($"nowhere://{Key}@example.test:2000", out _)!;
        var bridge = NowhereBridge.Create(node, []);
        var service = new NowhereBridgeService();
        var attempt = () => service.StartAsync([bridge], Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), null);
        await attempt.Should().ThrowAsync<Exception>();
        await service.StopAsync();
        service.WorkingSet64.Should().Be(0);
    }

    [Fact]
    public async Task SeparateContextsReferencingSameNode_ShouldOwnBothLocalListeners()
    {
        await using var fixture = await Fixture.StartAsync(ECoreType.Xray, "tcp", true);
        var original = fixture.Context.NowhereBridges.Values.Single();
        var second = NowhereBridge.Create(fixture.Original, [original.SocksNode.Port],
            fixture.Context.AppConfig.Inbound[0].LocalPort);
        second.Node.IndexId.Should().Be(original.Node.IndexId);
        second.SocksNode.Port.Should().NotBe(original.SocksNode.Port);
        await fixture.BridgeService.StopAsync();
        await fixture.BridgeService.StartAsync([original, second],
            Environment.GetEnvironmentVariable("NOWHERE_VALIDATION_EXE")!, null,
            cancellationToken: TestContext.Current.CancellationToken);
        await fixture.CheckTcpAsync();
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, second.SocksNode.Port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 2 }, timeout.Token);
        var reply = new byte[2];
        await stream.ReadExactlyAsync(reply, timeout.Token);
        reply.Should().Equal(5, 2);
        var user = Encoding.UTF8.GetBytes(second.SocksNode.Username);
        var password = Encoding.UTF8.GetBytes(second.SocksNode.Password);
        await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user)
            .Append((byte)password.Length).Concat(password).ToArray(), timeout.Token);
        await stream.ReadExactlyAsync(reply, timeout.Token);
        reply.Should().Equal(1, 0);
        await fixture.BridgeService.StopAsync();
        Utils.IsTcpPortAvailable(original.SocksNode.Port, IPAddress.Loopback).Should().BeTrue();
        Utils.IsTcpPortAvailable(second.SocksNode.Port, IPAddress.Loopback).Should().BeTrue();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nowhere-runtime-" + Guid.NewGuid().ToString("N"));
        private readonly List<Process> _processes = [];
        private readonly List<Task> _readers = [];
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(30));
        private readonly Config _originalConfig = AppManager.Instance.Config;
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private Task? _echoTask;
        private Task? _udpTask;
        private int _connections;
        public int TargetConnections => Volatile.Read(ref _connections);
        public ProfileItem Original { get; private set; } = null!;
        public CoreConfigContext Context { get; private set; } = null!;
        public string ConfigJson { get; private set; } = "";
        public NowhereBridgeService BridgeService { get; } = new();
        public ConcurrentQueue<string> BridgeMessages { get; } = new();

        public static async Task<Fixture> StartAsync(ECoreType coreType, string transport, bool morph, string? invalid = null)
        {
            var nowhere = Environment.GetEnvironmentVariable("NOWHERE_VALIDATION_EXE");
            var native = Environment.GetEnvironmentVariable(coreType == ECoreType.Xray ? "XRAY_VALIDATION_EXE" : "SINGBOX_VALIDATION_EXE");
            if (string.IsNullOrEmpty(nowhere) || string.IsNullOrEmpty(native))
                Assert.Skip("Set NOWHERE_VALIDATION_EXE and the native core executable to run the loopback integration.");
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture._directory);
                fixture._tcp.Start();
                fixture._echoTask = fixture.EchoTcpAsync();
                fixture._udpTask = fixture.EchoUdpAsync();
                var portalPort = Utils.GetFreePort();
                while (!Utils.IsUdpPortAvailable(portalPort, IPAddress.Loopback)) portalPort = Utils.GetFreePort();
                var pin = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.StartProcess(nowhere!, [$"portal://{Key}@127.0.0.1/{transport}:{portalPort}?morph={(morph ? 1 : 0)}"], line =>
                {
                    var match = Regex.Match(line, @"\b[0-9a-f]{64}\b");
                    if (match.Success) pin.TrySetResult(match.Value);
                });
                var fingerprint = await pin.Task.WaitAsync(TimeSpan.FromSeconds(5), fixture._lifetime.Token);
                var key = invalid == "key" ? new string('a', 32) : Key;
                if (invalid == "pin") fingerprint = new string('b', 64);
                var node = NowhereFmt.Resolve(
                    $"nowhere://{key}@127.0.0.1:{portalPort}?net={transport}&morph={(morph ? 1 : 0)}&pin={fingerprint}&rate=100&etar=100#Loopback", out _)!;
                node.IndexId = "nowhere-fixture";
                node.CoreType = coreType;
                fixture.Original = node;
                var config = CoreConfigTestFactory.CreateConfig();
                config.Inbound[0].LocalPort = Utils.GetFreePort();
                config.Inbound[0].SniffingEnabled = false;
                config.GuiItem.EnableUdpInterception = false;
                config.GuiItem.ProxyStunTraffic = false;
                AppManager.Instance.Reset();
                CoreConfigTestFactory.BindAppManagerConfig(config);
                var context = new CoreConfigContext
                {
                    Node = node, RunCoreType = coreType, AppConfig = config,
                    SimpleDnsItem = config.SimpleDNSItem,
                    RoutingItem = new RoutingItem
                    {
                        RuleSet = JsonUtils.Serialize(new List<RulesItem>
                        {
                            new() { Network = "tcp,udp", OutboundTag = Global.ProxyTag }
                        })
                    },
                    IsWindows = Utils.IsWindows()
                };
                var (resolved, result) = await CoreConfigContextBuilder.ResolveNodeAsync(context, node, false);
                result.Errors.Should().BeEmpty();
                fixture.Context = context with { Node = resolved };
                var repeated = await CoreConfigContextBuilder.ResolveNodeAsync(fixture.Context, node, false);
                repeated.Item1.Port.Should().Be(resolved.Port);
                fixture.Context.NowhereBridges.Should().ContainSingle();
                var generated = coreType == ECoreType.Xray
                    ? new CoreConfigV2rayService(fixture.Context).GenerateClientConfigContent()
                    : new CoreConfigSingboxService(fixture.Context).GenerateClientConfigContent();
                generated.Success.Should().BeTrue(generated.Msg);
                fixture.ConfigJson = generated.Data!.ToString()!;
                var path = Path.Combine(fixture._directory, "native.json");
                await File.WriteAllTextAsync(path, fixture.ConfigJson, fixture._lifetime.Token);
                await fixture.BridgeService.StartAsync(fixture.Context.NowhereBridges.Values, nowhere!,
                    (_, message) =>
                    {
                        fixture.BridgeMessages.Enqueue(message);
                        return Task.CompletedTask;
                    },
                    cancellationToken: fixture._lifetime.Token);
                fixture.StartProcess(native!, ["run", "-c", path], _ => { });
                var localPort = fixture.Context.AppConfig.Inbound[0].LocalPort;
                for (var i = 0; i < 80; i++)
                {
                    try
                    {
                        using var client = new TcpClient();
                        await client.ConnectAsync(IPAddress.Loopback, localPort, fixture._lifetime.Token);
                        return fixture;
                    }
                    catch (SocketException) { await Task.Delay(50, fixture._lifetime.Token); }
                }
                throw new IOException("Generated native proxy did not become ready.");
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        private void StartProcess(string executable, string[] arguments, Action<string> observe)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = _directory
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start)!;
            _processes.Add(process);
            async Task ReadAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line) observe(line);
            }
            _readers.Add(ReadAsync(process.StandardOutput));
            _readers.Add(ReadAsync(process.StandardError));
        }

        public async Task CheckTrafficAsync()
        {
            await CheckTcpAsync();
            using var channel = new Socks5UdpChannel(Global.Loopback, Context.AppConfig.Inbound[0].LocalPort);
            (await channel.EstablishUdpAssociationAsync(_lifetime.Token)).Should().BeTrue();
            var target = (IPEndPoint)_udp.Client.LocalEndPoint!;
            foreach (var size in new[] { 48, 4000 })
            {
                var payload = RandomNumberGenerator.GetBytes(size);
                await channel.SendAsync(target, payload);
                var (_, reply) = await channel.ReceiveAsync(_lifetime.Token);
                reply.Should().Equal(payload);
            }
            // A real DNS wire-format query/response over the same proxied UDP path.
            byte[] query = [0xd0, 0x0d, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, 101, 120, 97, 109, 112, 108, 101, 4, 116, 101, 115, 116, 0, 0, 1, 0, 1];
            await channel.SendAsync(target, query);
            var (_, response) = await channel.ReceiveAsync(_lifetime.Token);
            response[0].Should().Be(0xd0);
            response[1].Should().Be(0x0d);
            (response[2] & 0x80).Should().Be(0x80);
        }

        public async Task CheckTcpAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Context.AppConfig.Inbound[0].LocalPort, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var method = new byte[2];
            await stream.ReadExactlyAsync(method, timeout.Token);
            method.Should().Equal(5, 0);
            var targetPort = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            var request = new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 0 };
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)targetPort);
            await stream.WriteAsync(request, timeout.Token);
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            header[1].Should().Be(0);
            await stream.ReadExactlyAsync(new byte[(header[3] == 4 ? 16 : 4) + 2], timeout.Token);
            var payload = RandomNumberGenerator.GetBytes(4096);
            await stream.WriteAsync(payload, timeout.Token);
            var reply = new byte[payload.Length];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            reply.Should().Equal(payload);
        }

        private async Task EchoTcpAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _tcp.AcceptTcpClientAsync(_lifetime.Token);
                    Interlocked.Increment(ref _connections);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, _lifetime.Token)) > 0)
                        await stream.WriteAsync(buffer.AsMemory(0, count), _lifetime.Token);
                }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }

        private async Task EchoUdpAsync()
        {
            try
            {
                while (true)
                {
                    var message = await _udp.ReceiveAsync(_lifetime.Token);
                    if (message.Buffer.Length == 30 && message.Buffer[0] == 0xd0 && message.Buffer[1] == 0x0d)
                        message.Buffer[2] |= 0x80;
                    await _udp.SendAsync(message.Buffer, message.RemoteEndPoint, _lifetime.Token);
                }
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            foreach (var process in _processes)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            await BridgeService.StopAsync();
            _tcp.Stop();
            _udp.Dispose();
            await Task.WhenAll(_readers);
            if (_echoTask != null) await _echoTask;
            if (_udpTask != null) await _udpTask;
            CoreConfigTestFactory.BindAppManagerConfig(_originalConfig);
            _lifetime.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
