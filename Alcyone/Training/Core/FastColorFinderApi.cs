namespace FastColorFinder.Core;

/// <summary>
/// 最小调用入口。训练工具生成 weightString 后，业务代码无需模型文件。
/// </summary>
public static class FastColorFinderApi
{
    public static MatchResult Find(Bitmap bitmap, string weightString, int thresholdPercent = 78, int step = 1)
        => FastMatcher.Find(bitmap, weightString, thresholdPercent, step);

    public static MatchResult FindOnScreen(Rectangle searchRegion, string weightString, int thresholdPercent = 78, int step = 1)
    {
        using var capture = new Win32ScreenCapture(searchRegion);
        capture.Capture();
        return FastMatcher.Find(capture.Bits, capture.Width, capture.Height,
            capture.Stride, weightString, thresholdPercent, step);
    }
}
