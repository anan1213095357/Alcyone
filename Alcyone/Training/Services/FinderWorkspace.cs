using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FastColorFinder.Core;

namespace FastColorFinder.Services;

public sealed class FinderWorkspace : IDisposable
{
    private readonly object _captureSync = new();
    private readonly object _searchSync = new();
    private readonly object _stateSync = new();

    private byte[]? _editorImage;
    public byte[]? SaveEditorImage() { lock (_captureSync) return _editorImage?.ToArray() ?? CapturePreviewPng(); }
    public void RestoreEditorImage(Rectangle region, byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width != region.Width || bitmap.Height != region.Height) throw new InvalidOperationException("训练图片尺寸不匹配。");
        SetCaptureRegion(region);
        lock (_captureSync) { _editorImage = png.ToArray(); }
    }
    private Win32ScreenCapture? _capture;
    private Win32ScreenCapture? _searchCapture;
    private Rectangle _captureRegion;
    private Rectangle _searchRegion;
    private List<Point> _points = new();
    private ColorModel? _model;
    private string _weightString = string.Empty;

    public Rectangle CaptureRegion { get { lock (_stateSync) return _captureRegion; } }
    public Rectangle SearchRegion { get { lock (_stateSync) return _searchRegion; } }
    public IReadOnlyList<Point> Points { get { lock (_stateSync) return _points.ToArray(); } }
    public string WeightString { get { lock (_stateSync) return _weightString; } }
    public ColorModel? Model { get { lock (_stateSync) return _model; } }

    public void SetCaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) return;

        lock (_captureSync)
        {
            _editorImage = null;
            _capture?.Dispose();
            _capture = new Win32ScreenCapture(region);
        }

        lock (_stateSync)
        {
            _captureRegion = region;
            _points.Clear();
            _model = null;
            _weightString = string.Empty;

            if (_searchRegion.Width <= 0 || _searchRegion.Height <= 0)
                _searchRegion = region;
        }

        if (SearchRegion == region)
        {
            lock (_searchSync)
            {
                _searchCapture?.Dispose();
                _searchCapture = new Win32ScreenCapture(region);
            }
        }
    }

    public void SetSearchRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0) return;

        lock (_searchSync)
        {
            _searchCapture?.Dispose();
            _searchCapture = new Win32ScreenCapture(region);
        }

        lock (_stateSync) _searchRegion = region;
    }

    public void SetPoints(IEnumerable<Point> points)
    {
        var region = CaptureRegion;
        var list = points
            .Where(p => p.X >= 0 && p.Y >= 0 && p.X < region.Width && p.Y < region.Height)
            .Take(128)
            .ToList();

        lock (_stateSync)
        {
            _points = list;
            _model = null;
            _weightString = string.Empty;
        }
    }

    public void ClearPoints()
    {
        lock (_stateSync)
        {
            _points.Clear();
            _model = null;
            _weightString = string.Empty;
        }
    }

    public void SetWeightString(string text)
    {
        text ??= string.Empty;
        ColorModel? model = null;
        if (!string.IsNullOrWhiteSpace(text))
            model = ColorModelCodec.Decode(text.Trim());

        lock (_stateSync)
        {
            _weightString = text.Trim();
            _model = model;
        }
    }

    public byte[]? CapturePreviewPng()
    {
        lock (_captureSync)
        {
            if (_capture is null)
                return null;

            var frame = _capture.Capture();

            using var ms = new MemoryStream();
            frame.Save(ms, ImageFormat.Png);

            _editorImage = ms.ToArray();
            return _editorImage.ToArray();
        }
    }

    public async Task<ColorModel> TrainAsync(
        int seconds,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        seconds = Math.Clamp(seconds, 2, 120);
        var points = Points.ToList();
        var region = CaptureRegion;
        if (region.Width <= 0 || region.Height <= 0)
            throw new InvalidOperationException("先框选捕获区域。");
        if (points.Count < 3)
            throw new InvalidOperationException("至少标注 3 个点，第一个点就是锚点。");

        var trainer = new TemporalTrainer(points);
        var sw = Stopwatch.StartNew();
        int lastPercent = -1;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_captureSync)
            {
                if (_capture is null) throw new InvalidOperationException("捕获区域不存在。");
                trainer.AddSample(_capture.Capture());
            }

            int percent = Math.Clamp((int)(sw.Elapsed.TotalSeconds / seconds * 100), 0, 100);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Invoke(percent, trainer.SampleCount);
            }

            await Task.Delay(40, cancellationToken);
        }

        var model = trainer.Build("TemporalColor", region.Width, region.Height);
        var text = ColorModelCodec.Encode(model);

        lock (_stateSync)
        {
            _model = model;
            _weightString = text;
        }

        progress?.Invoke(100, trainer.SampleCount);
        return model;
    }

    public MatchResult TestOnce(string weightString, int thresholdPercent, int step)
    {
        if (string.IsNullOrWhiteSpace(weightString))
            throw new InvalidOperationException("权重字符串为空。");

        lock (_searchSync)
        {
            if (_searchCapture is null)
                throw new InvalidOperationException("先框选搜索区域。");
            _searchCapture.Capture();
            return FastMatcher.Find(_searchCapture.Bits, _searchCapture.Width,
                _searchCapture.Height, _searchCapture.Stride, weightString, thresholdPercent, step);
        }
    }

    public (int X, int Y) ToAbsolute(MatchResult result)
    {
        var region = SearchRegion;
        return (region.X + result.AnchorX, region.Y + result.AnchorY);
    }

    public void Dispose()
    {
        lock (_captureSync)
        {
            _editorImage = null;
            _capture?.Dispose();
            _capture = null;
        }
        lock (_searchSync)
        {
            _searchCapture?.Dispose();
            _searchCapture = null;
        }
    }
}
