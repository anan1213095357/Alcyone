using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace FastColorFinder.Core;

public static class FastMatcher
{
    private readonly record struct MatchPoint(
        int Dx,
        int Dy,
        int MeanDY,
        int MeanDCb,
        int MeanDCr,
        int TolDY,
        int TolDCb,
        int TolDCr,
        int RequiredWeight,
        int ChangeRatePermille);

    private sealed class CompiledModel
    {
        public required MatchPoint[] Points { get; init; }
        public required int TotalRequiredWeight { get; init; }

        public required int StartXOffset { get; init; }
        public required int StartYOffset { get; init; }
        public required int EndXOffset { get; init; }
        public required int EndYOffset { get; init; }
    }

    // 同一个权重字符串只解析/编译一次。
    private static readonly ConcurrentDictionary<string, CompiledModel> StringCache
        = new(StringComparer.Ordinal);

    public static MatchResult Find(
        Bitmap bitmap,
        string weightString,
        int thresholdPercent = 78,
        int step = 1)
    {
        if (string.IsNullOrWhiteSpace(weightString))
            return MatchResult.Empty();

        CompiledModel compiled = StringCache.GetOrAdd(
            weightString,
            static s => Compile(ColorModelCodec.Decode(s)));

        return FindCompiled(bitmap, compiled, thresholdPercent, step);
    }

    public static MatchResult Find(
        Bitmap bitmap,
        ColorModel model,
        int thresholdPercent = 78,
        int step = 1)
    {
        return FindCompiled(
            bitmap,
            Compile(model),
            thresholdPercent,
            step);
    }

    // 仅用于本项目拥有并复用的 DIB，公开 Bitmap 入口仍保留格式检查和 LockBits。
    internal static unsafe MatchResult Find(
        IntPtr bits,
        int width,
        int height,
        int stride,
        string weightString,
        int thresholdPercent = 78,
        int step = 1)
    {
        if (string.IsNullOrWhiteSpace(weightString))
            return MatchResult.Empty();

        CompiledModel compiled = StringCache.GetOrAdd(
            weightString,
            static s => Compile(ColorModelCodec.Decode(s)));

        return FindPixels((byte*)bits, width, height, stride, compiled,
            thresholdPercent, step, Stopwatch.StartNew());
    }

    private static unsafe MatchResult FindCompiled(
        Bitmap bitmap,
        CompiledModel compiled,
        int thresholdPercent,
        int step)
    {
        var sw = Stopwatch.StartNew();

        if (compiled.Points.Length < 2 ||
            compiled.TotalRequiredWeight <= 0)
        {
            return MatchResult.Empty(sw.ElapsedTicks);
        }

        if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb &&
            bitmap.PixelFormat != PixelFormat.Format32bppArgb &&
            bitmap.PixelFormat != PixelFormat.Format32bppRgb)
        {
            throw new ArgumentException(
                "匹配图必须为 32bpp RGB/ARGB/PARGB。",
                nameof(bitmap));
        }

        thresholdPercent = Math.Clamp(thresholdPercent, 1, 100);
        step = Math.Clamp(step, 1, 8);

        int startX = compiled.StartXOffset;
        int startY = compiled.StartYOffset;
        int endX = bitmap.Width - 1 - compiled.EndXOffset;
        int endY = bitmap.Height - 1 - compiled.EndYOffset;

