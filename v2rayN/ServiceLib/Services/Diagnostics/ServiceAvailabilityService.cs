namespace ServiceLib.Services.Diagnostics;

/// <summary>Availability heuristics from Clash Verge v2.5.8; these do not prove account or playback access.</summary>
public sealed class ServiceAvailabilityService : IDisposable
{
    private const string BilibiliCn = "https://api.bilibili.com/pgc/player/web/playurl?avid=82846771&qn=0&type=&otype=json&ep_id=307247&fourk=1&fnver=0&fnval=16&module=bangumi";
    private const string BilibiliHk = "https://api.bilibili.com/pgc/player/web/playurl?avid=18281381&cid=29892777&qn=0&type=&otype=json&ep_id=183799&fourk=1&fnver=0&fnval=16&module=bangumi";
    private const string NetflixCdn = "https://api.fast.com/netflix/speedtest/v2?https=true&token=YXNkZmFzZGxmbnNkYWZoYXNkZmhrYWxm&urlCount=5";
    private const string DisneyBase = "https://disney.api.edge.bamgrid.com/";
    // Public browser device key used by the reference checker, not an account credential.
    private const string DisneyAuthorization = "Bearer ZGlzbmV5JmJyb3dzZXImMS4wLjA.Cu56AgSfBTDag5NiRA81oLHkDZfu5L3CKadnefEAY84";
    private readonly DiagnosticsHttpClient _http;
    private readonly ServiceAvailabilityOptions _options;
    private readonly SemaphoreSlim _checks;

    public static IReadOnlyList<AvailabilityService> Services { get; } =
        Array.AsReadOnly(Enum.GetValues<AvailabilityService>());

    public static IReadOnlyList<ServiceAvailabilityDefinition> ServiceDefinitions { get; } =
        Array.AsReadOnly(Services.Select(id => new ServiceAvailabilityDefinition(id, GetName(id))).ToArray());

    public ServiceAvailabilityService(IWebProxy proxy, ServiceAvailabilityOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
        _http = new(proxy, _options);
        _checks = new(_options.MaxConcurrency);
    }

    /// <summary>The supplied client is caller-owned and must already enforce the required proxy and disable automatic redirects.</summary>
    public ServiceAvailabilityService(HttpClient client, ServiceAvailabilityOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
        _http = new(client, _options);
        _checks = new(_options.MaxConcurrency);
    }

