using System.Runtime.InteropServices;
using System.Text.Json;
using static StateMachine.DesktopNative;

namespace StateMachine;

// Explorer's icon layer can consume mouse input before wallpaper WebViews receive it.
internal sealed class DesktopPointerRouter : IDisposable
{
    private delegate nint MouseProcedure(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint Data, Flags, Time; public nuint Extra; }
    public sealed record HitRegion(double Left, double Top, double Right, double Bottom);
    public sealed record HitRegions(double Ratio, HitRegion[] Rectangles);
    private readonly Dictionary<nint, HitRegions> _windows = new();
    private readonly MouseProcedure _procedure;
    private nint _hook, _captured, _capturedChild;
    private bool _paused;
    private uint _lastDown;
    private NativePoint _lastPoint;
    private nint _lastTarget;
    public bool Paused { get => _paused; set { _paused = value; if (value) { _captured = 0; _lastTarget = 0; } } }
    public DesktopPointerRouter()
    {
        _procedure = HandleMouse;
        _hook = SetWindowsHookExW(14, _procedure, GetModuleHandleW(null), 0);
        if (_hook == 0) throw new System.ComponentModel.Win32Exception();
    }
    public void Update(nint handle, string json)
    {
        try { var regions = JsonSerializer.Deserialize<HitRegions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); if (regions is { Ratio: > 0 and <= 8, Rectangles.Length: <= 2000 }) _windows[handle] = regions; }
        catch (JsonException) { }
    }
    public void Remove(nint handle) { _windows.Remove(handle); if (_captured == handle) _captured = 0; }
    private nint HandleMouse(int code, nuint message, nint data)
    {
        try
        {
            if (code < 0 || Paused || _windows.Count == 0 || message is not (0x0200 or 0x0201 or 0x0202 or 0x020a)) return CallNextHookEx(_hook, code, message, data);
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            var point = mouse.Point;
            var target = _captured;
            // Idle mouse movement needs no wallpaper routing or global hit testing.
            if (message == 0x0200 && target == 0) return CallNextHookEx(_hook, code, message, data);
            if (target == 0)
            {
                var underMouse = WindowFromPoint(point);
                GetWindowThreadProcessId(underMouse, out var process);
                if (process == Environment.ProcessId) return CallNextHookEx(_hook, code, message, data);
                var root = GetAncestor(underMouse, 2);
                var name = new System.Text.StringBuilder(256); GetClassNameW(root, name, name.Capacity);
                if (name.ToString() is not ("Progman" or "WorkerW")) return CallNextHookEx(_hook, code, message, data);
                foreach (var (window, regions) in _windows)
                {
                    if (!IsWindow(window) || !GetWindowRect(window, out var bounds)) continue;
                    var x = (point.X - bounds.Left) / regions.Ratio; var y = (point.Y - bounds.Top) / regions.Ratio;
                    if (message == 0x020a
                        ? point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom
                        : regions.Rectangles.Any(r => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom)) { target = window; break; }
                }
            }
            if (target == 0 || !IsWindow(target)) { _captured = 0; return CallNextHookEx(_hook, code, message, data); }
            if (message == 0x0201) _captured = target;
            var routedMessage = (uint)message;
            if (message == 0x0201)
            {
                if (_lastTarget == target && unchecked(mouse.Time - _lastDown) <= GetDoubleClickTime()
                    && Math.Abs(point.X - _lastPoint.X) <= GetSystemMetrics(36) / 2
                    && Math.Abs(point.Y - _lastPoint.Y) <= GetSystemMetrics(37) / 2)
                { routedMessage = 0x0203; _lastTarget = 0; }
                else { _lastTarget = target; _lastDown = mouse.Time; _lastPoint = point; }
            }
            // Route to the WebView's actual child HWND, using its client coordinates.
            var cachedChild = message is 0x0200 or 0x0202 && IsWindow(_capturedChild);
            var child = cachedChild ? _capturedChild : target;
            for (var depth = 0; !cachedChild && depth < 8; depth++)
            {
                var client = point; MapWindowPoints(0, child, ref client, 1);
                var next = ChildWindowFromPointEx(child, client, 3);
                if (next == 0 || next == child) break;
                child = next;
            }
            if (message == 0x0201) _capturedChild = child;
            if (message != 0x020a) MapWindowPoints(0, child, ref point, 1); // Wheel messages use screen coordinates.
            var buttons = message == 0x0201 || (message == 0x0200 && _captured != 0) ? 1u : 0u;
            PostMessageW(child, routedMessage, message == 0x020a ? mouse.Data & 0xffff0000u : buttons, (nint)((point.Y << 16) | (point.X & 0xffff)));
            if (message == 0x0202) _captured = 0;
            // WH_MOUSE_LL runs before Windows updates the cursor position. Forwarding
            // WM_MOUSEMOVE must never suppress the physical pointer movement.
            return message == 0x0200 ? CallNextHookEx(_hook, code, message, data) : 1;
        }
        catch { return CallNextHookEx(_hook, code, message, data); }
    }
    public void Dispose() { if (_hook != 0) UnhookWindowsHookEx(_hook); _hook = 0; _windows.Clear(); _captured = 0; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int id, MouseProcedure procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint window, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
