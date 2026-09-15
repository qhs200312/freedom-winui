using Microsoft.Win32;
using System.Xml.Linq;

namespace ServiceLib.Services.Privacy;

public sealed class UdpInterceptionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProcessService? _process;
    private WindowsJobService? _job;
    private FileStream? _directoryLock;
    private CancellationTokenSource? _monitorCancellation;
    private string _status = "关闭";
    public string Status => Volatile.Read(ref _status);

    public async Task StartAsync(Config config, int socksPort, bool supportedConfiguration,
        Func<bool> coreAlive, Func<bool, string, Task> report)
    {
        await _gate.WaitAsync();
        try
        {
            await StopUnderLock();
            _status = "关闭";
            if (!config.GuiItem.EnableUdpInterception) return;
            if (config.TunModeItem.EnableTun)
            {
                _status = "已暂停（TUN 接管）";
                return;
            }
            if (!coreAlive())
            {
                _status = "等待内核";
                return;
            }
            if (!supportedConfiguration)
            {
                throw new InvalidOperationException("原型仅支持默认 Xray / sing-box 配置，不支持自定义完整模板");
            }

            var directory = Utils.GetBinPath(UdpInterceptionConfig.ComponentDirectory);
            var executable = Path.Combine(directory, "ProxiFyre.exe");
            var marker = Path.Combine(directory, UdpInterceptionConfig.OwnershipMarker);
            var available = File.Exists(executable) && File.Exists(marker)
                && (await File.ReadAllTextAsync(marker)).Trim() == UdpInterceptionConfig.ComponentVersion;
            var driverInstalled = false;
            if (Utils.IsWindows())
            {
                using var driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ndisrd");
                driverInstalled = driver is not null && Convert.ToInt32(driver.GetValue("Start", 4)) != 4;
            }
            var inbound = config.Inbound.First();
            var error = UdpInterceptionConfig.GetPrerequisiteError(Utils.IsWindows(), Utils.IsAdministrator(),
                available, driverInstalled, !string.IsNullOrEmpty(inbound.User), inbound.UdpEnabled);
            if (error is not null) throw new InvalidOperationException(error);
            if (!Utils.IsWindows()) return;

            foreach (var existing in Process.GetProcessesByName("ProxiFyre"))
            {
                using (existing)
                {
                    if (!existing.HasExited)
                        throw new InvalidOperationException("已有其他 ProxiFyre 实例运行，请先停止，避免重复接管");
                }
            }

            var applications = UdpInterceptionConfig.ParseApplications(config.GuiItem.UdpInterceptionApplications);
            var json = UdpInterceptionConfig.Build(config.GuiItem.UdpInterceptionApplications, socksPort);
            _directoryLock = new FileStream(Path.Combine(directory, ".freedom-udp.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _status = "检查本地 UDP 入口";
            if (!await ProbeLocalUdpAsync(socksPort))
                throw new InvalidOperationException("本地 SOCKS5 UDP ASSOCIATE 检查失败");
            _status = "检查 STUN 代理链路";
            if (!await ProbeExternalStunAsync(socksPort))
                throw new InvalidOperationException("STUN UDP 代理链路不可用，已保持普通代理模式");

            await WriteAtomically(Path.Combine(directory, "app-config.json"), json);
            await WriteAtomically(Path.Combine(directory, "NLog.config"), CreateLoggingConfiguration());
            var readiness = new UdpInterceptionReadiness(applications, socksPort);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _status = "启动中";
            var process = new ProcessService(executable, "run", directory, true, false, null,
                (_, message) =>
                {
                    // The pinned engine reports readiness only after the driver starts.
                    // Require every application association too: a live process alone
                    // does not mean any traffic is actually intercepted.
                    if (readiness.Observe(message)) ready.TrySetResult();
                    Logging.SaveLog($"UDP interception: {message.Trim()}");
                    return Task.CompletedTask;
                });
            _process = process;
            _job = new WindowsJobService();
            await process.StartAsync();
            if (!_job.AddProcess(process.Handle))
                throw new InvalidOperationException("无法设置 UDP 转发进程的退出清理");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!ready.Task.IsCompleted)
            {
                if (process.HasExited) throw new InvalidOperationException("转发进程启动失败，请查看 bin/proxifyre/logs");
                await Task.Delay(100, timeout.Token);
            }
            if (process.HasExited) throw new InvalidOperationException("转发进程已退出");
            _status = "运行中（实验性）";
            _monitorCancellation = new CancellationTokenSource();
            _ = MonitorAsync(process, coreAlive, report, _monitorCancellation.Token);
            await report(false, "普通模式 UDP 接管已启动；仅作用于已配置应用。此原型不是完整防泄漏边界。");
        }
        catch (Exception ex)
        {
            await StopUnderLock();
            var reason = ex is OperationCanceledException
                ? "等待驱动和应用关联就绪超时，请查看转发日志"
                : ex.Message;
            _status = $"未接管：{reason}";
            Logging.SaveLog("UDP interception startup failed.", ex);
            await report(true, $"{_status}。普通代理仍可使用，但 UDP 可能直连。");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopUnderLock();
            _status = "未启动";
        }
        finally { _gate.Release(); }
    }

    private async Task MonitorAsync(ProcessService process, Func<bool> coreAlive,
        Func<bool, string, Task> report, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, token);
                await _gate.WaitAsync(token);
                try
                {
                    if (!ReferenceEquals(_process, process)) return;
                    if (!process.HasExited && coreAlive()) continue;
                    await StopUnderLock();
                    _status = "未接管：内核或转发进程退出";
                    await report(true, "UDP 接管已停止，目标应用可能恢复直连。");
                    return;
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task StopUnderLock()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        if (_process is not null)
        {
            try { await _process.StopAsync(); }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
        if (Utils.IsWindows()) _job?.Dispose();
        _job = null;
        _directoryLock?.Dispose();
        _directoryLock = null;
    }

    public static async Task<bool> ProbeLocalUdpAsync(int port)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var channel = new UdpTest.Socks5UdpChannel(Global.Loopback, port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (await channel.EstablishUdpAssociationAsync(timeout.Token)) return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
            await Task.Delay(150);
        }
        return false;
    }

    public static async Task<bool> ProbeExternalStunAsync(int port,
        Func<string, int, TimeSpan, Task<TimeSpan>>? probe = null)
    {
        probe ??= (target, socksPort, timeout) =>
            UdpTest.UdpTestService.Create("stun").SendUdpRequestAsync(target, socksPort, timeout);
        foreach (var target in new[] { "stun.l.google.com:19302", "stun.cloudflare.com:3478" })
        {
            try
            {
                var elapsed = await probe(target, port, TimeSpan.FromSeconds(4));
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromSeconds(4)) return true;
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"UDP interception STUN probe failed for {target}: {ex.Message}");
            }
        }
        return false;
    }

    private static async Task WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content);
        File.Move(temporary, path, true);
    }

    private static string CreateLoggingConfiguration()
    {
        XNamespace ns = "http://www.nlog-project.org/schemas/NLog.xsd";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        return new XDocument(new XElement(ns + "nlog", new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            new XElement(ns + "targets",
                new XElement(ns + "target", new XAttribute("name", "console"),
                    new XAttribute(xsi + "type", "Console"), new XAttribute("layout", "${message}")),
                new XElement(ns + "target", new XAttribute("name", "file"),
                    new XAttribute(xsi + "type", "File"),
                    new XAttribute("fileName", "${basedir}/logs/freedom-udp.log"),
                    new XAttribute("archiveAboveSize", "2097152"), new XAttribute("maxArchiveFiles", "2"))),
            new XElement(ns + "rules", new XElement(ns + "logger",
                new XAttribute("name", "*"), new XAttribute("minlevel", "Info"),
                new XAttribute("writeTo", "console,file"))))).ToString();
    }
}
