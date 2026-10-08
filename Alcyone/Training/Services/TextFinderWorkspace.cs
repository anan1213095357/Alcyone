using System.Drawing;
using System.Drawing.Imaging;
using FastTextFinderRuntime;

namespace FastColorFinder.Services;

public sealed class TextFinderWorkspace : IDisposable
{
    private readonly object _sync = new();
    private Bitmap? _screenshot;
    public byte[]? SaveEditorImage()
    {
        lock (_sync) { if (_screenshot is null) return null; using var stream = new MemoryStream(); _screenshot.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    }
    public void RestoreEditorImage(Rectangle region, byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        if (bitmap.Width != region.Width || bitmap.Height != region.Height) throw new InvalidOperationException("训练图片尺寸不匹配。");
        lock (_sync) { _screenshot?.Dispose(); _screenshot = new Bitmap(bitmap); _captureRegion = region; }
    }
    private TextScreenCapture.Session? _captureSession, _searchSession;
    private Rectangle _captureRegion;
    private Rectangle _searchRegion;
    private bool _customSearchRegion;
    public Rectangle CaptureRegion { get { lock (_sync) return _captureRegion; } }
    public Rectangle SearchRegion { get { lock (_sync) return _searchRegion; } }
    public bool CustomSearchRegion { get { lock (_sync) return _customSearchRegion; } }

    public byte[] Capture(Rectangle? region = null)
    {
        lock (_sync)
        {
            var target = region ?? _captureRegion;
            if (target.Width <= 0) throw new InvalidOperationException("先框选截图区域。");
            bool replacement = _captureSession is null || _captureSession.Region != target;
            var session = replacement ? new TextScreenCapture.Session(target) : _captureSession!;
            try
            {
                var screenshot = session.Capture();
                using var stream = new MemoryStream();
                screenshot.Save(stream, ImageFormat.Png);
                var png = stream.ToArray();
                // 标定/提取保留独立快照，不受后续训练或搜索更新借用帧影响。
                var saved = new Bitmap(screenshot);
                if (replacement) { _captureSession?.Dispose(); _captureSession = session; }
                _screenshot?.Dispose(); _screenshot = saved;
                _captureRegion = target;
                if (!_customSearchRegion)
                {
                    _searchRegion = target;
                    if (_searchSession is not null && _searchSession.Region != target)
                    { _searchSession.Dispose(); _searchSession = null; }
                }
                return png;
            }
            catch { if (replacement) session.Dispose(); throw; }
        }
    }

    public byte[]? CapturePreviewPng()
    {
        lock (_sync) return _captureRegion.Width <= 0 ? null : Capture();
    }

    public byte[]? SearchPreviewPng()
    {
        lock (_sync)
        {
            if (_searchRegion.Width <= 0) return null;
            var frame = SearchFrame();
            using var output = new MemoryStream(); frame.Save(output, ImageFormat.Png); return output.ToArray();
        }
    }

    public void UseCaptureForSearch()
    {
        lock (_sync)
        {
            if (_captureRegion.Width <= 0) throw new InvalidOperationException("先设置截图区域。");
            _customSearchRegion = false; _searchRegion = _captureRegion;
            if (_searchSession is not null && _searchSession.Region != _searchRegion)
            { _searchSession.Dispose(); _searchSession = null; }
        }
    }

    public Color ReadColor(int x, int y)
    {
        lock (_sync)
        {
            if (_screenshot is null || x < 0 || y < 0 || x >= _screenshot.Width || y >= _screenshot.Height)
                throw new InvalidOperationException("点位超出截图。");
            return _screenshot.GetPixel(x, y);
        }
    }

    public TextGlyphTrainer Extract(Rectangle selection, IReadOnlyList<TextColor> colors)
    {
        lock (_sync)
            return new TextGlyphTrainer(_screenshot ?? throw new InvalidOperationException("先截图。"), selection, colors);
    }

    public async Task<TextGlyph> TrainAsync(TextGlyphTrainer trainer, string text, int seconds,
        Action<int, int> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("先指定点阵代表的文字。");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        seconds = Math.Clamp(seconds, 2, 120);
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_captureRegion.Width <= 0) throw new InvalidOperationException("先截图。");
                if (_captureSession is null)
                    _captureSession = new TextScreenCapture.Session(_captureRegion);
                var frame = _captureSession.Capture();
                trainer.AddSample(frame);
            }
            progress(Math.Min(99, (int)(watch.Elapsed.TotalSeconds / seconds * 100)), trainer.SampleCount);
            await Task.Delay(40, token);
        }
        progress(100, trainer.SampleCount);
        return trainer.Build(text);
    }

    public void SetSearchRegion(Rectangle region)
    {
        lock (_sync)
        {
            // 先验证截图成功，再提交新搜索区；失败不会覆盖原状态。
            var session = new TextScreenCapture.Session(region);
            try
            {
                session.Capture();
                _searchSession?.Dispose(); _searchSession = session;
                _searchRegion = region; _customSearchRegion = true;
            }
            catch { session.Dispose(); throw; }
        }
    }

    public IReadOnlyList<TextMatch> Test(string weights, string? text, int threshold, int deviation, int step, CancellationToken token)
        => Scan(weights, text, threshold, deviation, step, token).Matches;

    public TextScanResult Scan(string weights, string? text, int threshold, int deviation, int step, CancellationToken token)
    {
        lock (_sync)
        {
            if (_searchRegion.Width <= 0) throw new InvalidOperationException("先框选搜索区域。");
            var frame = SearchFrame();
            var scan = FastTextSearch.Scan(frame, weights, text, threshold, deviation, step, cancellationToken: token);
            TextMatch Absolute(TextMatch hit) => hit with { X = hit.X >= 0 ? hit.X + _searchRegion.X : -1, Y = hit.Y >= 0 ? hit.Y + _searchRegion.Y : -1 };
            return scan with { Matches = scan.Matches.Select(Absolute).ToArray(), BestMatch = Absolute(scan.BestMatch) };
        }
    }

    public Task<TextOcrResult> RecognizeAsync(string weightString, int similarityThreshold = 85,
        int colorDeviation = 0, int step = 1, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bitmap snapshot;
            Rectangle region;
            lock (_sync)
            {
                if (_searchRegion.Width <= 0) throw new InvalidOperationException("先框选截图或搜索区域。");
                region = _searchRegion;
                snapshot = new Bitmap(SearchFrame());
            }
            // The owned snapshot lets the live preview keep capturing while matching runs.
            using (snapshot)
            {
                var result = FastTextSearch.Recognize(snapshot, weightString, similarityThreshold, colorDeviation, step,
                    cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result.Offset(region.X, region.Y);
            }
        }, cancellationToken);

    // 调用方持有 _sync，借用帧只在这一锁的范围内读取。
    private Bitmap SearchFrame()
    {
        if (_searchSession is null || _searchSession.Region != _searchRegion)
        {
            var session = new TextScreenCapture.Session(_searchRegion);
            _searchSession?.Dispose(); _searchSession = session;
        }
        return _searchSession.Capture();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _screenshot?.Dispose(); _screenshot = null;
            _captureSession?.Dispose(); _captureSession = null;
            _searchSession?.Dispose(); _searchSession = null;
        }
    }
}
