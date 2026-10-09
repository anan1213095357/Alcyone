namespace FastColorFinder.Core;

/// <summary>
/// 兼容入口。底层已不再使用 Graphics.CopyFromScreen。
/// 高频循环请直接复用 Win32ScreenCapture。
/// </summary>
public static class ScreenshotService
{
    public static Bitmap Capture(Rectangle screenRect)
    {
        using var capture = new Win32ScreenCapture(screenRect);
        return capture.CaptureClone();
    }
}
