using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FastTextFinderRuntime;

public sealed record TextColor(int R, int G, int B, int Deviation)
{
    public bool Contains(int r, int g, int b, int extra = 0) =>
        Math.Abs(r - R) <= Math.Min(255, Deviation + extra) &&
        Math.Abs(g - G) <= Math.Min(255, Deviation + extra) &&
        Math.Abs(b - B) <= Math.Min(255, Deviation + extra);
}

public sealed class TextGlyph
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int SampleCount { get; set; }
    public List<TextColor> Colors { get; set; } = new();
    public bool[] Mask { get; set; } = Array.Empty<bool>();
    public int[] Weights { get; set; } = Array.Empty<int>();
}

public sealed class TextDictionary
{
    public string Name { get; set; } = "默认字库";
    public List<TextGlyph> Glyphs { get; set; } = new();
}

public static class TextModelCodec
{
    public static string Encode(TextDictionary model)
    {
        Validate(model);
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.SmallestSize, true))
            JsonSerializer.Serialize(zip, model);
        return "FTF1:" + Convert.ToBase64String(output.ToArray());
    }

    public static TextDictionary Decode(string text)
    {
        try
        {
            text = text.Trim();
            if (!text.StartsWith("FTF1:", StringComparison.Ordinal) || text.Length > 4_000_000)
                throw new FormatException();
            using var input = new MemoryStream(Convert.FromBase64String(text[5..]));
            using var zip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = zip.Read(buffer)) > 0)
            {
                if (output.Length + count > 16_000_000) throw new FormatException();
                output.Write(buffer, 0, count);
            }
            var model = JsonSerializer.Deserialize<TextDictionary>(output.ToArray()) ?? throw new FormatException();
            Validate(model);
            return model;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException or ArgumentException or NullReferenceException)
        {
            throw new FormatException("无效的找字权重字符串，请使用 FTF1 字库。", ex);
        }
    }

    public static void Validate(TextDictionary model)
    {
        if (string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 128 || model.Glyphs is null || model.Glyphs.Count > 256)
            throw new FormatException("字库名称不能为空，且最多包含 256 个模板。");
        foreach (var glyph in model.Glyphs)
        {
            if (glyph is null || string.IsNullOrWhiteSpace(glyph.Text) || glyph.Text.Length > 128 ||
                glyph.Width <= 0 || glyph.Height <= 0 || (long)glyph.Width * glyph.Height > 4096 ||
                glyph.Mask is null || glyph.Weights is null || glyph.Mask.Length != glyph.Width * glyph.Height ||
                glyph.Weights.Length != glyph.Mask.Length || glyph.Mask.Count(x => x) < 3 ||
                glyph.Weights.Any(x => x is < 1 or > 1000) || glyph.SampleCount < 1 ||
                glyph.Colors is null || glyph.Colors.Count is < 1 or > 32 ||
                glyph.Colors.Any(c => c is null || c.R is < 0 or > 255 || c.G is < 0 or > 255 || c.B is < 0 or > 255 || c.Deviation is < 0 or > 255))
                throw new FormatException("字库点阵、颜色或权重数据不完整。");
        }
    }
}

public static class TextDictionaryFile
{
    private sealed class Document
    {
        public string Format { get; set; } = "FastTextFinder.Dictionary";
        public int Version { get; set; } = 1;
        public TextDictionary Dictionary { get; set; } = new();
    }
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Write(TextDictionary dictionary)
    {
        TextModelCodec.Validate(dictionary);
        return JsonSerializer.Serialize(new Document { Dictionary = dictionary }, Options);
    }
    public static TextDictionary Read(string text)
    {
        text = text.Trim().TrimStart('\uFEFF').Trim();
        if (text.StartsWith("FTF1:", StringComparison.Ordinal)) return TextModelCodec.Decode(text);
        try
        {
            using var parsed = JsonDocument.Parse(text);
            if (!parsed.RootElement.TryGetProperty("Format", out var format) || format.GetString() != "FastTextFinder.Dictionary" ||
                !parsed.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 1)
                throw new FormatException();
            var document = JsonSerializer.Deserialize<Document>(text) ?? throw new FormatException();
            if (document.Format != "FastTextFinder.Dictionary" || document.Version != 1 || document.Dictionary is null)
                throw new FormatException();
            TextModelCodec.Validate(document.Dictionary);
            return document.Dictionary;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NullReferenceException or InvalidOperationException)
        { throw new FormatException("TXT 字库格式无效，请打开导出的找字字库文件或 FTF1 权重文件。", ex); }
    }
    public static TextDictionary Load(string path) => Read(File.ReadAllText(path, System.Text.Encoding.UTF8));
    public static void Save(string path, TextDictionary dictionary) => File.WriteAllText(path, Write(dictionary), new System.Text.UTF8Encoding(false));
    public static TextGlyph Clone(TextGlyph glyph) => JsonSerializer.Deserialize<TextGlyph>(JsonSerializer.Serialize(glyph))!;
}

