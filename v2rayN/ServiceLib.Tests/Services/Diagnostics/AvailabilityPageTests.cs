using ServiceLib.Services.Diagnostics;
using Xunit;

namespace ServiceLib.Tests.Services.Diagnostics;

public class AvailabilityPageTests
{
    [Theory]
    [InlineData(81280792, true, "ALLOW", AvailabilityState.Available)]
    [InlineData(999, true, "ALLOW", AvailabilityState.Unknown)]
    [InlineData(81280792, false, "ALLOW", AvailabilityState.Unknown)]
    [InlineData(81280792, true, "DENY", AvailabilityState.Unknown)]
    public async Task Netflix_ValidatesRequestedTitleAndAvailability(int id, bool available, string policy, AvailabilityState expected)
    {
        var page = "netflix.reactContext = " + JsonSerializer.Serialize(new
        {
            models = new
            {
                geo = new { data = new { requestCountry = new { id = "GBR" }, supportedCountry = policy } },
                nmTitleGQL = new { data = new { metaData = new { videoId = id, isAvailable = available, url = "https://fixture.example/" } } }
            }
        }).Replace("https://", @"https:\x2F\x2F") + ";";
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            request.RequestUri!.Host == "api.fast.com"
                ? DiagnosticFixtureHandler.Response("""{"targets":[{"location":{"country":"US"}}]}""")
                : DiagnosticFixtureHandler.Response(page)));
        var result = await checker.CheckAsync(AvailabilityService.Netflix, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State);
        if (expected == AvailabilityState.Available)
        {
            Assert.Equal(AvailabilityReason.CatalogAvailable, result.Reason);
            Assert.Equal("GB", result.RegionCode);
            Assert.Equal("https://www.netflix.com/title/81280792", result.RegionEvidenceUrl);
        }
    }

    [Fact]
    public async Task Netflix_FollowsObservedSameServiceRedirectForMetadata()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.fast.com") return DiagnosticFixtureHandler.Response("{}");
            if (uri.AbsolutePath == "/title/81280792") return DiagnosticFixtureHandler.Redirect("/jp/title/81280792");
            return DiagnosticFixtureHandler.Response("""
                netflix.reactContext = {"models":{"geo":{"data":{"requestCountry":{"id":"JP"}}},
                "nmTitleGQL":{"data":{"metaData":{"videoId":81280792,"isAvailable":true}}}}};
                """);
        }));
        var result = await checker.CheckAsync(AvailabilityService.Netflix, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityReason.CatalogAvailable, result.Reason);
        Assert.Equal("JP", result.RegionCode);
    }

    [Theory]
    [InlineData("web_player_prototype", "US", true)]
    [InlineData("web_player", "USA", true)]
    [InlineData("unrelated", "US", false)]
    public async Task Spotify_UsesPlayerConfigInsteadOfPublicSelector(string app, string market, bool identified)
    {
        var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { appName = app, market, isAnonymous = true })));
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.Host == "open.spotify.com"
                ? $"<script id=\"appServerConfig\" type=\"text/plain\">{config}</script>"
                : """{"countries":[{"countryCode":"HK"}]}""")));
        var result = await checker.CheckAsync(AvailabilityService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(identified ? AvailabilityState.Limited : AvailabilityState.Unknown, result.State);
        Assert.Equal(identified ? "US" : null, result.RegionCode);
        Assert.NotEqual("Yes", result.Status);
        if (identified) Assert.Equal(AvailabilityReason.PageReachable, result.Reason);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("eyJtYXJrZXQiOiJVUyJ9")]
    [InlineData("bnVsbA==")]
    public async Task Spotify_MalformedPlayerConfigDoesNotInventSuccess(string config)
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.Host == "open.spotify.com"
                ? $"<script id=\"appServerConfig\">{config}</script>" : "{}")));
        var result = await checker.CheckAsync(AvailabilityService.Spotify, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Null(result.RegionCode);
    }

    [Fact]
    public async Task Gemini_BootstrapPageIsReachableWithoutClaimingModelAccess()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("""
                <title>Gemini</title><script>window.WIZ_global_data = {"SNlM0e":"fixture"};</script>
                <script>[1,2,1,200,"USA",0]</script>
                """)));
        var result = await checker.CheckAsync(AvailabilityService.Gemini, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Limited, result.State);
        Assert.Equal(AvailabilityReason.PageReachable, result.Reason);
        Assert.Equal("US", result.RegionCode);
    }

    [Fact]
    public async Task TikTok_TranslationStringsDoNotOverrideActualBootstrap()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace") ? "loc=GB\n" : """
                <script id="tiktok-environment-webapp" type="application/json">{"regionInfo":{"vgeo":"VGeo-US"}}</script>
                <script id="service-region" type="application/json">{"vgeo":"VGeo-US"}</script>
                <script>{"translations":{"blocked":"TikTok is not available in your region"}}</script>
                """)));
        var result = await checker.CheckAsync(AvailabilityService.TikTok, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Limited, result.State);
        Assert.Equal(AvailabilityReason.PageReachable, result.Reason);
        Assert.Equal("US", result.RegionCode);
        Assert.Equal("https://www.tiktok.com/", result.RegionEvidenceUrl);
    }

    [Fact]
    public async Task TikTok_NumericHydrationRegionDoesNotThrow()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
            DiagnosticFixtureHandler.Response("""
                <script id="__UNIVERSAL_DATA_FOR_REHYDRATION__">
                {"__DEFAULT_SCOPE__":{"webapp.app-context":{"region":42}}}</script>
                """)));
        var result = await checker.CheckAsync(AvailabilityService.TikTok, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Limited, result.State);
        Assert.Null(result.RegionCode);
    }

    [Fact]
    public async Task Prime_RecognizesHtmlEncodedOfferPageWithoutInventingRestrictionFlag()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(_ =>
        {
            var response = DiagnosticFixtureHandler.Response("""
                <script data-a-state='{"key":"context"}'>{&#34;currentTerritory&#34;:&#34;USA&#34;}</script>
                """);
            response.RequestMessage = new(HttpMethod.Get, "https://www.primevideo.com/offers/nonprimehomepage");
            return response;
        }));
        var result = await checker.CheckAsync(AvailabilityService.PrimeVideo, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Limited, result.State);
        Assert.Equal(AvailabilityReason.PageReachable, result.Reason);
        Assert.Equal("US", result.RegionCode);
    }

    [Fact]
    public async Task ChatGpt_AnonymousModelCatalogIsReachable()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace") ? "loc=US\n" :
                request.RequestUri.Host == "api.openai.com" ? """{"cookie_consent_required":false}""" :
                """{"models":[{"slug":"auto"}]}""")));
        var result = await checker.CheckAsync(AvailabilityService.ChatGPT, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Limited, result.State);
        Assert.Equal(AvailabilityReason.PageReachable, result.Reason);
    }

    [Theory]
    [InlineData(AvailabilityService.ChatGPT)]
    [InlineData(AvailabilityService.Claude)]
    public async Task AiProductGatePreservesTraceRegionAndExplainsChallenge(AvailabilityService service)
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace") ? "loc=US\n" :
                request.RequestUri.Host == "api.openai.com" ? "{}" : "<title>Just a moment</title>",
                request.RequestUri.AbsolutePath.EndsWith("trace") || request.RequestUri.Host == "api.openai.com"
                    ? HttpStatusCode.OK : HttpStatusCode.Forbidden)));
        var result = await checker.CheckAsync(service, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal(AvailabilityReason.HumanVerification, result.Reason);
        Assert.Equal("US", result.RegionCode);
    }

    [Fact]
    public async Task Claude_AuthenticatedBootstrapRequiresSignIn()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("trace") ? "loc=US\n" :
                """{"error":{"type":"authentication_error"}}""",
                request.RequestUri.AbsolutePath.EndsWith("trace") ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)));
        var result = await checker.CheckAsync(AvailabilityService.Claude, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unknown, result.State);
        Assert.Equal(AvailabilityReason.AuthenticationRequired, result.Reason);
    }

    [Fact]
    public async Task Bahamut_ExplicitTokenRegionDenialIsUnavailable()
    {
        using var checker = new ServiceAvailabilityService(DiagnosticFixtureHandler.Create(request =>
            DiagnosticFixtureHandler.Response(request.RequestUri!.AbsolutePath.EndsWith("getdeviceid.php")
                ? """{"deviceid":"fixture"}""" : """{"error":{"message":"本動畫僅限台灣地區觀看"}}""")));
        var result = await checker.CheckAsync(AvailabilityService.Bahamut, TestContext.Current.CancellationToken);
        Assert.Equal(AvailabilityState.Unavailable, result.State);
        Assert.Equal(AvailabilityReason.UnsupportedRegion, result.Reason);
    }
}
