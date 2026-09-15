using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AmazTool;

[SupportedOSPlatform("windows")]
internal static class ProxyGuard
{
    private const string GuardRegistryPath = @"Software\v2rayN\ProxyGuard";
    private const string InternetSettingsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void Run(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var processId) || string.IsNullOrWhiteSpace(args[2]))
        {
            return;
        }

        // Run after normal applications during Windows shutdown so the parent can exit first.
        NativeMethods.SetProcessShutdownParameters(0x100, 0);
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit();
        }
        catch
        {
            // The parent exited before the guard opened its process handle.
        }

        ClearProxyIfOwned(args[2]);
    }

    private static void ClearProxyIfOwned(string ownerToken)
    {
        try
        {
            using var guardKey = Registry.CurrentUser.OpenSubKey(GuardRegistryPath);
            if (!string.Equals(guardKey?.GetValue("OwnerToken") as string, ownerToken, StringComparison.Ordinal)
                || !IsRecordedProxyActive(guardKey))
            {
                return;
            }

            using var internetSettings = Registry.CurrentUser.OpenSubKey(InternetSettingsRegistryPath, true);
            if (internetSettings is null)
            {
                return;
            }

            internetSettings.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            internetSettings.SetValue("ProxyServer", string.Empty, RegistryValueKind.String);
            internetSettings.SetValue("ProxyOverride", string.Empty, RegistryValueKind.String);
            internetSettings.SetValue("AutoConfigURL", string.Empty, RegistryValueKind.String);
            NativeMethods.InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        catch
        {
        }
        finally
        {
            DeleteOwnershipIfOwned(ownerToken);
        }
    }

    private static bool IsRecordedProxyActive(RegistryKey guardKey)
    {
        var expectedProxy = guardKey.GetValue("ProxyServer") as string;
        var expectedPac = guardKey.GetValue("AutoConfigUrl") as string;
        using var internetSettings = Registry.CurrentUser.OpenSubKey(InternetSettingsRegistryPath);
        if (internetSettings is null)
        {
            return false;
        }

        var proxyEnabled = Convert.ToInt32(internetSettings.GetValue("ProxyEnable", 0)) != 0;
        var currentProxy = internetSettings.GetValue("ProxyServer") as string;
        var currentPac = internetSettings.GetValue("AutoConfigURL") as string;
        return (!string.IsNullOrWhiteSpace(expectedProxy)
                && proxyEnabled
                && string.Equals(currentProxy, expectedProxy, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrWhiteSpace(expectedPac)
                   && string.Equals(currentPac, expectedPac, StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteOwnershipIfOwned(string ownerToken)
    {
        try
        {
            var isOwner = false;
            using (var guardKey = Registry.CurrentUser.OpenSubKey(GuardRegistryPath))
            {
                isOwner = string.Equals(guardKey?.GetValue("OwnerToken") as string, ownerToken, StringComparison.Ordinal);
            }
            if (isOwner)
            {
                Registry.CurrentUser.DeleteSubKeyTree(GuardRegistryPath, false);
            }
        }
        catch
        {
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessShutdownParameters(uint level, uint flags);

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
    }
}