// 每次调用获取当前字库快照。切换字库不会修改已开始的查找。
public sealed class TextDictionaryStore
{
    private readonly object _sync = new();
    private string _weights = TextModelCodec.Encode(new TextDictionary());
    public string WeightString { get { lock (_sync) return _weights; } }
    public string CurrentName => TextModelCodec.Decode(WeightString).Name;
    public void SetDictionary(string weightString)
    {
        var normalized = TextModelCodec.Encode(TextModelCodec.Decode(weightString));
        lock (_sync) _weights = normalized;
    }
    public void LoadFromFile(string path) => SetDictionary(TextModelCodec.Encode(TextDictionaryFile.Load(path)));
    public void SaveToFile(string path) => TextDictionaryFile.Save(path, TextModelCodec.Decode(WeightString));
}

// 将 Bitmap 一次读入像素缓冲，扫描过程中不调用 GetPixel。
public sealed class TextPixels
{
    private readonly byte[] _data;
    public int Width { get; }
    public int Height { get; }
    public TextPixels(Bitmap bitmap)
    {
        Width = bitmap.Width;
        Height = bitmap.Height;
        if (bitmap.PixelFormat == PixelFormat.Format32bppRgb)
        {
            _data = ReadPixels(bitmap, Width, Height);
            return;
        }
        using var copy = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy)) graphics.DrawImageUnscaled(bitmap, 0, 0);
        _data = ReadPixels(copy, Width, Height);
    }
    private static byte[] ReadPixels(Bitmap bitmap, int width, int height)
    {
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var data = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), data, y * width * 4, width * 4);
            return data;
        }
        finally { bitmap.UnlockBits(bits); }
    }
    public Color ColorAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x));
        int i = (y * Width + x) * 4;
        return Color.FromArgb(_data[i + 2], _data[i + 1], _data[i]);
    }
    public bool[] Classify(IReadOnlyList<TextColor> colors, int extra = 0)
    {
        var mask = new bool[Width * Height];
        for (int i = 0; i < mask.Length; i++)
        {
            for (int colorIndex = 0; colorIndex < colors.Count; colorIndex++)
                if (colors[colorIndex].Contains(_data[i * 4 + 2], _data[i * 4 + 1], _data[i * 4], extra))
                { mask[i] = true; break; }
        }
        return mask;
    }
}

public sealed class TextGlyphTrainer
{
    public Rectangle Bounds { get; }
    public bool[] Mask { get; }
    public IReadOnlyList<TextColor> Colors { get; }
    private readonly int[] _matches;
    public int SampleCount { get; private set; }

    private TextGlyphTrainer(Rectangle bounds, bool[] mask, IReadOnlyList<TextColor> colors)
    { Bounds = bounds; Mask = mask.ToArray(); Colors = colors.ToArray(); _matches = new int[mask.Length]; }
    public TextGlyphTrainer CreateTrainingSession() => new(Bounds, Mask, Colors);

