using System.ComponentModel;
using System.Runtime.InteropServices;
using Photino.NET;
using StateMachine.Localization;
using static StateMachine.DesktopNative;

namespace StateMachine;

/// <summary>One read-only webview per monitor, all observing the editor's existing runtime.
/// Wallpaper windows have their own viewport/DPI; the editor never becomes a shell child.</summary>
public sealed class DesktopWallpaperService(UiText text, ILogger<DesktopWallpaperService> logger) : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const int HotkeyId = 0x414c;
    private PhotinoWindow? _window;
    private nint _helper, _host;
    private string _url = "";
    private readonly string _viewToken = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, PhotinoWindow> _surfaces = new(StringComparer.Ordinal);
    private DesktopDisplay[] _displays = [];
    private DesktopPresentation? _presentation;
    private readonly string _className = "Alcyone.Desktop." + Guid.NewGuid().ToString("N");
    private WindowProc? _procedure;
    private uint _taskbarCreated;
    private NotifyIconData _tray;
    private bool _trayAdded, _hotkey, _disposed;

    public bool Available => OperatingSystem.IsWindows() && _window is not null && !_disposed;
    public bool IsDesktop { get; private set; }
    public IReadOnlyList<DesktopDisplay> Displays => Volatile.Read(ref _displays);
    public DesktopPresentation? Presentation => Volatile.Read(ref _presentation);
    public bool HotkeyAvailable => _hotkey;
    public string? Error { get; private set; }
    public event Action? Changed;

    public void Attach(PhotinoWindow window, string url)
    {
        _window = window;
        _url = url;
        Volatile.Write(ref _displays, DesktopDisplays.Enumerate());
    }

    public DesktopPresentation? GetPresentation(string token) => token == _viewToken ? Presentation : null;

    public Task SetDesktopAsync(bool enabled, DesktopPresentation? presentation = null)
    {
        if (!Available) throw new InvalidOperationException(text["请在 Windows 桌面程序中使用此功能。"]);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _window!.Invoke(() =>
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(DesktopWallpaperService));
                Error = null;
                if (enabled)
                {
                    if (presentation is null) throw new ArgumentNullException(nameof(presentation));
                    var previous = Presentation;
                    Volatile.Write(ref _presentation, presentation);
                    try { EnterDesktop(); }
                    catch { Volatile.Write(ref _presentation, previous); throw; }
                }
                else { StopDesktop(); RestoreEditor(); }
                Changed?.Invoke();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                logger.LogError(ex, "Desktop mode change failed");
                Changed?.Invoke();
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private void EnsureControls()
    {
        if (_helper == 0)
        {
            // An independent top-level window keeps tray/hotkey messages reachable while
            // the webview is a child of Explorer, including TaskbarCreated broadcasts.
            _procedure = HandleMessage;
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
                Instance = GetModuleHandleW(null), ClassName = _className
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception();
            _helper = CreateWindowExW(0x80, _className, "Alcyone desktop controls", 0,
                0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
            if (_helper == 0) throw new Win32Exception();
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _hotkey = RegisterHotKey(_helper, HotkeyId, 0x4003, 0x79); // Ctrl+Alt+F10, no repeat.
        }
        if (_trayAdded) return;
        _tray = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _helper, Id = 1,
            Flags = 1 | 2 | 4, CallbackMessage = TrayMessage,
            Icon = LoadIconW(0, (nint)32512),
            Tip = text["Alcyone · 双击返回设置"], Info = "", InfoTitle = ""
        };
        _trayAdded = Shell_NotifyIconW(0, ref _tray);
        if (!_trayAdded) throw new InvalidOperationException(text["无法创建系统托盘入口，已保留设置界面。"]);
        _tray.Version = 4;
        Shell_NotifyIconW(4, ref _tray);
    }

    private void EnterDesktop()
    {
        EnsureControls();
        var host = FindWallpaperHost();
        if (host == 0) throw new InvalidOperationException(text["无法找到 Windows 桌面层，请重启资源管理器后重试。"]);
        _host = host;
        ReconcileDisplays(force: true);
        if (_surfaces.Count == 0) throw new InvalidOperationException(text["没有检测到可用显示器。"]);
        IsDesktop = true;
        // Watch DPI changes as well as display broadcasts. Some scaling changes do not
        // send WM_DISPLAYCHANGE to an off-screen control window.
        SetTimer(_helper, 1, 2000, 0);
        ShowWindow(_window!.WindowHandle, 6);
    }

    private PhotinoWindow CreateSurface(DesktopDisplay display)
    {
        var surface = new PhotinoWindow(_window)
            .SetTitle("Alcyone Desktop")
            .SetNotificationsEnabled(false)
            .SetUseOsDefaultSize(false).SetUseOsDefaultLocation(false)
            .SetMinSize(1, 1).SetSize(display.Width, display.Height)
            .SetLeft(display.X).SetTop(display.Y)
            .SetChromeless(true).SetResizable(false).SetMinimized(true)
            .SetContextMenuEnabled(false).SetDevToolsEnabled(false)
            .Load(_url + "/desktop/" + _viewToken);
        try
        {
            // Photino 4 creates secondary windows without starting another message loop
            // when the main window's loop is already running.
            surface.WaitForClose();
            var handle = surface.WindowHandle;
            if (!IsWindow(handle)) throw new InvalidOperationException(text["无法创建桌面画板窗口。"]);
            SetStyle(handle, -16, (nint)((GetWindowLongPtrW(handle, -16).ToInt64() & ~0xA1CF0000L) | 0x40000000L));
            SetStyle(handle, -20, (nint)((GetWindowLongPtrW(handle, -20).ToInt64() & ~0x40000L) | 0x08000080L));
            Reparent(handle, _host);
            PositionSurface(surface, display);
            ShowWindow(handle, 4);
            return surface;
        }
        catch
        {
            CloseSurface(surface);
            throw;
        }
    }

    private void RestoreEditor()
    {
        var handle = _window!.WindowHandle;
        ShowWindow(handle, 9);
        // An unplugged monitor must not leave the settings window off-screen.
        if (GetWindowRect(handle, out var rect) && Displays.Count > 0 &&
            !Displays.Any(display => rect.Left < display.X + display.Width && rect.Right > display.X &&
                rect.Top < display.Y + display.Height && rect.Bottom > display.Y))
        {
            var primary = Displays.FirstOrDefault(display => display.Primary) ?? Displays[0];
            SetWindowPos(handle, 0, primary.X, primary.Y, primary.Width, primary.Height, 0x0014);
        }
        SetForegroundWindow(handle);
    }

    private void ReconcileDisplays(bool force = false)
    {
        var displays = DesktopDisplays.Enumerate();
        if (displays.Length == 0) return; // Transient disconnect/lock: keep the last layout.
        if (!IsWindow(_host))
        {
            _host = FindWallpaperHost();
            if (_host == 0) throw new InvalidOperationException(text["无法找到 Windows 桌面层，请重启资源管理器后重试。"]);
            force = true;
        }
        if (!force && displays.SequenceEqual(_displays) && _surfaces.Count == displays.Length &&
            _surfaces.Values.All(surface => IsWindow(surface.WindowHandle))) return;
        var created = new List<string>();
        try
        {
            foreach (var display in displays)
            {
                if (_surfaces.TryGetValue(display.Device, out var surface) && IsWindow(surface.WindowHandle))
                {
                    if (force) Reparent(surface.WindowHandle, _host);
                    PositionSurface(surface, display);
                }
                else
                {
                    _surfaces[display.Device] = CreateSurface(display);
                    created.Add(display.Device);
                }
            }
        }
        catch
        {
            foreach (var device in created) { CloseSurface(_surfaces[device]); _surfaces.Remove(device); }
            throw;
        }
        foreach (var device in _surfaces.Keys.Except(displays.Select(display => display.Device)).ToArray())
        {
            CloseSurface(_surfaces[device]);
            _surfaces.Remove(device);
        }
        Volatile.Write(ref _displays, displays);
        Error = null;
        Changed?.Invoke();
    }

    private void PositionSurface(PhotinoWindow surface, DesktopDisplay display)
    {
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            var origin = new NativePoint { X = display.X, Y = display.Y };
            MapWindowPoints(0, _host, ref origin, 1);
            if (!SetWindowPos(surface.WindowHandle, (nint)1, origin.X, origin.Y,
                display.Width, display.Height, 0x0030)) throw new Win32Exception();
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static void CloseSurface(PhotinoWindow surface)
    {
        if (IsWindow(surface.WindowHandle)) surface.Close();
    }

    private void StopDesktop()
    {
        IsDesktop = false;
        if (_helper != 0) KillTimer(_helper, 1);
        foreach (var surface in _surfaces.Values) CloseSurface(surface);
        _surfaces.Clear();
        Volatile.Write(ref _presentation, null);
        _host = 0;
    }

    private static nint FindWallpaperHost()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == 0) return 0;
        // Explorer's wallpaper host is undocumented. Fail closed if no known layer exists.
        SendMessageTimeoutW(progman, 0x052c, 0, 0, 2, 1000, out _);
        SendMessageTimeoutW(progman, 0x052c, 0x0d, 0, 2, 1000, out _);
        SendMessageTimeoutW(progman, 0x052c, 0x0d, 1, 2, 1000, out _);
        // Recent Windows 11 shells can place WorkerW inside Progman.
        var host = FindWindowExW(progman, 0, "WorkerW", null);
        if (host != 0 && FindWindowExW(host, 0, "SHELLDLL_DefView", null) == 0) return host;
        host = 0;
        EnumWindows((window, _) =>
        {
            if (FindWindowExW(window, 0, "SHELLDLL_DefView", null) == 0) return true;
            var candidate = FindWindowExW(0, window, "WorkerW", null);
            if (candidate == 0 || FindWindowExW(candidate, 0, "SHELLDLL_DefView", null) != 0) return true;
            host = candidate;
            return false;
        }, 0);
        return host;
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (_disposed) return DefWindowProcW(hwnd, message, wParam, lParam);
            if (message == 0x0312 && wParam == HotkeyId)
            {
                RestoreEditor(); Changed?.Invoke(); return 0;
            }
            if (message == TrayMessage)
            {
                var notification = (uint)(lParam.ToInt64() & 0xffff);
                if (notification is 0x0203 or 0x0401) { RestoreEditor(); Changed?.Invoke(); }
                else if (notification is 0x007b or 0x0205) ShowTrayMenu();
                return 0;
            }
            if (IsDesktop && message is 0x007e or 0x001a or 0x0219 or 0x0113)
                ReconcileDisplays(); // Topology, work area, device and periodic DPI refresh.
            if (_taskbarCreated != 0 && message == _taskbarCreated)
            {
                _trayAdded = false;
                EnsureControls();
                if (IsDesktop) ReconcileDisplays(force: true);
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            logger.LogError(ex, "Desktop shell message failed");
            // Do not repeatedly steal focus or retry an unavailable shell every two seconds.
            StopDesktop();
            RestoreEditor();
            Changed?.Invoke();
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenuW(menu, 0, 1, text["打开设置界面"]);
            if (IsDesktop) AppendMenuW(menu, 0, 3, text["关闭所有屏幕桌面"]);
            AppendMenuW(menu, 0, 2, text["退出 Alcyone"]);
            GetCursorPos(out var cursor);
            SetForegroundWindow(_helper);
            var command = TrackPopupMenu(menu, 0x0102, cursor.X, cursor.Y, 0, _helper, 0);
            PostMessageW(_helper, 0, 0, 0);
            if (command == 1) { RestoreEditor(); Changed?.Invoke(); }
            else if (command == 3) { StopDesktop(); RestoreEditor(); Changed?.Invoke(); }
            else if (command == 2)
            {
                RestoreEditor();
                PostMessageW(_window!.WindowHandle, 0x0010, 0, 0);
            }
        }
        finally { DestroyMenu(menu); }
    }

    private static void Reparent(nint window, nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetParent(window, parent) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
    }

    private static void SetStyle(nint window, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtrW(window, index, value) == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
    }

    // Called on the Photino UI thread after its message loop ends.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDesktop();
        if (_trayAdded) Shell_NotifyIconW(2, ref _tray);
        if (_hotkey) UnregisterHotKey(_helper, HotkeyId);
        if (_helper != 0) DestroyWindow(_helper);
        if (_procedure is not null) UnregisterClassW(_className, GetModuleHandleW(null));
        _window = null;
    }
}
