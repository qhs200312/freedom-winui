using System.Formats.Tar;
using System.IO.Compression;

namespace ServiceLib.Services;

/// <summary>Prepares core files while the core is running; the caller stops it before Apply.</summary>
public static class CoreArchiveInstaller
{
    private const int MaxEntries = 20_000;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static PreparedUpdate Prepare(string archivePath, string targetDirectory,
        IEnumerable<string> expectedNames, Action<string>? beforeWrite = null)
    {
        ArgumentNullException.ThrowIfNull(expectedNames);
        var expected = expectedNames.Select(ValidateExecutableName).Distinct(NameComparer).ToArray();
        if (expected.Length == 0) throw new ArgumentException("Expected executable names are required.", nameof(expectedNames));
        var archive = Path.GetFullPath(archivePath);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("Cannot update a filesystem root.");
        RequireNormalPath(archive, directory: false, allowMissing: false);
        RequireNormalPath(target, directory: true, allowMissing: true);
        RequireNormalPath(parent, directory: true, allowMissing: false);
        var workspace = Path.Combine(parent, ".freedom-core-update-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(workspace, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new HashSet<string>(NameComparer);
            var seenNames = new HashSet<string>(NameComparer);
            long expanded = 0;
            var entries = 0;
            using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    CountEntry();
                    var name = Normalize(entry.FullName);
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                        || unixType is not (0 or 0x8000 or 0x4000))
                        throw new InvalidDataException("Core archive links and special files are not allowed.");
                    if (name.EndsWith('/') || unixType == 0x4000)
                    {
                        if (entry.Length != 0) throw new InvalidDataException("Archive directory contains file data.");
                        continue;
                    }
                    using var content = entry.Open();
                    Extract(name, content, entry.Length);
                }
            }
            else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    CountEntry();
                    var name = Normalize(entry.Name);
                    if (entry.EntryType == TarEntryType.Directory) continue;
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException("Core archive links and special files are not allowed.");
                    if (name.EndsWith('/')) throw new InvalidDataException("Archive file has a directory name.");
                    Extract(name, entry.DataStream ?? Stream.Null, entry.Length);
                }
            }
            else if (archive.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                Extract(expected[0], gzip, null);
            }
            else throw new InvalidDataException("The core package must be zip, gz, or tar.gz.");

            ValidateExecutables(stage, files, expected);
            foreach (var name in files)
            {
                var destination = Path.Combine(target, name);
                RequireNormalPath(destination, directory: false, allowMissing: true);
                if (string.Equals(destination, archive, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("The core package cannot replace its own archive.");
            }
            return new PreparedUpdate(target, workspace, files
                .OrderBy(name => expected.Contains(name, NameComparer))
                .ThenBy(name => name, NameComparer).ToArray(), expected, beforeWrite);

            void CountEntry()
            {
                if (++entries > MaxEntries) throw new InvalidDataException("Too many core archive entries.");
            }

            void Extract(string name, Stream content, long? length)
            {
                var leaf = name.Split('/')[^1];
                if (!seenNames.Add(leaf)) throw new InvalidDataException("Duplicate flattened core file: " + leaf);
                if (length.HasValue && (length.Value < 0 || length.Value > MaxExpandedBytes - expanded))
                    throw new InvalidDataException("The expanded core package is too large.");
                if (IsGeoFile(leaf))
                {
                    // Include skipped tar/zip data in the decompressed size budget.
                    expanded = checked(expanded + (length ?? 0));
                    return;
                }
                var path = Path.Combine(stage, leaf);
                RequireNormalPath(path, directory: false, allowMissing: true);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = content.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (count > MaxExpandedBytes - expanded)
                        throw new InvalidDataException("The expanded core package is too large.");
                    expanded += count;
                    received += count;
                    output.Write(buffer, 0, count);
                }
                if (length.HasValue && received != length.Value)
                    throw new InvalidDataException("The core archive entry is incomplete: " + name);
                files.Add(leaf);
            }
        }
        catch
        {
            Cleanup(workspace);
            throw;
        }
    }

    private static string ValidateExecutableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\')
            || Normalize(name) != name || IsGeoFile(name))
            throw new ArgumentException("Expected executable names must be plain file names.");
        return name;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidDataException("Empty core archive path.");
        var name = value.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':'))
            throw new InvalidDataException("Absolute core archive path.");
        var parts = name.TrimEnd('/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part != part.Trim(' ', '.')
                || part.Any(character => char.IsControl(character) || "*?\"<>|".Contains(character)))
                throw new InvalidDataException("Unsafe core archive path.");
            var device = part.Split('.')[0];
            if (Regex.IsMatch(device, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Reserved core archive path.");
        }
        return name;
    }

    private static bool IsGeoFile(string name)
        => name.Contains("geo", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Country.mmdb", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateExecutables(string stage, IEnumerable<string> files, string[] expected)
    {
        var executables = files.Where(name => expected.Contains(name, NameComparer)).ToArray();
        if (executables.Length == 0) throw new InvalidDataException("The package has no expected core executable.");
        Span<byte> signature = stackalloc byte[4];
        foreach (var name in executables)
        {
            var path = Path.Combine(stage, name);
            RequireNormalPath(path, directory: false, allowMissing: false);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < signature.Length)
                    throw new InvalidDataException("The core executable is empty or truncated: " + name);
                input.ReadExactly(signature);
                var valid = OperatingSystem.IsWindows()
                    ? signature[0] == 'M' && signature[1] == 'Z'
                    : OperatingSystem.IsMacOS() ? IsMachO(signature)
                        : signature.SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 });
                if (!valid) throw new InvalidDataException("The core executable has an invalid binary signature: " + name);
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static bool IsMachO(ReadOnlySpan<byte> signature)
    {
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(signature);
        return magic is 0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE
            or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA;
    }

    internal static void RequireNormalPath(string path, bool directory, bool allowMissing)
    {
        var current = Path.GetFullPath(path);
        var leaf = true;
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) when (!leaf || allowMissing)
            { current = Path.GetDirectoryName(current); leaf = false; continue; }
            catch (DirectoryNotFoundException) when (!leaf || allowMissing)
            { current = Path.GetDirectoryName(current); leaf = false; continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Core update paths cannot contain links or reparse points: " + current);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((!leaf || directory) != isDirectory)
                throw new InvalidDataException("Core update path is not a normal " + (leaf && !directory ? "file: " : "directory: ") + current);
            leaf = false;
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void Cleanup(string workspace)
    {
        try
        {
            if (workspace != Path.GetFullPath(workspace)
                || !Regex.IsMatch(Path.GetFileName(workspace), @"\A\.freedom-core-update-[a-f0-9]{32}\z"))
                throw new InvalidDataException("Invalid core staging cleanup directory.");
            RequireNormalPath(workspace, directory: true, allowMissing: true);
            if (!Directory.Exists(workspace)) return;
            // Inspect the full tree before a recursive delete so substituted links are never followed.
            var pending = new Stack<string>();
            pending.Push(workspace);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Core staging cleanup encountered a link: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception ex) { Logging.SaveLog("Core update staging cleanup", ex); }
    }

    public sealed class PreparedUpdate : IDisposable
{
    private readonly string _target;
    private readonly string _workspace;
    private readonly string[] _files;
    private readonly string[] _expected;
    private readonly Action<string>? _beforeWrite;
    private readonly object _gate = new();
    private bool _attempted;
    private bool _disposed;
    private bool _keepBackup;

    internal PreparedUpdate(string target, string workspace, string[] files, string[] expected, Action<string>? beforeWrite)
    {
        _target = target;
        _workspace = workspace;
        _files = files;
        _expected = expected;
        _beforeWrite = beforeWrite;
    }

    /// <summary>Replaces files after the caller has stopped the core. May be called only once.</summary>
    public void Apply()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attempted) throw new InvalidOperationException("This prepared core update has already been applied or attempted.");
            _attempted = true;
            var stage = Path.Combine(_workspace, "stage");
            var backup = Path.Combine(_workspace, "backup");
            var restore = Path.Combine(_workspace, "restore");
            CoreArchiveInstaller.RequireNormalPath(_workspace, directory: true, allowMissing: false);
            CoreArchiveInstaller.ValidateExecutables(stage, _files, _expected);
            CoreArchiveInstaller.RequireNormalPath(_target, directory: true, allowMissing: true);
            foreach (var name in _files)
            {
                CoreArchiveInstaller.RequireNormalPath(Path.Combine(stage, name), directory: false, allowMissing: false);
                CoreArchiveInstaller.RequireNormalPath(Path.Combine(_target, name), directory: false, allowMissing: true);
            }
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(restore);
            var createdTarget = !Directory.Exists(_target);
            var journal = new List<(string Name, bool Existed)>();
            try
            {
                Directory.CreateDirectory(_target);
                foreach (var name in _files)
                {
                    var destination = Path.Combine(_target, name);
                    CoreArchiveInstaller.RequireNormalPath(destination, directory: false, allowMissing: true);
                    var existed = File.Exists(destination);
                    if (existed)
                    {
                        var saved = Path.Combine(backup, name);
                        CoreArchiveInstaller.RequireNormalPath(saved, directory: false, allowMissing: true);
                        File.Copy(destination, saved);
                    }
                    _beforeWrite?.Invoke(name);
                    CoreArchiveInstaller.RequireNormalPath(destination, directory: false, allowMissing: true);
                    CoreArchiveInstaller.RequireNormalPath(Path.Combine(stage, name), directory: false, allowMissing: false);
                    journal.Add((name, existed));
                    File.Move(Path.Combine(stage, name), destination, overwrite: true);
                }
            }
            catch (Exception applyError)
            {
                var errors = new List<Exception>();
                foreach (var item in journal.AsEnumerable().Reverse())
                {
                    try
                    {
                        var destination = Path.Combine(_target, item.Name);
                        CoreArchiveInstaller.RequireNormalPath(destination, directory: false, allowMissing: true);
                        if (item.Existed)
                        {
                            var saved = Path.Combine(backup, item.Name);
                            var replacement = Path.Combine(restore, item.Name);
                            CoreArchiveInstaller.RequireNormalPath(saved, directory: false, allowMissing: false);
                            CoreArchiveInstaller.RequireNormalPath(replacement, directory: false, allowMissing: true);
                            File.Copy(saved, replacement);
                            File.Move(replacement, destination, overwrite: true);
                        }
                        else File.Delete(destination);
                    }
                    catch (Exception rollbackError) { errors.Add(rollbackError); }
                }
                if (createdTarget)
                {
                    try
                    {
                        CoreArchiveInstaller.RequireNormalPath(_target, directory: true, allowMissing: true);
                        if (Directory.Exists(_target) && !Directory.EnumerateFileSystemEntries(_target).Any())
                            Directory.Delete(_target);
                    }
                    catch (Exception rollbackError) { errors.Add(rollbackError); }
                }
                if (errors.Count > 0)
                {
                    _keepBackup = true;
                    throw new CoreArchiveRollbackException(backup,
                        new AggregateException(new[] { applyError }.Concat(errors)));
                }
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_keepBackup) CoreArchiveInstaller.Cleanup(_workspace);
        }
    }
}
}

public sealed class CoreArchiveRollbackException : IOException
{
    public string BackupDirectory { get; }

    internal CoreArchiveRollbackException(string backupDirectory, Exception inner)
        : base("Core update rollback was incomplete. Recovery files retained at: " + backupDirectory, inner)
        => BackupDirectory = backupDirectory;
}
