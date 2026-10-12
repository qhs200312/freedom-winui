using ServiceLib.Services.Diagnostics;
using Xunit;

namespace ServiceLib.Tests.Services.Diagnostics;

public class ServiceAvailabilityTests
{
    public static IEnumerable<object[]> AllServices() =>
        ServiceAvailabilityService.Services.Select(id => new object[] { id });

    [Fact]
    public void Catalog_PreservesReferenceOrderAndStableIds()
    {
        Assert.Equal(12, ServiceAvailabilityService.ServiceDefinitions.Count);
        Assert.Equal(ServiceAvailabilityService.Services, ServiceAvailabilityService.ServiceDefinitions.Select(d => d.Id));
        Assert.Equal("ChatGPT Web", ServiceAvailabilityService.GetName(AvailabilityService.ChatGPT));
        Assert.Equal("Bahamut Anime", ServiceAvailabilityService.GetName(AvailabilityService.Bahamut));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServiceAvailabilityService.GetName((AvailabilityService)100));
    }

    [Theory]
    [InlineData(AvailabilityService.BilibiliMainland, 0, AvailabilityState.Available)]
    [InlineData(AvailabilityService.BilibiliHongKongTaiwan, 0, AvailabilityState.Available)]
    [InlineData(AvailabilityService.BilibiliMainland, -10403, AvailabilityState.Unavailable)]
    [InlineData(AvailabilityService.BilibiliHongKongTaiwan, -10403, AvailabilityState.Unavailable)]
    [InlineData(AvailabilityService.BilibiliMainland, -500, AvailabilityState.Failed)]
    public async Task Bilibili_UsesExplicitCodeAndNeverInventsRegion(AvailabilityService id, int code, AvailabilityState state)
    {
        using var handler = DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response($"{{\"code\":{code}}}"));
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State);
        Assert.Null(result.RegionCode);
        Assert.Contains(id == AvailabilityService.BilibiliMainland ? "ep_id=307247" : "ep_id=183799", result.EvidenceUrl);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"code\"")]
    [InlineData("{\"code\":\"0\"}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{}")]
    [InlineData("{broken")]
    public async Task Bilibili_MalformedShapeProducesResultInsteadOfThrowing(string body)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response(body)));
        var result = await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.Equal(AvailabilityReason.InvalidResponse, result.Reason);
    }

    [Fact]
    public async Task Bilibili_ExplicitGeoCodeOverridesAmbiguous403()
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("""{"code":-10403}""", HttpStatusCode.Forbidden)));
        Assert.Equal(AvailabilityState.Unavailable,
            (await service.CheckAsync(AvailabilityService.BilibiliMainland, TestContext.Current.CancellationToken)).State);
    }

    [Theory]
    [InlineData("{\"animeSn\":37783}", AvailabilityState.Available)]
    [InlineData("{\"animeSn\":null}", AvailabilityState.Unknown)]
    [InlineData("{\"error\":\"try later\"}", AvailabilityState.Unknown)]
    [InlineData("{\"message\":\"animeSn\"}", AvailabilityState.Unknown)]
    [InlineData("[]", AvailabilityState.Failed)]
    public async Task Bahamut_RequiresTokenFieldAndEscapesDevice(string tokenBody, AvailabilityState expected)
    {
        using var handler = DiagnosticFixtureHandler.Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getdeviceid.php"))
                return DiagnosticFixtureHandler.Response("""{"deviceid":"a&b=c"}""");
            if (request.RequestUri.AbsolutePath.EndsWith("token.php"))
            {
                Assert.Contains("device=a%26b%3Dc", request.RequestUri.Query);
                return DiagnosticFixtureHandler.Response(tokenBody);
            }
            return DiagnosticFixtureHandler.Response("""<html data-geo="TWN"></html>""");
        });
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.Bahamut, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == AvailabilityState.Available ? "TW" : null, result.RegionCode);
    }

    [Theory]
    [InlineData("""{"error":{"code":"unsupported_country"}}""", 403, AvailabilityState.Unavailable)]
    [InlineData("""{"cookie_required":true}""", 200, AvailabilityState.Unknown)]
    [InlineData("""{"error":"unauthorized"}""", 401, AvailabilityState.Unknown)]
    [InlineData("[]", 200, AvailabilityState.Failed)]
    [InlineData("\"unsupported_country\"", 200, AvailabilityState.Failed)]
    [InlineData("{\"documentation\":\"unsupported_country\"}", 200, AvailabilityState.Unknown)]
    public async Task ChatGpt_ComplianceDoesNotProveAccountAccess(string body, int status, AvailabilityState expected)
    {
        using var handler = DiagnosticFixtureHandler.Create(request => request.RequestUri!.AbsolutePath.EndsWith("trace")
            ? DiagnosticFixtureHandler.Response("ip=203.0.113.1\nloc=USA\n")
            : DiagnosticFixtureHandler.Response(body, (HttpStatusCode)status));
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.ChatGPT, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        if (expected != AvailabilityState.Failed)
        {
            Assert.Equal("US", result.RegionCode);
            Assert.Equal("https://chat.openai.com/cdn-cgi/trace", result.RegionEvidenceUrl);
        }
    }

    [Fact]
    public async Task ChatGpt_ChallengeTraceCannotSupplyRegion()
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace")
                ? "<title>Just a moment</title>\nloc=US\n" : "{}")));
        var result = await service.CheckAsync(AvailabilityService.ChatGPT, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Null(result.RegionCode);
        Assert.Null(result.RegionEvidenceUrl);
    }

    [Theory]
    [InlineData("CN", "appears in")]
    [InlineData("US", "outside")]
    [InlineData("UK", "outside")]
    public async Task Claude_TraceAndBlocklistRemainInferential(string region, string detail)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("loc=" + region + "\r\n")));
        var result = await service.CheckAsync(AvailabilityService.Claude, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal("Unknown", result.Status);
        Assert.Contains(detail, result.Detail!);
        Assert.Equal(region == "UK" ? "GB" : region, result.RegionCode);
    }

    [Theory]
    [InlineData("USA", "US")]
    [InlineData("CHN", "CN")]
    [InlineData("HKG", "HK")]
    [InlineData("MAC", "MO")]
    [InlineData("GBR", "GB")]
    [InlineData("UK", "GB")]
    [InlineData("EL", "GR")]
    [InlineData("ZZZ", null)]
    [InlineData("USAX", null)]
    public async Task Gemini_MarkerOffsetAndIsoAliases(string code, string? expected)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("<script>[1,2,1,200,\"" + code + "\",0]</script>")));
        var result = await service.CheckAsync(AvailabilityService.Gemini, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.RegionCode);
        Assert.Equal(AvailabilityState.Unknown, result.State);
    }

    [Theory]
    [InlineData("""{"browseId":"SPunlimited","GL":"JP"}""", AvailabilityState.Available)]
    [InlineData("""<h1>YouTube Premium</h1><p>Ad-free video</p><span id='country-code'>UK</span>""", AvailabilityState.Available)]
    [InlineData("YouTube Premium is not available in your country", AvailabilityState.Unavailable)]
    [InlineData("<html>YouTube Premium</html>", AvailabilityState.Unknown)]
    public async Task YouTube_RequiresOfferSignalOrExplicitRestriction(string body, AvailabilityState expected)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response(body)));
        var result = await service.CheckAsync(AvailabilityService.YouTubePremium, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        if (body.Contains("country-code")) Assert.Equal("GB", result.RegionCode);
    }

    [Theory]
    [InlineData("""{"isServiceRestricted":true,"currentTerritory":"US"}""", AvailabilityState.Unavailable)]
    [InlineData("""{"isServiceRestricted":false,"currentTerritory":"USA"}""", AvailabilityState.Available)]
    [InlineData("""{"currentTerritory":"US"}""", AvailabilityState.Unknown)]
    [InlineData("""{"message":"isServiceRestricted"}""", AvailabilityState.Unknown)]
    public async Task Prime_RestrictionFlagMustBeTrue(string body, AvailabilityState expected)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response(body)));
        var result = await service.CheckAsync(AvailabilityService.PrimeVideo, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
    }

    [Theory]
    [InlineData("""{"countries":[{"countryCode":"US"},{"countryCode":"HK"}]}""")]
    [InlineData("""{"countryCode":"US"}""")]
    [InlineData("""[{"countryCode":"US"}]""")]
    [InlineData("""{"countries":[{"countryCode":"US"}],"translations":{"denial":"not available in your country"}}""")]
    public async Task Spotify_PublicSelectorNeverProvesRegionOrAvailability(string body)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ => DiagnosticFixtureHandler.Response(body)));
        var result = await service.CheckAsync(AvailabilityService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Null(result.RegionCode);
        Assert.Contains("country-selector", result.Detail!);
    }

    [Fact]
    public async Task Spotify_ExplicitErrorMessageCanEstablishRestriction()
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("""{"error":{"message":"Not available in your country"}}""", HttpStatusCode.Forbidden)));
        var result = await service.CheckAsync(AvailabilityService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unavailable, result.State);
        Assert.Null(result.RegionCode);
    }

    [Theory]
    [InlineData("loc=US\n", "<html>Hello</html>", AvailabilityState.Unknown)]
    [InlineData("", """{"region":"US-en"}""", AvailabilityState.Unknown)]
    [InlineData("access denied", "access denied", AvailabilityState.Unknown)]
    [InlineData("TikTok is not available in your region", "hello", AvailabilityState.Unavailable)]
    public async Task TikTok_ArbitrarySuccessAndTraceDoNotMeanYes(string trace, string page, AvailabilityState expected)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace") ? trace : page)));
        var result = await service.CheckAsync(AvailabilityService.TikTok, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        Assert.NotEqual("Yes", result.Status);
    }

    [Theory]
    [InlineData("JP", "JP")]
    [InlineData("USA", "US")]
    [InlineData("ZZZ", null)]
    public async Task Netflix_CdnIsOnlyEdgeEvidence(string country, string? expected)
    {
        using var handler = DiagnosticFixtureHandler.Create(request => request.RequestUri!.Host == "api.fast.com"
            ? DiagnosticFixtureHandler.Response("{\"targets\":[{\"location\":{\"country\":\"" + country + "\"}}]}")
            : DiagnosticFixtureHandler.Response());
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.Netflix, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal(expected, result.RegionCode);
        Assert.Contains("unverified", result.Detail!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("/gb-en/title/80018499", "GB")]
    [InlineData("https://other.example/jp/title/80018499", null)]
    [InlineData("http://www.netflix.com/jp/title/80018499", null)]
    public async Task Netflix_TitleFallbackUsesOnlyObservedNetflixRedirect(string? location, string? region)
    {
        using var handler = DiagnosticFixtureHandler.Create(request =>
        {
            if (request.RequestUri!.Host == "api.fast.com") return DiagnosticFixtureHandler.Response("[]");
            if (request.RequestUri.AbsolutePath.EndsWith("80018499") && location != null)
                return DiagnosticFixtureHandler.Redirect(location);
            return DiagnosticFixtureHandler.Response();
        });
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.Netflix, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal(region, result.RegionCode);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.StartsWith("https://other.example"));
    }

    [Fact]
    public async Task Netflix_Two404sDoNotProveOriginalsPlayback()
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            request.RequestUri!.Host == "api.fast.com" ? DiagnosticFixtureHandler.Response("""{"targets":[]}""") :
            DiagnosticFixtureHandler.Response(status: HttpStatusCode.NotFound)));
        var result = await service.CheckAsync(AvailabilityService.Netflix, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.NotEqual(AvailabilityReason.OriginalsOnly, result.Reason);
        Assert.Contains("Originals Only", result.Detail!);
    }

    [Theory]
    [InlineData(true, AvailabilityState.Available)]
    [InlineData(false, AvailabilityState.Unavailable)]
    public async Task Disney_RequiresDeviceSessionSupportedFlag(bool supported, AvailabilityState expected)
    {
        using var handler = DisneyHandler("{\"extensions\":{\"session\":{\"countryCode\":\"JPN\",\"inSupportedLocation\":" +
            (supported ? "true" : "false") + "}}}");
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        Assert.Equal("JP", result.RegionCode);
        Assert.Contains("graphql", result.EvidenceUrl);
    }

    [Fact]
    public async Task Disney_MainPageRegionFallbackRemainsUnknown()
    {
        using var handler = DisneyHandler("not JSON", """{"region":"HK"}""");
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal("HK", result.RegionCode);
    }

    [Fact]
    public async Task Disney_ExplicitForbiddenLocationIsUnavailable()
    {
        using var handler = DiagnosticFixtureHandler.Create(request => request.RequestUri!.AbsolutePath == "/devices"
            ? DiagnosticFixtureHandler.Response("""{"assertion":"fixture"}""")
            : DiagnosticFixtureHandler.Response("""{"errors":[{"code":"forbidden-location"}]}""", HttpStatusCode.Forbidden));
        using var service = new ServiceAvailabilityService(handler);
        var result = await service.CheckAsync(AvailabilityService.DisneyPlus, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unavailable, result.State);
        Assert.Equal(AvailabilityReason.UnsupportedRegion, result.Reason);
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task EveryService_Generic403IsUnknown(AvailabilityService id)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("Access denied", HttpStatusCode.Forbidden)));
        var result = await service.CheckAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task EveryService_AntiBotOverridesApparentMarkers(AvailabilityService id)
    {
        const string body = """<title>Just a moment</title>{"code":0,"deviceid":"fake","animeSn":37783,"assertion":"fake","region":"US","currentTerritory":"US","isServiceRestricted":true} unsupported_country YouTube Premium ad-free""";
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response(body)));
        var result = await service.CheckAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Null(result.RegionCode);
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task EveryService_451IsExplicitUnavailability(AvailabilityService id)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response(status: HttpStatusCode.UnavailableForLegalReasons)));
        var result = await service.CheckAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unavailable, result.State);
    }

    [Theory]
    [InlineData(AvailabilityService.Bahamut)]
    [InlineData(AvailabilityService.ChatGPT)]
    [InlineData(AvailabilityService.DisneyPlus)]
    [InlineData(AvailabilityService.Spotify)]
    public async Task ObjectEndpoints_RejectNonObjectOrScalarJson(AvailabilityService id)
    {
        using var service = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("null")));
        var result = await service.CheckAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Failed, result.State);
        Assert.Equal(AvailabilityReason.InvalidResponse, result.Reason);
    }

    private static DiagnosticFixtureHandler DisneyHandler(string graph, string fallback = "{}") =>
        DiagnosticFixtureHandler.Create(request => request.RequestUri!.AbsolutePath switch
        {
            "/devices" => DiagnosticFixtureHandler.Response("""{"assertion":"fixture-assertion"}"""),
            "/token" => DiagnosticFixtureHandler.Response("""{"refresh_token":"fixture-refresh"}"""),
            "/graph/v1/device/graphql" => DiagnosticFixtureHandler.Response(graph),
            _ => DiagnosticFixtureHandler.Response(fallback)
        });
}
