using System.Globalization;
using System.Net.Http;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

[Collection("UDP interception config")]
public sealed class LanAuthenticationTests
{
    public static IEnumerable<object[]> Modes()
    {
        foreach (var core in new[] { ECoreType.Xray, ECoreType.sing_box })
        foreach (var lan in new[] { false, true })
        foreach (var newPort in new[] { false, true })
        foreach (var credentials in new[] { ("user", "password"), ("user", ""), ("", "password"), ("", "") })
            yield return [core, lan, newPort, credentials.Item1, credentials.Item2];
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task GeneratedInbounds_AuthenticateOnlyTheLanListenerAndWarnWhenIncomplete(
        ECoreType core, bool lan, bool newPort, string user, string password)
    {
        var config = CoreConfigTestFactory.CreateConfig(core);
        var inbound = config.Inbound[0];
        inbound.AllowLANConn = lan;
        inbound.NewPort4LAN = newPort;
        inbound.SecondLocalPortEnabled = true;
        inbound.User = user;
        inbound.Pass = password;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(core), core);
        var notices = new List<string>();
        using var subscription = AppEvents.SendSnackMsgRequested.AsObservable().Subscribe(notices.Add);

        var result = await CoreConfigHandler.GenerateClientConfig(context, null);

        Assert.True(result.Success, result.Msg);
        var inbounds = JsonNode.Parse(result.Data!.ToString()!)!["inbounds"]!.AsArray();
        Assert.Equal(lan && newPort ? 3 : 2, inbounds.Count);
        foreach (var item in inbounds)
        {
            var tag = item!["tag"]!.GetValue<string>();
            var shared = lan && tag == (newPort ? "socks3" : "socks");
            Assert.Equal(shared ? "0.0.0.0" : Global.Loopback, item["listen"]!.GetValue<string>());
            var offset = tag == "socks3" ? 2 : tag == "socks2" ? 1 : 0;
            Assert.Equal(inbound.LocalPort + offset,
                item[core == ECoreType.Xray ? "port" : "listen_port"]!.GetValue<int>());
            var authenticated = shared && user.Length > 0 && password.Length > 0;
            if (core == ECoreType.Xray)
            {
                Assert.Equal(authenticated ? "password" : "noauth", item["settings"]!["auth"]!.GetValue<string>());
                if (authenticated)
                {
                    var account = Assert.Single(item["settings"]!["accounts"]!.AsArray())!;
                    Assert.Equal(user, account["user"]!.GetValue<string>());
                    Assert.Equal(password, account["pass"]!.GetValue<string>());
                }
                else
                    Assert.Null(item["settings"]!["accounts"]);
            }
            else if (authenticated)
            {
                var account = Assert.Single(item["users"]!.AsArray())!;
                Assert.Equal(user, account["username"]!.GetValue<string>());
                Assert.Equal(password, account["password"]!.GetValue<string>());
            }
            else
                Assert.Null(item["users"]);
        }
        Assert.Equal(lan && (user.Length == 0 || password.Length == 0),
            notices.Contains(ResUI.LanAuthenticationWarning));

        var credentials = ConnectionHandler.GetLocalWebProxy().Credentials as NetworkCredential;
        var localAuth = lan && !newPort && user.Length > 0 && password.Length > 0;
        Assert.Equal(localAuth, credentials != null);
        if (localAuth)
        {
            Assert.Equal(user, credentials!.UserName);
            Assert.Equal(password, credentials.Password);
        }
        var resolveSystemProxy = typeof(DownloadService).GetMethod("GetSystemProxy",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var systemLocal = new WebProxy($"http://127.0.0.1:{inbound.LocalPort}");
        var selected = (IWebProxy)resolveSystemProxy.Invoke(null, ["https://fixture.example.test/", systemLocal])!;
        Assert.Equal(localAuth, selected.Credentials is NetworkCredential);
        Assert.Null(systemLocal.Credentials);
        var external = new WebProxy($"http://proxy.example.test:{inbound.LocalPort}");
        Assert.Same(external, resolveSystemProxy.Invoke(null, ["https://fixture.example.test/", external]));
        Assert.Null(external.Credentials);
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableLegacyProtect = true;
        var preSocks = ConfigHandler.GetPreSocksItem(config,
            CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray), ECoreType.Xray)!;
        Assert.Equal(localAuth ? user : string.Empty, preSocks.Username);
        Assert.Equal(localAuth ? password : string.Empty, preSocks.Password);
        var customPreSocks = ConfigHandler.GetPreSocksItem(config,
            new ProfileItem { ConfigType = EConfigType.Custom, PreSocksPort = inbound.LocalPort }, ECoreType.Xray)!;
        Assert.Equal(string.Empty, customPreSocks.Username);
        Assert.Equal(string.Empty, customPreSocks.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("fr")]
    [InlineData("hu")]
    [InlineData("id")]
    [InlineData("ru")]
    [InlineData("fa")]
    public void Warning_IsAvailableInEverySupportedLanguage(string culture)
    {
        var resources = ResUI.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false);
        Assert.False(string.IsNullOrEmpty(resources?.GetString(nameof(ResUI.LanAuthenticationWarning))));
    }

