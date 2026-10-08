using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastColorFinderRuntime;

/// <summary>
/// 单文件高速多点找色运行库。
/// 直接复制本文件到你的项目即可使用。
///
/// 项目需要：
///   <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
/// 目标平台：Windows / net8.0-windows 或兼容 Windows 目标。
///
/// 权重字符串支持 FCF3，并兼容 FCF2。
/// </summary>
public static class FastColorSearch
{
    private static readonly ConcurrentDictionary<string, CompiledModel> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 循环搜索屏幕区域。
    /// 参数顺序固定为：范围 -> 权重串 -> 色偏 -> 相似度 -> 成功回调 -> 失败回调 -> 循环秒数 -> 间隔毫秒。
    /// </summary>
    public static async Task<SearchOutcome> FindAsync(
        Rectangle range,
        string weightString,
        int colorDeviation,
        double similarityThreshold,
        Action<SearchSuccess>? onSuccess,
        Action<SearchFailure>? onFailure,
        double loopSeconds,
        int intervalMilliseconds,
        CancellationToken cancellationToken = default)
    {
        Validate(range, weightString, colorDeviation, similarityThreshold, loopSeconds, intervalMilliseconds);

        var compiled = Cache.GetOrAdd(weightString, static text => Compile(Decode(text)));
        using var capture = new Win32Capture(range);

        var totalWatch = Stopwatch.StartNew();
        int attempts = 0;
        InternalMatch best = InternalMatch.Empty;

        // loopSeconds == 0 时仍然至少执行一次。
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            InternalMatch current = await Task.Run(() =>
            {
                capture.Capture();
                return Match(
                    capture.Bits,
                    capture.Width,
                    capture.Height,
                    capture.Stride,
                    compiled,
                    colorDeviation,
                    similarityThreshold);
            }, cancellationToken);

            attempts++;

            if (current.Score > best.Score)
                best = current;

            if (current.Found)
            {
                totalWatch.Stop();

                var success = new SearchSuccess(
                    SearchRange: range,
                    X: range.X + current.AnchorX,
                    Y: range.Y + current.AnchorY,
                    Similarity: current.Score,
                    Attempts: attempts,
                    Elapsed: totalWatch.Elapsed,
                    MatchMilliseconds: current.ElapsedMilliseconds);

                onSuccess?.Invoke(success);
                return SearchOutcome.FromSuccess(success);
            }

            if (loopSeconds <= 0 || totalWatch.Elapsed.TotalSeconds >= loopSeconds)
                break;

            if (intervalMilliseconds > 0)
            {
                double remainingMs = loopSeconds * 1000.0 - totalWatch.Elapsed.TotalMilliseconds;
                if (remainingMs <= 0)
                    break;

                int delay = (int)Math.Min(intervalMilliseconds, Math.Max(0, remainingMs));
                if (delay > 0)
                    await Task.Delay(delay, cancellationToken);
            }
        }

        totalWatch.Stop();

        int bestX = best.AnchorX >= 0 ? range.X + best.AnchorX : -1;
        int bestY = best.AnchorY >= 0 ? range.Y + best.AnchorY : -1;

        var failure = new SearchFailure(
            SearchRange: range,
            BestX: bestX,
            BestY: bestY,
            BestSimilarity: Math.Max(0, best.Score),
            Attempts: attempts,
            Elapsed: totalWatch.Elapsed,
            Reason: "在指定循环时间内未达到相似度阈值。",
            LastMatchMilliseconds: Math.Max(0, best.ElapsedMilliseconds));

