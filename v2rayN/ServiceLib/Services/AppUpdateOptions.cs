namespace ServiceLib.Services;

public sealed class AppUpdateOptions
{
    public required string InstallationDirectory { get; init; }
    public required string StagingDirectory { get; init; }
    public required string CurrentVersion { get; init; }
    public required string HelperPath { get; init; }
    public bool UseLocalAppData { get; init; }
    public bool Supported { get; init; } = true;
    public required Func<bool, CancellationToken, Task<UpdateResult>> CheckReleaseAsync { get; init; }
    public required Func<string, string, IProgress<DownloadProgressSnapshot>, CancellationToken, Task> DownloadFileAsync { get; init; }
    public required Func<string, CancellationToken, Task<string?>> DownloadChecksumAsync { get; init; }
    public Func<ProcessStartInfo, bool> StartHelper { get; init; } = info =>
    {
        using var process = Process.Start(info);
        return process != null;
    };

    internal static AppUpdateOptions Create(Config config)
    {
        var installation = Path.GetFullPath(Utils.GetBaseDirectory());
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ToUpperInvariant())))[..16];
        return new()
        {
            InstallationDirectory = installation,
            StagingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "freedom-updates", identity),
            CurrentVersion = Utils.GetVersionInfo(),
            HelperPath = Path.Combine(installation, "AmazTool.exe"),
            UseLocalAppData = Environment.GetEnvironmentVariable(Global.LocalAppData) == "1",
            Supported = Utils.IsWindows() && !Utils.IsPackagedInstall()
                && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64,
            CheckReleaseAsync = (preRelease, token) =>
                new UpdateService(config, (_, _) => Task.CompletedTask)
                    .CheckHasUpdateOnly(ECoreType.v2rayN, preRelease).WaitAsync(token),
            DownloadFileAsync = (url, path, progress, token) =>
                new DownloadService().DownloadFileWithProgressAsync(url, path, true, progress, token),
            DownloadChecksumAsync = (url, token) =>
                new DownloadService().DownloadUpdateChecksumAsync(url, token)
        };
    }
}