    [Theory]
    [InlineData(ECoreType.Xray, false, "XRAY_VALIDATION_EXE")]
    [InlineData(ECoreType.Xray, true, "XRAY_VALIDATION_EXE")]
    [InlineData(ECoreType.sing_box, false, "SINGBOX_VALIDATION_EXE")]
    [InlineData(ECoreType.sing_box, true, "SINGBOX_VALIDATION_EXE")]
    public async Task ActualCore_RejectsUnauthenticatedAndWrongPasswordClients(
        ECoreType core, bool newPort, string executableVariable)
    {
        var executable = Environment.GetEnvironmentVariable(executableVariable);
        if (string.IsNullOrEmpty(executable)) Assert.Skip($"Set {executableVariable} to run the actual core.");
        var config = CoreConfigTestFactory.CreateConfig(core);
        var inbound = config.Inbound[0];
        inbound.LocalPort = Utils.GetFreePort();
        inbound.AllowLANConn = true;
        inbound.NewPort4LAN = newPort;
        inbound.User = "fixture-user";
        inbound.Pass = "fixture-password";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        AppManager.Instance.Reset();
        Assert.True(AppManager.Instance.EnsureLocalPortsAvailable());
        var port = inbound.LocalPort + (newPort ? 2 : 0);
        var context = CoreConfigTestFactory.CreateContext(config, CoreConfigTestFactory.CreateSocksNode(core), core);
        var result = await CoreConfigHandler.GenerateClientConfig(context, null);
        Assert.True(result.Success, result.Msg);
        using var witness = new TcpListener(IPAddress.Loopback, 0);
        witness.Start();
        var targetPort = ((IPEndPoint)witness.LocalEndpoint).Port;
        var generated = JsonNode.Parse(result.Data!.ToString()!)!;
        var rules = generated[core == ECoreType.Xray ? "routing" : "route"]!["rules"]!.AsArray();
        rules.Insert(0, core == ECoreType.Xray
            ? JsonNode.Parse("""{"type":"field","ip":["127.0.0.1"],"outboundTag":"direct"}""")
            : JsonNode.Parse("""{"ip_cidr":["127.0.0.1/32"],"outbound":"direct"}"""));
        var directory = Directory.CreateTempSubdirectory("lan-auth-").FullName;
        var path = Path.Combine(directory, "config.json");
        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await File.WriteAllTextAsync(path, generated.ToJsonString(), timeout.Token);
            var start = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "run", "-c", path }) start.ArgumentList.Add(argument);
            process = Process.Start(start)!;
            stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            while (true)
            {
                using var ready = new TcpClient();
                try
                {
                    await ready.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                    break;
                }
                catch (SocketException ex)
                {
                    if (process.HasExited)
                    {
                        var diagnostic = ((await stdout) + (await stderr))
                            .Replace(ClashApiManager.Instance.ApiSecret, "[redacted]", StringComparison.Ordinal);
                        throw new InvalidOperationException($"Fixture core exited ({process.ExitCode}): {diagnostic}", ex);
                    }
                    await Task.Delay(100, timeout.Token);
                }
            }
            using (var anonymous = new TcpClient())
            {
                await anonymous.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await using var stream = anonymous.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                var response = new byte[2];
                await stream.ReadExactlyAsync(response, timeout.Token);
                Assert.Equal(new byte[] { 5, 255 }, response);
            }
            Assert.NotEqual(0, await AuthenticateAsync("wrong-fixture-password"));
            Assert.Equal(0, await AuthenticateAsync(inbound.Pass));
            Assert.Equal("407", await HttpConnectAsync(null));
            Assert.Equal("407", await HttpConnectAsync("wrong-fixture-password"));
            Assert.Equal("200", await HttpConnectAsync(inbound.Pass));
            using (var accepted = await witness.AcceptTcpClientAsync(timeout.Token)) { }

            var serving = ServeHttpWitnessAsync();
            using var httpClient = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = true, Proxy = ConnectionHandler.GetLocalWebProxy(),
            });
            Assert.Equal("fixture", await httpClient.GetStringAsync($"http://127.0.0.1:{targetPort}/", timeout.Token));
            await serving;
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(true);
            if (process != null) await process.WaitForExitAsync(CancellationToken.None);
            if (stdout != null) await stdout;
            if (stderr != null) await stderr;
            process?.Dispose();
            Directory.Delete(directory, true);
        }

        async Task<byte> AuthenticateAsync(string password)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 2 }, timeout.Token);
            var response = new byte[2];
            await stream.ReadExactlyAsync(response, timeout.Token);
            Assert.Equal(new byte[] { 5, 2 }, response);
            var username = Encoding.ASCII.GetBytes(inbound.User);
            var secret = Encoding.ASCII.GetBytes(password);
            await stream.WriteAsync(new byte[] { 1, (byte)username.Length }
                .Concat(username).Concat(new[] { (byte)secret.Length }).Concat(secret).ToArray(), timeout.Token);
            await stream.ReadExactlyAsync(response, timeout.Token);
            Assert.Equal(1, response[0]);
            return response[1];
        }

        async Task<string> HttpConnectAsync(string? password)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var stream = client.GetStream();
            var authorization = password == null ? "" : "Proxy-Authorization: Basic "
                + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{inbound.User}:{password}")) + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"CONNECT 127.0.0.1:{targetPort} HTTP/1.1\r\nHost: 127.0.0.1:{targetPort}\r\n{authorization}\r\n"), timeout.Token);
            using var reader = new StreamReader(stream);
            var status = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(status);
            return status.Split(' ')[1];
        }

        async Task ServeHttpWitnessAsync()
        {
            using var connection = await witness.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header)
                Assert.False(header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture"), timeout.Token);
        }
    }
}
