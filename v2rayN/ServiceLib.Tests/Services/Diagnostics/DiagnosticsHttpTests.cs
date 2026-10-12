using System.Net.Http;
using ServiceLib.Services.Diagnostics;
using Xunit;

namespace ServiceLib.Tests.Services.Diagnostics;

public class DiagnosticsHttpTests
{
    [Fact]
    public async Task InjectedClient_RemainsCallerOwned()
    {
        using var handler = DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response("""{"code":0}"""));
        using var client = new HttpClient(handler);
        using (var service = new ServiceAvailabilityService(client))
        {
            Assert.Equal(AvailabilityState.Available,
                (await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken)).State);
        }
        Assert.False(handler.WasDisposed);
        using var response = await client.GetAsync("https://fixture.example/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void MissingProxy_IsRejectedBeforeAnyRequest()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceAvailabilityService((IWebProxy)null!));
    }

    [Fact]
    public async Task SuppliedProxy_BypassAdviceCannotCreateDirectRoute()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var tunnel = ReadProxyRequestAsync();
        try
        {
            using var service = new ServiceAvailabilityService(new BypassingProxy(new Uri($"http://127.0.0.1:{port}")));
            var result = await service.CheckAsync(AvailabilityService.BilibiliMainland, timeout.Token);
            var request = await tunnel;
            Assert.StartsWith("CONNECT api.bilibili.com:443 ", request);
            Assert.Equal(AvailabilityState.Failed, result.State);
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await tunnel; }
            catch (OperationCanceledException) { }
        }

        async Task<string> ReadProxyRequestAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            var request = new StringBuilder();
            var bytes = new byte[1024];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(bytes, timeout.Token);
                if (count == 0 || request.Length > 8192) throw new IOException("Invalid fixture CONNECT request.");
                request.Append(Encoding.ASCII.GetString(bytes, 0, count));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            return request.ToString();
        }
    }

    [Theory]
    [InlineData(302, false, false)]
    [InlineData(307, true, true)]
    [InlineData(303, false, false)]
    public async Task SameOriginRedirect_PreservesAuthAndExpectedMethod(int status, bool post, bool body)
    {
        var seen = false;
        using var handler = new DiagnosticFixtureHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/devices")
                return DiagnosticFixtureHandler.Redirect("/device-result", (HttpStatusCode)status);
            if (request.RequestUri.AbsolutePath == "/device-result")
            {
                seen = true;
                Assert.NotNull(request.Headers.Authorization);
                Assert.Equal(post ? HttpMethod.Post : HttpMethod.Get, request.Method);
                Assert.Equal(body, request.Content != null);
                if (request.Content != null)
                    Assert.Contains("deviceFamily", await request.Content.ReadAsStringAsync(token));
                return DiagnosticFixtureHandler.Response("""{"assertion":"fixture"}""");
            }
            return DiagnosticFixtureHandler.Response("""{"code":"forbidden-location"}""", HttpStatusCode.Forbidden);
        });
        using var service = new ServiceAvailabilityService(handler);
        Assert.Equal(AvailabilityState.Unavailable,
            (await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken)).State);
        Assert.True(seen);
    }

    [Fact]
    public async Task CrossOrigin302_StripsAuthAndBodyPermanently()
    {
        var seen = false;
        using var handler = DiagnosticFixtureHandler.Create(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/devices")
                return DiagnosticFixtureHandler.Redirect("https://fixture.example/device");
            if (request.RequestUri.Host == "fixture.example")
            {
                seen = true;
                Assert.Null(request.Headers.Authorization);
                Assert.Null(request.Content);
                Assert.Equal(HttpMethod.Get, request.Method);
                return DiagnosticFixtureHandler.Redirect("https://disney.api.edge.bamgrid.com/device-return");
            }
            if (request.RequestUri.AbsolutePath == "/device-return")
            {
                Assert.Null(request.Headers.Authorization);
                return DiagnosticFixtureHandler.Response("""{"assertion":"fixture"}""");
            }
            return DiagnosticFixtureHandler.Response("""{"code":"forbidden-location"}""");
        });
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.True(seen);
        Assert.Equal(AvailabilityState.Unavailable, result.State);
    }

    [Fact]
    public async Task CrossOrigin307_DoesNotSendAuthenticatedBody()
    {
        using var handler = DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Redirect("https://fixture.example/device", HttpStatusCode.TemporaryRedirect));
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.Single(handler.RequestedUrls);
    }

    [Theory]
    [InlineData("http://fixture.example/")]
    [InlineData("https://api.bilibili.com/loop")]
    public async Task RedirectDowngradeAndLoops_StopWithoutDirectFallback(string target)
    {
        using var handler = DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Redirect(target));
        using var service = new ServiceAvailabilityService(handler, new() { MaxRedirects = 2 });
        var result = await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith("http://"));
        Assert.InRange(handler.RequestedUrls.Count, 1, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseLimit_AppliesToDeclaredAndStreamingLength(bool streaming)
    {
        using var handler = DiagnosticFixtureHandler.Create(_ =>
        {
            var response = DiagnosticFixtureHandler.Response(new string('x', 200));
            if (streaming) response.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string('x', 200)));
            return response;
        });
        using var service = new ServiceAvailabilityService(handler, new() { MaxResponseBytes = 64 });
        var result = await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.Equal(AvailabilityReason.ResponseTooLarge, result.Reason);
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task ResponseAtExactLimit_IsAccepted()
    {
        const string payload = """{"code":0}""";
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response(payload)), new() { MaxResponseBytes = Encoding.UTF8.GetByteCount(payload) });
        Assert.Equal(AvailabilityState.Available,
            (await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Timeout_ReturnsFailedAndReleasesConcurrencySlot()
    {
        var calls = 0;
        using var handler = new DiagnosticFixtureHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DiagnosticFixtureHandler.Response("""{"code":0}""");
        });
        using var service = new ServiceAvailabilityService(handler, new()
        {
            CheckTimeout = TimeSpan.FromMilliseconds(100),
            MaxConcurrency = 1
        });
        var first = await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityReason.Timeout, first.Reason);
        Assert.Equal(AvailabilityState.Failed, first.State);
        var second = await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Available, second.State);
    }

    [Fact]
    public async Task CallerCancellation_ThrowsForActiveAndQueuedChecks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DiagnosticFixtureHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DiagnosticFixtureHandler.Response();
        });
        using var service = new ServiceAvailabilityService(handler, new() { MaxConcurrency = 1 });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var active = service.CheckAsync(AvailabilityService.BilibiliMainland, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var queued = service.CheckAsync(AvailabilityService.Claude, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task ConcurrentCheckCalls_RespectSharedServiceLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var gate = new object();
        using var handler = new DiagnosticFixtureHandler(async (_, token) =>
        {
            lock (gate)
            {
                active++;
                peak = Math.Max(peak, active);
                if (active == 2) entered.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(token);
                return DiagnosticFixtureHandler.Response("""{"code":0}""");
            }
            finally { lock (gate) active--; }
        });
        using var service = new ServiceAvailabilityService(handler, new() { MaxConcurrency = 2 });
        var checks = Enumerable.Range(0, 8).Select(_ =>
            service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, handler.RequestedUrls.Count);
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(checks);
        Assert.Equal(2, peak);
        Assert.All(results, result => Assert.Equal(AvailabilityState.Available, result.State));
    }

    [Fact]
    public async Task CheckAll_ReportsCompletionAndReturnsCatalogOrderDespiteIndividualFailure()
    {
        using var handler = DiagnosticFixtureHandler.Create(request =>
        {
            if (request.RequestUri!.Host == "api.bilibili.com") throw new HttpRequestException("fixture transport failure");
            return DiagnosticFixtureHandler.Response(status: HttpStatusCode.UnavailableForLegalReasons);
        });
        using var service = new ServiceAvailabilityService(handler);
        var seen = new ConcurrentQueue<ServiceAvailabilityResult>();
        var results = await service.CheckAllAsync(new InlineProgress(seen.Enqueue), TestContext.Current.CancellationToken);
        Assert.Equal(ServiceAvailabilityService.Services, results.Select(r => r.Service));
        Assert.Equal(12, seen.Count);
        Assert.Equal(2, results.Count(r => r.State == AvailabilityState.Failed));
        Assert.Equal(10, results.Count(r => r.State == AvailabilityState.Unavailable));
    }

    private sealed class InlineProgress(Action<ServiceAvailabilityResult> report) : IProgress<ServiceAvailabilityResult>
    {
        public void Report(ServiceAvailabilityResult value) => report(value);
    }

    private sealed class BypassingProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => endpoint;
        public bool IsBypassed(Uri host) => true;
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
    }
}
