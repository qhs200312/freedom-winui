using System.Net.Http;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public sealed class ClashControllerSecurityTests
{
    [Fact]
    public async Task GeneratedClashConfig_OverridesProfileAndMixinControllerCredentials()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.ClashUIItem.EnableMixinContent = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var directory = Directory.CreateTempSubdirectory("clash-controller-").FullName;
        var source = Path.Combine(directory, "profile.yaml");
        var output = Path.Combine(directory, "generated.yaml");
        var mixin = Utils.GetConfigPath(Global.ClashMixinConfigFileName);
        var originalMixin = File.Exists(mixin) ? await File.ReadAllBytesAsync(mixin, TestContext.Current.CancellationToken) : null;
        try
        {
            await File.WriteAllTextAsync(source,
                "secret: profile-secret\nexternal-controller: 0.0.0.0:9090\nproxies: []\nproxy-groups: []\nrules: []\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(mixin, "secret: ''\nexternal-controller: 0.0.0.0:9091\n", TestContext.Current.CancellationToken);
            var result = await new CoreConfigClashService(config).GenerateClientCustomConfig(
                new ProfileItem { Address = source, Remarks = "fixture" }, output);

            Assert.True(result.Success, result.Msg);
            var generated = YamlUtils.FromYaml<Dictionary<string, object>>(
                await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken))!;
            Assert.Equal($"{Global.Loopback}:{AppManager.Instance.StatePort2}", generated["external-controller"].ToString());
            Assert.True(generated["secret"].ToString() == ClashApiManager.Instance.ApiSecret);
            Assert.True(Guid.TryParse(generated["secret"].ToString(), out _));
            Assert.True(new ClashApiManager().ApiSecret != ClashApiManager.Instance.ApiSecret);
        }
        finally
        {
            if (originalMixin != null) await File.WriteAllBytesAsync(mixin, originalMixin, CancellationToken.None);
            else File.Delete(mixin);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedSingboxController_UsesTheClientSessionSecret(bool template)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box), ECoreType.sing_box);
        if (template)
        {
            context = context with
            {
                FullConfigTemplate = new FullConfigTemplateItem
                {
                    Enabled = true,
                    Config = new JsonObject
                    {
                        ["experimental"] = new JsonObject
                        {
                            ["clash_api"] = new JsonObject
                            {
                                ["external_controller"] = $"127.0.0.1:{AppManager.Instance.StatePort2}",
                                ["secret"] = "template-secret",
                            }
                        }
                    }.ToJsonString()
                }
            };
        }
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        Assert.True(result.Success, result.Msg);
        var api = JsonNode.Parse(result.Data!.ToString()!)!["experimental"]!["clash_api"]!;
        Assert.True(api["secret"]!.GetValue<string>() == ClashApiManager.Instance.ApiSecret);
    }

    [Fact]
    public async Task AllControllerOperations_SendBearerWithoutChangingJsonBodiesOrLeakingToGeneralHttp()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var previousPort = typeof(AppManager).GetField("_statePort2", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var savedPort = previousPort.GetValue(AppManager.Instance);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        previousPort.SetValue(AppManager.Instance, port);
        var manager = new ClashApiManager();
        var requests = new List<(string Line, bool HasAuthorization, bool Authenticated, string Body)>();
        var serving = ServeAsync();
        try
        {
            Assert.NotNull(await manager.GetClashProxiesAsync().WaitAsync(timeout.Token));
            await manager.ClashSetActiveProxy("group", "node").WaitAsync(timeout.Token);
            await manager.ClashConfigUpdate(new() { ["mode"] = "rule" }).WaitAsync(timeout.Token);
            await manager.ClashConfigReload("fixture.yaml").WaitAsync(timeout.Token);
            Assert.NotNull(await manager.GetClashConnectionsAsync().WaitAsync(timeout.Token));
            await manager.ClashConnectionClose("connection-id").WaitAsync(timeout.Token);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.ClashProxiesDelayTest(false, [new() { Name = "node", Type = "socks5" }], (item, _) =>
            {
                if (item == null) completed.TrySetResult();
                return Task.CompletedTask;
            });
            await completed.Task.WaitAsync(timeout.Token);
            await HttpClientHelper.Instance.TryGetAsync($"http://127.0.0.1:{port}/general").WaitAsync(timeout.Token);
            await serving.WaitAsync(timeout.Token);

            Assert.Equal(10, requests.Count);
            Assert.All(requests.Take(9), request =>
                Assert.True(request.Authenticated, "Controller bearer header must match."));
            Assert.False(requests[^1].HasAuthorization);
            Assert.Contains(requests, r => r.Line.StartsWith("PUT /proxies/group ")
                && JsonNode.Parse(r.Body)!["name"]!.GetValue<string>() == "node");
            Assert.Contains(requests, r => r.Line.StartsWith("PATCH /configs ")
                && JsonNode.Parse(r.Body)!["mode"]!.GetValue<string>() == "rule");
            Assert.Contains(requests, r => r.Line.StartsWith("PUT /configs?force=true ")
                && JsonNode.Parse(r.Body)!["path"]!.GetValue<string>() == "fixture.yaml");
            Assert.Contains(requests, r => r.Line.StartsWith("DELETE /connections/connection-id "));
            Assert.Contains(requests, r => r.Line.StartsWith("GET /proxies/node/delay?"));
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            previousPort.SetValue(AppManager.Instance, savedPort);
            try { await serving; }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        async Task ServeAsync()
        {
            for (var i = 0; i < 10; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token) ?? "";
                string? authorization = null;
                var contentLength = 0;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                        authorization = header["Authorization:".Length..].Trim();
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(header["Content-Length:".Length..].Trim());
                }
                var body = new char[contentLength];
                if (body.Length > 0)
                    Assert.Equal(body.Length, await reader.ReadBlockAsync(body.AsMemory(), timeout.Token));
                requests.Add((line, authorization != null, authorization == $"Bearer {manager.ApiSecret}", new string(body)));
                var json = line.StartsWith("GET /providers/proxies ")
                    ? """{"providers":{}}"""
                    : line.StartsWith("GET /proxies ")
                        ? """{"proxies":{}}"""
                        : """{"connections":[],"delay":1}""";
                var payload = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
                await stream.WriteAsync(payload, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task ActualMihomo_RejectsMissingAndWrongTokensAndAcceptsManagerRequests()
    {
        var executable = Environment.GetEnvironmentVariable("MIHOMO_VALIDATION_EXE");
        if (string.IsNullOrEmpty(executable)) Assert.Skip("Set MIHOMO_VALIDATION_EXE to run the actual core.");
        var config = CoreConfigTestFactory.CreateConfig();
        config.Inbound[0].LocalPort = Utils.GetFreePort();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        AppManager.Instance.Reset();
        var directory = Directory.CreateTempSubdirectory("mihomo-auth-").FullName;
        var source = Path.Combine(directory, "source.yaml");
        var output = Path.Combine(directory, "config.yaml");
        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await File.WriteAllTextAsync(source, "proxies: []\nproxy-groups: []\nrules:\n  - MATCH,DIRECT\n", timeout.Token);
            var generated = await new CoreConfigClashService(config).GenerateClientCustomConfig(
                new ProfileItem { Address = source, Remarks = "fixture" }, output);
            Assert.True(generated.Success, generated.Msg);
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "-d", directory, "-f", output }) start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var url = $"http://127.0.0.1:{AppManager.Instance.StatePort2}/connections";
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(url, timeout.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    break;
                }
                catch (HttpRequestException) when (!process.HasExited)
                {
                    await Task.Delay(100, timeout.Token);
                }
            }
            using var wrongToken = new HttpRequestMessage(HttpMethod.Get, url);
            wrongToken.Headers.Authorization = new("Bearer", "wrong-fixture-token");
            using var rejected = await client.SendAsync(wrongToken, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.NotNull(await ClashApiManager.Instance.GetClashConnectionsAsync().WaitAsync(timeout.Token));
            Assert.NotNull(await ClashApiManager.Instance.GetClashProxiesAsync().WaitAsync(timeout.Token));
            await ClashApiManager.Instance.ClashConnectionClose("").WaitAsync(timeout.Token);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(true);
            if (process != null) await process.WaitForExitAsync(CancellationToken.None);
            // Drain output without emitting credentials to test logs.
            if (stdout != null) await stdout;
            if (stderr != null) await stderr;
            process?.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
