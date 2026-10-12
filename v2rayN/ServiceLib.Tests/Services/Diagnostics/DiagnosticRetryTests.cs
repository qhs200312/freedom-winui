using ServiceLib.Services.Diagnostics;
using Xunit;

namespace ServiceLib.Tests.Services.Diagnostics;

public class DiagnosticRetryTests
{
    [Fact]
    public async Task ReadOnlyTransientFailure_RetriesOnceOnSameEndpoint()
    {
        var requests = 0;
        using var handler = DiagnosticFixtureHandler.Create(_ => ++requests == 1
            ? DiagnosticFixtureHandler.Response(status: HttpStatusCode.ServiceUnavailable)
            : DiagnosticFixtureHandler.Response("""{"code":0}"""));
        using var checker = new ServiceAvailabilityService(handler);
        var result = await checker.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Available, result.State);
        Assert.Equal(2, requests);
        Assert.Single(handler.RequestedUrls.Distinct());
    }

    [Fact]
    public async Task RepeatedTransientFailure_ReportsBusyAndStops()
    {
        var requests = 0;
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
        {
            requests++;
            return DiagnosticFixtureHandler.Response(status: HttpStatusCode.ServiceUnavailable);
        }));
        var result = await checker.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.Equal(AvailabilityReason.ServiceBusy, result.Reason);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task DevicePost_IsNotReplayed()
    {
        var requests = 0;
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            return DiagnosticFixtureHandler.Response(status: HttpStatusCode.BadGateway);
        }));
        var result = await checker.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityReason.ServiceBusy, result.Reason);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task RetryAfterOutsideBudget_IsRespectedWithoutImmediateRetry()
    {
        var requests = 0;
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
        {
            requests++;
            var response = DiagnosticFixtureHandler.Response(status: HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return response;
        }));
        var result = await checker.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityReason.ServiceBusy, result.Reason);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CancellationDuringRetry_IsPropagated()
    {
        using var stop = new CancellationTokenSource();
        var requests = 0;
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
        {
            requests++;
            stop.Cancel();
            return DiagnosticFixtureHandler.Response(status: HttpStatusCode.ServiceUnavailable);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            checker.CheckAsync(AvailabilityService.BilibiliMainland, stop.Token));
        Assert.Equal(1, requests);
    }
}
