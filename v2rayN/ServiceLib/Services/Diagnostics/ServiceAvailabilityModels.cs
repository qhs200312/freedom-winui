namespace ServiceLib.Services.Diagnostics;

public enum AvailabilityService
{
    BilibiliMainland,
    BilibiliHongKongTaiwan,
    ChatGPT,
    Claude,
    Gemini,
    YouTubePremium,
    Bahamut,
    Netflix,
    DisneyPlus,
    PrimeVideo,
    Spotify,
    TikTok
}

public enum AvailabilityState
{
    Available,
    Unavailable,
    Limited,
    Failed,
    Unknown
}

public enum AvailabilityReason
{
    Available,
    Restricted,
    UnsupportedRegion,
    IpBanned,
    OriginalsOnly,
    ComingSoon,
    NetworkError,
    Timeout,
    HttpError,
    InvalidResponse,
    MissingEvidence,
    ResponseTooLarge,
    PageReachable,
    CatalogAvailable,
    AuthenticationRequired,
    HumanVerification,
    AccessDenied,
    SampleUnavailable,
    ServiceBusy
}

public sealed record ServiceAvailabilityDefinition(AvailabilityService Id, string Name);

public sealed record ServiceAvailabilityResult(
    AvailabilityService Service,
    string Name,
    AvailabilityState State,
    AvailabilityReason Reason,
    string? RegionCode,
    string EvidenceUrl,
    string? RegionEvidenceUrl,
    DateTimeOffset CheckedAt,
    TimeSpan Elapsed,
    string? Detail = null)
{
    public string Status => Reason switch
    {
        AvailabilityReason.UnsupportedRegion => "Unsupported Country/Region",
        AvailabilityReason.IpBanned when Service == AvailabilityService.DisneyPlus => "No (IP Banned By Disney+)",
        AvailabilityReason.IpBanned when Service == AvailabilityService.Netflix => "No (IP Banned By Netflix)",
        AvailabilityReason.OriginalsOnly => "Originals Only",
        AvailabilityReason.ComingSoon => "Soon",
        AvailabilityReason.NetworkError => "Failed (Network Connection)",
        AvailabilityReason.PageReachable => "Web page reachable",
        AvailabilityReason.CatalogAvailable => "Catalog available",
        AvailabilityReason.AuthenticationRequired => "Sign-in required",
        AvailabilityReason.HumanVerification => "Human verification required",
        AvailabilityReason.AccessDenied => "Access denied",
        AvailabilityReason.SampleUnavailable => "Reference titles unavailable",
        AvailabilityReason.ServiceBusy => "Service temporarily unavailable",
        _ => State switch
        {
            AvailabilityState.Available => "Yes",
            AvailabilityState.Unavailable => "No",
            AvailabilityState.Failed => "Failed",
            _ => "Unknown"
        }
    };
}

public sealed record ServiceAvailabilityOptions
{
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxConcurrency { get; init; } = 4;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxRedirects { get; init; } = 5;

    internal void Validate()
    {
        if (CheckTimeout <= TimeSpan.Zero || CheckTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(CheckTimeout));
        if (MaxConcurrency is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        if (MaxResponseBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (MaxRedirects is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(MaxRedirects));
    }
}
