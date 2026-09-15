using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReactiveUI.Builder;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Manager;

namespace v2rayN.WinUI;

public partial class App : Application
{
    public static EventWaitHandle? ProgramStarted { get; private set; }
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            Logging.SaveLog("WinUI UnhandledException", args.Exception);
            args.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (IsRestartLaunch(args)
            && Environment.GetCommandLineArgs().Skip(1)
                .Concat((args.Arguments ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(Global.UseLocalAppDataArgument, StringComparer.Ordinal))
        {
            Environment.SetEnvironmentVariable(Global.LocalAppData, "1");
        }
        var instanceKey = $"v2rayN.WinUI.{Utils.GetMd5(Utils.GetExePath())}";
        ProgramStarted = new EventWaitHandle(false, EventResetMode.AutoReset, instanceKey, out var createdNew);
        if (!createdNew)
        {
            if (IsRestartLaunch(args))
            {
                ProgramStarted.Dispose();
                ProgramStarted = null;
                if (!await WaitForPreviousInstanceAsync(instanceKey))
                {
                    Logging.SaveLog("Timed out waiting for the previous WinUI instance to exit.");
                    Environment.Exit(1);
                    return;
                }
            }
            else
            {
                ProgramStarted.Set();
                Environment.Exit(0);
                return;
            }
        }

        if (!AppManager.Instance.InitApp())
        {
            _window = new Window { Content = new TextBlock { Text = "freedom configuration could not be loaded.", Margin = new Thickness(24) } };
            _window.Activate();
            return;
        }

        ProxyGuardManager.RecoverStaleProxy();
        ClearManagedSystemProxy();

        AppManager.Instance.InitComponents();
        ProxyGuardManager.Start();
        await ConfigHandler.SaveConfig(AppManager.Instance.Config);
        if (Utils.IsWindows())
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                ClearManagedSystemProxy();
            };
        }
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithWinUI()
            .BuildApp();
        _window = new MainWindow();
        _window.Activate();
    }

    internal static void ClearManagedSystemProxy()
    {
        try
        {
            if (Utils.IsWindows()
                && (AppManager.Instance.Config.SystemProxyItem.SysProxyType != ESysProxyType.Unchanged
                    || ProxyGuardManager.IsCurrentOwner))
            {
                ProxySettingWindows.UnsetProxy();
                ProxyGuardManager.ReleaseOwnership();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Unable to clear the managed system proxy.", ex);
        }
    }

    private static bool IsRestartLaunch(LaunchActivatedEventArgs args)
    {
        if (string.Equals(args.Arguments?.Trim(), Global.RebootAs, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument.Trim(), Global.RebootAs, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> WaitForPreviousInstanceAsync(string instanceKey)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(100);
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, instanceKey, out var createdNew);
            if (createdNew)
            {
                ProgramStarted = handle;
                return true;
            }
            handle.Dispose();
        }
        return false;
    }
}
