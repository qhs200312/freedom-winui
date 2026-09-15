using System.Diagnostics;

namespace AmazTool;

internal class Utils
{
    public static string GetExePath()
    {
        return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    public static string StartupPath()
    {
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public static string GetPath(string fileName)
    {
        var startupPath = StartupPath();
        if (string.IsNullOrEmpty(fileName))
        {
            return startupPath;
        }
        return Path.Combine(startupPath, fileName);
    }

    public const string AppName = "freedom";
    public static readonly string[] CompatibleAppNames = [AppName, "v2rayN"];

    public static void StartApplication(string? directory = null, bool useLocalAppData = false)
    {
        directory ??= StartupPath();
        var executable = CompatibleAppNames.Select(name => Path.Combine(directory,
            OperatingSystem.IsWindows() ? name + ".exe" : name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("freedom executable was not found.");
        Process process = new()
        {
            StartInfo = new()
            {
                UseShellExecute = true,
                FileName = executable,
                WorkingDirectory = directory,
                Arguments = useLocalAppData ? "rebootas --use-local-app-data" : string.Empty
            }
        };
        process.Start();
    }

    public static void Waiting(int second)
    {
        for (var i = second; i > 0; i--)
        {
            Console.WriteLine(i);
            Thread.Sleep(1000);
        }
    }
}
