using System.Diagnostics;

namespace AmazTool;

internal static class UpdateProcessWaiter
{
    public static void Wait(Process process, int timeoutMilliseconds, Func<Process, bool> isOwned, long? startTime = null)
    {
        if (process.HasExited) return;
        if (startTime.HasValue && process.StartTime.ToUniversalTime().Ticks != startTime.Value)
            throw new InvalidOperationException("The application process ID was reused.");
        if (!isOwned(process)) throw new InvalidOperationException("The process does not belong to the installation.");
        if (!process.WaitForExit(timeoutMilliseconds))
            throw new TimeoutException("The application is still running; the pending update was left for a later exit.");
    }
}
