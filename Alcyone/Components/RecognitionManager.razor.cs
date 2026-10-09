using System.Globalization;
using System.Text;
using FastColorFinder.Core;
using FastTextFinderRuntime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using StateMachine.Automation;
using Microsoft.JSInterop;

namespace StateMachine.Components;

public partial class RecognitionManager : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Parameter] public List<RecognitionItem> Items { get; set; } = new();
    [Parameter] public string? SelectedId { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnChanged { get; set; }
    [Parameter] public EventCallback<RecognitionItem> OnTest { get; set; }
    [Parameter] public Func<RecognitionItem, string> Status { get; set; } = _ => "未检测";
    [Parameter] public bool Busy { get; set; }
    private string? _selected, _loadedId, _loadedWeights, _error;
    private string _newMode = "feature", _rawWeights = "", _glyphFilter = "", _itemFilter = "";
    private string _newFeatureText = "";
    private TextDictionary? _dictionary;
    private ColorModel? _colorModel;
    private const long ImportFileLimit = 64_000_000; // Same limit as the training tool.
    private bool _importing;
    private int _importInputVersion;
    private string? _importStatus;
    private bool IsBusy => Busy || _importing || _selectingRange;
    private ElementReference _dialogElement;
    private IJSObjectReference? _locationModule;
    private string? _requestedSelection;
    private bool _locateAfterRender = true;
    private RecognitionItem? Current => Items.FirstOrDefault(i => i.Id == _selected);
    private RecognitionItem? CurrentRoot => Current?.ParentId is { } parent ? Items.FirstOrDefault(i => i.Id == parent) : Current;
    private IEnumerable<RecognitionItem> Roots => Items.Where(i => i.ParentId is null);
    private readonly Dictionary<string, (string Weights, TextDictionary Dictionary)> _dictionaryCache = new();
    private readonly Dictionary<string, (string Weights, ColorModel Model)> _colorCache = new();
    private readonly Dictionary<string, string> _featureDrafts = new();
    private IEnumerable<RecognitionItem> VisibleItems => CurrentRoot is { IsFolder: true } root
        ? Items.Where(i => i.ParentId == root.Id) : Roots.Where(i => i.Id == CurrentRoot?.Id);
    private string? TargetEntry(string? id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        return item?.IsFolder == true ? Items.FirstOrDefault(i => i.ParentId == item.Id)?.Id ?? item.Id : item?.Id;
    }
    protected override void OnInitialized()
    {
        RecognitionLibrary.NormalizeFolders(Items);
        _selected = TargetEntry(SelectedId) ?? TargetEntry(Roots.FirstOrDefault()?.Id);
        _requestedSelection = SelectedId;
    }
    protected override void OnParametersSet()
    {
        RecognitionLibrary.NormalizeFolders(Items);
        if (_requestedSelection != SelectedId)
        {
            _requestedSelection = SelectedId;
            if (Items.Any(i => i.Id == SelectedId))
            { _selected = TargetEntry(SelectedId); _itemFilter = _glyphFilter = ""; _locateAfterRender = true; }
        }
        if (Current?.Id != _loadedId || Current?.Settings.WeightString != _loadedWeights) LoadPreview();
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await ConnectTrainingAsync();
        if (!_locateAfterRender || Current is null) return;
        _locateAfterRender = false;
        try
        {
            _locationModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./js/recognition-manager.js");
            await _locationModule.InvokeVoidAsync("locateEntry", _dialogElement, CurrentRoot?.Id, _selected);
        }
        catch (JSDisconnectedException) { }
    }
    private void Select(string id, bool locate = false)
    {
        if (_importing || !Items.Any(i => i.Id == id)) return;
        if (locate) _locateAfterRender = true;
        id = TargetEntry(id)!;
        if (_selected == id) return;
        _selected = id; _glyphFilter = ""; LoadPreview();
    }
    private TextDictionary ReadDictionary(RecognitionItem item)
    {
        if (_dictionaryCache.TryGetValue(item.Id, out var cached) && cached.Weights == item.Settings.WeightString) return cached.Dictionary;
        var dictionary = TextDictionaryFile.Read(item.Settings.WeightString);
        NormalizeGlyphIds(dictionary);
        _dictionaryCache[item.Id] = (item.Settings.WeightString, dictionary);
        return dictionary;
    }
    private TextDictionary? DictionaryForTable(RecognitionItem item)
    { try { return ReadDictionary(item); } catch (FormatException) { return null; } }
    private ColorModel? ColorForTable(RecognitionItem item)
    {
        try
        {
            if (_colorCache.TryGetValue(item.Id, out var cached) && cached.Weights == item.Settings.WeightString) return cached.Model;
            var model = ColorModelCodec.Decode(item.Settings.WeightString);
            _colorCache[item.Id] = (item.Settings.WeightString, model);
            return model;
        }
        catch (FormatException) { return null; }
    }
    private string FeatureString(RecognitionItem item) => _featureDrafts.GetValueOrDefault(item.Id, item.Settings.WeightString);
    private async Task SaveFeatureString(RecognitionItem item, string value)
    {
        if (IsBusy) return;
        Select(item.Id);
        _featureDrafts[item.Id] = value;
        try
        {
            ColorFeatureImport.Read(value).ApplyTo(item.Settings);
            _featureDrafts.Remove(item.Id);
            LoadPreview(); await Changed();
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private void LoadPreview()
    {
        _dictionary = null; _colorModel = null; _error = null;
        _loadedId = Current?.Id; _loadedWeights = Current?.Settings.WeightString; _rawWeights = _loadedWeights ?? "";
        if (Current is not { } item || string.IsNullOrWhiteSpace(_rawWeights)) return;
        try
        {
            if (item.Settings.Mode == "dictionary")
            {
                _dictionary = ReadDictionary(item);
            }
            else if (item.Settings.Mode == "feature") _colorModel = ColorForTable(item) ?? ColorModelCodec.Decode(_rawWeights);
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task Changed() { _error = null; await OnChanged.InvokeAsync(); }
    private async Task Add()
    {
        var prefix = RecognitionItem.KindName(_newMode); var index = 1;
        while (Roots.Any(i => i.Name == $"{prefix}{index}")) index++;
        var item = new RecognitionItem { Name = $"{prefix}{index}", IsFolder = _newMode == "feature", Settings = new() { Mode = _newMode } };
        Items.Add(item); _itemFilter = ""; Select(item.Id, true); await Changed();
    }
    private async Task Delete(RecognitionItem item)
    {
        var parent = item.ParentId;
        foreach (var child in Items.Where(i => i.ParentId == item.Id).ToArray()) Items.Remove(child);
        Items.Remove(item); _dictionaryCache.Remove(item.Id); _colorCache.Remove(item.Id); _featureDrafts.Remove(item.Id);
        if (Current is null) _selected = TargetEntry(parent) ?? TargetEntry(Roots.FirstOrDefault()?.Id);
        _locateAfterRender = true; LoadPreview(); await Changed();
    }
    private static string RangeValue(ColorProbeSettings settings) => $"{settings.X},{settings.Y},{settings.Width},{settings.Height}";
    private async Task ChangeRange(RecognitionItem item, string? value)
    {
        var parts = (value ?? "").Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = new int[4];
        if (parts.Length != 4 || !parts.Select((part, index) => int.TryParse(part, out numbers[index])).All(valid => valid)
            || numbers[2] <= 0 || numbers[3] <= 0)
        { _error = "查找范围请填写 X,Y,宽,高，宽和高必须大于 0。"; return; }
        item.Settings.X = numbers[0]; item.Settings.Y = numbers[1];
        item.Settings.Width = numbers[2]; item.Settings.Height = numbers[3];
        await Changed();
    }
    private string? _renamingLibraryId;
    private string _libraryNameDraft = "";
    private void BeginLibraryRename(RecognitionItem item)
    {
        if (IsBusy) return;
        _renamingLibraryId = item.Id;
        _libraryNameDraft = item.Name;
    }
    private async Task CommitLibraryRenameAsync(RecognitionItem item)
    {
        if (_renamingLibraryId != item.Id || IsBusy) return;
        var name = _libraryNameDraft.Trim();
        _renamingLibraryId = null;
        if (name.Length > 0 && name != item.Name) await Rename(item, name);
    }
    private async Task LibraryRenameKeyAsync(RecognitionItem item, Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Escape") _renamingLibraryId = null;
        else if (e.Key == "Enter") await CommitLibraryRenameAsync(item);
    }

    private async Task Rename(RecognitionItem item, string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || Items.Any(i => i != item && i.ParentId == item.ParentId && i.Name == name))
        { _error = "项目名称不能为空或重复。"; return; }
        item.Name = name; await Changed();
    }
    private async Task ApplyWeights()
    {
        if (Current is not { } item) return;
        try
        {
            if (item.Settings.Mode == "feature") ColorFeatureImport.Read(_rawWeights).ApplyTo(item.Settings);
            else item.Settings.WeightString = TextModelCodec.Encode(TextDictionaryFile.Read(_rawWeights));
            _featureDrafts.Remove(item.Id);
            LoadPreview(); await Changed();
            _importStatus = item.Settings.Mode == "feature"
                ? $"已录入 {_colorModel!.Points.Count} 个颜色点 · 查找范围 ({item.Settings.X}, {item.Settings.Y}, {item.Settings.Width}, {item.Settings.Height})"
                : $"已录入 {_dictionary!.Glyphs.Count} 个文字模板";
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task AppendFeature()
    {
        if (IsBusy) return;
        try
        {
            var parsed = ColorFeatureImport.Read(_newFeatureText);
            RecognitionLibrary.NormalizeFolders(Items);
            var root = CurrentRoot;
            if (root is not { IsFolder: true }) throw new InvalidOperationException("请先在左侧新建或选择一个多点色库。");
            var index = 1;
            while (Items.Any(i => i.ParentId == root.Id && i.Name == $"颜色特征{index}")) index++;
            var item = new RecognitionItem { Name = $"颜色特征{index}", ParentId = root.Id, Settings = Current?.Settings.Snapshot() ?? new() };
            item.Settings.Mode = "feature";
            item.Settings.FileName = "";
            parsed.ApplyTo(item.Settings);
            Items.Add(item);
            _itemFilter = _glyphFilter = "";
            Select(item.Id, true);
            LoadPreview();
            await Changed();
            _newFeatureText = "";
            _importStatus = $"已新增 {item.Name} · {_colorModel!.Points.Count} 个颜色点，可继续粘贴录入下一项。";
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task ImportDictionary(InputFileChangeEventArgs e) => await ImportWeights(e);
    private async Task ImportNewDictionary(InputFileChangeEventArgs e) => await ImportWeightsCore(e, false);
    private async Task ImportWeights(InputFileChangeEventArgs e) => await ImportWeightsCore(e, true);
    private async Task ImportWeightsCore(InputFileChangeEventArgs e, bool replaceCurrent)
    {
        if (IsBusy) return;
        var selected = replaceCurrent ? Current : null;
        _importing = true;
        _error = null;
        _importStatus = $"正在读取：{e.File.Name}…";
        try
        {
            using var reader = new StreamReader(e.File.OpenReadStream(ImportFileLimit));
            var data = await reader.ReadToEndAsync();
            data = data.Trim().TrimStart('\uFEFF').Trim();
            // Detect the exported format instead of treating a dictionary as a color string.
            var isFeature = data.StartsWith("FCF2|", StringComparison.Ordinal) || data.StartsWith("FCF3|", StringComparison.Ordinal);
            TextDictionary? dictionary = null;
            ColorModel? colors = null;
            string weights;
            if (isFeature)
            {
                colors = ColorModelCodec.Decode(data);
                weights = ColorModelCodec.Encode(colors);
            }
            else
            {
                dictionary = TextDictionaryFile.Read(data);
                NormalizeGlyphIds(dictionary);
                weights = TextModelCodec.Encode(dictionary);
            }
            var mode = isFeature ? "feature" : "dictionary";
            var item = selected is not null && !selected.IsFolder && selected.Settings.Mode == mode ? selected : null;
            if (item is null)
            {
                var name = dictionary?.Name ?? Path.GetFileNameWithoutExtension(e.File.Name);
                var prefix = string.IsNullOrWhiteSpace(name) ? RecognitionItem.KindName(mode) : name;
                name = prefix;
                for (int i = 2; Items.Any(entry => entry.Name == name); i++) name = $"{prefix}{i}";
                item = new RecognitionItem { Name = name, Settings = new() { Mode = mode } };
                Items.Add(item);
            }
            item.Settings.WeightString = weights;
            item.Settings.FileName = e.File.Name;
            if (isFeature) RecognitionLibrary.NormalizeFolders(Items);
            _selected = item.Id;
            _locateAfterRender = true;
            _itemFilter = _glyphFilter = "";
            LoadPreview();
            await Changed();
            _importStatus = dictionary is not null
                ? $"已导入 {e.File.Name} · {dictionary.Glyphs.Count} 个文字模板"
                : $"已导入 {e.File.Name} · {colors!.Points.Count} 个颜色点";
        }
        catch (Exception ex)
        {
            _importStatus = null;
            _error = $"导入 {e.File.Name} 失败：{ex.Message}";
        }
        finally { _importing = false; _importInputVersion++; }
    }
    private static void NormalizeGlyphIds(TextDictionary dictionary)
    {
        // The tool accepts missing/duplicate template IDs; they must be unique for list keys.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var glyph in dictionary.Glyphs)
            if (string.IsNullOrWhiteSpace(glyph.Id) || !ids.Add(glyph.Id))
            {
                glyph.Id = Guid.NewGuid().ToString("N");
                ids.Add(glyph.Id);
            }
    }
    private async Task SaveDictionary(TextDictionary dictionary)
    {
        if (Current is not { } item) return;
        var weights = TextModelCodec.Encode(dictionary);
        item.Settings.WeightString = weights; _dictionary = dictionary;
        _dictionaryCache[item.Id] = (weights, dictionary);
        _loadedWeights = _rawWeights = weights; await Changed();
    }
    private TextDictionary DictionaryCopy() => TextModelCodec.Decode(TextModelCodec.Encode(_dictionary!));
    private async Task RenameGlyph(TextGlyph glyph, string? value)
    {
        try
        {
            var copy = DictionaryCopy(); copy.Glyphs.First(g => g.Id == glyph.Id).Text = value?.Trim() ?? "";
            await SaveDictionary(copy);
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task SaveDictionaryName()
    {
        if (_dictionary is null) return;
        try { await SaveDictionary(_dictionary); }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task EnableGlyph(TextGlyph glyph, bool enabled)
    {
        try { var copy = DictionaryCopy(); copy.Glyphs.First(g => g.Id == glyph.Id).Enabled = enabled; await SaveDictionary(copy); }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task RemoveGlyph(TextGlyph glyph)
    {
        try
        {
            var copy = DictionaryCopy(); copy.Glyphs.RemoveAll(g => g.Id == glyph.Id); await SaveDictionary(copy);
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task DuplicateGlyph(TextGlyph glyph)
    {
        try { var copy = DictionaryCopy(); var clone = TextDictionaryFile.Clone(glyph); clone.Id = Guid.NewGuid().ToString("N"); copy.Glyphs.Add(clone); await SaveDictionary(copy); }
        catch (Exception ex) { _error = ex.Message; }
    }
    private static MarkupString Thumbnail(TextGlyph glyph)
    {
        var path = new StringBuilder();
        for (int i = 0; i < glyph.Mask.Length; i++) if (glyph.Mask[i]) path.Append($"M{i % glyph.Width} {i / glyph.Width}h1v1h-1z");
        return new($"<svg viewBox='0 0 {glyph.Width} {glyph.Height}' aria-hidden='true' style='width:100%;height:100%;shape-rendering:crispEdges'><path fill='#e8edf5' d='{path}'/></svg>");
    }
    private static string PointColor(ColorModel model, TrainedPoint point)
    {
        // These are reconstructed preview colors, not additional matching thresholds.
        var y = model.AnchorMeanY + (point.IsAnchor ? 0 : point.MeanDY);
        var cb = model.AnchorMeanCb + (point.IsAnchor ? 0 : point.MeanDCb) - 128;
        var cr = model.AnchorMeanCr + (point.IsAnchor ? 0 : point.MeanDCr) - 128;
        int Channel(double value) => Math.Clamp((int)Math.Round(value), 0, 255);
        return $"#{Channel(y + 1.402 * cr):X2}{Channel(y - .344136 * cb - .714136 * cr):X2}{Channel(y + 1.772 * cb):X2}";
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await DisconnectTrainingAsync();
        _trainingReference?.Dispose();
        if (_trainingModule is not null)
        {
            try { await _trainingModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        if (_locationModule is null) return;
        try { await _locationModule.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
