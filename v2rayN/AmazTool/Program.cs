namespace AmazTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            // If no arguments are provided, display usage guidelines and exit
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            // Log all arguments for debugging purposes
            foreach (var arg in args)
            {
                Console.WriteLine(arg);
            }

            // Parse command based on first argument
            switch (args[0].ToLowerInvariant())
            {
                case "rebootas":
                    // Handle application restart
                    HandleRebootAsync();
                    break;

                case "upgrade":
                    HandleUpgrade(args);
                    break;

                case "proxyguard":
                    if (OperatingSystem.IsWindows())
                    {
                        ProxyGuard.Run(args);
                    }
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    // Display help information
                    ShowHelp();
                    break;

                default:
                    // Default behavior: handle as upgrade data
                    // Maintain backward compatibility with existing usage pattern
                    var argData = Uri.UnescapeDataString(string.Join(" ", args));
                    HandleUpgrade(argData);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Global exception handling
            Console.WriteLine($"An error occurred: {ex.Message}");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }

    /// <summary>
    /// Display help information and usage guidelines
    /// </summary>
    private static void ShowHelp()
    {
        Console.WriteLine(Resx.Resource.Guidelines);
        Console.WriteLine("Available commands:");
        Console.WriteLine("  rebootas             - Restart the application");
        Console.WriteLine("  help                 - Display this help information");
        Thread.Sleep(5000);
    }

    /// <summary>
    /// Handle application restart
    /// </summary>
    private static void HandleRebootAsync()
    {
        Console.WriteLine("Restarting application...");
        Thread.Sleep(1000);
        Utils.StartApplication();
    }

    /// <summary>
    /// Handle application upgrade with the provided data
    /// </summary>
    /// <param name="upgradeData">Data for the upgrade process</param>
    private static void HandleUpgrade(string[] args)
    {
        if (args.Length < 2)
        {
            ShowHelp();
            return;
        }

        var processId = args.Length > 2 && int.TryParse(args[2], out var parsedProcessId)
            ? parsedProcessId
            : (int?)null;
        Environment.ExitCode = UpgradeApp.Upgrade(args[1], processId,
            args.Length > 3 ? args[3] : null,
            args.Length > 4 && args[4] == "--use-local-app-data") ? 0 : 1;
    }

    private static void HandleUpgrade(string upgradeData)
    {
        Console.WriteLine("Upgrading application...");
        UpgradeApp.Upgrade(upgradeData);
    }
}
