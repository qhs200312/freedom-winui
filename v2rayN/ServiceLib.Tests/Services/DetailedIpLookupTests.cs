using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class DetailedIpLookupTests
{
    [Fact]
    public async Task ProviderFailure_ShouldUseOnlySuppliedClientAndKeepDetailedFallback()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host != "ipinfo.io") throw new HttpRequestException("Proxy upstream refused the provider.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ip":"2001:db8::4","country":"JP","city":"Tokyo","org":"AS64500 Example Network","timezone":"Asia/Tokyo"}""")
            });
        }));
        var result = await new DetailedIpLookupService(client).DetectAsync("https://first.example/json", TestContext.Current.CancellationToken);
        requests.Should().Equal("first.example", "ipwho.is", "ipinfo.io");
        result.Ip.Should().Be("2001:db8::4");
        result.Asn.Should().Be(64500);
        result.Organization.Should().Be("Example Network");
    }

    [Fact]
    public async Task AllProxyRequestsFail_ShouldFailWithoutCreatingDirectClient()
    {
        var count = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref count);
            throw new HttpRequestException("Proxy unavailable");
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new DetailedIpLookupService(client).DetectAsync(cancellationToken: TestContext.Current.CancellationToken));
        count.Should().Be(3);
    }

    [Fact]
    public async Task FailedMalformedAndOversizedResponses_ShouldNotReplaceGoodIp()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host switch
            {
                "first.example" => new string('x', 128 * 1024 + 1),
                "ipwho.is" => """{"success":false,"ip":"192.0.2.9","country_code":"CN"}""",
                "ipinfo.io" => "not json",
                _ => """{"ip":"192.0.2.4"}"""
            })
        })));
        var result = await new DetailedIpLookupService(client).DetectAsync("https://first.example/json", TestContext.Current.CancellationToken);
        result.Ip.Should().Be("192.0.2.4");
        result.CountryCode.Should().BeNull();
    }

    [Fact]
    public async Task UserCancellation_ShouldStopFallbackRequests()
    {
        using var source = new CancellationTokenSource();
        var count = 0;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            Interlocked.Increment(ref count);
            source.Cancel();
            await Task.Delay(1000, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DetailedIpLookupService(client).DetectAsync(cancellationToken: source.Token));
        count.Should().Be(1);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
