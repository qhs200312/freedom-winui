namespace ServiceLib.Services;

/// <summary>
/// One coordinator per application lifetime. Downloading never shuts down the app.
/// After a successful handoff the caller completes its normal shutdown sequence.
/// </summary>
public sealed class AppUpdateService
{
    private readonly AppUpdateOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cancellationLock = new();
    private CancellationTokenSource? _operation;
    private readonly string _stage;
    private readonly string _installation;
    private volatile PendingAppUpdate? _pending;
    private volatile UpdateStatusSnapshot _status = new(ECoreType.v2rayN, UpdateStage.Idle);
    private bool _handedOff;
    private volatile AppUpdateRelease? _release;

    public AppUpdateService(Config config) : this(AppUpdateOptions.Create(config)) { }

    public AppUpdateService(AppUpdateOptions options)
    {
        _options = options;
        _stage = Path.GetFullPath(options.StagingDirectory);
        _installation = Path.GetFullPath(options.InstallationDirectory);
    }

    public UpdateStatusSnapshot Status => _status;
    public PendingAppUpdate? PendingUpdate => _pending;
    public AppUpdateRelease? AvailableRelease => _release;
    public event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    public void Cancel()
    {
        lock (_cancellationLock) _operation?.Cancel();
    }

    public async Task<AppUpdateRelease?> CheckAsync(bool preRelease = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Supported) { Publish(UpdateStage.Unsupported); return null; }
            if (_handedOff) return _pending?.Release;
            var operation = BeginOperation(cancellationToken);
            if (_pending == null) await RestorePendingCoreAsync(operation.Token).ConfigureAwait(false);
            Publish(UpdateStage.Checking);
            var result = await _options.CheckReleaseAsync(preRelease, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (result.Success && result.Version != null && !string.IsNullOrWhiteSpace(result.Url))
            {
                var release = (result as AppUpdateCheckResult)?.Release
                    ?? new AppUpdateRelease(result.Version.ToVersionString(), result.Url);
                if (release.Version != result.Version.ToVersionString() || release.Url != result.Url)
                    throw new InvalidDataException("The app release metadata does not match its update result.");
                ValidateRelease(release);
                if (result.Version <= new SemanticVersion(_options.CurrentVersion))
                {
                    _release = _pending?.Release;
                    Publish(_pending != null ? UpdateStage.Ready : UpdateStage.UpToDate);
                    return _release;
                }
                if (SamePackage(_pending?.Release, release))
                {
                    var updated = _pending! with { Release = release };
                    if (updated != _pending)
                    {
                        await SavePendingManifestAsync(updated, operation.Token).ConfigureAwait(false);
                        _pending = updated;
                    }
                }
                _release = release;
                Publish(SamePackage(_pending?.Release, release) ? UpdateStage.Ready : UpdateStage.Available);
                return release;
            }
            _release = _pending?.Release;
            Publish(_pending != null ? UpdateStage.Ready
                : result.IsUpToDate ? UpdateStage.UpToDate : UpdateStage.Failed,
                error: result.IsUpToDate ? null : result.Msg);
            return _release;
        }
        catch (OperationCanceledException) when (IsOperationCancelled(cancellationToken))
        { Publish(UpdateStage.Cancelled); return null; }
        catch (Exception ex)
        {
            _release = _pending?.Release;
            Publish(_pending != null ? UpdateStage.Ready : UpdateStage.Failed, error: ex.Message);
            return _release;
        }
        finally { EndOperation(); _gate.Release(); }
    }

    public Task<PendingAppUpdate?> DownloadAsync(CancellationToken cancellationToken = default)
        => DownloadAsync(applyOnExit: false, cancellationToken);

    public async Task<PendingAppUpdate?> DownloadAsync(bool applyOnExit, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? candidate = null;
        try
        {
            if (!_options.Supported) { Publish(UpdateStage.Unsupported); return null; }
            if (_handedOff) throw new InvalidOperationException("An update has already been handed to the installer.");
            var release = _release ?? _pending?.Release
                ?? throw new InvalidOperationException("Check for an update before downloading.");
            ValidateRelease(release);
            var operation = BeginOperation(cancellationToken);
            if (_pending != null && SamePackage(_pending.Release, release)
                && await AppUpdateIntegrity.VerifyAsync(_pending.PackagePath, _pending.Sha256, operation.Token).ConfigureAwait(false))
            {
                var updated = _pending with { Release = release, ApplyOnExit = applyOnExit };
                await SavePendingManifestAsync(updated, operation.Token).ConfigureAwait(false);
                _pending = updated;
                Publish(UpdateStage.Ready);
                return _pending;
            }
            Directory.CreateDirectory(_stage);
            var packageName = Guid.NewGuid().ToString("N") + ".zip";
            candidate = Path.Combine(_stage, packageName + ".partial");
            Publish(UpdateStage.Downloading);
            var progress = new InlineProgress<DownloadProgressSnapshot>(value =>
            {
                if (operation.IsCancellationRequested || _status.Stage != UpdateStage.Downloading) return;
                Publish(UpdateStage.Downloading, progress: value);
            });
            await _options.DownloadFileAsync(release.Url, candidate, progress, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            Publish(UpdateStage.Verifying);
            var checksum = await _options.DownloadChecksumAsync(release.ChecksumUrl, operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            if (!await AppUpdateIntegrity.VerifyAsync(candidate, checksum, operation.Token).ConfigureAwait(false))
                throw new InvalidDataException("The update package SHA-256 is missing or does not match.");
            var hash = AppUpdateIntegrity.ParseChecksum(checksum)!;
            var package = Path.Combine(_stage, packageName);
            File.Move(candidate, package);
            candidate = package;
            var ready = new PendingAppUpdate(release, package, hash, applyOnExit);
            await SavePendingManifestAsync(ready, operation.Token).ConfigureAwait(false);
            var previous = _pending;
            _pending = ready;
            candidate = null;
            if (previous != null && previous.PackagePath != package) TryDelete(previous.PackagePath);
            Publish(UpdateStage.Ready);
            return _pending;
        }
        catch (OperationCanceledException) when (IsOperationCancelled(cancellationToken))
        { Publish(UpdateStage.Cancelled); return null; }
        catch (Exception ex) { Publish(UpdateStage.Failed, error: ex.Message); return null; }
        finally { if (candidate != null) TryDelete(candidate); EndOperation(); _gate.Release(); }
    }

    /// <summary>Persist the user's exit-install choice for an already verified ready package.</summary>
    public async Task<bool> SetApplyOnExitAsync(bool applyOnExit, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Supported || _handedOff) return false;
            if (_pending == null && !await RestorePendingCoreAsync(cancellationToken).ConfigureAwait(false)) return false;
            if (!await AppUpdateIntegrity.VerifyAsync(_pending!.PackagePath, _pending.Sha256, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The staged update package has changed.");
            var updated = _pending with { ApplyOnExit = applyOnExit };
            await SavePendingManifestAsync(updated, cancellationToken).ConfigureAwait(false);
            _pending = updated;
            Publish(UpdateStage.Ready);
            return true;
        }
        catch (OperationCanceledException) { Publish(UpdateStage.Cancelled); return false; }
        catch (Exception ex) { Publish(UpdateStage.Failed, error: ex.Message); return false; }
        finally { _gate.Release(); }
    }

    public async Task<bool> RestorePendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Supported) { Publish(UpdateStage.Unsupported); return false; }
            return await RestorePendingCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Publish(UpdateStage.Cancelled); return false; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// restartAfterInstall=false applies on normal exit without reopening the app.
    /// This method launches the owned hidden helper, but never exits or kills the app.
    /// </summary>
    public async Task<bool> HandoffAsync(bool restartAfterInstall, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Supported) { Publish(UpdateStage.Unsupported); return false; }
            if (_handedOff) return true;
            if (_pending == null && !await RestorePendingCoreAsync(cancellationToken).ConfigureAwait(false)) return false;
            var pending = _pending!;
            cancellationToken.ThrowIfCancellationRequested();
            if (!await AppUpdateIntegrity.VerifyAsync(pending.PackagePath, pending.Sha256, cancellationToken).ConfigureAwait(false))
            {
                _pending = null;
                TryDelete(ManifestPath);
                throw new InvalidDataException("The staged update package has changed.");
            }
            var helper = await Task.Run(() => AppUpdatePreparation.PrepareHelper(_options.HelperPath, _stage),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo
            {
                FileName = helper, WorkingDirectory = Path.GetDirectoryName(helper)!,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            using var currentProcess = Process.GetCurrentProcess();
            foreach (var argument in new[]
            {
                "upgrade", pending.PackagePath, Environment.ProcessId.ToString(), _installation,
                "--sha256", pending.Sha256, "--wait-for-normal-exit", "--wait-seconds", "120",
                "--process-start-time", currentProcess.StartTime.ToUniversalTime().Ticks.ToString()
            }) info.ArgumentList.Add(argument);
            if (!restartAfterInstall) info.ArgumentList.Add("--no-restart");
            if (_options.UseLocalAppData) info.ArgumentList.Add(Global.UseLocalAppDataArgument);
            if (!_options.StartHelper(info)) throw new IOException("The update helper could not be started.");
            _handedOff = true;
            Publish(UpdateStage.HandedOff);
            return true;
        }
        catch (OperationCanceledException) { Publish(UpdateStage.Cancelled); return false; }
        catch (Exception ex) { Publish(UpdateStage.Failed, error: ex.Message); return false; }
        finally { _gate.Release(); }
    }

    private string ManifestPath => Path.Combine(_stage, "pending.json");

    private async Task<bool> RestorePendingCoreAsync(CancellationToken token)
    {
        if (!File.Exists(ManifestPath))
        {
            _pending = null;
            return false;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<PendingManifest>(await File.ReadAllTextAsync(ManifestPath, token).ConfigureAwait(false))
                ?? throw new InvalidDataException("The pending update manifest is empty.");
            if (!string.Equals(Path.GetFullPath(manifest.InstallationDirectory), _installation, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(manifest.PackageFile, "^[a-f0-9]{32}\\.zip$"))
                throw new InvalidDataException("The pending update belongs to a different installation.");
            ValidateRelease(manifest.Release);
            var package = Path.Combine(_stage, manifest.PackageFile);
            if (new SemanticVersion(manifest.Release.Version) <= new SemanticVersion(_options.CurrentVersion))
            {
                TryDelete(ManifestPath);
                TryDelete(package);
                _pending = null;
                _release = null;
                Publish(UpdateStage.UpToDate);
                return false;
            }
            Publish(UpdateStage.Verifying);
            token.ThrowIfCancellationRequested();
            if (!await AppUpdateIntegrity.VerifyAsync(package, manifest.Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("The staged update package SHA-256 does not match.");
            _pending = new(manifest.Release, package, manifest.Sha256, manifest.ApplyOnExit);
            _release = manifest.Release;
            Publish(UpdateStage.Ready);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _pending = null;
            _release = null;
            TryDelete(ManifestPath);
            Publish(UpdateStage.Failed, error: ex.Message);
            return false;
        }
    }

    private async Task SavePendingManifestAsync(PendingAppUpdate pending, CancellationToken token)
    {
        var manifest = new PendingManifest(_installation, Path.GetFileName(pending.PackagePath),
            pending.Release, pending.Sha256, pending.ApplyOnExit);
        var temporary = Path.Combine(_stage, "pending-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, ManifestPath, true);
        }
        finally { TryDelete(temporary); }
    }

    private static void ValidateRelease(AppUpdateRelease release)
    {
        // CoreUrls is the branded repository source in this branch (there is no Global.NUrl).
        var root = $"{Global.GithubUrl}/{Global.CoreUrls[ECoreType.v2rayN]}/releases/download/";
        var version = new SemanticVersion(release.Version);
        if (!Uri.TryCreate(release.Url, UriKind.Absolute, out var uri)
            || !Regex.IsMatch(release.Version, @"\Av?[0-9]+\.[0-9]+\.[0-9]+\z")
            || !System.Version.TryParse(version.ToVersionString(), out _)
            || uri.AbsoluteUri != root + "v" + version.ToVersionString() + "/" + Path.GetFileName(uri.AbsolutePath)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || Path.GetFileName(uri.AbsolutePath) is not ("freedom-windows-64.zip" or "freedom-windows-arm64.zip")
            || version == new SemanticVersion(0, 0, 0))
            throw new InvalidDataException("The app update is not a branded release package.");
    }

    private static bool SamePackage(AppUpdateRelease? first, AppUpdateRelease second)
        => first != null && first.Version == second.Version && first.Url == second.Url;

    private CancellationTokenSource BeginOperation(CancellationToken token)
    {
        lock (_cancellationLock) return _operation = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    private bool IsOperationCancelled(CancellationToken token)
    {
        lock (_cancellationLock) return token.IsCancellationRequested || (_operation?.IsCancellationRequested ?? false);
    }

    private void EndOperation()
    {
        lock (_cancellationLock)
        {
            _operation?.Dispose();
            _operation = null;
        }
    }

    private void Publish(UpdateStage stage, string? error = null, DownloadProgressSnapshot? progress = null)
    {
        var snapshot = new UpdateStatusSnapshot(ECoreType.v2rayN, stage, _release?.Version ?? _pending?.Release.Version,
            progress?.Percentage, progress?.BytesReceived ?? 0, progress?.TotalBytes, error, _pending?.PackagePath);
        _status = snapshot;
        StatusChanged?.Invoke(this, snapshot);
        AppEvents.UpdateStatusChanged.Publish(snapshot);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private sealed record PendingManifest(string InstallationDirectory, string PackageFile, AppUpdateRelease Release,
        string Sha256, bool ApplyOnExit = false);
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