    /// <summary>Injection seam for fixture handlers; the service owns the handler.</summary>
    public ServiceAvailabilityService(HttpMessageHandler handler, ServiceAvailabilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _options = options ?? new();
        _options.Validate();
        _http = new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, _options, ownsClient: true);
        _checks = new(_options.MaxConcurrency);
    }

    public static string GetName(AvailabilityService service) => service switch
    {
        AvailabilityService.BilibiliMainland => "\u54d4\u54e9\u54d4\u54e9\u5927\u9646",
        AvailabilityService.BilibiliHongKongTaiwan => "\u54d4\u54e9\u54d4\u54e9\u6e2f\u6fb3\u53f0",
        AvailabilityService.ChatGPT => "ChatGPT Web",
        AvailabilityService.YouTubePremium => "YouTube Premium",
        AvailabilityService.Bahamut => "Bahamut Anime",
        AvailabilityService.DisneyPlus => "Disney+",
        AvailabilityService.PrimeVideo => "Prime Video",
        _ when Enum.IsDefined(service) => service.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    public async Task<ServiceAvailabilityResult> CheckAsync(AvailabilityService service, CancellationToken cancellationToken = default)
    {
        var name = GetName(service);
        await _checks.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CheckTimeout);
        Probe result;
        try
        {
            result = await RunAsync(service, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = Failed(Endpoint(service), AvailabilityReason.Timeout);
        }
        catch (ResponseLimitException)
        {
            result = Failed(Endpoint(service), AvailabilityReason.ResponseTooLarge);
        }
        catch (HttpRequestException)
        {
            result = Failed(Endpoint(service), AvailabilityReason.NetworkError);
        }
        catch (IOException)
        {
            result = Failed(Endpoint(service), AvailabilityReason.NetworkError);
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException)
        {
            result = Failed(Endpoint(service), AvailabilityReason.InvalidResponse);
        }
        finally
        {
            _checks.Release();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(service, name, result.State, result.Reason, result.Region, result.Url,
            result.RegionUrl, DateTimeOffset.UtcNow, timer.Elapsed, result.Detail);
    }

    /// <summary>Reports completion as it occurs; returned results retain the catalog order. Caller cancellation throws.</summary>
    public async Task<IReadOnlyList<ServiceAvailabilityResult>> CheckAllAsync(
        IProgress<ServiceAvailabilityResult>? progress = null, CancellationToken cancellationToken = default)
    {
        return await Task.WhenAll(Services.Select(async service =>
        {
            var result = await CheckAsync(service, cancellationToken).ConfigureAwait(false);
            progress?.Report(result);
            return result;
        })).ConfigureAwait(false);
    }

    private Task<Probe> RunAsync(AvailabilityService service, CancellationToken token) => service switch
    {
        AvailabilityService.BilibiliMainland or AvailabilityService.BilibiliHongKongTaiwan => BilibiliAsync(Endpoint(service), token),
        AvailabilityService.Bahamut => BahamutAsync(token),
        AvailabilityService.ChatGPT => ChatGptAsync(token),
        AvailabilityService.Claude => ClaudeAsync(token),
        AvailabilityService.Gemini => GeminiAsync(token),
        AvailabilityService.Spotify => SpotifyAsync(token),
        AvailabilityService.Netflix => NetflixAsync(token),
        AvailabilityService.DisneyPlus => DisneyAsync(token),
        _ => PageAsync(service, token)
    };

    private static string Endpoint(AvailabilityService service) => service switch
    {
        AvailabilityService.BilibiliMainland => BilibiliCn,
        AvailabilityService.BilibiliHongKongTaiwan => BilibiliHk,
        AvailabilityService.Bahamut => "https://ani.gamer.com.tw/ajax/getdeviceid.php",
        AvailabilityService.ChatGPT => "https://api.openai.com/compliance/cookie_requirements",
        AvailabilityService.Claude => "https://claude.ai/cdn-cgi/trace",
        AvailabilityService.Gemini => "https://gemini.google.com",
        AvailabilityService.Netflix => NetflixCdn,
        AvailabilityService.DisneyPlus => DisneyBase + "devices",
        AvailabilityService.PrimeVideo => "https://www.primevideo.com",
        AvailabilityService.Spotify => "https://www.spotify.com/api/content/v1/country-selector?platform=web&format=json",
        AvailabilityService.TikTok => "https://www.tiktok.com/cdn-cgi/trace",
        AvailabilityService.YouTubePremium => "https://www.youtube.com/premium?hl=en",
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    private async Task<Probe> BilibiliAsync(string url, CancellationToken token)
    {
        var response = await _http.SendAsync(url, token).ConfigureAwait(false);
        if (ChallengeOrLegal(response) is { } early) return early;
        if (!response.IsSuccess)
        {
            try
            {
                using var error = ParseObject(response.Body);
                if (error.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var errorCode) && errorCode == -10403) return No(url);
            }
            catch (JsonException) { }
            return HttpFailure(response);
        }
        using var doc = ParseObject(response.Body);
        if (!doc.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var number))
            return Failed(url, AvailabilityReason.InvalidResponse);
        return number switch
        {
            0 => Yes(url) with { Detail = "Reference episode play URL accepted; full catalog and playback are not tested." },
            -10403 => No(url),
            _ => Failed(url, AvailabilityReason.InvalidResponse)
        };
    }

    private async Task<Probe> BahamutAsync(CancellationToken token)
    {
        var device = await _http.SendAsync(Endpoint(AvailabilityService.Bahamut), token).ConfigureAwait(false);
        if (ChallengeOrLegal(device) is { } early) return early;
        if (!device.IsSuccess) return HttpFailure(device);
        using var doc = ParseObject(device.Body);
        if (!doc.RootElement.TryGetProperty("deviceid", out var deviceId) ||
            deviceId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(deviceId.GetString()))
            return Failed(device.Url.ToString(), AvailabilityReason.InvalidResponse);
        var url = "https://ani.gamer.com.tw/ajax/token.php?adID=89422&sn=37783&device=" + Uri.EscapeDataString(deviceId.GetString()!);
        var response = await _http.SendAsync(url, token).ConfigureAwait(false);
        if (ChallengeOrLegal(response) is { } tokenEarly) return tokenEarly;
        if (!response.IsSuccess) return HttpFailure(response);
        using var body = ParseObject(response.Body);
        var animeSn = AvailabilityEvidence.Find(body.RootElement, "animeSn");
        var hasAnime = animeSn is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var id) && id > 0 ||
            animeSn is { ValueKind: JsonValueKind.String } text && !string.IsNullOrWhiteSpace(text.GetString());
        if (!hasAnime)
        {
            if (HasRestrictionMessage(response.Body, "not available in your region") ||
                HasRestrictionMessage(response.Body, "僅限台灣") || HasRestrictionMessage(response.Body, "僅限臺灣") ||
                HasRestrictionMessage(response.Body, "地區限制"))
                return No(url, AvailabilityReason.UnsupportedRegion);
            return Unknown(url) with { Detail = "Token response contains no verified anime availability signal." };
        }
        var page = await OptionalGetAsync("https://ani.gamer.com.tw/", token).ConfigureAwait(false);
        var region = page is { IsSuccess: true } && !AvailabilityEvidence.IsChallenge(page.Body)
            ? AvailabilityEvidence.Attribute(page.Body, "data-geo") : null;
        return Yes(url, region, page?.Url.ToString()) with { Detail = "Anonymous anime token accepted; playback and account entitlement are not tested." };
    }

    private async Task<Probe> ChatGptAsync(CancellationToken token)
    {
        const string traceUrl = "https://chat.openai.com/cdn-cgi/trace";
        var trace = await OptionalGetAsync(traceUrl, token).ConfigureAwait(false);
        var region = trace is { IsSuccess: true } && !AvailabilityEvidence.IsChallenge(trace.Body)
            ? AvailabilityEvidence.TraceRegion(trace.Body) : null;
        var url = Endpoint(AvailabilityService.ChatGPT);
        var response = await _http.SendAsync(url, token).ConfigureAwait(false);
        if (ChallengeOrLegal(response) is { } early) return early;
        JsonDocument doc;
        try { doc = ParseObject(response.Body); }
        catch (JsonException) when (!response.IsSuccess)
        {
            return HttpFailure(response) with { Region = region, RegionUrl = region == null ? null : traceUrl };
        }
        using (doc)
        {
            var code = AvailabilityEvidence.Find(doc.RootElement, "code");
            var type = AvailabilityEvidence.Find(doc.RootElement, "type");
            if ((code is { ValueKind: JsonValueKind.String } codeValue && AvailabilityEvidence.Contains(codeValue.GetString()!, "unsupported_country")) ||
                (type is { ValueKind: JsonValueKind.String } typeValue && AvailabilityEvidence.Contains(typeValue.GetString()!, "unsupported_country")))
                return No(url, AvailabilityReason.UnsupportedRegion, region, traceUrl);
        }
        if (!response.IsSuccess) return HttpFailure(response) with { Region = region, RegionUrl = region == null ? null : traceUrl };
        var web = await OptionalGetAsync("https://chatgpt.com/backend-anon/models", token).ConfigureAwait(false);
        if (web != null)
        {
            if (ChallengeOrLegal(web) is { } gated)
                return gated with { Region = region, RegionUrl = region == null ? null : traceUrl };
            if (IsCountryRestriction(web.Body))
                return No(web.Url.ToString(), AvailabilityReason.UnsupportedRegion, region, traceUrl);
            if (!web.IsSuccess)
                return HttpFailure(web) with { Region = region, RegionUrl = region == null ? null : traceUrl };
            try
            {
                using var models = ParseObject(web.Body);
                if (models.RootElement.TryGetProperty("models", out var catalog) && catalog.ValueKind == JsonValueKind.Array &&
                    catalog.EnumerateArray().Any(model => model.ValueKind == JsonValueKind.Object &&
                        model.TryGetProperty("slug", out var slug) && slug.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(slug.GetString())))
                    return Reachable(web.Url.ToString(), region, traceUrl) with
                    {
                        Detail = "Anonymous ChatGPT model catalog is reachable; account and model responses are not tested."
                    };
            }
            catch (JsonException) { }
        }
        return Unknown(url) with
        {
            Region = region, RegionUrl = region == null ? null : traceUrl,
            Detail = "Compliance endpoint reachable; this does not verify ChatGPT account or model access."
        };
    }

    private async Task<Probe> ClaudeAsync(CancellationToken token)
    {
        var url = Endpoint(AvailabilityService.Claude);
        var response = await _http.SendAsync(url, token).ConfigureAwait(false);
        if (ChallengeOrLegal(response) is { } early) return early;
        if (!response.IsSuccess) return HttpFailure(response);
        var region = AvailabilityEvidence.TraceRegion(response.Body);
        var bootstrap = await OptionalGetAsync("https://claude.ai/api/bootstrap", token).ConfigureAwait(false);
        if (bootstrap != null)
        {
            if (ChallengeOrLegal(bootstrap) is { } gated)
                return gated with { Region = region, RegionUrl = region == null ? null : url };
            if (IsCountryRestriction(bootstrap.Body))
                return No(bootstrap.Url.ToString(), AvailabilityReason.UnsupportedRegion, region, url);
            if (!bootstrap.IsSuccess)
                return HttpFailure(bootstrap) with { Region = region, RegionUrl = region == null ? null : url };
            try
            {
                using var doc = ParseObject(bootstrap.Body);
                if (doc.RootElement.TryGetProperty("organizations", out var organizations) &&
                    organizations.ValueKind == JsonValueKind.Array)
                    return Reachable(bootstrap.Url.ToString(), region, url) with { Detail = "Claude bootstrap endpoint is reachable; account and model access are not tested." };
            }
            catch (JsonException) { }
        }
        if (region == null) return Unknown(url);
        string[] blocked = ["AF", "BY", "CN", "CU", "HK", "IR", "KP", "MO", "RU", "SY"];
        return Unknown(url) with
        {
            Region = region, RegionUrl = url,
            Detail = blocked.Contains(region)
                ? "Trace geography appears in the reference blocklist; Claude access remains unverified."
                : "Trace geography is outside the reference blocklist; Claude access remains unverified."
        };
    }

    private async Task<Probe> GeminiAsync(CancellationToken token)
    {
        var url = Endpoint(AvailabilityService.Gemini);
        var response = await _http.SendAsync(url, token).ConfigureAwait(false);
        if (ChallengeOrLegal(response) is { } early) return early;
        if (!response.IsSuccess) return HttpFailure(response);
        const string regionMarker = ",2,1,200,\"";
        var marker = response.Body.IndexOf(regionMarker, StringComparison.Ordinal);
        var rest = marker < 0 ? "" : response.Body[(marker + regionMarker.Length)..];
        var endQuote = rest.IndexOf('"');
        var rawRegion = endQuote is 2 or 3 ? rest[..endQuote] : null;
        var region = AvailabilityEvidence.Region(rawRegion);
        if (AvailabilityPageEvidence.Read(AvailabilityService.Gemini, response.Body, response.Url).Identified)
            return Reachable(response.Url.ToString(), region) with
            {
                Detail = "Gemini application page and bootstrap data are reachable; sign-in and model responses are not tested."
            };
        if (region == null) return Unknown(url);
        string[] blocked = ["CN", "RU", "BY", "CU", "IR", "KP", "SY", "HK", "MO"];
        return Unknown(url) with
        {
            Region = region, RegionUrl = url,
            Detail = blocked.Contains(region)
                ? "Page geography appears in the reference blocklist; Gemini model access remains unverified."
                : "Page geography is outside the reference blocklist; Gemini model access remains unverified."
        };
    }

    private async Task<Probe> PageAsync(AvailabilityService service, CancellationToken token)
    {
        var url = Endpoint(service);
        DiagnosticResponse response;
        try { response = await _http.SendAsync(url, token).ConfigureAwait(false); }
        catch (HttpRequestException) when (service == AvailabilityService.TikTok)
        {
            return await TikTokFallbackAsync(null, token).ConfigureAwait(false);
        }
        var result = ClassifyPage(service, response);
        if (service == AvailabilityService.TikTok && (result.Region == null || result.State is AvailabilityState.Failed or AvailabilityState.Unknown))
            return await TikTokFallbackAsync(result, token).ConfigureAwait(false);
        return result;
    }

    private async Task<Probe> SpotifyAsync(CancellationToken token)
    {
        var player = await OptionalGetAsync("https://open.spotify.com/", token).ConfigureAwait(false);
        if (player != null)
        {
            if (ChallengeOrLegal(player) is { } gated) return gated;
            if (HasRestrictionMessage(player.Body, "not available in your country"))
                return No(player.Url.ToString(), AvailabilityReason.UnsupportedRegion);
            if (!player.IsSuccess) return HttpFailure(player);
            var signal = AvailabilityPageEvidence.Read(AvailabilityService.Spotify, player.Body, player.Url);
            if (signal.Identified)
                return Reachable(player.Url.ToString(), signal.Region) with
                {
                    Detail = "Spotify Web Player bootstrap is reachable; region is its reported market. Account eligibility and audio playback are not tested."
                };
        }
        return await PageAsync(AvailabilityService.Spotify, token).ConfigureAwait(false);
    }

    private async Task<Probe> TikTokFallbackAsync(Probe? initial, CancellationToken token)
    {
        var response = await OptionalGetAsync("https://www.tiktok.com/", token).ConfigureAwait(false);
        if (response == null)
            return initial ?? Failed("https://www.tiktok.com/", AvailabilityReason.NetworkError);
        var fallback = ClassifyPage(AvailabilityService.TikTok, response);
        if (initial?.State == AvailabilityState.Unavailable)
            return initial with { Region = initial.Region ?? fallback.Region, RegionUrl = initial.RegionUrl ?? fallback.RegionUrl };
        if (fallback.State == AvailabilityState.Failed && initial?.State == AvailabilityState.Unknown)
            return initial with { Detail = initial.Detail + " Main-page fallback failed." };
        return fallback with { Region = fallback.Region ?? initial?.Region, RegionUrl = fallback.RegionUrl ?? initial?.RegionUrl };
    }

    private static Probe ClassifyPage(AvailabilityService service, DiagnosticResponse response)
    {
        var url = response.Url.ToString();
        var body = response.Body;
        if (ChallengeOrLegal(response) is { } early) return early;
        var host = service switch
        {
            AvailabilityService.YouTubePremium => "youtube.com", AvailabilityService.PrimeVideo => "primevideo.com",
            AvailabilityService.Spotify => "spotify.com", _ => "tiktok.com"
        };
        if (!AvailabilityPageEvidence.IsHost(response.Url, host))
            return Unknown(url) with { Detail = "Service redirected to another site; product availability is unverified." };
        string? region;
        if (service == AvailabilityService.YouTubePremium)
        {
            region = AvailabilityEvidence.QuotedField(body, "GL") ??
                AvailabilityEvidence.QuotedField(body, "countryCode") ?? AvailabilityEvidence.QuotedField(body, "country_code");
            if (region == null)
            {
                var match = Regex.Match(body, "id=[\"']country-code[\"'][^>]*>\\s*([^<]+)<", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (match.Success) region = match.Groups[1].Value;
            }
            if (AvailabilityEvidence.Contains(body, "youtube premium is not available in your country",
                "premium is not available in your country", "premium is not available in your region"))
                return No(url, region: region, regionUrl: url);
            if (!response.IsSuccess) return HttpFailure(response);
            return string.Equals(AvailabilityEvidence.QuotedField(body, "browseId"), "SPunlimited", StringComparison.Ordinal) ||
                (AvailabilityEvidence.Contains(body, "youtube premium") && AvailabilityEvidence.Contains(body, "ad-free"))
                ? Yes(url, region) with { Detail = "Premium offer page detected; account subscription and playback are not tested." }
                : Unknown(url);
        }
        if (service == AvailabilityService.PrimeVideo)
        {
            var decoded = WebUtility.HtmlDecode(body);
            var restriction = AvailabilityEvidence.BooleanField(decoded, "isServiceRestricted");
            if (restriction == true) return No(url);
            if (!response.IsSuccess) return HttpFailure(response);
            region = AvailabilityEvidence.QuotedField(decoded, "currentTerritory");
            if (restriction == false && AvailabilityEvidence.Region(region) != null)
                return Yes(url, region) with { Detail = "Page reports unrestricted service territory; account and playback are not tested." };
            var signal = AvailabilityPageEvidence.Read(service, body, response.Url);
            if (signal.Identified)
                return Reachable(url, signal.Region ?? region) with
                {
                    Detail = "Prime Video non-member offer page and page data are reachable; account and playback are not tested."
                };
            return Unknown(url) with
                {
                    Region = AvailabilityEvidence.Region(region),
                    RegionUrl = AvailabilityEvidence.Region(region) == null ? null : url,
                    Detail = "Page territory alone does not establish Prime Video availability."
                };
        }
        if (service == AvailabilityService.Spotify)
        {
            if (HasRestrictionMessage(body, "not available in your country")) return No(url);
            if (!response.IsSuccess) return HttpFailure(response);
            if (response.Url.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    return Failed(url, AvailabilityReason.InvalidResponse);
            }
            return Unknown(url) with
            {
                Detail = "Public country-selector content does not establish visitor region, account eligibility or streaming access."
            };
        }
        region = AvailabilityEvidence.Region(AvailabilityEvidence.QuotedField(body, "region")?.Split('-')[0]) ??
            AvailabilityEvidence.TraceRegion(body);
        if (HasRestrictionMessage(body, "not available in your region") || HasRestrictionMessage(body, "tiktok is not available") ||
            AvailabilityEvidence.Contains(AvailabilityPageEvidence.VisibleText(body), "not available in your region", "tiktok is not available"))
            return No(url, region: region, regionUrl: url);
        if (!response.IsSuccess) return HttpFailure(response);
        var page = AvailabilityPageEvidence.Read(service, body, response.Url);
        if (page.Identified)
            return Reachable(url, page.Region ?? region) with
            {
                Detail = "TikTok application bootstrap is reachable; video delivery and account access are not tested."
            };
        return Unknown(url) with
        {
            Region = region, RegionUrl = region == null ? null : url,
            Detail = "Trace or page geography does not verify TikTok product access."
        };
    }

    private async Task<Probe> NetflixAsync(CancellationToken token)
    {
        string? edgeRegion = null;
        var cdn = await OptionalGetAsync(NetflixCdn, token).ConfigureAwait(false);
        if (cdn != null && ChallengeOrLegal(cdn) is { } cdnEarly) return cdnEarly;
        if (cdn?.Status == HttpStatusCode.Forbidden) return HttpFailure(cdn);
        if (cdn is { IsSuccess: true })
        {
            try
            {
                using var doc = ParseObject(cdn.Body);
                if (doc.RootElement.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array &&
                    targets.GetArrayLength() > 0 && targets[0].ValueKind == JsonValueKind.Object &&
                    targets[0].TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object &&
                    location.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.String &&
                    AvailabilityEvidence.Region(country.GetString()) is { } observed)
                    edgeRegion = observed;
            }
            catch (JsonException) { }
        }
        var title1 = NetflixTitleAsync("https://www.netflix.com/title/81280792", token);
        var title2 = NetflixTitleAsync("https://www.netflix.com/title/70143836", token);
        var titles = await Task.WhenAll(title1, title2).ConfigureAwait(false);
        foreach (var title in titles.Where(title => title.IsSuccess))
        {
            if (ChallengeOrLegal(title) != null) continue;
            var signal = AvailabilityPageEvidence.Read(AvailabilityService.Netflix, title.Body, title.Url);
            if (signal is { Identified: true, CatalogAvailable: true })
                return Yes(title.Url.ToString(), signal.Region) with
                {
                    Reason = AvailabilityReason.CatalogAvailable,
                    Detail = "Netflix title metadata explicitly reports isAvailable=true for the requested title. Region is the page's request country; account and playback are not tested."
                };
        }
        if (titles.FirstOrDefault(r => r.Status == HttpStatusCode.UnavailableForLegalReasons) is { } legal)
            return No(legal.Url.ToString()) with { Detail = "HTTP 451" };
        if (titles.All(r => r.Status == HttpStatusCode.NotFound))
            return Unknown(titles[0].Url.ToString()) with { Reason = AvailabilityReason.SampleUnavailable,
                Detail = "Both reference titles returned 404; the reference Originals Only heuristic does not prove playback access." };
        if (titles.FirstOrDefault(title => ChallengeOrLegal(title) != null) is { } challenged)
            return ChallengeOrLegal(challenged)!;
        if (titles.Any(r => r.Status == HttpStatusCode.Forbidden))
            return HttpFailure(titles.First(r => r.Status == HttpStatusCode.Forbidden));
        if (edgeRegion != null)
            return Unknown(NetflixCdn) with
            {
                Region = edgeRegion, RegionUrl = NetflixCdn,
                Detail = "Fast.com reports a CDN edge region; Netflix catalog, originals and full playback remain unverified."
            };
        var available = titles.FirstOrDefault(r => r.Status is HttpStatusCode.OK or HttpStatusCode.MovedPermanently);
        if (available == null) return HttpFailure(titles[0]);
        var regionResponse = await OptionalGetAsync("https://www.netflix.com/title/80018499", token, followRedirects: false, readBody: false).ConfigureAwait(false);
        var redirect = regionResponse?.Location;
        var segment = redirect?.Scheme == Uri.UriSchemeHttps &&
            (redirect.Host == "netflix.com" || redirect.Host.EndsWith(".netflix.com", StringComparison.OrdinalIgnoreCase))
            ? redirect.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        var region = segment == null ? null : AvailabilityEvidence.Region(segment.Split('-')[0]);
        return Unknown(available.Url.ToString()) with
        {
            Region = region, RegionUrl = region == null ? null : regionResponse?.Url.ToString(),
            Detail = "Reference title endpoint reachable; redirect territory is observed only when present. Playback remains unverified."
        };
    }

    private async Task<DiagnosticResponse> NetflixTitleAsync(string url, CancellationToken token)
    {
        var response = await _http.SendAsync(url, token, followRedirects: false).ConfigureAwait(false);
        if ((int)response.Status is 301 or 302 or 303 or 307 or 308 && response.Location is { } target &&
            AvailabilityPageEvidence.IsHost(target, "netflix.com"))
            return await _http.SendAsync(target.ToString(), token).ConfigureAwait(false);
        return response;
    }

    private async Task<Probe> DisneyAsync(CancellationToken token)
    {
        var device = await _http.SendAsync(DisneyBase + "devices", token, HttpMethod.Post,
            """{"deviceFamily":"browser","applicationRuntime":"chrome","deviceProfile":"windows","attributes":{}}""",
            authorization: DisneyAuthorization).ConfigureAwait(false);
        if (ChallengeOrLegal(device) is { } early) return early;
        if (!device.IsSuccess) return HttpFailure(device);
        using var deviceDoc = ParseObject(device.Body);
        var assertion = AvailabilityEvidence.Find(deviceDoc.RootElement, "assertion");
        if (assertion?.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(assertion.Value.GetString()))
            return Failed(device.Url.ToString(), AvailabilityReason.InvalidResponse);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["latitude"] = "0", ["longitude"] = "0", ["platform"] = "browser",
            ["subject_token"] = assertion.Value.GetString()!,
            ["subject_token_type"] = "urn:bamtech:params:oauth:token-type:device"
        });
        var exchange = await _http.SendAsync(DisneyBase + "token", token, HttpMethod.Post,
            await form.ReadAsStringAsync(token).ConfigureAwait(false), "application/x-www-form-urlencoded", DisneyAuthorization).ConfigureAwait(false);
        if (ChallengeOrLegal(exchange) is { } tokenEarly) return tokenEarly;
        if (AvailabilityEvidence.Contains(exchange.Body, "forbidden-location"))
            return No(exchange.Url.ToString(), AvailabilityReason.UnsupportedRegion);
        if (!exchange.IsSuccess) return HttpFailure(exchange);
        using var tokenDoc = ParseObject(exchange.Body);
        var refresh = AvailabilityEvidence.Find(tokenDoc.RootElement, "refresh_token");
        if (refresh?.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(refresh.Value.GetString()))
            return Failed(exchange.Url.ToString(), AvailabilityReason.InvalidResponse);
        var landing = await OptionalGetAsync("https://disneyplus.com", token, readBody: false).ConfigureAwait(false);
        var graph = await _http.SendAsync(DisneyBase + "graph/v1/device/graphql", token, HttpMethod.Post,
            JsonSerializer.Serialize(new
            {
                query = "mutation refreshToken($input: RefreshTokenInput!) { refreshToken(refreshToken: $input) { activeSession { sessionId } } }",
                variables = new { input = new { refreshToken = refresh.Value.GetString() } }
            }), authorization: DisneyAuthorization).ConfigureAwait(false);
        if (ChallengeOrLegal(graph) is { } graphEarly) return graphEarly;
        if (graph.IsSuccess)
        {
            try
            {
                using var doc = ParseObject(graph.Body);
                var value = AvailabilityEvidence.Find(doc.RootElement, "countryCode");
                var region = value?.ValueKind == JsonValueKind.String ? AvailabilityEvidence.Region(value.Value.GetString()) : null;
                if (region != null)
                {
                    if (landing is { IsSuccess: true } &&
                        (landing.Url.Host == "disneyplus.com" || landing.Url.Host.EndsWith(".disneyplus.com", StringComparison.OrdinalIgnoreCase)) &&
                        AvailabilityEvidence.Contains(landing.Url.AbsolutePath, "preview", "unavailable"))
                        return No(landing.Url.ToString());
                    var supported = AvailabilityEvidence.Find(doc.RootElement, "inSupportedLocation");
                    if (supported?.ValueKind == JsonValueKind.True)
                        return Yes(graph.Url.ToString(), region) with { Detail = "Anonymous device session reports supported location; subscription and playback are not tested." };
                    if (supported?.ValueKind == JsonValueKind.False)
                        return No(graph.Url.ToString(), AvailabilityReason.UnsupportedRegion, region, graph.Url.ToString())
                            with { Detail = "Device session reports unsupported location; the reference Soon label does not establish a launch date." };
                    return Unknown(graph.Url.ToString()) with { Region = region, RegionUrl = graph.Url.ToString() };
                }
            }
            catch (JsonException) { }
        }
        var fallback = await OptionalGetAsync("https://www.disneyplus.com/", token).ConfigureAwait(false);
        var fallbackRegion = fallback is { IsSuccess: true } && !AvailabilityEvidence.IsChallenge(fallback.Body)
            ? AvailabilityEvidence.Region(AvailabilityEvidence.QuotedField(fallback.Body, "region")) : null;
        if (fallbackRegion != null)
            return Unknown(fallback!.Url.ToString()) with
            {
                Region = fallbackRegion, RegionUrl = fallback.Url.ToString(),
                Detail = "Region from main page; supported location and playback remain unverified."
            };
        return graph.IsSuccess ? Failed(graph.Url.ToString(), AvailabilityReason.InvalidResponse) : HttpFailure(graph);
    }

    private async Task<DiagnosticResponse?> OptionalGetAsync(string url, CancellationToken token, bool followRedirects = true, bool readBody = true)
    {
        try { return await _http.SendAsync(url, token, followRedirects: followRedirects, readBody: readBody).ConfigureAwait(false); }
        catch (HttpRequestException) { return null; }
        catch (IOException ex) when (ex is not ResponseLimitException) { return null; }
    }

    private sealed record Probe(AvailabilityState State, AvailabilityReason Reason, string Url,
        string? Region = null, string? RegionUrl = null, string? Detail = null);

    private static Probe Yes(string url, string? region = null, string? regionUrl = null)
    {
        region = AvailabilityEvidence.Region(region);
        return new(AvailabilityState.Available, AvailabilityReason.Available, url, region, region == null ? null : regionUrl ?? url);
    }
    private static Probe No(string url, AvailabilityReason reason = AvailabilityReason.Restricted, string? region = null, string? regionUrl = null)
    {
        region = AvailabilityEvidence.Region(region);
        return new(AvailabilityState.Unavailable, reason, url, region, region == null ? null : regionUrl ?? url);
    }
    private static Probe Failed(string url, AvailabilityReason reason) => new(AvailabilityState.Failed, reason, url);
    private static Probe Unknown(string url) => new(AvailabilityState.Unknown, AvailabilityReason.MissingEvidence, url);
    private static Probe Reachable(string url, string? region = null, string? regionUrl = null) =>
        Yes(url, region, regionUrl) with { State = AvailabilityState.Limited, Reason = AvailabilityReason.PageReachable };
    private static Probe HttpFailure(DiagnosticResponse response) => response.Status switch
    {
        HttpStatusCode.Forbidden => Unknown(response.Url.ToString()) with { Reason = AvailabilityReason.AccessDenied, Detail = "HTTP 403; geographic restriction, authentication and anti-bot denial cannot be distinguished." },
        HttpStatusCode.Unauthorized => Unknown(response.Url.ToString()) with { Reason = AvailabilityReason.AuthenticationRequired, Detail = "HTTP 401; this service endpoint requires sign-in." },
        HttpStatusCode.UnavailableForLegalReasons => No(response.Url.ToString()) with { Detail = "HTTP 451" },
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            Failed(response.Url.ToString(), AvailabilityReason.ServiceBusy) with { Detail = $"HTTP {(int)response.Status}" },
        _ => Failed(response.Url.ToString(), AvailabilityReason.HttpError) with { Detail = $"HTTP {(int)response.Status}" }
    };
    private static Probe? ChallengeOrLegal(DiagnosticResponse response) =>
        response.Status == HttpStatusCode.UnavailableForLegalReasons ? HttpFailure(response) :
        AvailabilityEvidence.IsChallenge(response.Body)
            ? Unknown(response.Url.ToString()) with { Reason = AvailabilityReason.HumanVerification, Detail = "Anti-bot challenge; service availability is unknown." } : null;

    private static bool IsCountryRestriction(string body)
    {
        try
        {
            using var doc = ParseObject(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) root = error;
            return new[] { "code", "type" }.Any(key => root.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String && AvailabilityEvidence.Contains(value.GetString()!,
                    "unsupported_country", "country_not_supported", "restricted_location"));
        }
        catch (JsonException) { return false; }
    }

    private static JsonDocument ParseObject(string body)
    {
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
        doc.Dispose();
        throw new JsonException("Expected a diagnostic JSON object.");
    }

    private static bool HasRestrictionMessage(string body, string marker)
    {
        try
        {
            using var doc = ParseObject(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                AvailabilityEvidence.Contains(message.GetString()!, marker)) return true;
            if (!root.TryGetProperty("error", out var error)) return false;
            if (error.ValueKind == JsonValueKind.String)
                return AvailabilityEvidence.Contains(error.GetString()!, marker);
            return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var errorMessage) &&
                errorMessage.ValueKind == JsonValueKind.String && AvailabilityEvidence.Contains(errorMessage.GetString()!, marker);
        }
        catch (JsonException) { return false; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _checks.Dispose();
    }
}
