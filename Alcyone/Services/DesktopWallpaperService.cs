using System.ComponentModel;
using System.Runtime.InteropServices;
using Photino.NET;
using StateMachine.Localization;
using static StateMachine.DesktopNative;

namespace StateMachine;

/// <summary>One read-only webview per monitor, all observing the editor's existing runtime.
/// Wallpaper windows have their own viewport/DPI; the editor never becomes a shell child.</summary>
public sealed class DesktopWallpaperService(UiText text, ILogger<DesktopWallpaperService> logger, DesktopPreferences preferences) : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const uint AiWheelMessage = 0x8002;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(nint Window, double X, double Y, int Delta)> _aiWheels = new();
    private const int HotkeyId = 0x414c;
    private PhotinoWindow? _window;
    private nint _helper, _host;
    private string _url = "";
    private bool _startupTray;
    private int _startupLeft, _startupTop;
    private nint _editorExtendedStyle;
    private readonly string _viewToken = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, PhotinoWindow> _surfaces = new(StringComparer.Ordinal);
    private DesktopDisplay[] _displays = [];
    private DesktopDisplay[]? _pendingDisplays;
    private long _pendingDisplaysSince;
    private DesktopPresentation? _presentation;
    private readonly string _className = "Alcyone.Desktop." + Guid.NewGuid().ToString("N");
    private WindowProc? _procedure;
    private uint _taskbarCreated;
    private NotifyIconData _tray;
    private bool _trayAdded, _hotkey, _disposed;
    private Timer? _animationMonitor;
    private int _animationPollPending;
    private nint _editorHandle;
    private bool _lastEditorHidden;
    private DesktopPointerRouter? _pointerRouter;
    private DesktopIconAvoidance? _iconAvoidance;
    private DesktopFilePacking? _filePacking;
    private DesktopDeleteInterceptor? _deleteInterceptor;
    private sealed record PendingPackingDelete(DesktopFilePacking.Selection File,nint Surface);
    private readonly Dictionary<string,PendingPackingDelete> _packingDeletes=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> _packingDeletePaths=new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentQueue<(nint Window,string Id,bool Success)> _packingResults=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<DesktopFilePacking.Item> _packingPreviews=new();
    public DesktopTextInput TextInput { get; } = new();
    public bool AnimationsPaused { get; private set; }
    [DllImport("user32.dll", EntryPoint = "LoadImageW",
    CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
    nint instance, string name, uint type,
    int width, int height, uint flags);
    public bool Available => OperatingSystem.IsWindows() && _window is not null && !_disposed;
    public bool IsDesktop { get; private set; }
    public IReadOnlyList<DesktopDisplay> Displays => Volatile.Read(ref _displays);
    public DesktopPresentation? Presentation => Volatile.Read(ref _presentation);
    public bool HotkeyAvailable => _hotkey;
    public bool IsMaximized => Available && IsZoomed(_window!.WindowHandle);
    public bool EditorHidden
    {
        get
        {
            var handle = Volatile.Read(ref _editorHandle);
            return Available && handle != 0 && (!IsWindowVisible(handle) || IsIconic(handle));
        }
    }
    public string? Error { get; private set; }
    public event Action? Changed;

    public void Attach(PhotinoWindow window, string url)
    {
        _window = window;
        _url = url;
        window.RegisterWebMessageReceivedHandler((sender, message) =>
        {
            if (_disposed || sender != _window) return;
            var handle = window.WindowHandle;
            Volatile.Write(ref _editorHandle, handle);
            switch (message)
            {
                case "alcyone:window:ready":
                    if (_startupTray)
                    {
                        _startupTray=false;
                        ShowWindow(handle,0);
                        SetWindowPos(handle,0,_startupLeft,_startupTop,0,0,0x0015);
                    }
                    ApplyWindowCorners(handle);
                    break;
                case "alcyone:window:state":
                    ApplyWindowCorners(handle);
                    break;
                case "alcyone:window:minimize":
                    ShowWindow(handle, IsDesktop ? 0u : 6u);
                    break;
                case "alcyone:window:maximize":
                    ShowWindow(handle, IsZoomed(handle) ? 9u : 3u);
                    ApplyWindowCorners(handle);
                    break;
                case "alcyone:window:close":
                    if (IsDesktop) ShowWindow(handle, 0);
                    else window.Close();
                    break;
                case "alcyone:window:drag":
                    if (IsZoomed(handle) && GetWindowRect(handle, out var maximized) && GetCursorPos(out var cursor))
                    {
                        var fraction = Math.Clamp((double)(cursor.X - maximized.Left) / Math.Max(1, maximized.Right - maximized.Left), 0, 1);
                        ShowWindow(handle, 9);
                        if (GetWindowRect(handle, out var restored))
                            SetWindowPos(handle, 0, cursor.X - (int)((restored.Right - restored.Left) * fraction), cursor.Y - 24, 0, 0, 0x0015);
                    }
                    ReleaseCapture();
                    // Let Windows run its native move loop, including snapping to screen edges.
                    GetCursorPos(out var dragCursor);
                    PostMessageW(handle, 0x00a1, 2, (nint)((dragCursor.Y << 16) | (dragCursor.X & 0xffff)));
                    break;
                default:
                    if (!message.StartsWith("alcyone:window:resize:", StringComparison.Ordinal) || IsZoomed(handle)) return;
                    var direction = message["alcyone:window:resize:".Length..] switch
                    {
                        "w" => 1, "e" => 2, "n" => 3, "nw" => 4,
                        "ne" => 5, "s" => 6, "sw" => 7, "se" => 8, _ => 0
                    };
                    if (direction == 0) return;
                    ReleaseCapture();
                    PostMessageW(handle, 0x0112, (nuint)(0xf000 + direction), 0);
                    break;
            }
            Changed?.Invoke();
        });
        Volatile.Write(ref _displays, DesktopDisplays.Enumerate());
        EnsureControls(); // Tray exists even when desktop presentation has never been enabled.
        _animationMonitor = new Timer(_ => PollFullscreenWindow(), null, 500, 500);
    }

    private static bool OtherWindowIsFullscreen()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId || processId == 0) return false;
        // Explorer's desktop is itself monitor-sized; it must not pause the wallpaper.
        var className = new System.Text.StringBuilder(256);
        GetClassNameW(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" ||
            FindWindowExW(foreground, 0, "SHELLDLL_DefView", null) != 0) return false;
        var monitor = MonitorFromWindow(foreground, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || !GetWindowRect(foreground, out var bounds)) return false;
        return bounds.Left <= info.Monitor.Left + 1 && bounds.Top <= info.Monitor.Top + 1 &&
            bounds.Right >= info.Monitor.Right - 1 && bounds.Bottom >= info.Monitor.Bottom - 1;
    }

    private void PollFullscreenWindow()
    {
        if (_disposed || Volatile.Read(ref _editorHandle) == 0 || Interlocked.Exchange(ref _animationPollPending, 1) != 0) return;
        try
        {
            var paused = OtherWindowIsFullscreen();
            var hidden = EditorHidden;
            if (_disposed || (paused == AnimationsPaused && hidden == _lastEditorHidden)) return;
            AnimationsPaused = paused;
            if (_pointerRouter is not null) _pointerRouter.Paused = paused;
            _lastEditorHidden = hidden;
            // Subscribers marshal to their Blazor circuit. Never call native WebView
            // messaging from a timer, a native callback, or a newly created surface.
            Changed?.Invoke();
        }
        catch (Exception ex) { if (!_disposed) logger.LogDebug(ex, "Fullscreen animation check failed"); }
        finally { Volatile.Write(ref _animationPollPending, 0); }
    }

    private static void ApplyWindowCorners(nint handle)
    {
        var preference = IsZoomed(handle) ? 1 : 2; // Square when maximized, round otherwise.
        DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        // A chromeless popup is not always rounded by DWM, even when the request succeeds.
        // Clip the actual window as well so the corners work on Windows 10 and 11.
        if (IsZoomed(handle)) { SetWindowRgn(handle, 0, true); return; }
        if (!GetWindowRect(handle, out var rectangle)) return;
        var region = CreateRoundRectRgn(0, 0, rectangle.Right - rectangle.Left + 1,
            rectangle.Bottom - rectangle.Top + 1, 20, 20);
        if (region != 0 && SetWindowRgn(handle, region, true) == 0) DeleteObject(region);
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
                    try { EnterDesktop(); preferences.RememberDesktop(true, presentation.ConfigKey); }
                    catch
                    {
                        if (previous is null) { StopDesktop(); RestoreEditor(); }
                        Volatile.Write(ref _presentation, previous);
                        throw;
                    }
                }
                else { preferences.RememberDesktop(false); StopDesktop(); RestoreEditor(); }
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
            Icon = LoadImageW(
    0,
    Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico"),
    1, 0, 0, 0x50),
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
        _pointerRouter ??= new DesktopPointerRouter { Paused = AnimationsPaused };
        _iconAvoidance ??= new DesktopIconAvoidance();
        _pointerRouter.PointerReleased = _iconAvoidance.Invalidate;
        _pointerRouter.AiWheel = (window, x, y, delta) =>
        {
            _aiWheels.Enqueue((window, x, y, delta));
            PostMessageW(_helper, AiWheelMessage, 0, 0);
        };
        ReconcileDisplays(force: true);
        if (_surfaces.Count == 0) throw new InvalidOperationException(text["没有检测到可用显示器。"]);
        IsDesktop = true;
        EnsurePackingInterceptor();
        // Watch DPI changes as well as display broadcasts. Some scaling changes do not
        // send WM_DISPLAYCHANGE to an off-screen control window.
        SetTimer(_helper, 1, 2000, 0);
        ShowWindow(_window!.WindowHandle, 0); // Hide the editor and its taskbar button; keep the tray.
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
        surface.RegisterWebMessageReceivedHandler((_, message) =>
        {
            if (!_disposed && message == "alcyone:desktop:text-close")
            {
                TextInput.Close(surface.WindowHandle);
                return;
            }
            const string packingCommit="alcyone:desktop:pack-commit:";
            if(!_disposed && message.StartsWith(packingCommit,StringComparison.Ordinal))
            {
                CommitPackedFile(surface.WindowHandle,message[packingCommit.Length..]);
                return;
            }
            const string closeById = "alcyone:desktop:text-close-id:";
            if (!_disposed && message.StartsWith(closeById, StringComparison.Ordinal))
            { TextInput.CloseById(surface.WindowHandle, message[closeById.Length..]); return; }
            const string replaceInput = "alcyone:desktop:text-replace:";
            if (!_disposed && message.StartsWith(replaceInput, StringComparison.Ordinal))
            {
                try { TextInput.Replace(surface.WindowHandle, message[replaceInput.Length..]); }
                catch (Exception ex) { logger.LogError(ex, "Desktop text replacement failed"); }
                return;
            }
            const string resizeInput = "alcyone:desktop:text-resize:";
            if (!_disposed && message.StartsWith(resizeInput, StringComparison.Ordinal))
            {
                try { TextInput.Resize(surface.WindowHandle, message[resizeInput.Length..]); }
                catch (Exception ex) { logger.LogError(ex, "Desktop text input resize failed"); }
                return;
            }
            const string input = "alcyone:desktop:text-input:";
            if (!_disposed && message.StartsWith(input, StringComparison.Ordinal))
            {
                try { TextInput.Open(surface.WindowHandle, message[input.Length..]); }
                catch (Exception ex) { logger.LogError(ex, "Desktop text input failed"); }
                return;
            }
            const string prefix = "alcyone:desktop:hitregions:";
            if (!_disposed && message.StartsWith(prefix, StringComparison.Ordinal))
            {
                _pointerRouter?.Update(surface.WindowHandle, message[prefix.Length..]);
                _iconAvoidance?.Update(surface.WindowHandle, message[prefix.Length..]);
            }
            const string moving = "alcyone:desktop:avoid:";
            if (!_disposed && message.StartsWith(moving, StringComparison.Ordinal))
                _iconAvoidance?.Update(surface.WindowHandle, message[moving.Length..], moving: true);
        });
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

    public void InitializeTrayWindow(int left,int top)
    {
        var handle=_window!.WindowHandle;
        _startupLeft=left;_startupTop=top;_startupTray=true;
        Volatile.Write(ref _editorHandle,handle);
        _editorExtendedStyle=GetWindowLongPtrW(handle,-20);
        SetStyle(handle,-20,(nint)((_editorExtendedStyle.ToInt64() & ~0x40000L)|0x80));
        // WebView needs an initially visible HWND. Warm it up off-screen without a taskbar entry.
        ShowWindow(handle,4);
    }
    private void RestoreEditor()
    {
        TextInput.Close();
        var handle = _window!.WindowHandle;
        if(_startupTray){_startupTray=false;SetWindowPos(handle,0,_startupLeft,_startupTop,0,0,0x0015);}
        SetStyle(handle,-20,_editorExtendedStyle);
        ShowWindow(handle, IsZoomed(handle) ? 3u : 9u);
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

    private void EnsurePackingInterceptor()
    {
        if(!preferences.FilePackingAnimation) return;
        _filePacking ??=new DesktopFilePacking();
        _deleteInterceptor ??=new DesktopDeleteInterceptor(()=>_filePacking?.SelectedFiles() ?? new(),RequestFilePacking);
    }
    private void RequestFilePacking(List<DesktopFilePacking.Selection> files)
    {
        if(!IsDesktop || _disposed || !preferences.FilePackingAnimation)return;
        foreach(var file in files)
        {
            if(!_packingDeletePaths.TryAdd(file.Path,0))continue;
            var id=Guid.NewGuid().ToString("N");_packingDeletes[id]=new(file,0);
            _packingPreviews.Enqueue(new(file.Name,file.X,file.Y,id));
        }
        PostMessageW(_helper,0x0113,1,0);
    }
    private void CommitPackedFile(nint surface,string id)
    {
        if(!IsDesktop || !preferences.FilePackingAnimation || !_packingDeletes.TryGetValue(id,out var pending) || pending.Surface!=surface)return;
        _packingDeletes.Remove(id);
        _=Task.Run(()=>
        {
            var success=false;
            try
            {
                var file=pending.File;
                FileSystemInfo info=file.Directory ? new DirectoryInfo(file.Path) : new FileInfo(file.Path);
                if(!info.Exists || info.CreationTimeUtc!=file.Created || info.LastWriteTimeUtc!=file.Written)
                    throw new IOException("桌面文件在动画期间发生变化，已取消删除。");
                if(file.Directory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(file.Path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                success=true;
            }
            catch(Exception ex){logger.LogError(ex,"Packing animation recycle operation failed");}
            finally
            {
                _packingDeletePaths.TryRemove(pending.File.Path,out _);
                if(!_disposed){_packingResults.Enqueue((surface,id,success));PostMessageW(_helper,0x0113,1,0);}
            }
        });
    }
    public void PreviewFilePacking()
    {
        if(!IsDesktop || _helper==0) return;
        var display=_displays.FirstOrDefault(); if(display is null) return;
        _packingPreviews.Enqueue(new DesktopFilePacking.Item("Alcyone.txt",display.X+display.Width/2,display.Y+display.Height/2));
        PostMessageW(_helper,0x0113,1,0);
    }
    private bool TryTakePacking(out DesktopFilePacking.Item? item)
    {
        if(_packingPreviews.TryDequeue(out item)) return true;
        item=null; return _filePacking?.TryTake(out item)==true;
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
            _surfaces.Values.All(surface => IsWindow(surface.WindowHandle))) { _pendingDisplays=null; return; }
        if (!force && _displays.Length > 0 && !displays.SequenceEqual(_displays))
        {
            if (_pendingDisplays is null || !displays.SequenceEqual(_pendingDisplays))
            {
                _pendingDisplays=displays; _pendingDisplaysSince=Environment.TickCount64;
                return;
            }
            if (Environment.TickCount64-_pendingDisplaysSince < 1000) return;
        }
        _pendingDisplays=null;
        var created = new List<string>();
        try
        {
            foreach (var display in displays)
            {
                if (_surfaces.TryGetValue(display.Device, out var surface) && IsWindow(surface.WindowHandle))
                {
                    if (GetParent(surface.WindowHandle) != _host) Reparent(surface.WindowHandle, _host);
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
            if (GetWindowRect(surface.WindowHandle,out var current) &&
                current.Left==display.X && current.Top==display.Y &&
                current.Right-current.Left==display.Width && current.Bottom-current.Top==display.Height) return;
            var origin = new NativePoint { X = display.X, Y = display.Y };
            MapWindowPoints(0, _host, ref origin, 1);
            if (!SetWindowPos(surface.WindowHandle, (nint)1, origin.X, origin.Y,
                display.Width, display.Height, 0x0030)) throw new Win32Exception();
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private void CloseSurface(PhotinoWindow surface)
    {
        TextInput.Close(surface.WindowHandle);
        _pointerRouter?.Remove(surface.WindowHandle);
        _iconAvoidance?.Remove(surface.WindowHandle);
        if (IsWindow(surface.WindowHandle)) surface.Close();
    }

    private void StopDesktop()
    {
        IsDesktop = false;
        _deleteInterceptor?.Dispose(); _deleteInterceptor=null; _packingDeletes.Clear(); _packingDeletePaths.Clear();
        _filePacking?.Dispose(); _filePacking=null;
        _pointerRouter?.Dispose(); _pointerRouter = null;
        _iconAvoidance?.Dispose(); _iconAvoidance = null;
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
            // Deliver on the native UI message loop, never from the mouse hook or a timer.
            if (message == AiWheelMessage)
            {
                while (_aiWheels.TryDequeue(out var wheel))
                {
                    var surface = _surfaces.Values.FirstOrDefault(s => s.WindowHandle == wheel.Window);
                    if (surface is not null && IsWindow(wheel.Window))
                        surface.SendWebMessage("alcyone:ai:wheel:" + System.Text.Json.JsonSerializer.Serialize(new { x = wheel.X, y = wheel.Y, delta = wheel.Delta }));
                }
                return 0;
            }
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
            if(IsDesktop && message==0x0113)
            {
                if(preferences.FilePackingAnimation) EnsurePackingInterceptor();
                else { _deleteInterceptor?.Dispose(); _deleteInterceptor=null; _filePacking?.Dispose(); _filePacking=null; _packingDeletes.Clear(); _packingDeletePaths.Clear(); }
            }
            if(message==0x0113)
            {
                while(_packingResults.TryDequeue(out var result))
                    if(IsWindow(result.Window)) _surfaces.Values.FirstOrDefault(s=>s.WindowHandle==result.Window)?.SendWebMessage("alcyone:desktop:pack-result:"+System.Text.Json.JsonSerializer.Serialize(new { id=result.Id,success=result.Success }));
            }
            if(IsDesktop && message==0x0113 && (_filePacking is not null || !_packingPreviews.IsEmpty))
            {
                while(TryTakePacking(out var item))
                {
                    if(item is null) continue;
                    foreach(var surface in _surfaces.Values)
                    {
                        if(!GetWindowRect(surface.WindowHandle,out var bounds) || item.X<bounds.Left || item.X>=bounds.Right || item.Y<bounds.Top || item.Y>=bounds.Bottom) continue;
                        if(item.Id is not null && _packingDeletes.TryGetValue(item.Id,out var pending)) _packingDeletes[item.Id]=pending with { Surface=surface.WindowHandle };
                        surface.SendWebMessage("alcyone:desktop:pack-file:"+System.Text.Json.JsonSerializer.Serialize(new { id=item.Id,name=item.Name,x=item.X-bounds.Left,y=item.Y-bounds.Top }));
                        break;
                    }
                }
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
            else if (command == 3) { preferences.RememberDesktop(false); StopDesktop(); RestoreEditor(); Changed?.Invoke(); }
            else if (command == 2)
            {
                if (AppShutdown.Requested) return;
                AppShutdown.Begin();
                var window = _window;
                try { Dispose(); }
                catch (Exception ex) { logger.LogError(ex,"Application exit cleanup failed"); }
                finally
                {
                    if (window is not null && IsWindow(window.WindowHandle)) window.Close();
                }
            }
        }
        finally { DestroyMenu(menu); }
    }

    [DllImport("user32.dll")] private static extern nint GetParent(nint window);

    private static void Reparent(nint window, nint parent)
    {
        if (GetParent(window)==parent) return;
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
        if (_trayAdded) { Shell_NotifyIconW(2, ref _tray); _trayAdded=false; }
        TextInput.Dispose();
        Volatile.Write(ref _editorHandle, 0);
        _animationMonitor?.Dispose();
        _animationMonitor = null;
        StopDesktop();
        if (_trayAdded) Shell_NotifyIconW(2, ref _tray);
        if (_hotkey) UnregisterHotKey(_helper, HotkeyId);
        if (_helper != 0) DestroyWindow(_helper);
        if (_procedure is not null) UnregisterClassW(_className, GetModuleHandleW(null));
        _window = null;
    }
}
