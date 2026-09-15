using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public class StunAddressResolutionTests
{
    [Fact]
    public void FilterAddresses_ShouldExcludeLocalMulticastAndStaleFakeIpAddresses()
    {
        var addresses = WebRtcRoutingPolicy.FilterAddresses(
        [
            "203.0.113.9", "::ffff:203.0.113.9", "2001:db8::9",
            "127.0.0.1", "10.0.0.1", "192.168.1.1", "169.254.1.1", "100.64.1.1",
            "::ffff:192.168.1.1", "224.0.0.1", "255.255.255.255", "198.18.1.1",
            "::", "::1", "fc00::1", "fe80::1", "ff02::1", "invalid"
        ]);
        addresses.Should().Equal("203.0.113.9", "2001:db8::9");
    }

    [Fact]
    public async Task ResolveAddresses_ShouldDeduplicateWithoutQueryingUnlistedHosts()
    {
        var queried = new ConcurrentBag<string>();
        var result = await WebRtcRoutingPolicy.ResolveAddressesAsync((host, _) =>
        {
            queried.Add(host);
            return Task.FromResult(new[] { IPAddress.Parse("203.0.113.9"), IPAddress.Parse("2001:db8::9") });
        }, TestContext.Current.CancellationToken);
        queried.Should().BeEquivalentTo(WebRtcRoutingPolicy.Domains);
        result.Addresses.Should().Equal("203.0.113.9", "2001:db8::9");
        result.FailedDomains.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAddresses_PartialFailure_ShouldKeepOnlySuccessfulResults()
    {
        var result = await WebRtcRoutingPolicy.ResolveAddressesAsync((host, _) =>
            host == "stun.cloudflare.com"
                ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound))
                : Task.FromResult(new[] { IPAddress.Parse("203.0.113.9") }), TestContext.Current.CancellationToken);
        result.Addresses.Should().Equal("203.0.113.9");
        result.FailedDomains.Should().Equal("stun.cloudflare.com");
    }

    [Fact]
    public async Task ResolveAddresses_LocalOnlyAnswers_ShouldNotCreateProxyTargets()
    {
        var result = await WebRtcRoutingPolicy.ResolveAddressesAsync((_, _) =>
            Task.FromResult(new[] { IPAddress.Loopback }), TestContext.Current.CancellationToken);
        result.Addresses.Should().BeEmpty();
        result.FailedDomains.Should().BeEquivalentTo(WebRtcRoutingPolicy.Domains);
    }

    [Fact]
    public async Task ResolveAddresses_CallerCancellation_ShouldPropagate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolve = () => WebRtcRoutingPolicy.ResolveAddressesAsync(
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()), cancellation.Token);
        await resolve.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ResolveAddresses_UnresponsiveResolver_ShouldReturnFailureInsteadOfHanging()
    {
        var result = await WebRtcRoutingPolicy.ResolveAddressesAsync(
            (_, _) => new TaskCompletionSource<IPAddress[]>().Task, TestContext.Current.CancellationToken);
        result.Addresses.Should().BeEmpty();
        result.FailedDomains.Should().BeEquivalentTo(WebRtcRoutingPolicy.Domains);
    }
}