    public TextGlyphTrainer(Bitmap screenshot, Rectangle selection, IReadOnlyList<TextColor> colors)
    {
        if (colors.Count is < 1 or > 32) throw new InvalidOperationException("先在字的笔画上标定颜色点。");
        if (selection.Width <= 0 || selection.Height <= 0 || !new Rectangle(0, 0, screenshot.Width, screenshot.Height).Contains(selection))
            throw new InvalidOperationException("先框选截图中的单字或文字模板。");
        Colors = colors.ToArray();
        var pixels = new TextPixels(screenshot);
        var classified = pixels.Classify(Colors);
        int left = selection.Right, top = selection.Bottom, right = -1, bottom = -1;
        for (int y = selection.Top; y < selection.Bottom; y++)
        for (int x = selection.Left; x < selection.Right; x++)
            if (classified[y * pixels.Width + x])
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < 0) throw new InvalidOperationException("未提取到笔画，请调整标定点或色偏。");
        // 保留一像素外边框，横线、竖线等实心笔画也能用背景约束区分。
        Bounds = Rectangle.FromLTRB(Math.Max(0, left - 1), Math.Max(0, top - 1),
            Math.Min(pixels.Width, right + 2), Math.Min(pixels.Height, bottom + 2));
        if ((long)Bounds.Width * Bounds.Height > 4096) throw new InvalidOperationException("点阵最多 4096 像素，请缩小框选范围。");
        Mask = new bool[Bounds.Width * Bounds.Height];
        for (int y = 0; y < Bounds.Height; y++)
        for (int x = 0; x < Bounds.Width; x++) Mask[y * Bounds.Width + x] = classified[(Bounds.Top + y) * pixels.Width + Bounds.Left + x];
        if (Mask.Count(x => x) < 3 || Mask.All(x => x)) throw new InvalidOperationException("点阵需要至少 3 个笔画像素，并包含背景；请检查色偏和框选范围。");
        _matches = new int[Mask.Length];
    }

    public void AddSample(Bitmap screenshot)
    {
        if (!new Rectangle(0, 0, screenshot.Width, screenshot.Height).Contains(Bounds)) throw new InvalidOperationException("训练截图尺寸变化，请重新提取。");
        using var crop = screenshot.Clone(Bounds, PixelFormat.Format32bppArgb);
        var current = new TextPixels(crop).Classify(Colors);
        for (int i = 0; i < Mask.Length; i++) if (current[i] == Mask[i]) _matches[i]++;
        SampleCount++;
    }

    public TextGlyph Build(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > 128) throw new InvalidOperationException("请指定点阵代表的文字（最多 128 字）。");
        if (SampleCount == 0) throw new InvalidOperationException("尚未采样。");
        return new TextGlyph { Text = text.Trim(), Width = Bounds.Width, Height = Bounds.Height,
            Colors = Colors.ToList(), Mask = Mask.ToArray(), SampleCount = SampleCount,
            Weights = _matches.Select(n => Math.Max(25, (int)Math.Round(1000.0 * n / SampleCount))).ToArray() };
    }
}

public sealed record TextMatch(bool Found, string Text, int X, int Y, int Width, int Height, double Similarity, double MatchMilliseconds)
{
    public int Attempts { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string Reason { get; init; } = "";
    public int BestX => X;
    public int BestY => Y;
    public double BestSimilarity => Similarity;
}
public sealed record TextScanResult(IReadOnlyList<TextMatch> Matches, TextMatch BestMatch, string Reason, double MatchMilliseconds);
public sealed record TextOcrLine(string Text, Rectangle Bounds, IReadOnlyList<TextMatch> Matches);
public sealed record TextOcrResult(string Text, IReadOnlyList<TextOcrLine> Lines, IReadOnlyList<TextMatch> Matches,
    double MatchMilliseconds, bool Truncated)
{
    public TextOcrResult Offset(int dx, int dy)
    {
        TextMatch Move(TextMatch hit) => hit with { X = checked(hit.X + dx), Y = checked(hit.Y + dy) };
        var lines = Lines.Select(line => line with
        {
            Bounds = new Rectangle(checked(line.Bounds.X + dx), checked(line.Bounds.Y + dy), line.Bounds.Width, line.Bounds.Height),
            Matches = Array.AsReadOnly(line.Matches.Select(Move).ToArray())
        }).ToArray();
        return this with { Lines = Array.AsReadOnly(lines), Matches = Array.AsReadOnly(Matches.Select(Move).ToArray()) };
    }
}

// 找字独立捕获：屏幕 DC 在同一调用线程内获取、使用和释放。
// 公共 Capture 返回独立 Bitmap，内部 Session 只复用目标 MemoryDC/DIB。
public static class TextScreenCapture
{
    public static Rectangle DesktopBounds => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    private static void ValidateRegion(Rectangle region)
    {
        var desktop = DesktopBounds;
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 64_000_000 ||
            region.X < desktop.X || region.Y < desktop.Y ||
            (long)region.X + region.Width > (long)desktop.X + desktop.Width ||
            (long)region.Y + region.Height > (long)desktop.Y + desktop.Height)
            throw new ArgumentOutOfRangeException(nameof(region), $"找字截图区域超出当前桌面：区域 {region}，桌面 {desktop}。请重新框选。");
    }

