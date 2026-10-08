using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FastTextFinderRuntime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace StateMachine.Components.Training;

public partial class TextTrainer
{
    [Parameter] public bool Active { get; set; } = true;
    private sealed class LibraryEntry { public TextDictionary Dictionary { get; set; } = new(); public bool Dirty { get; set; } }
    private readonly List<LibraryEntry> _libraries = new() { new() };
    private int _activeLibrary;
    private TextDictionary _dictionary => _libraries[_activeLibrary].Dictionary;
    private DotNetObjectReference<TextTrainer>? _reference;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _trainingCts;
    private readonly List<Seed> _seeds = new();
    private TextGlyphTrainer? _preview;
    private Rectangle _selection;
    private IReadOnlyList<TextMatch> _results = Array.Empty<TextMatch>();
    private string _weights = TextModelCodec.Encode(new TextDictionary()), _label = "", _query = "", _status = "就绪", _trainStatus = "等待提取点阵", _testStatus = "等待测试", _glyphFilter = "";
    private string? _searchImage;
    private int _selectedSeed = -1, _defaultDeviation = 20, _zoom = 2, _seconds = 5, _threshold = 85, _extraDeviation, _step = 1, _progress, _interval = 80;
    private double _loopSeconds = 3;
    private bool _busy, _training, _error, _selectTool = true, _disposed, _showCode;
    private TextGlyph? _editing;
    private int _editingIndex, _editPixel;
    private string _editError = "";
    private string _liveOcrWeights = "", _liveOcrStatus = "训练或打开字库后自动识别";
    private TextOcrResult? _liveOcrResult;
    private Task? _liveOcrLoop;
    private CancellationTokenSource? _liveOcrRound;
    private int _liveOcrRevision;
    private sealed record LiveOcrSnapshot(string Weights, int Library, int Threshold, int Deviation, int Step, Rectangle Region, int Revision);

