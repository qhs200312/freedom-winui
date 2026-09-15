using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public class SpeedtestServiceTests
{
    [Fact]
    public async Task GetTcpingTimeAsync_ReachableEndpoint_ReturnsLatency()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

            var delay = await SpeedtestService.GetTcpingTimeAsync(IPAddress.Loopback.ToString(), port);
            using var client = await acceptTask;

            delay.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task GetTcpingTimeAsync_ConnectionRefused_ReturnsTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var delay = await SpeedtestService.GetTcpingTimeAsync(IPAddress.Loopback.ToString(), port);

        delay.Should().Be(-1);
    }
}