    public static Bitmap Capture(Rectangle region)
    {
        ValidateRegion(region);
        // 屏幕 BitBlt 的 alpha 字节不可靠，使用 RGB，避免保存 PNG 后截图透明。
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var source = GetDC(IntPtr.Zero);
            if (source == IntPtr.Zero) throw CaptureError("GetDC", region, Marshal.GetLastWin32Error());
            try
            {
                var destination = graphics.GetHdc();
                try
                {
                    const int sourceCopy = 0x00CC0020;
                    const int captureBlt = 0x40000000;
                    if (!BitBlt(destination, 0, 0, region.Width, region.Height, source, region.X, region.Y, sourceCopy | captureBlt) &&
                        !BitBlt(destination, 0, 0, region.Width, region.Height, source, region.X, region.Y, sourceCopy))
                        throw CaptureError("BitBlt", region, Marshal.GetLastWin32Error());
                }
                finally { graphics.ReleaseHdc(destination); }
            }
            finally { ReleaseDC(IntPtr.Zero, source); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    // 内部循环复用目标 DIB；屏幕 DC 始终在当前调用线程内获取、使用并释放。
    // Capture 返回借用帧，所有者须串行完成像素读取/编码后再捕获下一帧。
    internal sealed class Session : IDisposable
    {
        private IntPtr _memoryDc, _dib, _oldBitmap;
        private Bitmap? _frame;
        private bool _disposed;
        public Rectangle Region { get; }

        public Session(Rectangle region)
        {
            ValidateRegion(region);
            Region = region;
            var source = GetDC(IntPtr.Zero);
            if (source == IntPtr.Zero) throw CaptureError("GetDC", region, Marshal.GetLastWin32Error());
            try
            {
                _memoryDc = CreateCompatibleDC(source);
                if (_memoryDc == IntPtr.Zero) throw CaptureError("CreateCompatibleDC", region, Marshal.GetLastWin32Error());
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = region.Width, Height = -region.Height,
                        Planes = 1, BitCount = 32, ImageSize = checked((uint)((long)region.Width * region.Height * 4))
                    }
                };
                _dib = CreateDIBSection(source, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero || bits == IntPtr.Zero) throw CaptureError("CreateDIBSection", region, Marshal.GetLastWin32Error());
                _oldBitmap = SelectObject(_memoryDc, _dib);
                if (_oldBitmap == IntPtr.Zero || _oldBitmap == new IntPtr(-1)) throw CaptureError("SelectObject", region, Marshal.GetLastWin32Error());
                _frame = new Bitmap(region.Width, region.Height, checked(region.Width * 4), PixelFormat.Format32bppRgb, bits);
            }
            catch { Dispose(); throw; }
            finally { ReleaseDC(IntPtr.Zero, source); }
        }

        public Bitmap Capture()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Session));
            ValidateRegion(Region);
            var source = GetDC(IntPtr.Zero);
            if (source == IntPtr.Zero) throw CaptureError("GetDC", Region, Marshal.GetLastWin32Error());
            try
            {
                const int sourceCopy = 0x00CC0020, captureBlt = 0x40000000;
                if (!BitBlt(_memoryDc, 0, 0, Region.Width, Region.Height, source, Region.X, Region.Y, sourceCopy | captureBlt) &&
                    !BitBlt(_memoryDc, 0, 0, Region.Width, Region.Height, source, Region.X, Region.Y, sourceCopy))
                    throw CaptureError("BitBlt", Region, Marshal.GetLastWin32Error());
                // GDI 完成写入后才允许 CPU 或 PNG 编码器读取 DIB 内存。
                if (!GdiFlush()) throw new InvalidOperationException($"找字屏幕捕获同步失败（GdiFlush，区域 {Region}）。请重新框选截图区域。");
                return _frame!;
            }
            finally { ReleaseDC(IntPtr.Zero, source); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _frame?.Dispose(); _frame = null;
            if (_memoryDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero && _oldBitmap != new IntPtr(-1))
                SelectObject(_memoryDc, _oldBitmap);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
            _memoryDc = _dib = _oldBitmap = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        ~Session() => Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, ImageSize;
            public int XPixelsPerMeter, YPixelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }

        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr source);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    }

    private static InvalidOperationException CaptureError(string operation, Rectangle region, int error) =>
        new($"找字屏幕捕获失败（{operation}，Win32 错误 {error}，X {region.X} Y {region.Y}，{region.Width}×{region.Height}）。请确认桌面未锁定，重新框选截图区域。");

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
}