    protected override void OnParametersSet()
    {
        if (!Active) { _liveOcrRevision++; _liveOcrRound?.Cancel(); }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        SyncWeights(); _reference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("ftfCanvas.init", "textAnnotationCanvas", _reference, $"/api/training/{Session.Id}/text");
        if (Initial.Settings.TrainingEditor?.ImagePng is {} png)
            await JS.InvokeVoidAsync("ftfCanvas.setImage", ImageUrl(png));
        await UpdateCanvasAsync();
        _liveOcrLoop = LiveOcrLoopAsync();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _liveOcrRevision++; _liveOcrRound?.Cancel();
        try { await UpdateCanvasAsync(); await action(); }
        catch (OperationCanceledException) { _trainStatus = "已取消训练"; Status("已取消，原字库保留。"); }
        catch (Exception ex) { Status(ex.Message, true); }
        finally { _busy = false; if (!_disposed) await UpdateCanvasAsync(); }
    }
    private Task SelectCaptureAsync() => RunAsync(async () =>
    {
        var region = await RegionSelector.SelectAsync();
        if (region is null) { Status("已取消框选。"); return; }
        await ShowScreenshotAsync(TextWorkspace.Capture(region));
    });
    private Task RecaptureAsync() => RunAsync(async () => await ShowScreenshotAsync(TextWorkspace.Capture()));
    private async Task ShowScreenshotAsync(byte[] png)
    {
        _seeds.Clear(); _selection = Rectangle.Empty; _selectedSeed = -1; Invalidate(); _selectTool = true; _searchImage = null;
        await JS.InvokeVoidAsync("ftfCanvas.setImage", ImageUrl(png));
        Status("已进入框选文字流程：在实时画面上拖出文字范围，再标定笔画颜色。");
    }
    private Task SelectSearchAsync() => RunAsync(async () =>
    {
        var region = await RegionSelector.SelectAsync();
        if (region is null) { Status("已取消框选。"); return; }
        TextWorkspace.SetSearchRegion(region.Value); ResetResults(); RefreshSearchImage(); Status("搜索区已更新，请检查预览是否完整包含文字。");
    });
    private Task UseCaptureForSearchAsync() => RunAsync(() =>
    {
        TextWorkspace.UseCaptureForSearch(); ResetResults(); RefreshSearchImage(); Status("搜索区已跟随当前截图区域。"); return Task.CompletedTask;
    });
    private void RefreshSearchImage() { var png = TextWorkspace.SearchPreviewPng(); _searchImage = png is null ? null : ImageUrl(png); }
    private static string ImageUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
    private async Task SetToolAsync(bool selection) { _selectTool = selection; await UpdateCanvasAsync(); }
    private Task UpdateCanvasAsync() => JS.InvokeVoidAsync("ftfCanvas.setState", _seeds, _selection, _selectedSeed, _selectTool, Math.Clamp(_zoom, 1, 8), _busy, TextWorkspace.CaptureRegion.Width).AsTask();
    [JSInvokable]
    public Task OnTextPreviewError(string message) { Status(message, true); return InvokeAsync(StateHasChanged); }
    [JSInvokable]
    public async Task OnTextPoint(int x, int y, int index, int r = -1, int g = -1, int b = -1)
    {
        if (_busy) return;
        try
        {
            var color = r is >= 0 and <= 255 && g is >= 0 and <= 255 && b is >= 0 and <= 255 ? Color.FromArgb(r, g, b) : TextWorkspace.ReadColor(x, y);
            if (index >= 0 && index < _seeds.Count)
            { var seed = _seeds[index]; seed.X = x; seed.Y = y; seed.R = color.R; seed.G = color.G; seed.B = color.B; }
            else
            {
                if (_seeds.Count >= 32) throw new InvalidOperationException("最多标定 32 个颜色点。");
                _seeds.Add(new Seed { X = x, Y = y, R = color.R, G = color.G, B = color.B, Deviation = Math.Clamp(_defaultDeviation, 0, 255) }); index = _seeds.Count - 1;
            }
            _selectedSeed = index; Invalidate(); await UpdateCanvasAsync();
        }
        catch (Exception ex) { Status(ex.Message, true); }
        await InvokeAsync(StateHasChanged);
    }
    [JSInvokable]
    public async Task OnTextSelection(int x, int y, int width, int height)
    {
        if (_busy) return;
        _selection = new Rectangle(x, y, width, height); Invalidate(); _selectTool = false;
        Status("文字范围已设置，请在笔画上标定颜色点。"); await UpdateCanvasAsync(); await InvokeAsync(StateHasChanged);
    }
    [JSInvokable]
    public async Task OnTextSeedSelected(int index) { if (!_busy) await SelectSeedAsync(index); await InvokeAsync(StateHasChanged); }
    private async Task SelectSeedAsync(int index) { _selectedSeed = index; await UpdateCanvasAsync(); }
    private async Task ChangeDeviationAsync(int index, ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out var value)) { _seeds[index].Deviation = Math.Clamp(value, 0, 255); Invalidate(); }
        await UpdateCanvasAsync();
    }
    private async Task DeleteSeedAsync(int index) { _seeds.RemoveAt(index); _selectedSeed = -1; Invalidate(); await UpdateCanvasAsync(); }
    private void Invalidate() { _preview = null; _progress = 0; _trainStatus = "框选和颜色设置后，需要提取点阵"; ResetResults(); }
    private void ResetResults() { _results = Array.Empty<TextMatch>(); _testStatus = "等待测试"; ResetLiveOcr(); }
    private List<TextColor> Colors() => _seeds.Select(s => new TextColor(s.R, s.G, s.B, s.Deviation)).ToList();
    private Task ExtractAsync() => RunAsync(() =>
    {
        _preview = TextWorkspace.Extract(_selection, Colors()); _trainStatus = "点阵已提取，请指定文字后训练"; Status("点阵已提取，训练会保留这份字形。"); return Task.CompletedTask;
    });
    private Task TrainAsync() => RunAsync(async () =>
    {
        if (_preview is null) throw new InvalidOperationException("先提取点阵。");
        if (string.IsNullOrWhiteSpace(_label)) throw new InvalidOperationException("先指定点阵代表的文字。");
        ApplyPendingWeights();
        if (_dictionary.Glyphs.Count >= 256) throw new InvalidOperationException("字库最多 256 个模板。");
        var trainer = _preview.CreateTrainingSession();
        _trainingCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _training = true; _progress = 0; Status("训练中，请保持文字在原位置。");
        try
        {
            var glyph = await TextWorkspace.TrainAsync(trainer, _label, _seconds, (percent, frames) =>
            { _progress = percent; _trainStatus = $"{percent}% · {frames} 帧"; _ = InvokeAsync(StateHasChanged); }, _trainingCts.Token);
            _dictionary.Glyphs.Add(glyph); Changed(); _trainStatus = $"完成 · {glyph.SampleCount} 帧"; Status($"“{glyph.Text}”已加入“{_dictionary.Name}”。");
        }
        finally { _training = false; _trainingCts.Dispose(); _trainingCts = null; }
    });
    private void CancelTraining() => _trainingCts?.Cancel();
    private void SyncWeights() { _weights = TextModelCodec.Encode(_dictionary); _liveOcrWeights = _weights; ResetLiveOcr(); }
    private void Changed() { _libraries[_activeLibrary].Dirty = true; SyncWeights(); ResetResults(); }
    private void ApplyPendingWeights()
    {
        if (_weights == TextModelCodec.Encode(_dictionary)) return;
        var decoded = TextModelCodec.Decode(_weights); _libraries[_activeLibrary].Dictionary = decoded; Changed();
    }
    private void ApplyWeights() { try { ApplyPendingWeights(); Status("权重已应用到当前字库。"); } catch (Exception ex) { Status(ex.Message, true); } }
    private void SwitchLibrary(ChangeEventArgs e)
    {
        try { ApplyPendingWeights(); _activeLibrary = int.Parse(e.Value!.ToString()!, CultureInfo.InvariantCulture); _glyphFilter = ""; SyncWeights(); ResetResults(); Status($"已切换到“{_dictionary.Name}”。"); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void NewLibrary()
    {
        try { ApplyPendingWeights(); _libraries.Add(new LibraryEntry { Dictionary = new TextDictionary { Name = $"字库 {_libraries.Count + 1}" }, Dirty = true }); _activeLibrary = _libraries.Count - 1; SyncWeights(); ResetResults(); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void RenameLibrary(ChangeEventArgs e)
    {
        string name = e.Value?.ToString()?.Trim() ?? "";
        if (name.Length is < 1 or > 128) { Status("字库名称需为 1～128 个字符。", true); return; }
        try { ApplyPendingWeights(); _dictionary.Name = name; Changed(); } catch (Exception ex) { Status(ex.Message, true); }
    }
    private Task OpenLibraryAsync(InputFileChangeEventArgs e) => RunAsync(async () =>
    {
        ApplyPendingWeights(); await using var stream = e.File.OpenReadStream(64_000_000, _lifetime.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var dictionary = TextDictionaryFile.Read(await reader.ReadToEndAsync(_lifetime.Token));
        _libraries.Add(new LibraryEntry { Dictionary = dictionary }); _activeLibrary = _libraries.Count - 1; _glyphFilter = ""; SyncWeights(); ResetResults();
        Status($"已打开“{dictionary.Name}”，包含 {dictionary.Glyphs.Count} 个模板，可继续编辑。");
    });
    private Task SaveLibraryAsync() => RunAsync(async () =>
    {
        ApplyPendingWeights(); var url = Downloads.Register(FileName(), TextDictionaryFile.Write(_dictionary));
        await JS.InvokeVoidAsync("ftfFiles.downloadUrl", url);
        _libraries[_activeLibrary].Dirty = false; Status($"已导出 {_dictionary.Name}.txt，可再次打开编辑。");
    });
    private string FileName() { var name = string.Concat(_dictionary.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' '); return (name.Length == 0 ? "字库" : name) + ".txt"; }
    private void RenameGlyph(int index, ChangeEventArgs e)
    {
        try { ApplyPendingWeights(); var text = e.Value?.ToString()?.Trim() ?? ""; if (text.Length is < 1 or > 128) throw new InvalidOperationException("模板文字不能为空。"); _dictionary.Glyphs[index].Text = text; Changed(); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void EnableGlyph(int index, ChangeEventArgs e) { try { ApplyPendingWeights(); _dictionary.Glyphs[index].Enabled = e.Value is true; Changed(); } catch (Exception ex) { Status(ex.Message, true); } }
    private void RemoveGlyph(int index) { try { ApplyPendingWeights(); _dictionary.Glyphs.RemoveAt(index); Changed(); Status("模板已删除。"); } catch (Exception ex) { Status(ex.Message, true); } }
    private void DuplicateGlyph(int index)
    {
        try { ApplyPendingWeights(); if (_dictionary.Glyphs.Count >= 256) throw new InvalidOperationException("字库最多 256 个模板。"); var copy = TextDictionaryFile.Clone(_dictionary.Glyphs[index]); copy.Id = Guid.NewGuid().ToString("N"); _dictionary.Glyphs.Insert(index + 1, copy); Changed(); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void EditGlyph(int index)
    {
        try { ApplyPendingWeights(); _editingIndex = index; _editing = TextDictionaryFile.Clone(_dictionary.Glyphs[index]); _editPixel = 0; _editError = ""; }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void CloseEditor() => _editing = null;
    private void ToggleEditPixel() { if (_editing is not null) _editing.Mask[_editPixel] = !_editing.Mask[_editPixel]; }
    private void ChangeEditWeight(ChangeEventArgs e) { if (_editing is not null && int.TryParse(e.Value?.ToString(), out int value)) _editing.Weights[_editPixel] = Math.Clamp(value, 1, 1000); }
    private static string ColorHex(TextColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private void ChangeEditColor(int index, ChangeEventArgs e)
    {
        if (_editing is null) return;
        if (int.TryParse(e.Value?.ToString()?.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) _editing.Colors[index] = _editing.Colors[index] with { R = (rgb >> 16) & 255, G = (rgb >> 8) & 255, B = rgb & 255 };
    }
    private void ChangeEditDeviation(int index, ChangeEventArgs e) { if (_editing is not null && int.TryParse(e.Value?.ToString(), out int value)) _editing.Colors[index] = _editing.Colors[index] with { Deviation = Math.Clamp(value, 0, 255) }; }
    private void SaveEditor()
    {
        if (_editing is null) return;
        try { _editing.Text = _editing.Text.Trim(); TextModelCodec.Validate(new TextDictionary { Glyphs = new() { _editing } }); _dictionary.Glyphs[_editingIndex] = _editing; Changed(); CloseEditor(); Status("点阵修改已应用，请点击“保存到识别库”更新原字典。"); }
        catch (Exception ex) { _editError = ex.Message; }
    }
    private static MarkupString Thumbnail(TextGlyph glyph)
    {
        var path = new StringBuilder(); for (int i = 0; i < glyph.Mask.Length; i++) if (glyph.Mask[i]) path.Append($"M{i % glyph.Width} {i / glyph.Width}h1v1h-1z");
        return new MarkupString($"<svg viewBox='0 0 {glyph.Width} {glyph.Height}' aria-hidden='true'><path fill='#eef4ff' d='{path}'/></svg>");
    }
    private Task TestAsync() => RunAsync(async () =>
    {
        ApplyPendingWeights(); ResetResults(); RefreshSearchImage(); string weights = _weights, query = _query.Trim();
        var scan = await Task.Run(() => TextWorkspace.Scan(weights, query, _threshold, _extraDeviation, _step, _lifetime.Token)); _results = scan.Matches;
        _testStatus = _results.Count == 0 ? $"未找到 · 最佳 {scan.BestMatch.Similarity:F1}% · X {scan.BestMatch.X} Y {scan.BestMatch.Y} · {scan.Reason}" : $"找到 {_results.Count} 处 · {scan.MatchMilliseconds:F2} ms";
        Status(_testStatus, _results.Count == 0);
    });
    private async Task CopyWeightsAsync()
    {
        try { ApplyPendingWeights(); await JS.InvokeVoidAsync("ftfFiles.copy", _weights); Status("找字权重字符串已复制。"); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private void ShowCallCode() { try { ApplyPendingWeights(); _showCode = true; } catch (Exception ex) { Status(ex.Message, true); } }
    private async Task CopyCodeAsync()
    {
        try { ApplyPendingWeights(); if (!_dictionary.Glyphs.Any(g => g.Enabled)) throw new InvalidOperationException("当前字库没有启用的模板，请先训练或打开字库。"); await JS.InvokeVoidAsync("ftfFiles.copy", BuildCallCode()); Status("完整找字调用代码已复制。"); }
        catch (Exception ex) { Status(ex.Message, true); }
    }
    private string BuildCallCode()
    {
        var range = TextWorkspace.SearchRegion.Width > 0 ? TextWorkspace.SearchRegion : new Rectangle(0, 0, 800, 600);
        string query = string.IsNullOrWhiteSpace(_query) ? "null" : CodeLiteral(_query.Trim());
        return $$"""
using System.Drawing;
using FastTextFinderRuntime;

string weightString = {{CodeLiteral(_weights)}};
var dictionary = new TextDictionaryStore();
dictionary.SetDictionary(weightString);

// 也可以加载下载的 TXT 字库。切换后，下次查找使用新字库：
// dictionary.LoadFromFile({{CodeLiteral(FileName())}});
// dictionary.LoadFromFile("另一个字库.txt");

TextMatch result = await FastTextSearch.FindAsync(
    new Rectangle(
        {{range.X}},
        {{range.Y}},
        {{range.Width}},
        {{range.Height}}
    ),

    dictionary.WeightString,

    text: {{query}},

    colorDeviation: {{_extraDeviation}},

    similarityThreshold: {{_threshold}},

    onSuccess: success =>
    {
        Console.WriteLine(
            "找到文字=" + success.Text +
            " X=" + success.X +
            " Y=" + success.Y +
            " 相似度=" + success.Similarity.ToString("F1") + "%" +
            " 查找次数=" + success.Attempts +
            " 总耗时=" + success.Elapsed.TotalMilliseconds.ToString("F0") + "ms" +
            " 本次匹配=" + success.MatchMilliseconds.ToString("F2") + "ms");
    },

    onFailure: failure =>
    {
        Console.WriteLine(
            "未找到 最佳X=" + failure.BestX +
            " 最佳Y=" + failure.BestY +
            " 最佳相似度=" + failure.BestSimilarity.ToString("F1") + "%" +
            " 查找次数=" + failure.Attempts +
            " 总耗时=" + failure.Elapsed.TotalMilliseconds.ToString("F0") + "ms" +
            " 原因=" + failure.Reason);
    },

    loopSeconds: {{_loopSeconds.ToString(CultureInfo.InvariantCulture)}},

    intervalMilliseconds: {{_interval}},

    step: {{_step}}
);
""";
    }
    private static string CodeLiteral(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private void Status(string text, bool error = false) { _status = text; _error = error; }
    private static string RegionText(Rectangle r) => r.Width <= 0 ? "尚未设置" : $"X {r.X} Y {r.Y} · {r.Width}×{r.Height}";

    private void ResetLiveOcr()
    {
        _liveOcrRevision++; _liveOcrRound?.Cancel();
        _liveOcrResult = null;
        _liveOcrStatus = "等待当前字库实时识别";
    }

    private bool LiveOcrSnapshotCurrent(LiveOcrSnapshot snapshot) => !_disposed && Active && !_busy &&
        snapshot.Weights == _liveOcrWeights && snapshot.Library == _activeLibrary &&
        snapshot.Threshold == _threshold && snapshot.Deviation == _extraDeviation && snapshot.Step == _step &&
        snapshot.Region == TextWorkspace.SearchRegion && snapshot.Revision == _liveOcrRevision;

    private async Task LiveOcrLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (!Active || _busy)
                {
                    await Task.Delay(150, _lifetime.Token);
                    continue;
                }

                string? waiting = _dictionary.Glyphs.Count == 0 ? "训练或打开字库后自动识别" :
                    !_dictionary.Glyphs.Any(glyph => glyph.Enabled) ? "当前字库没有启用模板，启用后自动识别" :
                    TextWorkspace.SearchRegion.Width <= 0 ? "设置截图或搜索区域后自动识别" : null;
                if (waiting is not null)
                {
                    if (_liveOcrStatus != waiting || _liveOcrResult is not null)
                    {
                        _liveOcrResult = null; _liveOcrStatus = waiting;
                        await InvokeAsync(StateHasChanged);
                    }
                    await Task.Delay(150, _lifetime.Token);
                    continue;
                }

                var snapshot = new LiveOcrSnapshot(_liveOcrWeights, _activeLibrary, _threshold, _extraDeviation,
                    _step, TextWorkspace.SearchRegion, _liveOcrRevision);
                using var round = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _liveOcrRound = round;
                try
                {
                    var result = await TextWorkspace.RecognizeAsync(snapshot.Weights, snapshot.Threshold,
                        snapshot.Deviation, snapshot.Step, round.Token);
                    // Discard an older frame when a library, region or matching setting changed during the scan.
                    if (!round.IsCancellationRequested && LiveOcrSnapshotCurrent(snapshot))
                    {
                        _liveOcrResult = result;
                        _liveOcrStatus = $"{result.Lines.Count} 行 · {result.Matches.Count} 处 · {result.MatchMilliseconds:F2} ms" +
                            (result.Truncated ? " · 已达结果上限" : result.Matches.Count == 0 ? " · 当前画面未匹配到字库文字" : "");
                        await InvokeAsync(StateHasChanged);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (LiveOcrSnapshotCurrent(snapshot))
                    {
                        _liveOcrStatus = "识别暂不可用：" + ex.Message;
                        await InvokeAsync(StateHasChanged);
                    }
                }
                finally { if (ReferenceEquals(_liveOcrRound, round)) _liveOcrRound = null; }
                // Each round finishes before the next one starts; hidden/busy pages never queue OCR work.
                await Task.Delay(150, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task CopyLiveOcrAsync()
    {
        if (string.IsNullOrEmpty(_liveOcrResult?.Text)) return;
        try { await JS.InvokeVoidAsync("ftfFiles.copy", _liveOcrResult.Text); _liveOcrStatus = "当前 OCR 文字已复制"; }
        catch (Exception ex) { _liveOcrStatus = "复制失败：" + ex.Message; }
    }

    private async Task CopyOcrCodeAsync()
    {
        try
        {
            ApplyPendingWeights();
            if (!_dictionary.Glyphs.Any(glyph => glyph.Enabled))
                throw new InvalidOperationException("当前字库没有启用的模板，请先训练或打开字库。");
            await JS.InvokeVoidAsync("ftfFiles.copy", BuildOcrCallCode());
            _liveOcrStatus = "完整实时 OCR 调用代码已复制";
        }
        catch (Exception ex) { _liveOcrStatus = "复制 OCR 代码失败：" + ex.Message; }
    }

    private string BuildOcrCallCode()
    {
        var region = TextWorkspace.SearchRegion.Width > 0 ? TextWorkspace.SearchRegion : new Rectangle(0, 0, 800, 600);
        return $$"""
// Windows .NET 8+；加入下载的 FastTextSearch.cs，并引用 System.Drawing.Common。
// 捕获前启用 DPI 感知；区域和结果坐标使用屏幕物理像素。
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using FastTextFinderRuntime;

OcrDpi.SetProcessDpiAwarenessContext(new IntPtr(-4));

var dictionary = new TextDictionaryStore();
dictionary.SetDictionary({{CodeLiteral(TextModelCodec.Encode(_dictionary))}});
// 也可加载已导出的 TXT 字库；下轮 OCR 使用最新字库：
// dictionary.LoadFromFile({{CodeLiteral(FileName())}});

var region = new Rectangle({{region.X}}, {{region.Y}}, {{region.Width}}, {{region.Height}});
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

try
{
    // 实时 OCR：识别当前字库中的全部已启用文字，按行拼接；每轮完成后开始下一轮。
    while (!cancellation.IsCancellationRequested)
    {
        string weights = dictionary.WeightString;
        TextOcrResult result = await Task.Run(() =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using Bitmap frame = TextScreenCapture.Capture(region);
            return FastTextSearch.Recognize(
                frame, weights,
                similarityThreshold: {{_threshold}},
                colorDeviation: {{_extraDeviation}},
                step: {{_step}},
                maxResults: 512,
                cancellationToken: cancellation.Token
            ).Offset(region.X, region.Y);
        }, cancellation.Token);

        cancellation.Token.ThrowIfCancellationRequested();
        Console.WriteLine(result.Text);
        Console.WriteLine($"行数={result.Lines.Count} 匹配数={result.Matches.Count} 匹配耗时={result.MatchMilliseconds:F2}ms");
        foreach (var line in result.Lines)
            Console.WriteLine($"{line.Text}：X={line.Bounds.X} Y={line.Bounds.Y} 宽={line.Bounds.Width} 高={line.Bounds.Height}");
        foreach (var hit in result.Matches)
            Console.WriteLine($"{hit.Text}：X={hit.X} Y={hit.Y} 相似度={hit.Similarity:F1}%");
        if (result.Truncated) Console.WriteLine("已达到本轮结果上限。");

        // 程序内可调用 cancellation.Cancel() 停止；控制台按 Ctrl+C 停止。
        await Task.Delay(150, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

internal static class OcrDpi
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
""";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _trainingCts?.Cancel(); _liveOcrRound?.Cancel();
        try { await JS.InvokeVoidAsync("ftfCanvas.dispose"); } catch (JSDisconnectedException) { }
        if (_liveOcrLoop is not null) await _liveOcrLoop;
        _reference?.Dispose(); _lifetime.Dispose();
    }
    public sealed class Seed { public int X { get; set; } public int Y { get; set; } public int R { get; set; } public int G { get; set; } public int B { get; set; } public int Deviation { get; set; } }
}
