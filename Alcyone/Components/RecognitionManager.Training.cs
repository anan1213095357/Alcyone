using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using StateMachine.Automation;
using StateMachine.Training;

namespace StateMachine.Components;

public partial class RecognitionManager
{
    private TrainingResult? _trainingInitial;
    private string? _trainingFolder;
    private ElementReference _trainingFrame;
    private DotNetObjectReference<RecognitionManager>? _trainingReference;
    private IJSObjectReference? _trainingModule, _trainingConnection;
    private bool _connectTraining;
    private readonly HashSet<string> _trainingTargets = new(StringComparer.Ordinal);

    private void OpenNewTraining()
    {
        if (CurrentRoot is not { } library) return;
        OpenTraining(library.Settings.Mode, edit: false);
    }

    private void OpenTraining(string mode, bool edit = false)
    {
        if (IsBusy) return;
        var item = edit && Current is { IsFolder: false } selected && selected.Settings.Mode == mode ? selected : null;
        _trainingFolder = mode == "feature" && CurrentRoot is { IsFolder: true } folder ? folder.Id : null;
        _trainingInitial = new TrainingResult { Name = item?.Name ?? (mode == "dictionary" ? "新字典" : "新颜色特征"),
            TargetId = item?.Id, Settings = item?.Settings.Snapshot() ?? new ColorProbeSettings {
                Mode = mode, Width = 0, Height = 0, Similarity = mode == "dictionary" ? 85 : 78 } };
        _trainingTargets.Clear();
        if (item is not null) _trainingTargets.Add(item.Id);
        _connectTraining = true;
    }
    private async Task ConnectTrainingAsync()
    {
        if (!_connectTraining || _trainingInitial is null) return;
        _connectTraining = false;
        _trainingModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./training/bridge.js");
        _trainingReference ??= DotNetObjectReference.Create(this);
        _trainingConnection = await _trainingModule.InvokeAsync<IJSObjectReference>("connectHost", _trainingFrame, _trainingReference, _trainingInitial);
    }
    [JSInvokable] public async Task<string> SaveTraining(TrainingResult result)
    {
        if (_trainingInitial is null || IsBusy) throw new InvalidOperationException("当前不能保存训练结果。");
        if (result.Settings.Mode != _trainingInitial.Settings.Mode) throw new InvalidOperationException("训练类型不匹配。");
        var settings = result.ValidatedSettings();
        RecognitionItem? target = null;
        if (result.TargetId is { } id)
        {
            if (!_trainingTargets.Contains(id)) throw new InvalidOperationException("不能覆盖其他识别项目。");
            target = Items.FirstOrDefault(i => i.Id == id && !i.IsFolder && i.Settings.Mode == settings.Mode)
                ?? throw new InvalidOperationException("原识别项目已不存在，请另存为新项。");
        }
        var oldName = target?.Name;
        var oldSettings = target?.Settings;
        RecognitionItem? createdFolder = null;
        bool isNew = target is null;
        target ??= new RecognitionItem();
        var name = result.Name.Trim();
        var prefix = name;
        for (int index = 2; Items.Any(i => i.Id != target.Id && i.Name == name); index++) name = $"{prefix}{index}";
        if (isNew && settings.Mode == "feature")
        {
            var folder = Items.FirstOrDefault(i => i.Id == _trainingFolder && i.IsFolder);
            if (folder is null)
            {
                folder = createdFolder = new RecognitionItem { IsFolder = true, Name = name + "色库" };
                Items.Add(folder);
            }
            target.ParentId = folder.Id;
        }
        target.Name = name; target.Settings = settings;
        if (isNew) Items.Add(target);
        try { await OnChanged.InvokeAsync(); }
        catch
        {
            if (isNew) Items.Remove(target);
            else { target.Name = oldName!; target.Settings = oldSettings!; }
            if (createdFolder is not null) Items.Remove(createdFolder);
            throw;
        }
        _trainingTargets.Add(target.Id);
        if (target.ParentId is not null) _trainingFolder = target.ParentId;
        _selected = target.Id; _itemFilter = _glyphFilter = ""; _locateAfterRender = true;
        LoadPreview(); _importStatus = $"已保存“{target.Name}”，可直接用于状态出口条件。";
        await InvokeAsync(StateHasChanged);
        return target.Id;
    }
    [JSInvokable] public async Task CloseTraining()
    {
        _trainingInitial = null;
        await DisconnectTrainingAsync();
        _locateAfterRender = true;
        await InvokeAsync(StateHasChanged);
    }
    private async Task DisconnectTrainingAsync()
    {
        if (_trainingConnection is null) return;
        try { await _trainingConnection.InvokeVoidAsync("dispose"); await _trainingConnection.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        _trainingConnection = null;
    }
}
