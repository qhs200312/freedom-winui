using Microsoft.Win32;

namespace ServiceLib.Handler.SysProxy;

[SupportedOSPlatform("windows")]
public static class ProxyGuardManager
{
    private const string GuardRegistryPath = @"Software\v2rayN\ProxyGuard";
    private const string OwnerTokenValue = "OwnerToken";
    private const string OwnerProcessIdValue = "OwnerProcessId";
    private const string OwnerStartTimeValue = "OwnerStartTimeUtcTicks";
    private const string ProxyServerValue = "ProxyServer";
    private const string AutoConfigUrlValue = "AutoConfigUrl";
    private static readonly object SyncRoot = new();
    private static string? _ownerToken;
    public static string? OwnerToken => _ownerToken;

    public static bool IsCurrentOwner
    {
        get
        {
            lock (SyncRoot)
            {
                if (_ownerToken.IsNullOrEmpty())
                {
                    return false;
                }

                using var key = Registry.CurrentUser.OpenSubKey(GuardRegistryPath);
                return string.Equals(key?.GetValue(OwnerTokenValue) as string, _ownerToken, StringComparison.Ordinal);
            }
        }
    }

    public static void RecoverStaleProxy()
    {
        if (!Utils.IsWindows())
        {
            return;
        }

        try
        {
            var shouldClear = false;
            using (var key = Registry.CurrentUser.OpenSubKey(GuardRegistryPath))
            {
                if (key is null || IsRecordedOwnerRunning(key))
                {
                    return;
                }
                shouldClear = IsRecordedProxyActive(key);
            }

            if (shouldClear)
            {
                ProxySettingWindows.UnsetProxy();
            }
            Registry.CurrentUser.DeleteSubKeyTree(GuardRegistryPath, false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Unable to recover a stale managed system proxy.", ex);
        }
    }

    public static void Start()
    {
        if (!Utils.IsWindows())
        {
            return;
        }

        lock (SyncRoot)
        {
            if (!_ownerToken.IsNullOrEmpty())
            {
                return;
            }

            var guardPath = Path.Combine(Utils.StartupPath(), "AmazTool.exe");
            if (!File.Exists(guardPath))
            {
                Logging.SaveLog($"System proxy guard was not found: {guardPath}");
                return;
            }

            var token = Guid.NewGuid().ToString("N");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = guardPath,
                    WorkingDirectory = Utils.StartupPath(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                startInfo.ArgumentList.Add("proxyguard");
                startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                startInfo.ArgumentList.Add(token);
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    Logging.SaveLog("Unable to start the system proxy guard.");
                    return;
                }
                _ownerToken = token;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("Unable to start the system proxy guard.", ex);
            }
        }
    }

    public static void RecordProxy(string? proxyServer, string? autoConfigUrl)
    {
        if (!Utils.IsWindows())
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_ownerToken.IsNullOrEmpty())
            {
                return;
            }

            try
            {
                using var process = Process.GetCurrentProcess();
                using var key = Registry.CurrentUser.CreateSubKey(GuardRegistryPath, true);
                key.SetValue(OwnerTokenValue, _ownerToken, RegistryValueKind.String);
                key.SetValue(OwnerProcessIdValue, Environment.ProcessId, RegistryValueKind.DWord);
                key.SetValue(OwnerStartTimeValue, process.StartTime.ToUniversalTime().Ticks, RegistryValueKind.QWord);
                key.SetValue(ProxyServerValue, proxyServer ?? string.Empty, RegistryValueKind.String);
                key.SetValue(AutoConfigUrlValue, autoConfigUrl ?? string.Empty, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("Unable to record managed system proxy ownership.", ex);
            }
        }
    }

    public static void ReleaseOwnership()
    {
        if (!Utils.IsWindows())
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_ownerToken.IsNullOrEmpty())
            {
                return;
            }

            try
            {
                var isOwner = false;
                using (var key = Registry.CurrentUser.OpenSubKey(GuardRegistryPath))
                {
                    isOwner = string.Equals(key?.GetValue(OwnerTokenValue) as string, _ownerToken, StringComparison.Ordinal);
                }
                if (isOwner)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(GuardRegistryPath, false);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("Unable to release managed system proxy ownership.", ex);
            }
        }
    }

    private static bool IsRecordedOwnerRunning(RegistryKey key)
    {
        var processId = Convert.ToInt32(key.GetValue(OwnerProcessIdValue, 0));
        var startTime = Convert.ToInt64(key.GetValue(OwnerStartTimeValue, 0L));
        if (processId <= 0 || startTime <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTime;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRecordedProxyActive(RegistryKey guardKey)
    {
        var expectedProxy = guardKey.GetValue(ProxyServerValue) as string;
        var expectedPac = guardKey.GetValue(AutoConfigUrlValue) as string;
        using var internetSettings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (internetSettings is null)
        {
            return false;
        }

        var proxyEnabled = Convert.ToInt32(internetSettings.GetValue("ProxyEnable", 0)) != 0;
        var currentProxy = internetSettings.GetValue("ProxyServer") as string;
        var currentPac = internetSettings.GetValue("AutoConfigURL") as string;
        return (!expectedProxy.IsNullOrEmpty()
                && proxyEnabled
                && string.Equals(currentProxy, expectedProxy, StringComparison.OrdinalIgnoreCase))
               || (!expectedPac.IsNullOrEmpty()
                   && string.Equals(currentPac, expectedPac, StringComparison.OrdinalIgnoreCase));
    }
}
