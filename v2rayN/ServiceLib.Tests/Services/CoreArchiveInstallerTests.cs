using System.Formats.Tar;
using System.IO.Compression;
using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class CoreArchiveInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "freedom-core-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Path, bool Directory)> _links = [];
    private string Target => Path.Combine(_root, "installed");
    private static string Executable => Utils.GetExeName("xray");
    private static byte[] UpdatedBinary => NativeBinary("new core payload");

    public CoreArchiveInstallerTests()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, Executable), "old running core");
        File.WriteAllText(Path.Combine(Target, "existing.txt"), "old resource");
        File.WriteAllText(Path.Combine(Target, "unrelated.json"), "user settings");
        File.WriteAllText(Path.Combine(Target, "geoip.dat"), "user geoip");
        File.WriteAllText(Path.Combine(Target, "geosite.dat"), "user geosite");
        File.WriteAllText(Path.Combine(Target, "Country.mmdb"), "user database");
    }

    [Theory]
    [InlineData("zip", "")]
    [InlineData("zip", "release/")]
    [InlineData("zip", "release/nested/")]
    [InlineData("tar.gz", "")]
    [InlineData("tar.gz", "sing-box-1.12.0-linux-amd64/")]
    [InlineData("tar.gz", "release/nested/")]
    public void PrepareAndApply_ShouldFlattenPackagesPreserveGeoAndLeaveArchiveUntouched(string format, string prefix)
    {
        var files = new (string, byte[])[]
        {
            (prefix + Executable, UpdatedBinary),
            (prefix + "existing.txt", Encoding.UTF8.GetBytes("new resource")),
            (prefix + "new.txt", Encoding.UTF8.GetBytes("added resource")),
            (prefix + "geoip.dat", Encoding.UTF8.GetBytes("bundled geoip")),
            (prefix + "geosite.dat", Encoding.UTF8.GetBytes("bundled geosite")),
            (prefix + "Country.mmdb", Encoding.UTF8.GetBytes("bundled database"))
        };
        var archive = format == "zip" ? Zip(files) : Tar(files);
        var originalArchive = File.ReadAllBytes(archive);
        var writes = new List<string>();
        using (var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable], writes.Add))
        {
            File.ReadAllText(Path.Combine(Target, Executable)).Should().Be("old running core");
            File.ReadAllText(Path.Combine(Target, "existing.txt")).Should().Be("old resource");
            File.Exists(Path.Combine(Target, "new.txt")).Should().BeFalse();
            writes.Should().BeEmpty();
            prepared.Apply();
            File.ReadAllBytes(Path.Combine(Target, Executable)).Should().Equal(UpdatedBinary);
            File.ReadAllText(Path.Combine(Target, "existing.txt")).Should().Be("new resource");
            File.ReadAllText(Path.Combine(Target, "new.txt")).Should().Be("added resource");
            writes.Last().Should().Be(Executable);
            Directory.GetDirectories(Target).Should().BeEmpty();
            AssertPreservedData();
        }
        File.ReadAllBytes(archive).Should().Equal(originalArchive);
        StagingDirectories().Should().BeEmpty();
    }

    [Fact]
    public void SingleGzip_ShouldUseFirstExpectedNameEvenWhenArchiveNameDiffers()
    {
        var first = Utils.GetExeName("mihomo-windows-amd64-v1");
        var archive = Gzip(UpdatedBinary);
        var original = File.ReadAllBytes(archive);
        using var prepared = CoreArchiveInstaller.Prepare(archive, Target, [first, Utils.GetExeName("mihomo")]);
        prepared.Apply();
        File.ReadAllBytes(Path.Combine(Target, first)).Should().Equal(UpdatedBinary);
        File.Exists(Path.Combine(Target, Utils.GetExeName("mihomo"))).Should().BeFalse();
        File.ReadAllBytes(archive).Should().Equal(original);
        AssertPreservedData();
    }

    [Fact]
    public void Archive_ShouldAllowAlternativeExpectedExecutableAndEmptyDirectoryEntries()
    {
        var alternative = Utils.GetExeName("sing-box");
        var archive = Zip([("release/", []), ("release/empty/", []), ("release/" + alternative, UpdatedBinary)]);
        using var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Utils.GetExeName("sing-box-client"), alternative]);
        prepared.Apply();
        File.ReadAllBytes(Path.Combine(Target, alternative)).Should().Equal(UpdatedBinary);
        Directory.GetDirectories(Target).Should().BeEmpty();
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar.gz")]
    [InlineData("gz")]
    public void DisposeWithoutApply_ShouldLeaveInstalledFilesAndRemoveStaging(string format)
    {
        var archive = format switch
        {
            "zip" => Zip([(Executable, UpdatedBinary)]),
            "tar.gz" => Tar([(Executable, UpdatedBinary)]),
            _ => Gzip(UpdatedBinary)
        };
        var original = File.ReadAllBytes(archive);
        using (CoreArchiveInstaller.Prepare(archive, Target, [Executable]))
            StagingDirectories().Should().ContainSingle();
        AssertOldCore();
        File.ReadAllBytes(archive).Should().Equal(original);
        StagingDirectories().Should().BeEmpty();
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("text")]
    [InlineData("wrong-platform")]
    [InlineData("missing")]
    public void MissingEmptyTextOrWrongPlatformExecutable_ShouldFailBeforeTouchingTarget(string problem)
    {
        var wrongSignature = OperatingSystem.IsWindows()
            ? new byte[] { 0x7f, 0x45, 0x4c, 0x46, 0x01 }
            : new byte[] { 0x4d, 0x5a, 0, 0, 0 };
        byte[] content = problem switch
        {
            "empty" => [],
            "wrong-platform" => wrongSignature,
            _ => Encoding.UTF8.GetBytes("not a native executable")
        };
        var archive = Zip([(problem == "missing" ? "README.txt" : Executable, content)]);
        AssertRejected(archive);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("root/../../outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("root\\..\\outside.txt")]
    [InlineData("root/file.txt:stream")]
    [InlineData("root/file.txt.")]
    [InlineData("root/CON.txt")]
    public void UnsafeZipPath_ShouldRejectEntirePackage(string name)
    {
        AssertRejected(Zip([(Executable, UpdatedBinary), (name, Encoding.UTF8.GetBytes("unsafe"))]));
        File.Exists(Path.Combine(_root, "outside.txt")).Should().BeFalse();
    }

    [Fact]
    public void UnsafeTarPath_ShouldRejectEntirePackage()
        => AssertRejected(Tar([(Executable, UpdatedBinary), ("../outside.txt", Encoding.UTF8.GetBytes("unsafe"))]));

    [Theory]
    [InlineData("zip", "xray")]
    [InlineData("zip", "XRAY")]
    [InlineData("tar.gz", "xray")]
    [InlineData("tar.gz", "XRAY")]
    public void DuplicateFlattenedNames_ShouldRejectRatherThanOverwrite(string format, string duplicate)
    {
        var files = new[] { ("one/" + Executable, UpdatedBinary), ("two/" + Utils.GetExeName(duplicate), UpdatedBinary) };
        AssertRejected(format == "zip" ? Zip(files) : Tar(files));
    }

    [Fact]
    public void ZipSymbolicLinkIncludingSkippedGeoFile_ShouldBeRejected()
    {
        var archive = Zip([(Executable, UpdatedBinary), ("geoip.dat", Encoding.UTF8.GetBytes("../outside"))]);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            zip.GetEntry("geoip.dat")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        AssertRejected(archive);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public void TarLinksIncludingSkippedGeoFile_ShouldBeRejected(TarEntryType type)
        => AssertRejected(Tar([(Executable, UpdatedBinary)], type));

    [Fact]
    public void InvalidZip_ShouldLeaveInstalledCoreAndArchiveUntouched()
    {
        var archive = Path.Combine(_root, "bad.zip");
        File.WriteAllText(archive, "not a zip archive");
        AssertRejected(archive);
    }

    [Fact]
    public void TooManyArchiveEntries_ShouldBeRejectedBeforeApply()
    {
        var archive = Path.Combine(_root, "too-many.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 20_001; index++) zip.CreateEntry("empty-" + index + "/");
        }
        AssertRejected(archive);
    }

    [Fact]
    public void ApplyFailure_ShouldRestoreOverwrittenFilesRemoveNewFilesAndKeepPackage()
    {
        var archive = Zip([(Executable, UpdatedBinary), ("existing.txt", Encoding.UTF8.GetBytes("new")),
            ("added.txt", Encoding.UTF8.GetBytes("added"))]);
        var original = File.ReadAllBytes(archive);
        using (var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable], name =>
        {
            if (name == Executable) throw new IOException("Injected write failure");
        }))
        {
            Action apply = prepared.Apply;
            apply.Should().Throw<IOException>().WithMessage("Injected write failure");
            AssertOldCore();
            File.Exists(Path.Combine(Target, "added.txt")).Should().BeFalse();
            AssertPreservedData();
        }
        File.ReadAllBytes(archive).Should().Equal(original);
        StagingDirectories().Should().BeEmpty();
    }

    [Fact]
    public void FailedRollback_ShouldRetainBackupAfterDisposeAndExposeRecoveryPath()
    {
        File.WriteAllText(Path.Combine(Target, "a.txt"), "original a");
        File.WriteAllText(Path.Combine(Target, "b.txt"), "original b");
        var archive = Zip([("a.txt", Encoding.UTF8.GetBytes("replacement a")),
            ("b.txt", Encoding.UTF8.GetBytes("replacement b")), (Executable, UpdatedBinary)]);
        var original = File.ReadAllBytes(archive);
        CoreArchiveRollbackException failure;
        using (var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable], name =>
        {
            if (name != "b.txt") return;
            // A directory introduced after replacement prevents restoration of the previous file.
            File.Delete(Path.Combine(Target, "a.txt"));
            Directory.CreateDirectory(Path.Combine(Target, "a.txt"));
            throw new IOException("Injected failure before the second write");
        }))
        {
            Action apply = prepared.Apply;
            failure = apply.Should().Throw<CoreArchiveRollbackException>().Which;
            failure.InnerException.Should().BeOfType<AggregateException>();
        }
        Directory.Exists(failure.BackupDirectory).Should().BeTrue();
        File.ReadAllText(Path.Combine(failure.BackupDirectory, "a.txt")).Should().Be("original a");
        File.ReadAllText(Path.Combine(failure.BackupDirectory, "b.txt")).Should().Be("original b");
        failure.Message.Should().Contain(failure.BackupDirectory);
        File.ReadAllBytes(archive).Should().Equal(original);
        File.ReadAllText(Path.Combine(Target, Executable)).Should().Be("old running core");
    }

    [Fact]
    public void NewTarget_ShouldBeCreatedOnlyByApplyAndRemovedOnRollback()
    {
        var target = Path.Combine(_root, "new-core");
        var archive = Zip([("a.txt", Encoding.UTF8.GetBytes("new")), (Executable, UpdatedBinary)]);
        using var prepared = CoreArchiveInstaller.Prepare(archive, target, [Executable], name =>
        {
            if (name == Executable) throw new IOException("Injected failure");
        });
        Directory.Exists(target).Should().BeFalse();
        Action apply = prepared.Apply;
        apply.Should().Throw<IOException>();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void ExistingTargetDirectoryAtFileDestination_ShouldFailPreparation()
    {
        Directory.CreateDirectory(Path.Combine(Target, "resource.txt"));
        var archive = Zip([(Executable, UpdatedBinary), ("resource.txt", Encoding.UTF8.GetBytes("new"))]);
        AssertRejected(archive);
        Directory.Exists(Path.Combine(Target, "resource.txt")).Should().BeTrue();
    }

    [Fact]
    public void ArchiveInsideTarget_ShouldNeverBeOverwrittenByPackageContents()
    {
        var archive = Zip([(Executable, UpdatedBinary), ("package.zip", Encoding.UTF8.GetBytes("replacement"))]);
        var destination = Path.Combine(Target, "package.zip");
        File.Move(archive, destination);
        AssertRejected(destination);
    }

    [Fact]
    public void TargetSymlinkIntroducedAfterPrepare_ShouldBeRejectedBeforeAnyWrites()
    {
        var archive = Zip([(Executable, UpdatedBinary), ("existing.txt", Encoding.UTF8.GetBytes("new"))]);
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "external data");
        using var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable]);
        File.Delete(Path.Combine(Target, "existing.txt"));
        Link(Path.Combine(Target, "existing.txt"), outside, directory: false);
        Action apply = prepared.Apply;
        apply.Should().Throw<InvalidDataException>();
        File.ReadAllText(outside).Should().Be("external data");
        File.ReadAllText(Path.Combine(Target, Executable)).Should().Be("old running core");
    }

    [Fact]
    public void LinkedTargetAncestor_ShouldBeRejected()
    {
        var real = Path.Combine(_root, "normal-parent");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "linked-parent");
        Link(link, real, directory: true);
        var archive = Zip([(Executable, UpdatedBinary)]);
        Action prepare = () =>
        {
            using var prepared = CoreArchiveInstaller.Prepare(archive, Path.Combine(link, "core"), [Executable]);
        };
        prepare.Should().Throw<InvalidDataException>();
        Directory.GetFileSystemEntries(real).Should().BeEmpty();
    }

    [Fact]
    public void LinkedArchive_ShouldBeRejected()
    {
        var archive = Zip([(Executable, UpdatedBinary)]);
        var link = Path.Combine(_root, "linked.zip");
        Link(link, archive, directory: false);
        AssertRejected(link);
    }

    [Fact]
    public void DanglingDestinationLink_ShouldBeRejectedRatherThanFollowed()
    {
        var archive = Zip([(Executable, UpdatedBinary), ("resource.txt", Encoding.UTF8.GetBytes("new"))]);
        Link(Path.Combine(Target, "resource.txt"), Path.Combine(_root, "absent.txt"), directory: false);
        AssertRejected(archive);
        File.Exists(Path.Combine(_root, "absent.txt")).Should().BeFalse();
    }

    [Fact]
    public void Apply_ShouldRejectRepeatedAttemptsAndDisposedInstances()
    {
        var archive = Zip([(Executable, UpdatedBinary)]);
        var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable]);
        prepared.Apply();
        Action apply = prepared.Apply;
        apply.Should().Throw<InvalidOperationException>();
        prepared.Dispose();
        prepared.Dispose();
        apply.Should().Throw<ObjectDisposedException>();
        StagingDirectories().Should().BeEmpty();
    }

    [Fact]
    public void NativeExecutable_ShouldHaveExecutePermissionsOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file permissions require a Unix host.");
            return;
        }
        var archive = Zip([(Executable, UpdatedBinary)]);
        using var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable]);
        prepared.Apply();
        var permissions = File.GetUnixFileMode(Path.Combine(Target, Executable));
        (permissions & UnixFileMode.UserExecute).Should().Be(UnixFileMode.UserExecute);
    }

    private void AssertRejected(string archive)
    {
        var original = File.ReadAllBytes(archive);
        Action prepare = () => { using var prepared = CoreArchiveInstaller.Prepare(archive, Target, [Executable]); };
        prepare.Should().Throw<InvalidDataException>();
        AssertOldCore();
        AssertPreservedData();
        File.ReadAllBytes(archive).Should().Equal(original);
        StagingDirectories().Should().BeEmpty();
    }

    private void AssertOldCore()
    {
        File.ReadAllText(Path.Combine(Target, Executable)).Should().Be("old running core");
        File.ReadAllText(Path.Combine(Target, "existing.txt")).Should().Be("old resource");
    }

    private void AssertPreservedData()
    {
        File.ReadAllText(Path.Combine(Target, "unrelated.json")).Should().Be("user settings");
        File.ReadAllText(Path.Combine(Target, "geoip.dat")).Should().Be("user geoip");
        File.ReadAllText(Path.Combine(Target, "geosite.dat")).Should().Be("user geosite");
        File.ReadAllText(Path.Combine(Target, "Country.mmdb")).Should().Be("user database");
    }

    private string[] StagingDirectories() => Directory.GetDirectories(_root, ".freedom-core-update-*");

    private string Zip(IEnumerable<(string Name, byte[] Data)> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var content = zip.CreateEntry(file.Name).Open();
            content.Write(file.Data);
        }
        return path;
    }

    private string Tar(IEnumerable<(string Name, byte[] Data)> files, TarEntryType? linkType = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar.gz");
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionMode.Compress);
        using var writer = new TarWriter(gzip, leaveOpen: true);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "empty/"));
        foreach (var file in files)
        {
            using var content = new MemoryStream(file.Data);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Name) { DataStream = content });
        }
        if (linkType.HasValue)
            writer.WriteEntry(new PaxTarEntry(linkType.Value, "geoip.dat") { LinkName = "../outside" });
        return path;
    }

    private string Gzip(byte[] data)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".gz");
        using var output = File.Create(path);
        using var gzip = new GZipStream(output, CompressionMode.Compress);
        gzip.Write(data);
        return path;
    }

    private static byte[] NativeBinary(string marker)
    {
        byte[] signature = OperatingSystem.IsWindows() ? [0x4d, 0x5a, 0, 0]
            : OperatingSystem.IsMacOS() ? [0xcf, 0xfa, 0xed, 0xfe] : [0x7f, 0x45, 0x4c, 0x46];
        return signature.Concat(Encoding.UTF8.GetBytes(marker)).ToArray();
    }

    private void Link(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            _links.Add((path, directory));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Creating symlinks is unavailable on this host: " + ex.Message);
        }
    }

    public void Dispose()
    {
        foreach (var link in _links)
        {
            if (link.Directory) Directory.Delete(link.Path);
            else File.Delete(link.Path);
        }
        var root = Path.GetFullPath(_root);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (root.StartsWith(parent + "freedom-core-tests-", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(root, recursive: true);
    }
}
