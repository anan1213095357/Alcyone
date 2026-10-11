using System.Runtime.InteropServices;
using System.Text;
using static StateMachine.DesktopNative;

namespace StateMachine;
internal static class DesktopIconHitTest
{
    public static bool IsIcon(nint window, NativePoint screenPoint)
    {
        var name = new StringBuilder(64); GetClassNameW(window, name, name.Capacity);
        if (name.ToString() != "SysListView32") return false;
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x38, false, pid);
        if (process == 0) return true;
        var buffer = VirtualAllocEx(process, 0, 24, 0x3000, 4);
        if (buffer == 0) { CloseHandle(process); return true; }
        var completed = false;
        try
        {
            MapWindowPoints(0, window, ref screenPoint, 1);
            var bytes = new byte[24]; BitConverter.GetBytes(screenPoint.X).CopyTo(bytes, 0); BitConverter.GetBytes(screenPoint.Y).CopyTo(bytes, 4);
            if (!WriteProcessMemory(process, buffer, bytes, 24, out _)) { completed = true; return true; }
            completed = SendMessageTimeoutW(window, 0x1012, 0, buffer, 2, 40, out var index) != 0;
            return !completed || unchecked((int)index) >= 0;
        }
        finally { if (completed) VirtualFreeEx(process, buffer, 0, 0x8000); CloseHandle(process); }
    }
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process, nint address, byte[] bytes, nuint size, out nuint written);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
