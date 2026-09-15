using System.IO.Compression;
using AmazTool;
using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "freedom-update-tests-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "installed");

    public AppUpdateTests()
    {
        Directory.CreateDirectory(Target);
        Write("freedom.exe", "old-launcher");
        Write("freedom.dll", "old-application");
    }

    [Theory]
    [InlineData("")]
    [InlineData("freedom-windows-64/")]
    public void Install_ShouldSupportFlatAndWrappedPackagesAndPreserveData(string prefix)
    {
        Write("guiConfigs/guiNConfig.json", "user-settings");
        Write("guiConfigs/guiNDB.db", "user-nodes");
        Write("guiLogs/run.txt", "user-logs");
        Write("bin/xray/xray.exe", "newer-core");
        Write("bin/proxifyre/app-config.json", "custom-routing");
        var archive = CreatePackage(prefix,
        [
            ("guiConfigs/guiNConfig.json", "bad"),
            ("guiConfigs/guiNDB.db", "bad"),
            ("guiLogs/run.txt", "bad"),
            ("bin/xray/xray.exe", "older-core"),
            ("bin/proxifyre/app-config.json", "bad"),
            ("empty.txt", "")
        ]);
        var writes = new List<string>();
        UpdateArchiveInstaller.Install(archive, Target, beforeWrite: writes.Add);
        File.ReadAllText(Path.Combine(Target, "freedom.exe")).Should().Be("new-freedom.exe");
        File.ReadAllText(Path.Combine(Target, "guiConfigs/guiNConfig.json")).Should().Be("user-settings");
        File.ReadAllText(Path.Combine(Target, "guiConfigs/guiNDB.db")).Should().Be("user-nodes");
        File.ReadAllText(Path.Combine(Target, "guiLogs/run.txt")).Should().Be("user-logs");
        File.ReadAllText(Path.Combine(Target, "bin/xray/xray.exe")).Should().Be("newer-core");
        File.ReadAllText(Path.Combine(Target, "bin/proxifyre/app-config.json")).Should().Be("custom-routing");
        File.Exists(Path.Combine(Target, "empty.txt")).Should().BeTrue();
        writes.Last().Should().Be("freedom.exe");
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("guiConfigs/../../escape.dll")]
    [InlineData("FREEDOM.dll")]
    public void InvalidPackage_ShouldBeRejectedBeforeStoppingApplication(string entry)
    {
        var archive = CreatePackage("", [(entry, "bad")]);
        var stopped = false;
        var apply = () => UpdateArchiveInstaller.Install(archive, Target, () => stopped = true);
        apply.Should().Throw<InvalidDataException>();
        stopped.Should().BeFalse();
        File.ReadAllText(Path.Combine(Target, "freedom.exe")).Should().Be("old-launcher");
    }

    [Fact]
    public void PartialWriteFailure_ShouldRestoreOldFiles()
    {
        var archive = CreatePackage("", [("new-resource.txt", "new")]);
        var apply = () => UpdateArchiveInstaller.Install(archive, Target, beforeWrite: file =>
        {
            if (file == "freedom.exe") throw new IOException("simulated failure");
        });
        apply.Should().Throw<IOException>();
        File.ReadAllText(Path.Combine(Target, "freedom.exe")).Should().Be("old-launcher");
        File.ReadAllText(Path.Combine(Target, "freedom.dll")).Should().Be("old-application");
        File.Exists(Path.Combine(Target, "new-resource.txt")).Should().BeFalse();
    }

    [Fact]
    public void FrameworkDependentPackage_ShouldBeRejected()
    {
        var archive = CreatePackage("", [], frameworkDependent: true);
        var apply = () => UpdateArchiveInstaller.Install(archive, Target);
        apply.Should().Throw<InvalidDataException>();
        File.ReadAllText(Path.Combine(Target, "freedom.dll")).Should().Be("old-application");
    }

    [Fact]
    public void Checksum_ShouldRejectMissingOrModifiedPayload()
    {
        var file = Path.Combine(_root, "download.zip");
        File.WriteAllText(file, "package");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        AppUpdateIntegrity.Verify(file, hash + "  freedom.zip").Should().BeTrue();
        AppUpdateIntegrity.Verify(file, null).Should().BeFalse();
        File.AppendAllText(file, "modified");
        AppUpdateIntegrity.Verify(file, hash).Should().BeFalse();
    }

    [Fact]
    public void HelperPreparation_ShouldRelocateRuntimeAndNotCopyUserConfig()
    {
        Write("AmazTool.exe", "helper");
        Write("AmazTool.dll", "helper-library");
        Write("AmazTool.runtimeconfig.json", "runtime-config");
        Write("coreclr.dll", "runtime");
        Write("guiConfigs/guiNConfig.json", "private");
        var helper = AppUpdatePreparation.PrepareHelper(Path.Combine(Target, "AmazTool.exe"), Path.Combine(_root, "temporary"));
        var directory = Path.GetDirectoryName(helper)!;
        directory.Should().NotBe(Target);
        File.ReadAllText(Path.Combine(directory, "coreclr.dll")).Should().Be("runtime");
        File.Exists(Path.Combine(directory, "AmazTool.runtimeconfig.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(directory, "guiConfigs")).Should().BeFalse();
    }

    private string CreatePackage(string prefix, (string Name, string Content)[] extras, bool frameworkDependent = false)
    {
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        if (prefix.Length > 0) archive.CreateEntry(prefix);
        var names = new[]
        {
            "freedom.exe", "freedom.dll", "freedom.pri", "App.xbf", "MainWindow.xbf", "AmazTool.exe", "AmazTool.dll",
            "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll"
        };
        foreach (var name in names) Add(prefix + name, "new-" + name);
        var runtime = frameworkDependent
            ? """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}"""
            : """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"}]}}""";
        Add(prefix + "freedom.runtimeconfig.json", runtime);
        Add(prefix + "AmazTool.runtimeconfig.json", runtime);
        foreach (var entry in extras) Add(prefix + entry.Name, entry.Content);
        return file;

        void Add(string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }

    private void Write(string relative, string content)
    {
        var file = Path.Combine(Target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    public void Dispose()
    {
        var allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_root, true);
    }
}
