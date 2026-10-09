using System.Diagnostics;

namespace FastColorFinder.Core;

/// <summary>
/// 业务侧极简搜索入口。
/// 参数顺序固定：范围、权重字符串、找到后的委托、最多搜索秒数。
/// 捕获层复用 Win32 DIB 缓冲，循环中不会重复创建截图 Bitmap。
/// </summary>
public static class FastColorSearch
{
    private const int ThresholdPercent = 70;
    private const int ScanStep = 1;

    public static bool Find(
        Rectangle range,
        string weightString,
        Action<int, int> onFound,
        double seconds)
    {
        Validate(range, weightString, onFound, seconds);

        using var capture = new Win32ScreenCapture(range);
        var timeout = Stopwatch.StartNew();

        do
        {
            capture.Capture();
            var result = FastMatcher.Find(capture.Bits, capture.Width, capture.Height,
                capture.Stride, weightString, ThresholdPercent, ScanStep);
            if (result.Found)
            {
                onFound(range.X + result.AnchorX, range.Y + result.AnchorY);
                return true;
            }
        }
        while (timeout.Elapsed.TotalSeconds < seconds);

        return false;
    }

    public static Task<bool> FindAsync(
        Rectangle range,
        string weightString,
        Action<int, int> onFound,
        double seconds,
        CancellationToken cancellationToken = default)
    {
        Validate(range, weightString, onFound, seconds);

        return Task.Run(() =>
        {
            using var capture = new Win32ScreenCapture(range);
            var timeout = Stopwatch.StartNew();

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                capture.Capture();
                var result = FastMatcher.Find(capture.Bits, capture.Width, capture.Height,
                    capture.Stride, weightString, ThresholdPercent, ScanStep);
                if (result.Found)
                {
                    onFound(range.X + result.AnchorX, range.Y + result.AnchorY);
                    return true;
                }
            }
            while (timeout.Elapsed.TotalSeconds < seconds);

            return false;
        }, cancellationToken);
    }

    private static void Validate(Rectangle range, string weightString, Action<int, int> onFound, double seconds)
    {
        if (range.Width <= 0 || range.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(range), "搜索范围必须大于 0。");
        if (string.IsNullOrWhiteSpace(weightString))
            throw new ArgumentException("权重字符串不能为空。", nameof(weightString));
        ArgumentNullException.ThrowIfNull(onFound);
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "搜索秒数必须大于 0。");
    }
}
