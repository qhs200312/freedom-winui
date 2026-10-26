using AwesomeAssertions;
using Xunit;

namespace ServiceLib.Tests.Services;

[Collection("UDP interception config")]
public class CoreMemoryTests
{
    [Fact]
    public void MainCoreMemory_ShouldExcludeThePreCoreAndClearWhenStopped()
    {
        var manager = CoreManager.Instance;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var mainField = typeof(CoreManager).GetField("_processService", flags)!;
        var preField = typeof(CoreManager).GetField("_processPreService", flags)!;
        var oldMain = mainField.GetValue(manager);
        var oldPre = preField.GetValue(manager);
        var main = TrackCurrentProcess();
        var pre = TrackCurrentProcess();
        try
        {
            mainField.SetValue(manager, main);
            preField.SetValue(manager, pre);
            var workingSet = manager.MainWorkingSet64;
            workingSet.Should().BeGreaterThan(0);
            manager.WorkingSet64.Should().BeGreaterThan(workingSet);
            mainField.SetValue(manager, null);
            manager.MainWorkingSet64.Should().Be(0);
            manager.WorkingSet64.Should().BeGreaterThan(0);
        }
        finally
        {
            mainField.SetValue(manager, oldMain);
            preField.SetValue(manager, oldPre);
            var processField = typeof(ProcessService).GetField("_process", flags)!;
            // ProcessService.Dispose terminates its process; release only our borrowed handles.
            ((Process)processField.GetValue(main)!).Dispose();
            ((Process)processField.GetValue(pre)!).Dispose();
        }
    }

    private static ProcessService TrackCurrentProcess()
    {
        // Borrow a read-only handle; no core or proxy process is started by this test.
        var service = new ProcessService(Environment.ProcessPath!, "", AppContext.BaseDirectory,
            false, false, null, null);
        var processField = typeof(ProcessService).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((Process)processField.GetValue(service)!).Dispose();
        processField.SetValue(service, Process.GetCurrentProcess());
        return service;
    }
}