public static class FastTextSearch
{
    private readonly record struct CompiledPoint(int X, int Y, bool Foreground, int Weight, double Quotient);

    private sealed class CompiledGlyph
    {
        public readonly bool Enabled;
        public readonly string Text;
        public readonly int Width, Height, InkLeft, InkTop, InkRight, InkBottom, ForegroundWeight, BackgroundWeight;
        public readonly TextColor[] Colors;
        public readonly CompiledPoint[] Points;
        private readonly int[] _backgroundPrefix;

        public CompiledGlyph(TextGlyph glyph)
        {
            Enabled = glyph.Enabled; Text = glyph.Text; Width = glyph.Width; Height = glyph.Height;
            Colors = glyph.Colors.ToArray();
            int foregroundWeight = 0, backgroundWeight = 0;
            int inkLeft = Width, inkTop = Height, inkRight = -1, inkBottom = -1;
            for (int i = 0; i < glyph.Mask.Length; i++)
                if (glyph.Mask[i])
                {
                    foregroundWeight += glyph.Weights[i];
                    inkLeft = Math.Min(inkLeft, i % Width); inkRight = Math.Max(inkRight, i % Width);
                    inkTop = Math.Min(inkTop, i / Width); inkBottom = Math.Max(inkBottom, i / Width);
                }
                else backgroundWeight += glyph.Weights[i];
            ForegroundWeight = foregroundWeight; BackgroundWeight = backgroundWeight;
            InkLeft = inkLeft; InkTop = inkTop; InkRight = inkRight; InkBottom = inkBottom;
            _backgroundPrefix = new int[(Width + 1) * (Height + 1)];
            for (int row = 0; row < Height; row++)
            {
                int rowSum = 0;
                for (int col = 0; col < Width; col++)
                {
                    int i = row * Width + col;
                    if (!glyph.Mask[i]) rowSum += glyph.Weights[i];
                    _backgroundPrefix[(row + 1) * (Width + 1) + col + 1] =
                        _backgroundPrefix[row * (Width + 1) + col + 1] + rowSum;
                }
            }
            // OrderBy is stable: equal normalized weights keep the original pixel order.
            var ordered = Enumerable.Range(0, glyph.Mask.Length)
                .OrderByDescending(i => glyph.Weights[i] / (double)(glyph.Mask[i] ? foregroundWeight : Math.Max(1, backgroundWeight))).ToArray();
            Points = new CompiledPoint[ordered.Length];
            for (int j = 0; j < ordered.Length; j++)
            {
                int i = ordered[j];
                Points[j] = new CompiledPoint(i % Width, i / Width, glyph.Mask[i], glyph.Weights[i],
                    glyph.Weights[i] / (double)(glyph.Mask[i] ? foregroundWeight : Math.Max(1, backgroundWeight)));
            }
        }

        public int AvailableBackground(int x1, int y1, int x2, int y2) =>
            _backgroundPrefix[y2 * (Width + 1) + x2] - _backgroundPrefix[y1 * (Width + 1) + x2] -
            _backgroundPrefix[y2 * (Width + 1) + x1] + _backgroundPrefix[y1 * (Width + 1) + x1];
    }

    private sealed class CompiledDictionary
    {
        public readonly CompiledGlyph[] Glyphs;
        public readonly int PointCount;
        public CompiledDictionary(TextDictionary model)
        {
            Glyphs = model.Glyphs.Select(g => new CompiledGlyph(g)).ToArray();
            PointCount = Glyphs.Sum(g => g.Points.Length);
        }
    }

