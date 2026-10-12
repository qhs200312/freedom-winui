using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class AppReleaseMetadataTests
{
    private const string ReleasesUrl = "https://api.github.com/repos/qhs200312/freedom-winui/releases";
    private const string DownloadRoot = "https://github.com/qhs200312/freedom-winui/releases/download/";
    private const string Package = "freedom-windows-64.zip";
    private static readonly MethodInfo MetadataMethod = typeof(UpdateService)
        .GetMethod("GetAppReleaseAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("7.23.8")]
    [InlineData("7.3.0")]
    public async Task RenumberedRelease_ShouldBeNewerThanBothPreviousAppVersions(string currentVersion)
    {
        var result = await Fetch(false, Package, JsonUtils.Serialize(Release("v7.30.0", "Version numbering correction.")), []);
        result.Success.Should().BeTrue();
        (new SemanticVersion(currentVersion) >= result.Version!).Should().BeFalse();
        ((AppUpdateCheckResult)result).Release.Url.Should().Be(DownloadRoot + "v7.30.0/" + Package);
    }

    [Theory]
    [InlineData("freedom-windows-64.zip")]
    [InlineData("freedom-windows-arm64.zip")]
    public async Task StableCheck_ShouldUseBrandedLatestApiOnceAndKeepBodyWithExactAsset(string package)
    {
        var release = Release("v7.23.9", "## Changes\n\n- Notes for 7.23.9.", package);
        release.Assets.Add(Release("v7.23.9", null,
            package == Package ? "freedom-windows-arm64.zip" : Package).Assets.Single());
        var requests = new List<string>();
        var result = await Fetch(false, package, JsonUtils.Serialize(release), requests);
        requests.Should().Equal(ReleasesUrl + "/latest");
        result.Success.Should().BeTrue();
        result.IsUpToDate.Should().BeFalse();
        result.Version!.ToVersionString().Should().Be("7.23.9");
        result.Url.Should().Be(DownloadRoot + "v7.23.9/" + package);
        var appRelease = ((AppUpdateCheckResult)result).Release;
        appRelease.ReleaseNotes.Should().Be(release.Body);
        appRelease.Url.Should().Be(result.Url);
        appRelease.Version.Should().Be(result.Version.ToVersionString());
        appRelease.ChecksumUrl.Should().Be(result.Url + ".sha256");
        result.Msg.Should().BeNull();
    }

    [Fact]
    public async Task PreReleaseCheck_ShouldSkipDraftsAndUseOnlySelectedReleaseBodyAndAsset()
    {
        var draft = Release("v7.25.0", "Draft notes.");
        draft.Draft = true;
        var preview = Release("v7.24.0", "Preview notes.");
        preview.Prerelease = true;
        var stable = Release("v7.23.9", "Stable notes.");
        var requests = new List<string>();
        var result = await Fetch(true, Package, JsonUtils.Serialize(new[] { draft, preview, stable }), requests);
        requests.Should().Equal(ReleasesUrl);
        result.Success.Should().BeTrue();
        ((AppUpdateCheckResult)result).Release.Should().Be(
            new AppUpdateRelease("7.24.0", DownloadRoot + "v7.24.0/" + Package, "Preview notes."));
    }

    [Fact]
    public async Task LatestReleaseMissingPackage_ShouldFailRatherThanUseOlderAssetOrNotes()
    {
        var latest = Release("v7.24.0", "Latest notes.", "freedom-windows-arm64.zip");
        var older = Release("v7.23.9", "Older notes.");
        var requests = new List<string>();
        var result = await Fetch(true, Package, JsonUtils.Serialize(new[] { latest, older }), requests);
        requests.Should().Equal(ReleasesUrl);
        AssertMetadataFailure(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public async Task UnavailableOrInvalidMetadata_ShouldFailRatherThanReportUpToDate(string? json)
    {
        var result = await Fetch(false, Package, json, []);
        AssertMetadataFailure(result);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v0.0.0")]
    [InlineData("v7.24.0-beta.1")]
    [InlineData("7.24.0")]
    [InlineData("v7.24")]
    [InlineData("v7.24.0.1")]
    [InlineData("v7.24.0\n")]
    [InlineData("v99999999999999999999.1.1")]
    [InlineData("v7.24.0.99999999999999999999")]
    public async Task InvalidAppTag_ShouldFailBeforeExposingNotes(string tag)
    {
        var result = await Fetch(false, Package, JsonUtils.Serialize(Release(tag, "Unusable notes.")), []);
        AssertMetadataFailure(result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StableEndpointReturningDraftOrPreview_ShouldFail(bool draft, bool preview)
    {
        var release = Release("v7.23.9", "Wrong channel.");
        release.Draft = draft;
        release.Prerelease = preview;
        AssertMetadataFailure(await Fetch(false, Package, JsonUtils.Serialize(release), []));
    }

    [Theory]
    [InlineData("https://github.com/2dust/v2rayN/releases/download/v7.23.9/freedom-windows-64.zip")]
    [InlineData("https://github.com/qhs200312/freedom-winui/releases/download/v7.24.0/freedom-windows-64.zip")]
    [InlineData("https://github.com/qhs200312/freedom-winui/releases/download/v7.23.9/freedom-windows-arm64.zip")]
    [InlineData("https://github.com/qhs200312/freedom-winui/releases/download/v7.23.9/freedom-windows-64.zip?extra=1")]
    [InlineData(null)]
    public async Task AssetFromDifferentRepoVersionOrArchitecture_ShouldNeverCarryReleaseNotes(string? url)
    {
        var release = Release("v7.23.9", "Notes must not be paired with another package.");
        release.Assets.Single().BrowserDownloadUrl = url;
        AssertMetadataFailure(await Fetch(false, Package, JsonUtils.Serialize(release), []));
    }

    [Fact]
    public async Task MissingOrDuplicateAssets_ShouldFailRatherThanConstructDownloadUrl()
    {
        var release = Release("v7.23.9", "Notes.");
        release.Assets.Add(release.Assets.Single());
        AssertMetadataFailure(await Fetch(false, Package, JsonUtils.Serialize(release), []));
        release.Assets = [];
        AssertMetadataFailure(await Fetch(false, Package, JsonUtils.Serialize(release), []));
    }

    [Fact]
    public async Task UnsupportedPackage_ShouldFailWithoutRequestingMetadata()
    {
        var requests = new List<string>();
        AssertMetadataFailure(await Fetch(false, "freedom-windows-64-desktop.zip", null, requests));
        requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingBody_ShouldBeValidAndNeverInventNotes(string? body)
    {
        var result = await Fetch(false, Package, JsonUtils.Serialize(Release("v7.23.9", body)), []);
        result.Success.Should().BeTrue();
        ((AppUpdateCheckResult)result).Release.ReleaseNotes.Should().Be(body);
    }

    [Fact]
    public async Task UpToDateComparison_ShouldPreserveAtomicMetadataAndStatusMessage()
    {
        var version = new SemanticVersion(Utils.GetVersionInfo()).ToVersionString();
        var result = await Fetch(false, Package, JsonUtils.Serialize(Release("v" + version, "Current release notes.")), []);
        var service = new UpdateService(new Config(), (_, _) => Task.CompletedTask);
        var parse = typeof(UpdateService).GetMethod("ParseDownloadUrl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parsed = await (Task<UpdateResult>)parse.Invoke(service, [ECoreType.v2rayN, result])!;
        parsed.Should().BeSameAs(result);
        parsed.Success.Should().BeFalse();
        parsed.IsUpToDate.Should().BeTrue();
        parsed.Msg.Should().NotBeNullOrWhiteSpace();
        ((AppUpdateCheckResult)parsed).Release.ReleaseNotes.Should().Be("Current release notes.");
    }

    private static GitHubRelease Release(string tag, string? body, string package = Package) => new()
    {
        TagName = tag, Body = body,
        Assets = [new GitHubReleaseAsset { Name = package, BrowserDownloadUrl = DownloadRoot + tag + "/" + package }]
    };

    private static Task<UpdateResult> Fetch(bool preview, string? package, string? json, List<string> requests)
    {
        Func<string, Task<string?>> download = url =>
        {
            requests.Add(url);
            return Task.FromResult(json);
        };
        return (Task<UpdateResult>)MetadataMethod.Invoke(null, [preview, package, download])!;
    }

    private static void AssertMetadataFailure(UpdateResult result)
    {
        result.Success.Should().BeFalse();
        result.IsUpToDate.Should().BeFalse();
        result.Should().NotBeOfType<AppUpdateCheckResult>();
        result.Version.Should().BeNull();
        result.Url.Should().BeNull();
        result.Msg.Should().NotBeNullOrWhiteSpace();
    }
}
