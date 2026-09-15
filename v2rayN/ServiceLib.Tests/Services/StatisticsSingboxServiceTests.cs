using AwesomeAssertions;
using ServiceLib.Models.Dto;
using ServiceLib.Services.Statistics;
using Xunit;

namespace ServiceLib.Tests.Services;

public class StatisticsSingboxServiceTests
{
    [Fact]
    public void Update_FirstSnapshot_ShouldNotReportHistoricalTraffic()
    {
        var tracker = new SingboxProxyTrafficTracker();

        var result = tracker.Update(Snapshot(
            Connection("proxy", "proxy", 10_000, 20_000),
            Connection("direct", "direct", 30_000, 40_000)));

        result.ProxyUp.Should().Be(0);
        result.ProxyDown.Should().Be(0);
    }

    [Fact]
    public void Update_MixedConnections_ShouldOnlyReportProxyDeltas()
    {
        var tracker = new SingboxProxyTrafficTracker();
        tracker.Update(Snapshot(
            Connection("proxy", "proxy", 10_000, 20_000),
            Connection("direct", "direct", 30_000, 40_000)));

        var result = tracker.Update(Snapshot(
            Connection("proxy", "proxy", 15_000, 28_000),
            Connection("direct", "direct", 130_000, 240_000)));

        result.ProxyUp.Should().Be(5);
        result.ProxyDown.Should().Be(8);
        result.DirectUp.Should().Be(0);
        result.DirectDown.Should().Be(0);
    }

    [Fact]
    public void Update_NewProxyConnection_ShouldReportBytesSinceItOpened()
    {
        var tracker = new SingboxProxyTrafficTracker();
        tracker.Update(Snapshot());

        var result = tracker.Update(Snapshot(Connection("proxy", "proxy", 2_500, 4_900)));

        result.ProxyUp.Should().Be(2);
        result.ProxyDown.Should().Be(4);
    }

    [Fact]
    public void Update_GeneratedProxyTag_ShouldBeRecognized()
    {
        var tracker = new SingboxProxyTrafficTracker();
        tracker.Update(Snapshot());

        var result = tracker.Update(Snapshot(Connection("proxy", "node-id-proxy-remark", 3_000, 7_000)));

        result.ProxyUp.Should().Be(3);
        result.ProxyDown.Should().Be(7);
    }

    [Fact]
    public void Update_DirectOrMissingChain_ShouldNotReportTraffic()
    {
        var tracker = new SingboxProxyTrafficTracker();
        tracker.Update(Snapshot());

        var result = tracker.Update(Snapshot(
            Connection("direct", "direct", 300_000, 700_000),
            new ConnectionItem { id = "missing", upload = 500_000, download = 900_000 }));

        result.ProxyUp.Should().Be(0);
        result.ProxyDown.Should().Be(0);
    }

    [Fact]
    public void Reset_ShouldSeedTheNextSnapshotAgain()
    {
        var tracker = new SingboxProxyTrafficTracker();
        tracker.Update(Snapshot());
        tracker.Update(Snapshot(Connection("proxy", "proxy", 3_000, 7_000)));

        tracker.Reset();
        var result = tracker.Update(Snapshot(Connection("proxy", "proxy", 8_000, 12_000)));

        result.ProxyUp.Should().Be(0);
        result.ProxyDown.Should().Be(0);
    }

    private static ClashConnections Snapshot(params ConnectionItem[] connections)
    {
        return new() { connections = [.. connections] };
    }

    private static ConnectionItem Connection(string id, string chain, ulong upload, ulong download)
    {
        return new()
        {
            id = id,
            chains = [chain],
            upload = upload,
            download = download
        };
    }
}
