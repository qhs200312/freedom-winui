using System.Diagnostics;

namespace AmazTool;

internal static class UpgradeApp
{
    public static bool Upgrade(string fileName, int? processId = null, string? targetDirectory = null,
        bool useLocalAppData = false)
    {
        var target = Path.GetFullPath(targetDirectory ?? Utils.StartupPath());
        try
        {
            UpdateArchiveInstaller.Install(fileName, target, () =>
            {
                if (processId.HasValue) StopOwnedProcess(processId.Value, target);
                foreach (var name in Utils.CompatibleAppNames)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        using (process)
                        {
                            if (IsOwnedProcess(process, target)) StopOwnedProcess(process.Id, target);
                        }
                    }
                }
            });
            WriteResult(target, "Update installed successfully.");
            Utils.StartApplication(target, useLocalAppData);
            return true;
        }
        catch (Exception ex)
        {
            WriteResult(target, "Update failed: " + ex);
            Console.Error.WriteLine(ex.Message);
            if (ex is not UpdateRollbackException)
            {
                try { Utils.StartApplication(target, useLocalAppData); }
                catch (Exception restartError) { WriteResult(target, "Restart failed: " + restartError.Message); }
            }
            return false;
        }
    }

    private static bool IsOwnedProcess(Process process, string target) =>
        Utils.CompatibleAppNames.Any(name => string.Equals(
            process.MainModule?.FileName,
            Path.Combine(target, OperatingSystem.IsWindows() ? name + ".exe" : name),
            StringComparison.OrdinalIgnoreCase));

    private static void StopOwnedProcess(int id, string target)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (process.HasExited) return;
            if (!IsOwnedProcess(process, target))
                throw new InvalidOperationException("Refusing to terminate a process outside the installation.");
            if (!process.WaitForExit(15_000))
            {
                process.Kill(true);
                if (!process.WaitForExit(5_000)) throw new IOException("The old application did not exit.");
            }
        }
        catch (ArgumentException) { }
    }

    private static void WriteResult(string target, string message)
    {
        try
        {
            if (!File.Exists(Path.Combine(target, "freedom.exe")) && !File.Exists(Path.Combine(target, "v2rayN.exe")))
                return;
            var logs = Path.Combine(target, "guiLogs");
            Directory.CreateDirectory(logs);
            File.AppendAllText(Path.Combine(logs, "freedom-update.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