    private sealed class ColorArrayComparer : IEqualityComparer<TextColor[]>
    {
        public static readonly ColorArrayComparer Instance = new();
        public bool Equals(TextColor[]? a, TextColor[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        public int GetHashCode(TextColor[] colors)
        {
            var hash = new HashCode();
            for (int i = 0; i < colors.Length; i++) hash.Add(colors[i]);
            return hash.ToHashCode();
        }
    }

    private sealed record CachedDictionary(string Weights, CompiledDictionary Dictionary);
    private const int MaxCompiledDictionaries = 8, MaxCachedPoints = 131_072;
    private const int MaxClassifiedMasks = 8, MaxClassifiedPixels = 16_000_000;
    private static readonly object CompiledSync = new();
    private static readonly Dictionary<string, LinkedListNode<CachedDictionary>> CompiledCache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CachedDictionary> CompiledRecency = new();
    private static int CachedPointCount;

    private static CompiledDictionary GetCompiled(string weightString)
    {
        // Invalid strings still use the codec's validation and exception contract.
        if (weightString is not null)
            lock (CompiledSync)
                if (CompiledCache.TryGetValue(weightString, out var cached))
                {
                    CompiledRecency.Remove(cached); CompiledRecency.AddFirst(cached);
                    return cached.Value.Dictionary;
                }
        var compiled = new CompiledDictionary(TextModelCodec.Decode(weightString!));
        if (compiled.PointCount > MaxCachedPoints) return compiled;
        lock (CompiledSync)
        {
            if (CompiledCache.TryGetValue(weightString!, out var existing))
            {
                CompiledRecency.Remove(existing); CompiledRecency.AddFirst(existing);
                return existing.Value.Dictionary;
            }
            while (CompiledCache.Count >= MaxCompiledDictionaries || CachedPointCount + compiled.PointCount > MaxCachedPoints)
            {
                var oldest = CompiledRecency.Last!;
                CompiledCache.Remove(oldest.Value.Weights); CompiledRecency.RemoveLast();
                CachedPointCount -= oldest.Value.Dictionary.PointCount;
            }
            var node = CompiledRecency.AddFirst(new CachedDictionary(weightString!, compiled));
            CompiledCache.Add(weightString!, node); CachedPointCount += compiled.PointCount;
        }
        return compiled;
    }

    private static void ValidateSearchArguments(int similarityThreshold, int colorDeviation, int step, int maxResults)
    {
        if (similarityThreshold is < 1 or > 100 || colorDeviation is < 0 or > 255 || step is < 1 or > 8 || maxResults is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "阈值 1～100，色偏 0～255，步长 1～8，结果上限 1～10000。");
    }

    public static TextMatch Find(Bitmap frame, string weightString, string? text = null, int similarityThreshold = 85, int colorDeviation = 0, int step = 1)
    {
        var scan = Scan(frame, weightString, text, similarityThreshold, colorDeviation, step, 1);
        return scan.Matches.FirstOrDefault() ?? scan.BestMatch with { Found = false, Reason = scan.Reason };
    }

    public static IReadOnlyList<TextMatch> FindAll(Bitmap frame, string weightString, string? text = null,
        int similarityThreshold = 85, int colorDeviation = 0, int step = 1, int maxResults = 256, CancellationToken cancellationToken = default)
        => Scan(frame, weightString, text, similarityThreshold, colorDeviation, step, maxResults, cancellationToken).Matches;

    /// <summary>用当前点阵字库识别全部已知文字，按行从左到右组合；不猜测字库外的字形。坐标为图内坐标。</summary>
    public static TextOcrResult Recognize(Bitmap frame, string weightString, int similarityThreshold = 85,
        int colorDeviation = 0, int step = 1, int maxResults = 512, CancellationToken cancellationToken = default)
    {
        var scan = Scan(frame, weightString, null, similarityThreshold, colorDeviation, step, maxResults, cancellationToken);
        var rows = new List<(Rectangle Bounds, List<TextMatch> Hits)>();
        foreach (var hit in scan.Matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bounds = new Rectangle(hit.X, hit.Y, hit.Width, hit.Height);
            int rowIndex = -1;
            double bestOverlap = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i].Bounds;
                int overlap = Math.Min(row.Bottom, bounds.Bottom) - Math.Max(row.Top, bounds.Top);
                double ratio = overlap / (double)Math.Max(1, Math.Min(row.Height, bounds.Height));
                if (ratio >= 0.5 && ratio > bestOverlap) { rowIndex = i; bestOverlap = ratio; }
            }
            if (rowIndex < 0) rows.Add((bounds, new List<TextMatch> { hit }));
            else
            {
                var row = rows[rowIndex];
                row.Hits.Add(hit);
                rows[rowIndex] = (Rectangle.Union(row.Bounds, bounds), row.Hits);
            }
        }
        var lines = rows.OrderBy(row => row.Bounds.Top).ThenBy(row => row.Bounds.Left).Select(row =>
        {
            var hits = row.Hits.OrderBy(hit => hit.X).ThenBy(hit => hit.Y).ToArray();
            return new TextOcrLine(string.Concat(hits.Select(hit => hit.Text)), row.Bounds, Array.AsReadOnly(hits));
        }).ToArray();
        return new TextOcrResult(string.Join(Environment.NewLine, lines.Select(line => line.Text)), Array.AsReadOnly(lines),
            Array.AsReadOnly(lines.SelectMany(line => line.Matches).ToArray()), scan.MatchMilliseconds, scan.Matches.Count >= maxResults);
    }

