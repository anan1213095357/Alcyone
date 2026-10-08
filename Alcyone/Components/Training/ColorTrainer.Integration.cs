using Microsoft.AspNetCore.Components;
using StateMachine.Training;
using StateMachine.Automation;
using FastColorFinder.Core;

namespace StateMachine.Components.Training;

public partial class ColorTrainer
{
    [Parameter] public TrainingResult Initial { get; set; } = new();
    [Parameter] public Func<TrainingResult, Task<string>> SaveResult { get; set; } = default!;
    private string _entryName = "新颜色特征";
    private string? _targetId;
    private bool _saving;
    private bool _disposed;
    private string _savedFingerprint = "";
    private readonly CancellationTokenSource _lifetime = new();
    public bool IntegrationBusy => _busy || _saving;
    private string Fingerprint => System.Text.Json.JsonSerializer.Serialize(new { _entryName, _weightText, _threshold, _colorDeviation, _step, range = Workspace.SearchRegion, capture = Workspace.CaptureRegion, points = Workspace.Points });
    public bool HasUnsavedChanges => Fingerprint != _savedFingerprint;

    protected override void OnInitialized()
    {
        _entryName = Initial.Name; _targetId = Initial.TargetId;
        var settings = Initial.Settings;
        _weightText = settings.WeightString; _threshold = (int)settings.Similarity;
        _colorDeviation = settings.ColorDeviation; _step = settings.ScanStep;
        if (!string.IsNullOrWhiteSpace(_weightText)) Workspace.SetWeightString(_weightText);
        if (settings.TrainingEditor is { ImagePng: not null, Capture: not null } editor)
        {
            var region = editor.Capture;
            Workspace.RestoreEditorImage(new(region.X, region.Y, region.Width, region.Height), editor.ImagePng);
            Workspace.SetPoints(System.Text.Json.JsonSerializer.Deserialize<Point[]>(editor.PointsJson) ?? []);
            Workspace.SetWeightString(_weightText);
            _trainSeconds = editor.Seconds;
            _trainStatus = "已恢复原训练图片和标点";
        }
        else if (settings.TrainingCapture is { Width: > 0, Height: > 0 } capture)
        {
            try { SetCaptureForEditing(new(capture.X, capture.Y, capture.Width, capture.Height)); }
            catch (Exception ex) { _status = $"原捕获区无法恢复，请重新框选：{ex.Message}"; _statusError = true; }
        }
        else if (!string.IsNullOrWhiteSpace(_weightText))
        { _status = "已载入原颜色特征。旧记录未保存捕获位置，框选原目标区域后会恢复已有标点。"; }
        if (settings.Width > 0 && settings.Height > 0)
            Workspace.SetSearchRegion(new(settings.X, settings.Y, settings.Width, settings.Height));
        _savedFingerprint = Fingerprint;
    }

    private void SetCaptureForEditing(Rectangle region)
    {
        var model = string.IsNullOrWhiteSpace(_weightText) ? null : ColorModelCodec.Decode(_weightText);
        if (model is not null && model.Points.Any(point => point.X < 0 || point.Y < 0 || point.X >= region.Width || point.Y >= region.Height))
            throw new InvalidOperationException("捕获区域太小，无法容纳原标点。请框选包含整个原目标的区域。");
        Workspace.SetCaptureRegion(region);
        if (model is not null)
        {
            Workspace.SetPoints(model.Points.Select(point => new Point(point.X, point.Y)));
            Workspace.SetWeightString(_weightText);
            _trainStatus = "原标点已恢复，可拖动调整后重新训练";
        }
        else { _weightText = string.Empty; _trainStatus = "等待训练"; }
        _progress = 0;
    }
    private async Task SaveToLibraryAsync(bool asNew)
    {
        if (IntegrationBusy) return;
        _saving = true;
        try
        {
            var range = Workspace.SearchRegion;
            var result = new TrainingResult { Name = _entryName, TargetId = asNew ? null : _targetId,
                Settings = new ColorProbeSettings { Mode = "feature", WeightString = _weightText,
                    X = range.X, Y = range.Y, Width = range.Width, Height = range.Height,
                    Similarity = _threshold, ColorDeviation = _colorDeviation, ScanStep = _step,
                    TrainingEditor = new TrainingEditorState {
                        ImagePng = Workspace.SaveEditorImage(),
                        Capture = new(Workspace.CaptureRegion.X, Workspace.CaptureRegion.Y, Workspace.CaptureRegion.Width, Workspace.CaptureRegion.Height),
                        PointsJson = System.Text.Json.JsonSerializer.Serialize(Workspace.Points), Seconds = _trainSeconds },
                    TrainingCapture = Workspace.CaptureRegion is { Width: > 0, Height: > 0 } capture
                        ? new TrainingCaptureRegion(capture.X, capture.Y, capture.Width, capture.Height)
                        : Initial.Settings.TrainingCapture } };
            result.ValidatedSettings();
            _targetId = await SaveResult(result);
            _savedFingerprint = Fingerprint;
            SetStatus("已保存到状态机识别库，可直接用于出口条件。", false);
        }
        catch (Exception ex) { SetStatus($"保存失败：{ex.Message}", true); }
        finally { _saving = false; }
    }
}