        onFailure?.Invoke(failure);
        return SearchOutcome.FromFailure(failure);
    }

    private static void Validate(
        Rectangle range,
        string weightString,
        int colorDeviation,
        double similarityThreshold,
        double loopSeconds,
        int intervalMilliseconds)
    {
        if (range.Width <= 0 || range.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(range), "查找范围必须大于 0。");

        if (string.IsNullOrWhiteSpace(weightString))
            throw new ArgumentException("权重字符串不能为空。", nameof(weightString));

        if (colorDeviation < 0 || colorDeviation > 255)
            throw new ArgumentOutOfRangeException(nameof(colorDeviation), "色偏值必须在 0~255 之间。");

        if (similarityThreshold <= 0 || similarityThreshold > 100)
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "相似度阈值必须在 0~100 之间。");

        if (loopSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(loopSeconds), "循环秒数不能小于 0。");

        if (intervalMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds), "查找间隔不能小于 0。");
    }

    private static unsafe InternalMatch Match(
        IntPtr bits,
        int width,
        int height,
        int stride,
        CompiledModel compiled,
        int colorDeviation,
        double similarityThreshold)
    {
        var sw = Stopwatch.StartNew();

        if (compiled.Points.Length < 2 || compiled.TotalRequiredWeight <= 0)
            return InternalMatch.Empty;

        int requiredMatchedWeight = (int)Math.Ceiling(
            compiled.TotalRequiredWeight * similarityThreshold / 100.0);

        int allowedMissWeight = compiled.TotalRequiredWeight - requiredMatchedWeight;

        int startX = compiled.StartXOffset;
        int startY = compiled.StartYOffset;
        int endX = width - 1 - compiled.EndXOffset;
        int endY = height - 1 - compiled.EndYOffset;

        if (endX < startX || endY < startY)
            return InternalMatch.Empty;

        byte* scan0 = (byte*)bits;
        MatchPoint[] points = compiled.Points;
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

            for (int ay = startY; ay <= endY; ay++)
            {
                byte* anchorPixel = scan0 + (nint)ay * stride + (nint)startX * 4;
                for (int ax = startX; ax <= endX; ax++, anchorPixel += 4)
                {
                    Ycc anchor = FromBgr(anchorPixel[0], anchorPixel[1], anchorPixel[2]);

                    int matchedWeight = 0;
                    int missedWeight = 0;

                    for (int i = 0; i < points.Length; i++)
                    {
                        ref readonly MatchPoint p = ref points[i];

                        if (PassRelative(anchorPixel + offsets[i], anchor, p, colorDeviation))
                        {
                            matchedWeight += p.RequiredWeight;
                        }
                        else
                        {
                            missedWeight += p.RequiredWeight;

                            // 静态高权重点缺失后，尽快终止当前候选。
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

                    if (matchedWeight >= requiredMatchedWeight)
                    {
                        sw.Stop();

                        return new InternalMatch(
                            Found: true,
                            AnchorX: ax,
                            AnchorY: ay,
                            Score: matchedWeight * 100.0 / compiled.TotalRequiredWeight,
                            ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
                    }
                }
            }

            sw.Stop();

            return new InternalMatch(
                Found: false,
                AnchorX: bestX,
                AnchorY: bestY,
                Score: bestMatchedWeight * 100.0 / compiled.TotalRequiredWeight,
                ElapsedMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (rentedOffsets is not null)
                ArrayPool<nint>.Shared.Return(rentedOffsets);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool PassRelative(
        byte* pixel,
        Ycc anchor,
        in MatchPoint point,
        int colorDeviation)
    {
        // 训练容差决定这个点本来的颜色变化范围；
        // colorDeviation 是调用方额外允许的色偏。
        int b = pixel[0], g = pixel[1], r = pixel[2];
        int y = (77 * r + 150 * g + 29 * b) >> 8;
        if (Abs((y - anchor.Y) - point.MeanDY) > point.TolDY + colorDeviation)
            return false;

        int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
        if (Abs((cb - anchor.Cb) - point.MeanDCb) > point.TolDCb + colorDeviation)
            return false;

        int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
        return Abs((cr - anchor.Cr) - point.MeanDCr) <= point.TolDCr + colorDeviation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Ycc FromBgr(byte b, byte g, byte r)
    {
        int y = (77 * r + 150 * g + 29 * b) >> 8;
        int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
        int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
        return new Ycc(y, cb, cr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Abs(int value) => value < 0 ? -value : value;

    private static CompiledModel Compile(ColorModel model)
    {
        MatchPoint[] points = model.Points
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
                Math.Clamp(p.RequiredWeight, 1, 1000)))
            .OrderByDescending(p => p.RequiredWeight)
            .ToArray();

        if (points.Length < 2)
            throw new FormatException("权重字符串中的有效点太少。");

        int minDx = Math.Min(0, points.Min(p => p.Dx));
        int minDy = Math.Min(0, points.Min(p => p.Dy));
        int maxDx = Math.Max(0, points.Max(p => p.Dx));
        int maxDy = Math.Max(0, points.Max(p => p.Dy));

        return new CompiledModel
        {
            Points = points,
            TotalRequiredWeight = points.Sum(p => p.RequiredWeight),
            StartXOffset = -minDx,
            StartYOffset = -minDy,
            EndXOffset = maxDx,
            EndYOffset = maxDy
        };
    }

    private static ColorModel Decode(string text)
    {
        string[] blocks = text.Trim().Split('|');

        if (blocks.Length != 6)
            throw new FormatException("权重字符串格式不正确。");

        return blocks[0] switch
        {
            "FCF3" => DecodeV3(blocks),
            "FCF2" => DecodeV2(blocks),
            _ => throw new FormatException("权重字符串版本不支持。")
        };
    }

    private static ColorModel DecodeV3(string[] blocks)
    {
        int[] region = ParseInts(blocks[1], 2);
        int[] anchorPos = ParseInts(blocks[2], 2);
        int samples = ParseInt(blocks[3]);
        int[] anchor = ParseInts(blocks[4], 7);

        var model = new ColorModel
        {
            RegionWidth = region[0],
            RegionHeight = region[1],
            AnchorX = anchorPos[0],
            AnchorY = anchorPos[1],
            SampleCount = samples
        };

        model.Points.Add(new TrainedPoint
        {
            IsAnchor = true,
            Dx = 0,
            Dy = 0,
            RequiredWeight = 0
        });

        if (!string.IsNullOrWhiteSpace(blocks[5]))
        {
            foreach (string item in blocks[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int[] v = ParseInts(item, 10);

                model.Points.Add(new TrainedPoint
                {
                    Dx = v[0],
                    Dy = v[1],
                    MeanDY = v[2],
                    MeanDCb = v[3],
                    MeanDCr = v[4],
                    TolDY = Math.Max(1, v[5]),
                    TolDCb = Math.Max(1, v[6]),
                    TolDCr = Math.Max(1, v[7]),
                    RequiredWeight = Math.Clamp(v[9], 1, 1000),
                    IsAnchor = false
                });
            }
        }

        ValidateModel(model);
        return model;
    }

    private static ColorModel DecodeV2(string[] blocks)
    {
        int[] region = ParseInts(blocks[1], 2);
        int[] anchorPos = ParseInts(blocks[2], 2);
        int samples = ParseInt(blocks[3]);
        _ = ParseInts(blocks[4], 6);

        var model = new ColorModel
        {
            RegionWidth = region[0],
            RegionHeight = region[1],
            AnchorX = anchorPos[0],
            AnchorY = anchorPos[1],
            SampleCount = samples
        };

        model.Points.Add(new TrainedPoint
        {
            IsAnchor = true,
            Dx = 0,
            Dy = 0,
            RequiredWeight = 0
        });

        if (!string.IsNullOrWhiteSpace(blocks[5]))
        {
            foreach (string item in blocks[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int[] v = ParseInts(item, 9);

                model.Points.Add(new TrainedPoint
                {
                    Dx = v[0],
                    Dy = v[1],
                    MeanDY = v[2],
                    MeanDCb = v[3],
                    MeanDCr = v[4],
                    TolDY = Math.Max(1, v[5]),
                    TolDCb = Math.Max(1, v[6]),
                    TolDCr = Math.Max(1, v[7]),
                    RequiredWeight = Math.Clamp(v[8], 1, 1000),
                    IsAnchor = false
                });
            }
        }

        ValidateModel(model);
        return model;
    }

    private static void ValidateModel(ColorModel model)
    {
        if (model.RegionWidth <= 0 || model.RegionHeight <= 0)
            throw new FormatException("权重字符串中的区域尺寸无效。");

        if (model.Points.Count < 3)
            throw new FormatException("权重字符串中的有效点太少。");
    }

    private static int[] ParseInts(string value, int count)
    {
        string[] parts = value.Split(',');

        if (parts.Length != count)
            throw new FormatException("权重字符串字段数量不正确。");

        var values = new int[count];
        for (int i = 0; i < count; i++)
            values[i] = ParseInt(parts[i]);

        return values;
    }

    private static int ParseInt(string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new FormatException("权重字符串包含无效数字。");

        return result;
    }

    public sealed record SearchSuccess(
        Rectangle SearchRange,
        int X,
        int Y,
        double Similarity,
        int Attempts,
        TimeSpan Elapsed,
        double MatchMilliseconds);

    public sealed record SearchFailure(
        Rectangle SearchRange,
        int BestX,
        int BestY,
        double BestSimilarity,
        int Attempts,
        TimeSpan Elapsed,
        string Reason,
        double LastMatchMilliseconds);

    public sealed record SearchOutcome(
        bool Found,
        SearchSuccess? Success,
        SearchFailure? Failure)
    {
        internal static SearchOutcome FromSuccess(SearchSuccess value)
            => new(true, value, null);

        internal static SearchOutcome FromFailure(SearchFailure value)
            => new(false, null, value);
    }

    private readonly record struct Ycc(int Y, int Cb, int Cr);

    private readonly record struct MatchPoint(
        int Dx,
        int Dy,
        int MeanDY,
        int MeanDCb,
        int MeanDCr,
        int TolDY,
        int TolDCb,
        int TolDCr,
        int RequiredWeight);

    private readonly record struct InternalMatch(
        bool Found,
        int AnchorX,
        int AnchorY,
        double Score,
        double ElapsedMilliseconds)
    {
        public static InternalMatch Empty => new(false, -1, -1, 0, 0);
    }

    private sealed class CompiledModel
    {
        public required MatchPoint[] Points { get; init; }
        public required int TotalRequiredWeight { get; init; }
        public required int StartXOffset { get; init; }
        public required int StartYOffset { get; init; }
        public required int EndXOffset { get; init; }
        public required int EndYOffset { get; init; }
    }

    private sealed class ColorModel
    {
        public int RegionWidth { get; init; }
        public int RegionHeight { get; init; }
        public int AnchorX { get; init; }
        public int AnchorY { get; init; }
        public int SampleCount { get; init; }
        public List<TrainedPoint> Points { get; } = new();
    }

    private sealed class TrainedPoint
    {
        public bool IsAnchor { get; init; }
        public int Dx { get; init; }
        public int Dy { get; init; }
        public int MeanDY { get; init; }
        public int MeanDCb { get; init; }
        public int MeanDCr { get; init; }
        public int TolDY { get; init; }
        public int TolDCb { get; init; }
        public int TolDCr { get; init; }
        public int RequiredWeight { get; init; }
    }

    /// <summary>
    /// 持久 DC + DIBSection。循环查找期间不反复创建 Bitmap/Graphics。
    /// </summary>
    private sealed class Win32Capture : IDisposable
    {
        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const uint DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;

        private readonly Rectangle _region;
        private IntPtr _memoryDc;
        private IntPtr _dib;
        private IntPtr _oldBitmap;
        private bool _disposed;

        public IntPtr Bits { get; private set; }
        public int Width => _region.Width;
        public int Height => _region.Height;
        public int Stride => _region.Width * 4;

        public Win32Capture(Rectangle region)
        {
            _region = region;
            try
            {
                CreateSurface(region.Width, region.Height);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Capture()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Win32Capture));

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException("GetDC 失败。");

            try
            {
                bool ok = BitBlt(
                    _memoryDc, 0, 0, _region.Width, _region.Height,
                    screenDc, _region.Left, _region.Top, SRCCOPY | CAPTUREBLT);

                if (!ok)
                    throw new InvalidOperationException("BitBlt 屏幕捕获失败，Win32Error=" + Marshal.GetLastWin32Error());

                if (!GdiFlush())
                    throw new InvalidOperationException("GdiFlush 屏幕捕获同步失败。");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void CreateSurface(int width, int height)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
                throw new InvalidOperationException("GetDC 失败。");

            try
            {
                _memoryDc = CreateCompatibleDC(screenDc);
                if (_memoryDc == IntPtr.Zero)
                    throw new InvalidOperationException("CreateCompatibleDC 失败。");

                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = BI_RGB,
                        biSizeImage = (uint)(width * height * 4)
                    }
                };

                _dib = CreateDIBSection(
                    screenDc,
                    ref bmi,
                    DIB_RGB_COLORS,
                    out IntPtr bits,
                    IntPtr.Zero,
                    0);

                Bits = bits;

                if (_dib == IntPtr.Zero || Bits == IntPtr.Zero)
                    throw new InvalidOperationException("CreateDIBSection 失败。");

                _oldBitmap = SelectObject(_memoryDc, _dib);
                if (_oldBitmap == IntPtr.Zero || _oldBitmap == new IntPtr(-1))
                    throw new InvalidOperationException("SelectObject 失败。");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero && _oldBitmap != new IntPtr(-1))
                SelectObject(_memoryDc, _oldBitmap);

            if (_dib != IntPtr.Zero)
                DeleteObject(_dib);

            if (_memoryDc != IntPtr.Zero)
                DeleteDC(_memoryDc);

            _oldBitmap = IntPtr.Zero;
            _dib = IntPtr.Zero;
            _memoryDc = IntPtr.Zero;
            Bits = IntPtr.Zero;

            GC.SuppressFinalize(this);
        }

        ~Win32Capture() => Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public RGBQUAD bmiColors;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint usage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(
            IntPtr hdcDest,
            int xDest,
            int yDest,
            int width,
            int height,
            IntPtr hdcSrc,
            int xSrc,
            int ySrc,
            int rop);

        [DllImport("gdi32.dll")]
        private static extern bool GdiFlush();
    }
}
