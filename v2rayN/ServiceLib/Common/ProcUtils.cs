namespace ServiceLib.Common;

public static class ProcUtils
{
    private static readonly string _tag = "ProcUtils";

    public static void ProcessStart(string? fileName, string arguments = "")
    {
        _ = ProcessStart(fileName, arguments, null);
    }

    public static int? ProcessStart(string? fileName, string arguments, string? dir)
    {
        if (fileName.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            if (fileName.Contains(' '))
            {
                fileName = fileName.AppendQuotes();
            }
            if (arguments.Contains(' '))
            {
                arguments = arguments.AppendQuotes();
            }

            Process proc = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = dir ?? string.Empty
                }
            };
            _ = proc.Start();
            return dir is null ? null : proc.Id;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        return null;
    }

    public static int? ProcessStart(string? fileName, IEnumerable<string> arguments, string? dir)
    {
        if (fileName.IsNullOrEmpty())
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = fileName,
                WorkingDirectory = dir ?? string.Empty,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo);
            return process?.Id;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    public static string GetRestartArguments(bool useLocalAppData) => useLocalAppData
        ? $"{Global.RebootAs} {Global.UseLocalAppDataArgument}"
        : Global.RebootAs;

    public static bool RebootAsAdmin(bool blAdmin = true)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                UseShellExecute = true,
                Arguments = GetRestartArguments(Environment.GetEnvironmentVariable(Global.LocalAppData) == "1"),
                WorkingDirectory = Utils.StartupPath(),
                FileName = Utils.GetExePath(),
                Verb = blAdmin ? "runas" : null,
            };
            return Process.Start(startInfo) != null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }
}
