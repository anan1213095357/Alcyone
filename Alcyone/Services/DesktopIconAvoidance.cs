using System.Runtime.InteropServices;
using System.Text.Json;
using static StateMachine.DesktopNative;

namespace StateMachine;

// Explorer owns icon coordinates. Cross-process list-view messages require a
// buffer in Explorer's address space; never send a pointer to managed memory.
internal sealed class DesktopIconAvoidance : IDisposable
{
    private readonly object _gate = new(), _workGate = new();
    private readonly Dictionary<nint, (DesktopPointerRouter.HitRegions Fixed, DesktopPointerRouter.HitRegions? Moving)> _surfaces = new();
    private readonly Timer _timer;
    private int _dirty;
    private volatile bool _disposed;
    private nint _autoArrangeWindow;
    private uint _blockedProcess;
    private bool _ipcTimedOut;
    public DesktopIconAvoidance() => _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    public void Update(nint window, string json, bool moving = false)
    {
        try
        {
            var regions = JsonSerializer.Deserialize<DesktopPointerRouter.HitRegions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (regions is not { Ratio: > 0 and <= 8, Rectangles.Length: <= 2000 }) return;
            lock (_gate)
            {
                if (_disposed) return;
                if (moving) { if (_surfaces.TryGetValue(window, out var current)) _surfaces[window] = (current.Fixed, regions); }
                else _surfaces[window] = (regions, null);
                Interlocked.Exchange(ref _dirty, 1);
                _timer.Change(24, Timeout.Infinite);
            }
        }
        catch (JsonException) { }
    }
    public void Remove(nint window) { lock (_gate) _surfaces.Remove(window); }
    public void Invalidate()
    {
        lock (_gate) { if (_disposed) return; Interlocked.Exchange(ref _dirty, 1); _timer.Change(24, Timeout.Infinite); }
    }
    private void Run()
    {
        if (!Monitor.TryEnter(_workGate)) return;
        try
        {
            if (_disposed || Interlocked.Exchange(ref _dirty, 0) == 0) return;
            var dpi = SetThreadDpiAwarenessContext(-4);
            try { Avoid(); } catch (Exception ex) when (ex is ArgumentException or OverflowException or System.ComponentModel.Win32Exception) { }
            finally { if (dpi != 0) SetThreadDpiAwarenessContext(dpi); }
        }
        finally
        {
            Monitor.Exit(_workGate);
            lock (_gate) if (!_disposed && Volatile.Read(ref _dirty) != 0) _timer.Change(16, Timeout.Infinite);
        }
    }
    private void Avoid()
    {
        _ipcTimedOut = false;
        var obstacles = new List<NativeRect>();
        lock (_gate)
            foreach (var (window, value) in _surfaces)
            {
                if (!GetWindowRect(window, out var bounds)) continue;
                foreach (var regions in new[] { value.Fixed, value.Moving }.OfType<DesktopPointerRouter.HitRegions>())
                    foreach (var r in regions.Rectangles.Where(r => r.Avoid && !(ReferenceEquals(regions,value.Fixed) && r.Route && value.Moving?.Rectangles.Any(m=>m.Route)==true)))
                        if (double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom))
                            obstacles.Add(new NativeRect { Left = bounds.Left + (int)(r.Left * regions.Ratio) - 12, Top = bounds.Top + (int)(r.Top * regions.Ratio) - 12, Right = bounds.Left + (int)(r.Right * regions.Ratio) + 12, Bottom = bounds.Top + (int)(r.Bottom * regions.Ratio) + 12 });
            }
        if (obstacles.Count == 0) return;
        var list = FindIconList();
        if (list == 0 || !Send(list, 0x1004, 0, 0, out var count) || count == 0 || count > 1000) return;
        GetWindowThreadProcessId(list, out var pid);
        if (_blockedProcess == pid) return;
        _ipcTimedOut = false;
        var process = OpenProcess(0x0038, false, pid); // VM_OPERATION | VM_READ | VM_WRITE
        if (process == 0) return;
        var buffer = VirtualAllocEx(process, 0, 16, 0x3000, 4);
        if (buffer == 0) { CloseHandle(process); return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var icons = new List<(int Index, NativeRect Rect)>();
            var origin = new NativePoint(); MapWindowPoints(list, 0, ref origin, 1);
            for (var i = 0; i < (int)count; i++)
            {
                if (clock.ElapsedMilliseconds > 1000 || _disposed) return;
                if (!WriteProcessMemory(process, buffer, new byte[16], 16, out _) || !Send(list, 0x100e, (nuint)i, buffer, out var ok) || ok == 0) continue;
                var bytes = new byte[16]; if (!ReadProcessMemory(process, buffer, bytes, 16, out _)) continue;
                icons.Add((i, new NativeRect { Left = BitConverter.ToInt32(bytes, 0) + origin.X, Top = BitConverter.ToInt32(bytes, 4) + origin.Y, Right = BitConverter.ToInt32(bytes, 8) + origin.X, Bottom = BitConverter.ToInt32(bytes, 12) + origin.Y }));
            }
            var workAreas = new List<NativeRect>();
            var moves = new List<(int Index, int FromX, int FromY, int ToX, int ToY)>();
            EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect rect, nint data) => { var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() }; if (GetMonitorInfoW(monitor, ref info)) workAreas.Add(info.Work); return true; }, 0);
            for (var i = 0; i < icons.Count; i++)
            {
                var icon = icons[i];
                if (!obstacles.Any(o => Intersects(icon.Rect, o))) continue;
                var width = Math.Max(32, icon.Rect.Right - icon.Rect.Left); var height = Math.Max(32, icon.Rect.Bottom - icon.Rect.Top);
                var area = workAreas.FirstOrDefault(a => icon.Rect.Left >= a.Left && icon.Rect.Left < a.Right && icon.Rect.Top >= a.Top && icon.Rect.Top < a.Bottom);
                if (area.Right <= area.Left) continue;
                var candidates = new List<NativePoint>();
                for (var x = area.Left + 8; x + width <= area.Right && candidates.Count < 10000; x += Math.Max(96, width + 12))
                    for (var y = area.Top + 8; y + height <= area.Bottom && candidates.Count < 10000; y += Math.Max(90, height + 12)) candidates.Add(new NativePoint { X = x, Y = y });
                foreach (var point in candidates.OrderBy(p => Math.Pow(p.X - icon.Rect.Left, 2) + Math.Pow(p.Y - icon.Rect.Top, 2)))
                {
                    var destination = new NativeRect { Left = point.X, Top = point.Y, Right = point.X + width, Bottom = point.Y + height };
                    if (obstacles.Any(o => Intersects(destination, o)) || icons.Where((_, index) => index != i).Any(other => Intersects(destination, other.Rect))) continue;
                    if (!Send(list, 0x1010, (nuint)icon.Index, buffer, out var positioned) || positioned == 0) break;
                    var bytes = new byte[8]; if (!ReadProcessMemory(process, buffer, bytes, 8, out _)) break;
                    var x = BitConverter.ToInt32(bytes, 0) + destination.Left - icon.Rect.Left;
                    var y = BitConverter.ToInt32(bytes, 4) + destination.Top - icon.Rect.Top;
                    moves.Add((icon.Index, BitConverter.ToInt32(bytes,0), BitConverter.ToInt32(bytes,4), x, y));
                    icons[i] = (icon.Index, destination);
                    break;
                }
                if (clock.ElapsedMilliseconds > 100) { Interlocked.Exchange(ref _dirty, 1); break; }
            }
            if (moves.Count > 0 && !_ipcTimedOut)
            {
                var style = GetWindowLongPtrW(list, -16).ToInt64();
                if ((style & 0x100) != 0) { _autoArrangeWindow = list; SetWindowLongPtrW(list, -16, (nint)(style & ~0x100)); }
                // Animate all displaced icons together; never delay the mouse/UI thread.
                var animation = System.Diagnostics.Stopwatch.StartNew();
                var bytes = new byte[8];
                while (!_disposed && !_ipcTimedOut)
                {
                    var frame = animation.ElapsedMilliseconds;
                    var t = Math.Min(1, frame / 220d); var eased = 1 - Math.Pow(1 - t, 3);
                    foreach (var move in moves)
                    {
                        var x = (int)Math.Round(move.FromX + (move.ToX - move.FromX) * eased);
                        var y = (int)Math.Round(move.FromY + (move.ToY - move.FromY) * eased);
                        BitConverter.GetBytes(x).CopyTo(bytes, 0); BitConverter.GetBytes(y).CopyTo(bytes, 4);
                        if (!WriteProcessMemory(process, buffer, bytes, 8, out _) || !Send(list, 0x1031, (nuint)move.Index, buffer, out _)) break;
                    }
                    if (t >= 1) break;
                    Thread.Sleep((int)Math.Max(1, 16 - (animation.ElapsedMilliseconds - frame)));
                }
            }
        }
        finally
        {
            // A timed-out window message may still consume its pointer later.
            // Leave that tiny buffer alive until Explorer exits; suspend this PID.
            if (_ipcTimedOut) _blockedProcess = pid;
            else VirtualFreeEx(process, buffer, 0, 0x8000);
            CloseHandle(process);
        }
    }
    private static bool Intersects(NativeRect a, NativeRect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    private bool Send(nint window, uint message, nuint wParam, nint data, out nuint result)
    {
        if (_ipcTimedOut) { result = 0; return false; }
        var success = SendMessageTimeoutW(window, message, wParam, data, 2, 100, out result) != 0;
        if (!success) _ipcTimedOut = true;
        return success;
    }
    private static nint FindIconList()
    {
        nint list = 0;
        EnumWindows((window, _) =>
        {
            var name = new System.Text.StringBuilder(64); GetClassNameW(window, name, name.Capacity);
            if (name.ToString() is not ("Progman" or "WorkerW")) return true;
            EnumChildWindows(window, (child, parameter) => { var view = FindWindowExW(child, 0, "SysListView32", null); if(view!=0){var cls=new System.Text.StringBuilder(64);GetClassNameW(child,cls,cls.Capacity);if(cls.ToString()=="SHELLDLL_DefView")list=view;}return list==0; },0);
            return list == 0;
        }, 0);
        return list;
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _surfaces.Clear(); _timer.Dispose(); }
        lock (_workGate)
        {
            if (_autoArrangeWindow != 0 && IsWindow(_autoArrangeWindow)) SetWindowLongPtrW(_autoArrangeWindow, -16, (nint)(GetWindowLongPtrW(_autoArrangeWindow, -16).ToInt64() | 0x100));
        }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, EnumWindowsProc callback, nint parameter);
}
