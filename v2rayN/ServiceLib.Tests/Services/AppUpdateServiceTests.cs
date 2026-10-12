using AmazTool;
using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class AppUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "freedom-coordinator-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("fake release archive for lifecycle tests");
    private string Stage => Path.Combine(_root, "pending");
    private string Installation => Path.Combine(_root, "installed");
    private UpdateResult _release = Release("7.23.9");
    private string? _checksum;
    private bool _checksumThrows;
    private int _downloads;
    private readonly List<ProcessStartInfo> _starts = [];
    private Func<string, string, IProgress<DownloadProgressSnapshot>, CancellationToken, Task>? _download;
    private Func<bool, CancellationToken, Task<UpdateResult>>? _check;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public AppUpdateServiceTests()
    {
        Directory.CreateDirectory(Installation);
        File.WriteAllText(Path.Combine(Installation, "freedom.exe"), "running old app");
        File.WriteAllText(Path.Combine(Installation, "AmazTool.exe"), "helper");
        File.WriteAllText(Path.Combine(Installation, "AmazTool.dll"), "helper runtime");
        _checksum = Convert.ToHexString(SHA256.HashData(_payload));
    }

    [Fact]
    public async Task BackgroundDownload_ShouldPublishTypedProgressAndNeverLaunchHelper()
    {
        var service = Create();
        var statuses = new List<UpdateStatusSnapshot>();
        service.StatusChanged += (_, value) => statuses.Add(value);
        var observed = new List<UpdateStatusSnapshot>();
        using var subscription = AppEvents.UpdateStatusChanged.AsObservable()
            .Where(value => value.CoreType == ECoreType.v2rayN && value.PackagePath?.StartsWith(_root) == true)
            .Subscribe(observed.Add);
        (await service.CheckAsync(cancellationToken: Token)).Should().NotBeNull();
        var pending = await service.DownloadAsync(Token);
        pending.Should().NotBeNull();
        _starts.Should().BeEmpty();
        statuses.Select(value => value.Stage).Should().ContainInOrder(
            UpdateStage.Checking, UpdateStage.Available, UpdateStage.Downloading, UpdateStage.Verifying, UpdateStage.Ready);
        statuses.Should().Contain(value => value.Stage == UpdateStage.Downloading
            && value.BytesReceived == _payload.Length && value.Percentage == 100);
        observed.Should().Contain(value => value.Stage == UpdateStage.Ready);
        File.ReadAllText(Path.Combine(Installation, "freedom.exe")).Should().Be("running old app");
        File.Exists(Path.Combine(Stage, "pending.json")).Should().BeTrue();
        pending!.ApplyOnExit.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("bad checksum", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData(null, true)]
    public async Task MissingFailedOrWrongChecksum_ShouldNeverStageOrHandoff(string? checksum, bool throws)
    {
        _checksum = checksum;
        _checksumThrows = throws;
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        (await service.DownloadAsync(Token)).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.PendingUpdate.Should().BeNull();
        (await service.HandoffAsync(false, Token)).Should().BeFalse();
        _starts.Should().BeEmpty();
        File.Exists(Path.Combine(Stage, "pending.json")).Should().BeFalse();
        Directory.GetFiles(Stage, "*.partial").Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_ShouldVerifyPersistedPackageWithoutNetwork()
    {
        var original = Create();
        await original.CheckAsync(cancellationToken: Token);
        var pending = await original.DownloadAsync(Token);
        _download = (_, _, _, _) => throw new InvalidOperationException("No network during restore");
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.PendingUpdate.Should().Be(pending);
        restored.Status.Stage.Should().Be(UpdateStage.Ready);
        (await restored.DownloadAsync(Token)).Should().Be(pending);
        _downloads.Should().Be(1);
        _starts.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseNotes_ShouldFlowThroughCheckDownloadAndRestoreWithoutNetwork()
    {
        _release = ReleaseWithNotes("7.23.9", "## Changes\n\n- A release-specific fix.");
        var service = Create();
        var release = await service.CheckAsync(cancellationToken: Token);
        release!.ReleaseNotes.Should().Be("## Changes\n\n- A release-specific fix.");
        service.AvailableRelease.Should().Be(release);
        var pending = (await service.DownloadAsync(true, Token))!;
        pending.Release.Should().Be(release);
        _check = (_, _) => throw new IOException("No network during restore");
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.PendingUpdate.Should().Be(pending);
        restored.AvailableRelease!.ReleaseNotes.Should().Be(release.ReleaseNotes);
        (await restored.DownloadAsync(true, Token)).Should().Be(pending);
        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task LegacyManifest_ShouldRestoreAndCacheNewNotesWithoutRedownloadOrLosingConsent()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(true, Token))!;
        var manifestPath = Path.Combine(Stage, "pending.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["Release"]!.AsObject().Remove("ReleaseNotes");
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.PendingUpdate!.Release.ReleaseNotes.Should().BeNull();
        restored.PendingUpdate.ApplyOnExit.Should().BeTrue();
        _release = ReleaseWithNotes("7.23.9", "Notes added to the existing release.");
        await restored.CheckAsync(cancellationToken: Token);
        restored.Status.Stage.Should().Be(UpdateStage.Ready);
        restored.PendingUpdate!.Release.ReleaseNotes.Should().Be("Notes added to the existing release.");
        restored.PendingUpdate.PackagePath.Should().Be(pending.PackagePath);
        restored.PendingUpdate.ApplyOnExit.Should().BeTrue();
        var nextStartup = Create();
        (await nextStartup.RestorePendingAsync(Token)).Should().BeTrue();
        nextStartup.AvailableRelease.Should().Be(restored.AvailableRelease);
        (await nextStartup.DownloadAsync(true, Token))!.PackagePath.Should().Be(pending.PackagePath);
        _downloads.Should().Be(1);
        _starts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Revised notes for the same release.")]
    public async Task ChangedReleaseNotes_ShouldKeepReadyPackageAndRefreshManifest(string? notes)
    {
        _release = ReleaseWithNotes("7.23.9", "Original notes.");
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(true, Token))!;
        _release = ReleaseWithNotes("7.23.9", notes);
        await service.CheckAsync(cancellationToken: Token);
        service.Status.Stage.Should().Be(UpdateStage.Ready);
        service.AvailableRelease!.ReleaseNotes.Should().Be(notes);
        service.PendingUpdate!.Release.ReleaseNotes.Should().Be(notes);
        (await service.DownloadAsync(true, Token))!.PackagePath.Should().Be(pending.PackagePath);
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.AvailableRelease!.ReleaseNotes.Should().Be(notes);
        restored.PendingUpdate!.ApplyOnExit.Should().BeTrue();
        _downloads.Should().Be(1);
    }

    [Fact]
    public async Task NewReleaseWithoutNotes_ShouldNeverInheritPendingReleaseNotes()
    {
        _release = ReleaseWithNotes("7.23.9", "Only for 7.23.9.");
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var previous = await service.DownloadAsync(true, Token);
        _release = ReleaseWithNotes("7.24.0", null);
        var next = (await service.CheckAsync(cancellationToken: Token))!;
        next.Version.Should().Be("7.24.0");
        next.ReleaseNotes.Should().BeNull();
        service.PendingUpdate.Should().Be(previous);
        var pending = (await service.DownloadAsync(true, Token))!;
        pending.Release.ReleaseNotes.Should().BeNull();
        pending.Release.Version.Should().Be("7.24.0");
        _downloads.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFailure_ShouldClearStaleAvailableReleaseAndPreserveVerifiedPending(bool staged)
    {
        _release = ReleaseWithNotes("7.23.9", "Cached notes.");
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = staged ? await service.DownloadAsync(true, Token) : null;
        _release = ReleaseWithNotes("7.24.0", "Only for the unchecked release.");
        await service.CheckAsync(cancellationToken: Token);
        _check = (_, _) => Task.FromException<UpdateResult>(new IOException("metadata endpoint failed"));
        (await service.CheckAsync(cancellationToken: Token)).Should().Be(pending?.Release);
        service.AvailableRelease.Should().Be(pending?.Release);
        service.PendingUpdate.Should().Be(pending);
        service.Status.Stage.Should().Be(staged ? UpdateStage.Ready : UpdateStage.Failed);
        service.Status.Error.Should().Be("metadata endpoint failed");
        _starts.Should().BeEmpty();
    }

    [Fact]
    public async Task InconsistentMetadataCarrier_ShouldFailWithoutDisplayingUnrelatedNotes()
    {
        _release = ReleaseWithNotes("7.23.9", "Only for 7.23.9.");
        _release.Version = new SemanticVersion("7.24.0");
        var service = Create();
        (await service.CheckAsync(cancellationToken: Token)).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.AvailableRelease.Should().BeNull();
        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task RestoreOrHandoff_ShouldRejectTamperedPackage()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(Token))!;
        File.AppendAllText(pending.PackagePath, "tampered");
        (await service.HandoffAsync(false, Token)).Should().BeFalse();
        _starts.Should().BeEmpty();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.PendingUpdate.Should().BeNull();
        // Restore checks independently, before a helper can ever be launched.
        await service.CheckAsync(cancellationToken: Token);
        pending = (await service.DownloadAsync(Token))!;
        File.AppendAllText(pending.PackagePath, "tampered again");
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeFalse();
        restored.PendingUpdate.Should().BeNull();
        restored.Status.Stage.Should().Be(UpdateStage.Failed);
    }

    [Fact]
    public async Task InstalledVersion_ShouldDiscardObsoletePendingPackage()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(Token))!;
        var restored = Create(currentVersion: "7.23.9");
        (await restored.RestorePendingAsync(Token)).Should().BeFalse();
        restored.Status.Stage.Should().Be(UpdateStage.UpToDate);
        File.Exists(pending.PackagePath).Should().BeFalse();
    }

    [Fact]
    public async Task FailedNewDownload_ShouldPreservePreviousVerifiedPackage()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var previous = await service.DownloadAsync(Token);
        _release = Release("7.24.0");
        await service.CheckAsync(cancellationToken: Token);
        _checksum = null;
        (await service.DownloadAsync(Token)).Should().BeNull();
        service.PendingUpdate.Should().Be(previous);
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.PendingUpdate.Should().Be(previous);
    }

    [Fact]
    public async Task SilentDownload_ShouldPersistExitInstallConsentAcrossStartupChecks()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(applyOnExit: true, cancellationToken: Token))!;
        pending.ApplyOnExit.Should().BeTrue();
        _starts.Should().BeEmpty();
        var restored = Create();
        _release = new(false, "offline");
        (await restored.CheckAsync(cancellationToken: Token)).Should().Be(pending.Release);
        restored.PendingUpdate.Should().Be(pending);
        restored.Status.Stage.Should().Be(UpdateStage.Ready);
        _release = Release("7.24.0");
        await restored.CheckAsync(cancellationToken: Token);
        restored.PendingUpdate.Should().Be(pending);
        restored.Status.Stage.Should().Be(UpdateStage.Available);
    }

    [Fact]
    public async Task ReadyPackage_ShouldAllowPersistingAndRevokingExitInstallWithoutRedownload()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(Token))!;
        (await service.SetApplyOnExitAsync(true, Token)).Should().BeTrue();
        service.PendingUpdate!.ApplyOnExit.Should().BeTrue();
        service.PendingUpdate.PackagePath.Should().Be(pending.PackagePath);
        _downloads.Should().Be(1);
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeTrue();
        restored.PendingUpdate!.ApplyOnExit.Should().BeTrue();
        (await restored.SetApplyOnExitAsync(false, Token)).Should().BeTrue();
        var ordinary = Create();
        (await ordinary.RestorePendingAsync(Token)).Should().BeTrue();
        ordinary.PendingUpdate!.ApplyOnExit.Should().BeFalse();
    }

    [Fact]
    public async Task ClosingDuringDownload_ShouldCancelQuicklyAndKeepPreviouslyReadyExitUpdate()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var previous = (await service.DownloadAsync(true, Token))!;
        _release = Release("7.24.0");
        await service.CheckAsync(cancellationToken: Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _download = async (_, path, _, token) =>
        {
            await File.WriteAllTextAsync(path, "partial", token);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var download = service.DownloadAsync(true, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        service.Cancel();
        (await download.WaitAsync(TimeSpan.FromSeconds(2), Token)).Should().BeNull();
        service.PendingUpdate.Should().Be(previous);
        service.PendingUpdate!.ApplyOnExit.Should().BeTrue();
        (await service.HandoffAsync(false, Token).WaitAsync(TimeSpan.FromSeconds(2), Token)).Should().BeTrue();
        _starts.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancellation_ShouldCleanPartialAndPermitRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _download = async (_, path, progress, token) =>
        {
            await File.WriteAllTextAsync(path, "partial", token);
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var download = service.DownloadAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        service.Cancel();
        (await download).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Cancelled);
        Directory.GetFiles(Stage, "*.partial").Should().BeEmpty();
        _starts.Should().BeEmpty();
        _download = null;
        (await service.DownloadAsync(Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task SimultaneousDownloads_ShouldOnlyFetchOnePackage()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var results = await Task.WhenAll(service.DownloadAsync(Token), service.DownloadAsync(Token));
        results[0].Should().Be(results[1]);
        _downloads.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handoff_ShouldUseOwnedHiddenHelperAndCorrectRestartArguments(bool restart)
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        var pending = (await service.DownloadAsync(Token))!;
        (await service.HandoffAsync(restart, Token)).Should().BeTrue();
        (await service.HandoffAsync(restart, Token)).Should().BeTrue();
        _starts.Should().ContainSingle();
        var info = _starts.Single();
        info.UseShellExecute.Should().BeFalse();
        info.CreateNoWindow.Should().BeTrue();
        info.WindowStyle.Should().Be(ProcessWindowStyle.Hidden);
        Path.GetFullPath(info.FileName).Should().StartWith(Path.GetFullPath(Stage) + Path.DirectorySeparatorChar);
        var command = UpdateCommandOptions.Parse(info.ArgumentList.ToArray());
        command.PackagePath.Should().Be(pending.PackagePath);
        command.TargetDirectory.Should().Be(Path.GetFullPath(Installation));
        command.Sha256.Should().Be(pending.Sha256);
        command.RestartAfterInstall.Should().Be(restart);
        command.WaitForNormalExit.Should().BeTrue();
        command.WaitSeconds.Should().Be(120);
        command.ProcessStartTime.Should().NotBeNull();
        command.UseLocalAppData.Should().BeTrue();
        service.Status.Stage.Should().Be(UpdateStage.HandedOff);
        File.ReadAllText(Path.Combine(Installation, "freedom.exe")).Should().Be("running old app");
    }

    [Fact]
    public async Task UnbrandedPackage_ShouldFailWithoutDownloading()
    {
        _release.Url = "https://github.com/2dust/v2rayN/releases/download/7.23.9/v2rayN-windows-64.zip";
        var service = Create();
        (await service.CheckAsync(cancellationToken: Token)).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        (await service.DownloadAsync(Token)).Should().BeNull();
        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task FourPartAppVersion_ShouldBeRejectedInsteadOfComparedAsThreeParts()
    {
        _release = Release("7.23.9.1");
        var service = Create();
        (await service.CheckAsync(cancellationToken: Token)).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.AvailableRelease.Should().BeNull();
        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task UpToDateAndCheckError_ShouldHaveDistinctTypedStates()
    {
        _release = new(false, "current") { IsUpToDate = true };
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        service.Status.Stage.Should().Be(UpdateStage.UpToDate);
        _release = new(false, "release unavailable");
        await service.CheckAsync(cancellationToken: Token);
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.Status.Error.Should().Be("release unavailable");
    }

    [Fact]
    public async Task NetworkTimeout_ShouldReportFailureAndPermitRetry()
    {
        _download = (_, _, _, _) => Task.FromException(new OperationCanceledException("Network idle timeout"));
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        (await service.DownloadAsync(Token)).Should().BeNull();
        service.Status.Stage.Should().Be(UpdateStage.Failed);
        service.Status.Error.Should().Be("Network idle timeout");
        _download = null;
        (await service.DownloadAsync(Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task Restore_ShouldRejectManifestPathsOutsideOwnedStage()
    {
        var service = Create();
        await service.CheckAsync(cancellationToken: Token);
        await service.DownloadAsync(Token);
        var outside = Path.Combine(_root, "external.zip");
        File.WriteAllBytes(outside, _payload);
        var manifestPath = Path.Combine(Stage, "pending.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["PackageFile"] = "../external.zip";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var restored = Create();
        (await restored.RestorePendingAsync(Token)).Should().BeFalse();
        restored.Status.Stage.Should().Be(UpdateStage.Failed);
        File.Exists(outside).Should().BeTrue();
        _starts.Should().BeEmpty();
    }

    private AppUpdateService Create(string currentVersion = "7.23.8") => new(new AppUpdateOptions
    {
        InstallationDirectory = Installation, StagingDirectory = Stage, CurrentVersion = currentVersion,
        HelperPath = Path.Combine(Installation, "AmazTool.exe"), UseLocalAppData = true,
        CheckReleaseAsync = (preRelease, token) => _check?.Invoke(preRelease, token) ?? Task.FromResult(_release),
        DownloadChecksumAsync = (_, _) => _checksumThrows
            ? Task.FromException<string?>(new IOException("checksum endpoint failed"))
            : Task.FromResult(_checksum),
        DownloadFileAsync = async (url, path, progress, token) =>
        {
            Interlocked.Increment(ref _downloads);
            if (_download != null) { await _download(url, path, progress, token); return; }
            await File.WriteAllBytesAsync(path, _payload, token);
            progress.Report(new(_payload.Length, _payload.Length, 100));
        },
        StartHelper = info => { _starts.Add(info); return true; }
    });

    private static UpdateResult Release(string version) => new(true, new SemanticVersion(version))
    {
        Url = $"https://github.com/{Global.CoreUrls[ECoreType.v2rayN]}/releases/download/v{version}/freedom-windows-64.zip"
    };

    private static AppUpdateCheckResult ReleaseWithNotes(string version, string? notes)
        => new(new AppUpdateRelease(version,
            $"https://github.com/{Global.CoreUrls[ECoreType.v2rayN]}/releases/download/v{version}/freedom-windows-64.zip", notes));

    public void Dispose()
    {
        if (Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()) + "freedom-coordinator-tests-", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }
}
