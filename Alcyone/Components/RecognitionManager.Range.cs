using Microsoft.AspNetCore.Components;
using StateMachine.Automation;
using StateMachine.Training;

namespace StateMachine.Components;

public partial class RecognitionManager
{
    [Inject] private IRecognitionRegionSelector RangeSelector { get; set; } = default!;
    private bool _selectingRange;

    private async Task SelectSearchRange(RecognitionItem item)
    {
        if (IsBusy || item.IsFolder || !Items.Contains(item)) return;
        Select(item.Id);
        _selectingRange = true; _error = null;
        _importStatus = "拖动鼠标框选查找范围，Esc 取消。";
        try
        {
            var range = await RangeSelector.SelectAsync();
            if (range is null) { _importStatus = "已取消框选，原查找范围保留。"; return; }
            if (range.Value.Width <= 0 || range.Value.Height <= 0)
                throw new InvalidOperationException("查找范围的宽和高必须大于 0。");
            if (!Items.Contains(item)) throw new InvalidOperationException("识别项目已不存在。");
            var previous = item.Settings;
            var updated = previous.Snapshot();
            updated.X = range.Value.X; updated.Y = range.Value.Y;
            updated.Width = range.Value.Width; updated.Height = range.Value.Height;
            item.Settings = updated;
            try { await Changed(); }
            catch { item.Settings = previous; throw; }
            _importStatus = $"已保存“{item.Name}”的查找范围：{RangeValue(updated)}";
        }
        catch (Exception ex) { _importStatus = null; _error = $"框选查找范围失败：{ex.Message}"; }
        finally { _selectingRange = false; }
    }

    private void EditRecognition(RecognitionItem item)
    {
        if (IsBusy || item.IsFolder || !Items.Contains(item)) return;
        Select(item.Id);
        OpenTraining(item.Settings.Mode, edit: true);
    }
}
