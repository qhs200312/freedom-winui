namespace ServiceLib.Models.Dto;

public enum UpdateStage
{
    Idle, Checking, Available, UpToDate, Downloading, Verifying, Ready,
    HandedOff, Installing, Installed, Cancelled, Failed, Unsupported
}

public sealed record DownloadProgressSnapshot(long BytesReceived, long? TotalBytes, double? Percentage);

public sealed record UpdateStatusSnapshot(
    ECoreType CoreType, UpdateStage Stage, string? Version = null,
    double? Percentage = null, long BytesReceived = 0, long? TotalBytes = null,
    string? Error = null, string? PackagePath = null);

public sealed record AppUpdateRelease(string Version, string Url, string? ReleaseNotes = null)
{
    public string ChecksumUrl => Url + ".sha256";
}

/// <summary>App metadata from a single release; Msg remains an update status message.</summary>
public sealed class AppUpdateCheckResult : UpdateResult
{
    public AppUpdateRelease Release { get; }

    public AppUpdateCheckResult(AppUpdateRelease release) : base(true, new SemanticVersion(release.Version))
    {
        Release = release;
        Url = release.Url;
    }
}

public sealed record PendingAppUpdate(AppUpdateRelease Release, string PackagePath, string Sha256, bool ApplyOnExit = false);
