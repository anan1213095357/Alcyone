using System.ComponentModel;
using System.Runtime.InteropServices;
using static StateMachine.DesktopNative;

namespace StateMachine;

public sealed record DesktopDisplay(string Device, int X, int Y, int Width, int Height, uint Dpi, bool Primary);
public sealed record DesktopPresentation(string ConfigKey, MachineSession Session);

internal static class DesktopDisplays
{
    public static DesktopDisplay[] Enumerate()
    {
        var displays = new List<DesktopDisplay>();
        // Enumerate and position in physical coordinates, including negative monitor origins.
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect rectangle, nint parameter) =>
            {
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfoW(monitor, ref info)) return true;
                var bounds = info.Monitor;
                if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return true;
                var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96u;
                displays.Add(new(info.Device, bounds.Left, bounds.Top, bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top, dpi, (info.Flags & 1) != 0));
                return true;
            }, 0)) throw new Win32Exception();
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        return displays.DistinctBy(display => display.Device).OrderByDescending(display => display.Primary)
            .ThenBy(display => display.Device, StringComparer.Ordinal).ToArray();
    }
}
