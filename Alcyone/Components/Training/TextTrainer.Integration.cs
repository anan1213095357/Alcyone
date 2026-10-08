using Microsoft.AspNetCore.Components;
using StateMachine.Training;
using StateMachine.Automation;
using FastTextFinderRuntime;

namespace StateMachine.Components.Training;

public partial class TextTrainer
{
    [Parameter] public TrainingResult Initial { get; set; } = new();
    [Parameter] public Func<TrainingResult, Task<string>> SaveResult { get; set; } = default!;
    private readonly Dictionary<LibraryEntry, string> _targets = new();
    private readonly Dictionary<LibraryEntry, string> _saved = new();
    private readonly Dictionary<LibraryEntry, string> _savedWeights = new();
    private string _dictionaryMode = "ocr";
    private bool _saving;
    public bool IntegrationBusy => _busy || _saving || _editing is not null;
    private string Fingerprint => System.Text.Json.JsonSerializer.Serialize(new { _weights, _threshold, _extraDeviation, _step, _query, _dictionaryMode, range = TextWorkspace.SearchRegion, capture = TextWorkspace.CaptureRegion, _selection, _seeds, _label, _zoom, _seconds, _selectTool, preview = _preview?.Mask });
    public bool HasUnsavedChanges => _libraries.Any(entry => !_savedWeights.TryGetValue(entry, out var weights) || weights != TextModelCodec.Encode(entry.Dictionary)) || _editing is not null ||
        !_saved.TryGetValue(_libraries[_activeLibrary], out var saved) || saved != Fingerprint;
    protected override void OnInitialized()
    {
        var settings = Initial.Settings;
        var dictionary = string.IsNullOrWhiteSpace(settings.WeightString)
            ? new TextDictionary { Name = Initial.Name } : TextDictionaryFile.Read(settings.WeightString);
        _libraries[0].Dictionary = dictionary;
        if (Initial.TargetId is { } id) _targets[_libraries[0]] = id;
        _threshold = (int)settings.Similarity; _extraDeviation = settings.ColorDeviation;
        _step = settings.ScanStep; _query = settings.Query; _dictionaryMode = settings.DictionaryMode;
        if (settings.Width > 0 && settings.Height > 0)
            TextWorkspace.SetSearchRegion(new(settings.X, settings.Y, settings.Width, settings.Height));
        RestoreEditorState(settings.TrainingEditor);
        SyncWeights(); _saved[_libraries[0]] = Fingerprint; _savedWeights[_libraries[0]] = _weights;
    }
    private TrainingEditorState CaptureEditorState() => new() {
        ImagePng = TextWorkspace.SaveEditorImage(),
        Capture = new(TextWorkspace.CaptureRegion.X, TextWorkspace.CaptureRegion.Y, TextWorkspace.CaptureRegion.Width, TextWorkspace.CaptureRegion.Height),
        Selection = new(_selection.X, _selection.Y, _selection.Width, _selection.Height),
        SeedsJson = System.Text.Json.JsonSerializer.Serialize(_seeds), Label = _label,
        Zoom = _zoom, Seconds = _seconds, SelectedSeed = _selectedSeed, DefaultDeviation = _defaultDeviation,
        SelectTool = _selectTool, HasPreview = _preview is not null, PreviewMask = _preview?.Mask.ToArray()
    };
    private void RestoreEditorState(TrainingEditorState? editor)
    {
        if (editor?.ImagePng is null || editor.Capture is not { Width: > 0, Height: > 0 } region) return;
        TextWorkspace.RestoreEditorImage(new(region.X, region.Y, region.Width, region.Height), editor.ImagePng);
        _seeds.Clear(); _seeds.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<Seed>>(editor.SeedsJson) ?? []);
        if (editor.Selection is {} selection) _selection = new(selection.X, selection.Y, selection.Width, selection.Height);
        _label = editor.Label; _zoom = editor.Zoom; _seconds = editor.Seconds;
        _selectedSeed = editor.SelectedSeed; _defaultDeviation = editor.DefaultDeviation; _selectTool = editor.SelectTool;
        if (editor.HasPreview) {
            _preview = TextWorkspace.Extract(_selection, Colors());
            if (editor.PreviewMask is {} mask && mask.Length == _preview.Mask.Length) mask.CopyTo(_preview.Mask, 0);
            _trainStatus = "已恢复原点阵，可继续训练";
        }
        Status("已恢复原训练图片、文字范围和颜色标点。");
    }
    private async Task SaveToLibraryAsync(bool asNew)
    {
        if (IntegrationBusy) return;
        _saving = true; _busy = true;
        try
        {
            ApplyPendingWeights();
            var range = TextWorkspace.SearchRegion;
            var entry = _libraries[_activeLibrary];
            var result = new TrainingResult { Name = _dictionary.Name,
                TargetId = !asNew && _targets.TryGetValue(entry, out var id) ? id : null,
                Settings = new ColorProbeSettings { Mode = "dictionary", WeightString = _weights,
                    X = range.X, Y = range.Y, Width = range.Width, Height = range.Height,
                    Similarity = _threshold, ColorDeviation = _extraDeviation, ScanStep = _step,
                    Query = _query, DictionaryMode = _dictionaryMode, TrainingEditor = CaptureEditorState() } };
            result.ValidatedSettings();
            _targets[entry] = await SaveResult(result); entry.Dirty = false;
            _saved[entry] = Fingerprint;
            _savedWeights[entry] = _weights;
            Status($"已保存“{_dictionary.Name}”到状态机识别库 · {_dictionary.Glyphs.Count} 个模板。");
        }
        catch (Exception ex) { Status($"保存失败：{ex.Message}", true); }
        finally { _saving = false; _busy = false; }
    }
}
