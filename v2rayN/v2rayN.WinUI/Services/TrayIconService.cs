using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.Configs;

namespace v2rayN.WinUI.Services;

public sealed class TrayIconService : IDisposable
{
    private const int WmAppTray = 0x8001;
    private const int WmHotkey = 0x0312;
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int GwlWndProc = -4;
    private readonly nint _windowHandle;
    private readonly DispatcherQueue _dispatcher;
    private readonly WndProc _wndProc;
    private readonly nint _oldWndProc;
    private readonly Dictionary<int, EGlobalHotkey> _hotkeys = [];
    private readonly Dictionary<int, string> _routingCommands = [];
    private readonly Dictionary<int, string> _serverCommands = [];
    private List<TrayMenuEntry> _routingItems = [];
    private List<TrayMenuEntry> _serverItems = [];
    private string? _selectedRoutingId;
    private string? _selectedServerId;
    private nint _iconHandle;
    private ESysProxyType _proxyMode;
    private NotifyIconData _iconData;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<int>? MenuCommandRequested;
    public event Action<string>? RoutingRequested;
    public event Action<string>? ServerRequested;
    public event Action<EGlobalHotkey>? HotkeyRequested;
    public event Action<bool>? SessionEndingChanged;

    public TrayIconService(nint windowHandle, DispatcherQueue dispatcher, Config config)
    {
        _windowHandle = windowHandle;
        _dispatcher = dispatcher;
        _wndProc = WindowProc;
        _oldWndProc = SetWindowLongPtr(_windowHandle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));
        AddIcon(config.SystemProxyItem.SysProxyType);
        RegisterGlobalHotkeys(config);
    }

    public void ShowWindow()
    {
        ShowWindowNative(_windowHandle, 9);
        SetForegroundWindow(_windowHandle);
    }

    public void HideWindow()
    {
        ShowWindowNative(_windowHandle, 0);
    }

    private void AddIcon(ESysProxyType mode)
    {
        _proxyMode = mode;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", $"freedom.ico");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "freedom.ico");
        }

        _iconHandle = LoadImage(0, iconPath, 1, 32, 32, 0x10);
        _iconData = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _windowHandle,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4,
            uCallbackMessage = WmAppTray,
            hIcon = _iconHandle,
            szTip = "freedom"
        };
        if (_iconHandle == 0 || !Shell_NotifyIcon(0, ref _iconData))
        {
            Logging.SaveLog($"Unable to create WinUI tray icon. Win32 error: {Marshal.GetLastWin32Error()}");
            return;
        }

        _iconData.uTimeoutOrVersion = 4;
        Shell_NotifyIcon(4, ref _iconData);
        _iconData.uFlags = 0x1 | 0x2 | 0x4;
    }

    public void UpdateIcon(ESysProxyType mode)
    {
        _proxyMode = mode;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", $"freedom.ico");
        if (!File.Exists(iconPath))
        {
            return;
        }

        var previousIcon = _iconHandle;
        _iconHandle = LoadImage(0, iconPath, 1, 32, 32, 0x10);
        if (_iconHandle == 0)
        {
            return;
        }

        _iconData.hIcon = _iconHandle;
        _iconData.uFlags = 0x2;
        Shell_NotifyIcon(1, ref _iconData);
        _iconData.uFlags = 0x1 | 0x2 | 0x4;
        if (previousIcon != 0)
        {
            DestroyIcon(previousIcon);
        }
    }

    public void UpdateToolTip(string text)
    {
        var tooltip = string.IsNullOrWhiteSpace(text) ? "freedom" : text.Trim();
        if (tooltip.Length > 127)
        {
            tooltip = tooltip[..127];
        }

        if (string.Equals(_iconData.szTip, tooltip, StringComparison.Ordinal))
        {
            return;
        }

        _iconData.szTip = tooltip;
        _iconData.uFlags = 0x4;
        Shell_NotifyIcon(1, ref _iconData);
        _iconData.uFlags = 0x1 | 0x2 | 0x4;
    }

    public void UpdateMenuItems(
        IEnumerable<TrayMenuEntry> routingItems,
        string? selectedRoutingId,
        IEnumerable<TrayMenuEntry> serverItems,
        string? selectedServerId)
    {
        _routingItems = routingItems.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToList();
        _serverItems = serverItems.Where(item => !string.IsNullOrWhiteSpace(item.Id)).ToList();
        _selectedRoutingId = selectedRoutingId;
        _selectedServerId = selectedServerId;
    }

    private void RegisterGlobalHotkeys(Config config)
    {
        foreach (var item in config.GlobalHotkeys)
        {
            if (item.KeyCode is not int keyCode || ToVirtualKey(keyCode) is not int virtualKey)
            {
                continue;
            }

            var modifiers = (item.Alt ? 0x1u : 0) | (item.Control ? 0x2u : 0) | (item.Shift ? 0x4u : 0);
            var id = 1000 + (int)item.EGlobalHotkey;
            if (RegisterHotKey(_windowHandle, id, modifiers, (uint)virtualKey))
            {
                _hotkeys[id] = item.EGlobalHotkey;
            }
        }
    }

    public void ReloadGlobalHotkeys(Config config)
    {
        foreach (var id in _hotkeys.Keys)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _hotkeys.Clear();
        RegisterGlobalHotkeys(config);
    }

    private nint WindowProc(nint hWnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmQueryEndSession)
        {
            SessionEndingChanged?.Invoke(true);
        }
        else if (message == WmEndSession)
        {
            SessionEndingChanged?.Invoke(wParam != 0);
        }
        else if (message == WmAppTray)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xffff);
            if (mouseMessage is 0x0203 or 0x0202)
            {
                _dispatcher.TryEnqueue(() => ShowRequested?.Invoke());
                return 0;
            }
            if (mouseMessage == 0x0205)
            {
                ShowTrayMenu();
                return 0;
            }
        }
        else if (message == WmHotkey && _hotkeys.TryGetValue((int)wParam, out var hotkey))
        {
            _dispatcher.TryEnqueue(() => HotkeyRequested?.Invoke(hotkey));
            return 0;
        }
        return CallWindowProc(_oldWndProc, hWnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        const uint separator = 0x800;
        const uint checkedItem = 0x8;
        const uint popup = 0x10;
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 100, "显示主窗口");
        AppendMenu(menu, separator, 0, string.Empty);
        AppendMenu(menu, _proxyMode == ESysProxyType.ForcedClear ? checkedItem : 0, 201, "清除系统代理");
        AppendMenu(menu, _proxyMode == ESysProxyType.ForcedChange ? checkedItem : 0, 202, "自动配置系统代理");
        AppendMenu(menu, _proxyMode == ESysProxyType.Unchanged ? checkedItem : 0, 203, "不改变系统代理");
        AppendMenu(menu, _proxyMode == ESysProxyType.Pac ? checkedItem : 0, 204, "PAC 模式");
        AppendMenu(menu, 0, 205, "切换 TUN 模式");

        _routingCommands.Clear();
        if (_routingItems.Count > 0)
        {
            var routingMenu = CreatePopupMenu();
            for (var index = 0; index < _routingItems.Count; index++)
            {
                var itemCommand = 1000 + index;
                var item = _routingItems[index];
                _routingCommands[itemCommand] = item.Id;
                AppendMenu(routingMenu, item.Id == _selectedRoutingId ? checkedItem : 0, (nuint)itemCommand, item.Text);
            }
            AppendMenu(menu, popup, (nuint)routingMenu, "路由模式");
        }

        _serverCommands.Clear();
        if (_serverItems.Count > 0)
        {
            var serverMenu = CreatePopupMenu();
            for (var index = 0; index < _serverItems.Count; index++)
            {
                var itemCommand = 2000 + index;
                var item = _serverItems[index];
                _serverCommands[itemCommand] = item.Id;
                AppendMenu(serverMenu, item.Id == _selectedServerId ? checkedItem : 0, (nuint)itemCommand, item.Text);
            }
            AppendMenu(menu, popup, (nuint)serverMenu, "服务器");
        }

        AppendMenu(menu, separator, 0, string.Empty);
        AppendMenu(menu, 0, 301, "从剪贴板导入服务器");
        AppendMenu(menu, 0, 302, "更新全部订阅");
        AppendMenu(menu, 0, 303, "通过代理更新全部订阅");
        AppendMenu(menu, 0, 305, "复制代理命令");
        AppendMenu(menu, 0, 304, "重载核心");
        AppendMenu(menu, separator, 0, string.Empty);
        AppendMenu(menu, 0, 900, "退出");
        GetCursorPos(out var point);
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenu(menu, 0x100, point.X, point.Y, 0, _windowHandle, 0);
        DestroyMenu(menu);
        if (command == 100)
        {
            _dispatcher.TryEnqueue(() => ShowRequested?.Invoke());
        }
        else if (command == 900)
        {
            _dispatcher.TryEnqueue(() => ExitRequested?.Invoke());
        }
        else if (_routingCommands.TryGetValue((int)command, out var routingId))
        {
            _dispatcher.TryEnqueue(() => RoutingRequested?.Invoke(routingId));
        }
        else if (_serverCommands.TryGetValue((int)command, out var serverId))
        {
            _dispatcher.TryEnqueue(() => ServerRequested?.Invoke(serverId));
        }
        else if (command != 0)
        {
            _dispatcher.TryEnqueue(() => MenuCommandRequested?.Invoke((int)command));
        }
    }

    public void Dispose()
    {
        foreach (var id in _hotkeys.Keys)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        Shell_NotifyIcon(2, ref _iconData);
        if (_iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
        }

        if (_oldWndProc != 0)
        {
            SetWindowLongPtr(_windowHandle, GwlWndProc, _oldWndProc);
        }

        GC.KeepAlive(_wndProc);
    }

    private static int? ToVirtualKey(int key)
    {
        if (key is >= 34 and <= 43)
        {
            return 0x30 + key - 34;
        }

        if (key is >= 44 and <= 69)
        {
            return 0x41 + key - 44;
        }

        if (key is >= 74 and <= 83)
        {
            return 0x60 + key - 74;
        }

        if (key is >= 90 and <= 113)
        {
            return 0x70 + key - 90;
        }

        return key switch
        {
            2 => 0x08, 3 => 0x09, 6 => 0x0D, 13 => 0x1B, 18 => 0x20,
            19 => 0x21, 20 => 0x22, 21 => 0x23, 22 => 0x24, 23 => 0x25,
            24 => 0x26, 25 => 0x27, 26 => 0x28, 30 => 0x2C, 31 => 0x2D,
            32 => 0x2E, 84 => 0x6A, 85 => 0x6B, 87 => 0x6D, 88 => 0x6E, 89 => 0x6F,
            _ => null
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X; public int Y; }
    private delegate nint WndProc(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int x, int y, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint newProc);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint previous, nint hWnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindowNative(nint hWnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
}

public sealed record TrayMenuEntry(string Id, string Text);