    public static TextScanResult Scan(Bitmap frame, string weightString, string? text = null,
        int similarityThreshold = 85, int colorDeviation = 0, int step = 1, int maxResults = 256, CancellationToken cancellationToken = default)
    {
        ValidateSearchArguments(similarityThreshold, colorDeviation, step, maxResults);
        return ScanCompiled(frame, GetCompiled(weightString), text, similarityThreshold, colorDeviation, step, maxResults, cancellationToken);
    }

    private static TextScanResult ScanCompiled(Bitmap frame, CompiledDictionary model, string? text,
        int similarityThreshold, int colorDeviation, int step, int maxResults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var glyphs = model.Glyphs.Where(g => g.Enabled && (string.IsNullOrEmpty(text) || g.Text == text)).ToArray();
        if (glyphs.Length == 0) throw new InvalidOperationException("当前字库没有启用的指定文字模板。");
        var watch = Stopwatch.StartNew();
        var pixels = new TextPixels(frame);
        var candidates = new List<TextMatch>();
        var best = new TextMatch(false, text ?? "", -1, -1, 0, 0, 0, 0);
        double bestScore = -1;
        int frameWidth = frame.Width, frameHeight = frame.Height;
        var classifiedMasks = new Dictionary<TextColor[], bool[]>(ColorArrayComparer.Instance);
        int classifiedPixelCount = 0;
        foreach (var glyph in glyphs)
        {
            if (!classifiedMasks.TryGetValue(glyph.Colors, out var mask))
            {
                mask = pixels.Classify(glyph.Colors, colorDeviation);
                if (classifiedMasks.Count < MaxClassifiedMasks && mask.Length <= MaxClassifiedPixels - classifiedPixelCount)
                {
                    classifiedMasks.Add(glyph.Colors, mask); classifiedPixelCount += mask.Length;
                }
            }
            // 搜索区边缘只允许裁掉背景，笔画必须完整位于搜索区内。
            for (int y = -glyph.InkTop; y <= frameHeight - 1 - glyph.InkBottom; y += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = -glyph.InkLeft; x <= frameWidth - 1 - glyph.InkRight; x += step)
                {
                    int availableBackground = glyph.AvailableBackground(Math.Max(0, -x), Math.Max(0, -y),
                        Math.Min(glyph.Width, frameWidth - x), Math.Min(glyph.Height, frameHeight - y));
                    double lost = 0;
                    bool pruned = false;
                    double pruneLimit = 100 - Math.Min(similarityThreshold, Math.Max(0, bestScore)) + 0.000001;
                    for (int j = 0; j < glyph.Points.Length; j++)
                    {
                        var point = glyph.Points[j];
                        int px = x + point.X, py = y + point.Y;
                        if (px < 0 || py < 0 || px >= frameWidth || py >= frameHeight) continue;
                        if (mask[py * frameWidth + px] != point.Foreground)
                            lost += (point.Foreground || availableBackground == glyph.BackgroundWeight
                                ? point.Quotient : point.Weight / (double)Math.Max(1, availableBackground)) *
                                (availableBackground == 0 ? 100 : 50);
                        // 只有不能通过阈值、也不能改善最佳结果时才提前结束。
                        if (lost > pruneLimit) { pruned = true; break; }
                    }
                    if (pruned) continue;
                    double score = Math.Clamp(100 - lost, 0, 100);
                    bool found = score + 0.000001 >= similarityThreshold;
                    if (!found && score <= bestScore) continue;
                    var hit = new TextMatch(found, glyph.Text, Math.Max(0, x), Math.Max(0, y),
                        Math.Min(frameWidth, x + glyph.Width) - Math.Max(0, x), Math.Min(frameHeight, y + glyph.Height) - Math.Max(0, y), score, 0);
                    if (score > bestScore) { bestScore = score; best = hit; }
                    if (!hit.Found) continue;
                    int existing = FindOverlap(candidates, hit);
                    if (existing >= 0) { if (candidates[existing].Similarity < score) candidates[existing] = hit; }
                    else if (candidates.Count < maxResults) candidates.Add(hit);
                }
            }
        }
        double elapsed = watch.Elapsed.TotalMilliseconds;
        var results = candidates.OrderBy(c => c.Y).ThenBy(c => c.X).Select(c => c with { MatchMilliseconds = elapsed }).ToArray();
        string reason = results.Length > 0 ? "找到目标" : bestScore < 0 ? "搜索区小于模板笔画范围，或文字被搜索区裁切" : "最佳相似度未达到阈值；请检查字体、字号、色偏及搜索区是否完整包含文字";
        return new TextScanResult(results, best with { MatchMilliseconds = elapsed }, reason, elapsed);
    }

    private static int FindOverlap(List<TextMatch> candidates, TextMatch hit)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (Overlaps(candidates[i], hit)) return i;
        return -1;
    }

    private static bool Overlaps(TextMatch a, TextMatch b)
    {
        int w = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        int h = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return w * h > Math.Min(a.Width * a.Height, b.Width * b.Height) * 0.3;
    }

    // 返回屏幕绝对坐标；Bitmap 重载返回图像内坐标。
    public static async Task<TextMatch> FindAsync(Rectangle region, string weightString, string? text = null,
        int similarityThreshold = 85, int colorDeviation = 0, double loopSeconds = 3, int intervalMilliseconds = 80,
        Action<TextMatch>? onSuccess = null, Action<TextMatch>? onFailure = null, CancellationToken cancellationToken = default, int step = 1)
    {
        if (region.Width <= 0 || region.Height <= 0 || !double.IsFinite(loopSeconds) || loopSeconds is < 0 or > 600 || intervalMilliseconds is < 0 or > 60000)
            throw new ArgumentOutOfRangeException(nameof(region));
        var model = GetCompiled(weightString);
        var watch = Stopwatch.StartNew();
        TextMatch result;
        TextMatch? bestFailure = null;
        int attempts = 0;
        TextScreenCapture.Session? capture = null;
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await Task.Run(() =>
                {
                    capture ??= new TextScreenCapture.Session(region);
                    var bitmap = capture.Capture();
                    ValidateSearchArguments(similarityThreshold, colorDeviation, step, 1);
                    var scan = ScanCompiled(bitmap, model, text, similarityThreshold, colorDeviation, step, 1, cancellationToken);
                    return scan.Matches.FirstOrDefault() ?? scan.BestMatch with { Found = false, Reason = scan.Reason };
                }, cancellationToken);
                attempts++;
                result = result with { X = result.X >= 0 ? result.X + region.X : -1, Y = result.Y >= 0 ? result.Y + region.Y : -1,
                    Attempts = attempts, Elapsed = watch.Elapsed };
                if (result.Found)
                {
                    onSuccess?.Invoke(result);
                    return result;
                }
                if (bestFailure is null || result.Similarity > bestFailure.Similarity) bestFailure = result;
                if (watch.Elapsed.TotalSeconds >= loopSeconds) break;
                int delay = (int)Math.Min(Math.Max(1, intervalMilliseconds), Math.Ceiling((loopSeconds - watch.Elapsed.TotalSeconds) * 1000));
                if (delay > 0) await Task.Delay(delay, cancellationToken);
            } while (watch.Elapsed.TotalSeconds < loopSeconds);
            result = (bestFailure ?? result) with { Attempts = attempts, Elapsed = watch.Elapsed };
            onFailure?.Invoke(result);
            return result;
        }
        finally { capture?.Dispose(); }
    }
}
