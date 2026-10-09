using System.Drawing;
using System.Runtime.InteropServices;
namespace FastColorFinder.Native;
/// <summary>
/// 仅保存当前 Photino 主窗口句柄和窗口位置。
/// 不再调用 SetWindowDisplayAffinity，避免影响 OBS / 系统截图 / 其他录屏软件。
/// </summary>
internal static class WindowCaptureExclusion
{
    private static IntPtr _hwnd;
    public static IntPtr Handle => _hwnd;
    public static void TryExcludeByTitle(string title)
    {
        _hwnd = FindWindowW(null, title);
    }

    public static bool TryGetWindowRect(out Rectangle rectangle)
    {
        rectangle = Rectangle.Empty;
        if (_hwnd == IntPtr.Zero) return false;
        if (!IsWindow(_hwnd)) return false;
        if (!GetWindowRect(_hwnd, out RECT rect)) return false;
        rectangle = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return rectangle.Width > 0 && rectangle.Height > 0;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
}