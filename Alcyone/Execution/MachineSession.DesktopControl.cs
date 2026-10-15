namespace StateMachine.Execution;

public sealed partial class MachineSession
{
    public async Task SelectDesktopStateAsync(string stateId)
    {
        var resume = Snapshot.Runtime.Running;
        await StopAsync();
        if (_runTask is not null) await _runTask;
        await _dispatcher.InvokeAsync(async () =>
        {
            if (_executing) throw new InvalidOperationException("状态机正在执行单步，请稍后重试。");
            var target = Machine.States.FirstOrDefault(s => s.Id == stateId) ?? throw new InvalidOperationException("找不到目标状态。");
            BeginExecution();
            try
            {
                if (Runtime.CurrentStates.TryGetValue(target.RegionId, out var oldId) && Machine.States.FirstOrDefault(s => s.Id == oldId) is { } old)
                    await InvokeLifecycleAsync(old.AfterEnterAction);
                await InvokeLifecycleAsync(target.BeforeLeaveAction);
                Runtime.CurrentStates[target.RegionId] = target.Id;
                Runtime.EnteredStates[target.RegionId] = target.Id;
                _recognitionRevision++; _recognitionResults.Clear();
                AddLog("DESKTOP AI", $"切换到 {target.Name}", "good");
            }
            finally { EndExecution(); Notify(); }
        });
        if (resume) await StartAsync();
    }
    public Task EditDesktopStateAsync(string path, string stateId, string? name, bool? isStart) => DispatchAsync(() =>
    {
        var updated = Clone(Machine);
        var target = updated.States.FirstOrDefault(s => s.Id == stateId) ?? throw new InvalidOperationException("找不到目标状态。");
        if (name is not null) { if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new InvalidOperationException("状态名称不能为空或超过 120 字。"); target.Name = name.Trim(); }
        if (isStart is true) foreach (var state in updated.States.Where(s => s.RegionId == target.RegionId)) state.IsStart = state.Id == target.Id;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(updated, _jsonOptions)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Machine = updated; _publishedMachine = Clone(updated); Interlocked.Increment(ref _canvasRevision);
        _lastNotification = 0; Notify();
    });
}