        if (endX < startX || endY < startY)
            return MatchResult.Empty(sw.ElapsedTicks);

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            bitmap.PixelFormat);

        try
        {
            return FindPixels((byte*)data.Scan0, bitmap.Width, bitmap.Height,
                data.Stride, compiled, thresholdPercent, step, sw);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static unsafe MatchResult FindPixels(
        byte* scan0,
        int width,
        int height,
        int stride,
        CompiledModel compiled,
        int thresholdPercent,
        int step,
        Stopwatch sw)
    {
        if (compiled.Points.Length < 2 || compiled.TotalRequiredWeight <= 0)
            return MatchResult.Empty(sw.ElapsedTicks);

        thresholdPercent = Math.Clamp(thresholdPercent, 1, 100);
        step = Math.Clamp(step, 1, 8);
        MatchPoint[] points = compiled.Points;
        int totalRequiredWeight = compiled.TotalRequiredWeight;
        int requiredMatchedWeight = (totalRequiredWeight * thresholdPercent + 99) / 100;
        int allowedMissWeight = totalRequiredWeight - requiredMatchedWeight;
        int startX = compiled.StartXOffset;
        int startY = compiled.StartYOffset;
        int endX = width - 1 - compiled.EndXOffset;
        int endY = height - 1 - compiled.EndYOffset;

        if (endX < startX || endY < startY)
            return MatchResult.Empty(sw.ElapsedTicks);

        nint[]? rentedOffsets = null;
        Span<nint> offsets = points.Length <= 256
            ? stackalloc nint[points.Length]
            : (rentedOffsets = ArrayPool<nint>.Shared.Rent(points.Length)).AsSpan(0, points.Length);

        try
        {
            for (int i = 0; i < points.Length; i++)
                offsets[i] = (nint)points[i].Dy * stride + (nint)points[i].Dx * 4;

            int bestMatchedWeight = 0;
            int bestX = -1;
            int bestY = -1;

            for (int ay = startY; ay <= endY; ay += step)
            {
                byte* anchorPixel = scan0 + (nint)ay * stride + (nint)startX * 4;
                for (int ax = startX; ax <= endX; ax += step, anchorPixel += step * 4)
                {
                    Ycc anchor = FastColor.FromBgr(anchorPixel[0], anchorPixel[1], anchorPixel[2]);

                    int matchedWeight = 0;
                    int missedWeight = 0;

                    // Points 已按 RequiredWeight 从高到低排列：
                    // 静态强特征先判断，粒子/闪烁点最后判断。
                    for (int i = 0; i < points.Length; i++)
                    {
                        ref readonly MatchPoint p = ref points[i];

                        if (PassRelative(anchorPixel + offsets[i], anchor, p))
                        {
                            matchedWeight += p.RequiredWeight;

                            // 已经拿到足够的“必须权重”，当前候选直接成功。
                            // 后面的低权重动态点找不到也不影响结果。
                            if (matchedWeight >= requiredMatchedWeight)
                            {
                                sw.Stop();

                                double score =
                                    matchedWeight * 100.0 /
                                    totalRequiredWeight;

                                return new MatchResult(
                                    true,
                                    ax,
                                    ay,
                                    score,
                                    sw.ElapsedTicks);
                            }
                        }
                        else
                        {
                            missedWeight += p.RequiredWeight;

                            // 缺失的“必须权重”已经超过允许值。
                            // 哪怕后面所有动态点全部命中，也不可能翻盘。
                            if (missedWeight > allowedMissWeight)
                                break;
                        }
                    }

                    if (matchedWeight > bestMatchedWeight)
                    {
                        bestMatchedWeight = matchedWeight;
                        bestX = ax;
                        bestY = ay;
                    }
                }
            }

            sw.Stop();

            double bestScore =
                bestMatchedWeight * 100.0 /
                totalRequiredWeight;

            bool found =
                bestMatchedWeight >= requiredMatchedWeight;

            return new MatchResult(
                found,
                bestX,
                bestY,
                bestScore,
                sw.ElapsedTicks);
        }
        finally
        {
            if (rentedOffsets is not null)
                ArrayPool<nint>.Shared.Return(rentedOffsets);
        }
    }

    private static CompiledModel Compile(ColorModel model)
    {
        MatchPoint[] src = model.Points
            .Where(p => !p.IsAnchor)
            .Select(p => new MatchPoint(
                p.Dx,
                p.Dy,
                p.MeanDY,
                p.MeanDCb,
                p.MeanDCr,
                Math.Max(1, p.TolDY),
                Math.Max(1, p.TolDCb),
                Math.Max(1, p.TolDCr),
                Math.Clamp(p.RequiredWeight, 1, 1000),
                Math.Clamp(p.ChangeRatePermille, 0, 1000)))
            // 最稳定、最必须的点先检查。
            // 这样既符合判定逻辑，也能尽早淘汰错误候选。
            .OrderByDescending(p => p.RequiredWeight)
            .ToArray();

        int minDx = src.Length == 0
            ? 0
            : Math.Min(0, src.Min(p => p.Dx));

        int minDy = src.Length == 0
            ? 0
            : Math.Min(0, src.Min(p => p.Dy));

        int maxDx = src.Length == 0
            ? 0
            : Math.Max(0, src.Max(p => p.Dx));

        int maxDy = src.Length == 0
            ? 0
            : Math.Max(0, src.Max(p => p.Dy));

        return new CompiledModel
        {
            Points = src,
            TotalRequiredWeight = src.Sum(p => p.RequiredWeight),

            StartXOffset = -minDx,
            StartYOffset = -minDy,
            EndXOffset = maxDx,
            EndYOffset = maxDy
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool PassRelative(
        byte* pixel,
        Ycc anchor,
        in MatchPoint p)
    {
        // 注意：
        // Tol 只负责“像不像”；
        // RequiredWeight 只负责“不像时有多严重”。
        // 两个概念彻底分开。
        int b = pixel[0], g = pixel[1], r = pixel[2];
        int y = (77 * r + 150 * g + 29 * b) >> 8;
        if (FastColor.Abs((y - anchor.Y) - p.MeanDY) > p.TolDY)
            return false;

        int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
        if (FastColor.Abs((cb - anchor.Cb) - p.MeanDCb) > p.TolDCb)
            return false;

        int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
        return FastColor.Abs((cr - anchor.Cr) - p.MeanDCr) <= p.TolDCr;
    }
}
