namespace AmazTool;

internal sealed record UpdateCommandOptions(
    string PackagePath, int? ProcessId, string? TargetDirectory, bool UseLocalAppData,
    bool RestartAfterInstall, bool WaitForNormalExit, int WaitSeconds, string? Sha256, long? ProcessStartTime)
{
    public static UpdateCommandOptions Parse(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "upgrade", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Missing update package.");
        var index = 2;
        int? processId = null;
        string? directory = null;
        if (index < args.Length && !args[index].StartsWith("--"))
        {
            if (!int.TryParse(args[index++], out var id) || id <= 0) throw new ArgumentException("Invalid application process ID.");
            processId = id;
        }
        if (index < args.Length && !args[index].StartsWith("--")) directory = args[index++];
        var localData = false;
        var restart = true;
        var wait = false;
        var seconds = 120;
        string? hash = null;
        long? startTime = null;
        while (index < args.Length)
        {
            switch (args[index++])
            {
                case "--use-local-app-data": localData = true; break;
                case "--no-restart": restart = false; wait = true; break;
                case "--wait-for-normal-exit": wait = true; break;
                case "--sha256":
                    hash = Value();
                    if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid update SHA-256.");
                    break;
                case "--wait-seconds":
                    if (!int.TryParse(Value(), out seconds) || seconds < 1 || seconds > 600)
                        throw new ArgumentException("Update wait must be between 1 and 600 seconds.");
                    break;
                case "--process-start-time":
                    if (!long.TryParse(Value(), out var ticks) || ticks <= 0) throw new ArgumentException("Invalid process start time.");
                    startTime = ticks;
                    break;
                default: throw new ArgumentException("Unknown update option.");
            }
        }
        if (wait && (!processId.HasValue || directory == null || hash == null))
            throw new ArgumentException("Normal-exit updates require a process, target directory and SHA-256.");
        return new(args[1], processId, directory, localData, restart, wait, seconds, hash, startTime);

        string Value() => index < args.Length ? args[index++] : throw new ArgumentException("Missing update option value.");
    }
}
