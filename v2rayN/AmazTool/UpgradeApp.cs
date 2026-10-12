using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AmazTool;

internal static class UpgradeApp
{
    public static bool Upgrade(string fileName, int? processId = null, string? targetDirectory = null,
        bool useLocalAppData = false, bool restartAfterInstall = true, bool waitForNormalExit = false,
        int waitSeconds = 120, string? expectedSha256 = null, long? processStartTime = null,
        Action<string, bool>? restartApplication = null)
    {
        var target = Path.GetFullPath(targetDirectory ?? Utils.StartupPath());
        restartApplication ??= (directory, localData) => Utils.StartApplication(directory, localData);
        waitForNormalExit |= !restartAfterInstall;
        var exited = false;
        try
        {
            if (waitForNormalExit && (!processId.HasValue || expectedSha256 == null || waitSeconds < 1 || waitSeconds > 600))
                throw new ArgumentException("Normal-exit updates require an owned process, SHA-256 and a bounded wait.");
            UpdateArchiveInstaller.Install(fileName, target, () =>
            {
                var timer = Stopwatch.StartNew();
                if (processId.HasValue)
                {
                    if (waitForNormalExit) WaitOwnedProcess(processId.Value, target, waitSeconds * 1000, processStartTime);
                    else StopOwnedProcess(processId.Value, target);
                }
                foreach (var name in Utils.CompatibleAppNames)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        using (process)
                        {
                            if (!IsOwnedProcess(process, target)) continue;
                            if (waitForNormalExit)
                            {
                                var remaining = Math.Max(0, waitSeconds * 1000 - (int)timer.ElapsedMilliseconds);
                                UpdateProcessWaiter.Wait(process, remaining, p => IsOwnedProcess(p, target));
                            }
                            else StopOwnedProcess(process.Id, target);
                        }
                    }
                }
                exited = true;
            }, expectedSha256: expectedSha256);
            WriteResult(target, "Update installed successfully.");
            if (restartAfterInstall) restartApplication(target, useLocalAppData);
            return true;
        }
        catch (Exception ex)
        {
            WriteResult(target, "Update failed: " + ex);
            Console.Error.WriteLine(ex.Message);
            if (restartAfterInstall && (!waitForNormalExit || exited) && ex is not UpdateRollbackException)
            {
                try { restartApplication(target, useLocalAppData); }
                catch (Exception restartError) { WriteResult(target, "Restart failed: " + restartError.Message); }
            }
            return false;
        }
    }

    private static void WaitOwnedProcess(int id, string target, int timeout, long? startTime)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            UpdateProcessWaiter.Wait(process, timeout, p => IsOwnedProcess(p, target), startTime);
        }
        catch (ArgumentException) { } // The owned application has already exited.
    }

    private static bool IsOwnedProcess(Process process, string target)
    {
        var executable = GetProcessExecutable(process);
        return Utils.CompatibleAppNames.Any(name => string.Equals(
            executable,
            Path.Combine(target, OperatingSystem.IsWindows() ? name + ".exe" : name),
            StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetProcessExecutable(Process process)
    {
        if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName;
        // Module enumeration requires broader access than querying the executable path.
        using var handle = NativeMethods.OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 87 or 1168) return null; // The process exited during enumeration.
            throw new Win32Exception(error);
        }
        var buffer = new StringBuilder(32768);
        var size = buffer.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
            CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
            StringBuilder executable, ref int size);
    }

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
