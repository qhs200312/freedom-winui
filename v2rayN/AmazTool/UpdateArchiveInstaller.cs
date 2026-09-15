using System.IO.Compression;
using System.Text.Json;

namespace AmazTool;

internal static class UpdateArchiveInstaller
{
    private static readonly string[] RequiredFiles =
    [
        "freedom.exe", "freedom.dll", "freedom.pri", "App.xbf", "MainWindow.xbf",
        "freedom.runtimeconfig.json", "AmazTool.exe", "AmazTool.dll", "AmazTool.runtimeconfig.json",
        "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll"
    ];

    public static void Install(string archivePath, string directory, Action? beforeApply = null,
        Action<string>? beforeWrite = null)
    {
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target == Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Cannot update a filesystem root.");
        if (!File.Exists(Path.Combine(target, "freedom.exe")) && !File.Exists(Path.Combine(target, "v2rayN.exe")))
            throw new InvalidDataException("Target is not an existing freedom installation.");
        var temporary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "freedom-update-" + Guid.NewGuid().ToString("N")));
        var stage = Path.Combine(temporary, "stage");
        var backup = Path.Combine(temporary, "backup");
        var journal = new List<(string Relative, bool Existed)>();
        var keepBackup = false;
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > 20_000) throw new InvalidDataException("Too many update entries.");
            var names = archive.Entries.Select(entry => Normalize(entry.FullName)).ToList();
            var appNames = names.Where(name => name.Equals("freedom.exe", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("/freedom.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (appNames.Count != 1) throw new InvalidDataException("Update must contain exactly one freedom.exe.");
            var prefix = appNames[0][..^"freedom.exe".Length];
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long size = 0;
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                var name = names[index];
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mixed update archive roots.");
                var relative = name[prefix.Length..];
                if (relative.Length == 0 || name.EndsWith('/')) continue;
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Update symbolic links are not allowed.");
                size = checked(size + entry.Length);
                if (size > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Update is too large.");
                if (Preserve(relative, target)) continue;
                if (!files.Add(relative)) throw new InvalidDataException("Duplicate update file: " + relative);
                var destination = ChildPath(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
            foreach (var file in RequiredFiles)
            {
                var path = ChildPath(stage, file);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidDataException("Incomplete update: " + file);
            }
            foreach (var app in new[] { "freedom", "AmazTool" })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(ChildPath(stage, app + ".runtimeconfig.json")));
                var runtime = json.RootElement.GetProperty("runtimeOptions");
                if (runtime.TryGetProperty("framework", out _) || runtime.TryGetProperty("frameworks", out _)
                    || !runtime.TryGetProperty("includedFrameworks", out var included)
                    || !included.EnumerateArray().Any(item => item.GetProperty("name").GetString() == "Microsoft.NETCore.App"))
                    throw new InvalidDataException("Update is not self-contained: " + app);
                if (app == "freedom" && !included.EnumerateArray()
                    .Any(item => item.GetProperty("name").GetString() == "Microsoft.WindowsDesktop.App"))
                    throw new InvalidDataException("Update is missing the Desktop runtime declaration.");
            }

            beforeApply?.Invoke();
            try
            {
                // Install the launcher last; preserve rollback copies before overwriting.
                foreach (var relative in files.OrderBy(file => file.Equals("freedom.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    RejectReparsePoints(target, relative);
                    var destination = ChildPath(target, relative);
                    var existed = File.Exists(destination);
                    if (existed)
                    {
                        var saved = ChildPath(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                        File.Copy(destination, saved);
                    }
                    journal.Add((relative, existed));
                    beforeWrite?.Invoke(relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(ChildPath(stage, relative), destination, true);
                }
            }
            catch (Exception applyError)
            {
                var errors = new List<Exception>();
                foreach (var item in journal.AsEnumerable().Reverse())
                {
                    try
                    {
                        RejectReparsePoints(target, item.Relative);
                        var destination = ChildPath(target, item.Relative);
                        if (item.Existed) File.Copy(ChildPath(backup, item.Relative), destination, true);
                        else if (File.Exists(destination)) File.Delete(destination);
                    }
                    catch (Exception rollbackError) { errors.Add(rollbackError); }
                }
                if (errors.Count > 0)
                {
                    keepBackup = true;
                    throw new UpdateRollbackException("Rollback incomplete. Backup: " + backup,
                        new AggregateException(new[] { applyError }.Concat(errors)));
                }
                throw;
            }
        }
        finally
        {
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!keepBackup && temporary.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(temporary, true); } catch { }
            }
        }
    }

    private static string Normalize(string value)
    {
        var name = value.Replace('\\', '/');
        if (name.StartsWith('/') || name.Contains(':')) throw new InvalidDataException("Absolute update path.");
        foreach (var part in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part != part.TrimEnd(' ', '.')
                || part.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidDataException("Unsafe update path.");
        }
        return string.Join("/", name.Split('/', StringSplitOptions.RemoveEmptyEntries))
            + (name.EndsWith('/') ? "/" : string.Empty);
    }

    private static bool Preserve(string relative, string target)
    {
        var parts = relative.Split('/');
        if (new[] { "guiConfigs", "guiLogs", "guiTemps", "guiBackups", "binConfigs", ".git", "prerequisites" }
            .Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        var name = parts[^1];
        if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("guiNConfig.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts[0].Equals("bin", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 2 && parts[1].Equals("proxifyre", StringComparison.OrdinalIgnoreCase))
                return parts[2].Equals("logs", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("app-config.json", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".freedom-udp.lock", StringComparison.OrdinalIgnoreCase);
            return File.Exists(ChildPath(target, relative));
        }
        return false;
    }

    private static string ChildPath(string root, string relative)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escaped its root.");
        return path;
    }

    private static void RejectReparsePoints(string root, string relative)
    {
        var current = root;
        foreach (var part in new[] { string.Empty }.Concat(relative.Split('/')))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update target contains a reparse point.");
        }
    }
}

internal sealed class UpdateRollbackException(string message, Exception inner) : IOException(message, inner);
