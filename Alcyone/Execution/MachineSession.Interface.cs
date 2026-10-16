namespace StateMachine.Execution;

public sealed partial class MachineSession
{
    public Task SetInterfaceValueAsync(InterfaceValueChange change) => _dispatcher.InvokeAsync(() =>
    {
        var control=Machine.Screens.FirstOrDefault(s=>s.Id==change.ScreenId)?.Controls.FirstOrDefault(c=>c.Id==change.ControlId);
        if(control is null || control.Kind is not ("input" or "checkbox" or "select") || string.IsNullOrWhiteSpace(control.VariableName)) return;
        var variable=Machine.Variables.FirstOrDefault(v=>v.Name==control.VariableName) ?? throw new InvalidOperationException("绑定变量不存在："+control.VariableName);
        System.Text.Json.Nodes.JsonNode? value;
        switch(variable.Type)
        {
            case "number":
                if(!double.TryParse(change.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number) || !double.IsFinite(number)) throw new InvalidOperationException("请输入有效数值。");
                value=System.Text.Json.Nodes.JsonValue.Create(number); break;
            case "boolean":
                if(!bool.TryParse(change.Value,out var boolean)) throw new InvalidOperationException("请输入 true 或 false。");
                value=System.Text.Json.Nodes.JsonValue.Create(boolean); break;
            case "json": value=System.Text.Json.Nodes.JsonNode.Parse(change.Value); break;
            default: value=System.Text.Json.Nodes.JsonValue.Create(change.Value); break;
        }
        SetRuntimeVariable(variable.Name,value); Notify();
    });
    public Task UpdateInterfacePlacementAsync(string path,string id,double x,double y,double? width=null,double? height=null) => _dispatcher.InvokeAsync(() =>
    {
        if(!double.IsFinite(x)||!double.IsFinite(y)) return;
        var updated=Clone(Machine);
        var screen=updated.Screens.FirstOrDefault(s=>s.Id==id); if(screen is null) return;
        if (width is { } w && double.IsFinite(w)) screen.Width=Math.Clamp(w,280,4000);
        if (height is { } h && double.IsFinite(h)) screen.Height=Math.Clamp(h,180,2500);
        screen.IsPlaced=true; screen.X=Math.Clamp(x,0,Math.Max(0,4000-screen.Width)); screen.Y=Math.Clamp(y,0,Math.Max(0,2500-screen.Height));
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(temporary,System.Text.Json.JsonSerializer.Serialize(updated,_jsonOptions)); File.Move(temporary,path,true); }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
        Machine=updated; _publishedMachine=Clone(updated); Interlocked.Increment(ref _canvasRevision); _lastNotification=0; Notify();
    });
    private readonly SemaphoreSlim _interfaceActionLock = new(1, 1);
    public async Task InvokeInterfaceActionAsync(string screenId, string controlId)
    {
        if (!await _interfaceActionLock.WaitAsync(0)) return;
        var resume = Snapshot.Runtime.Running;
        try
        {
            if (Snapshot.Executing && !resume) throw new InvalidOperationException("请等待当前任务结束后再执行按钮动作。");
            if (string.IsNullOrWhiteSpace(Snapshot.Machine.Screens.FirstOrDefault(s => s.Id == screenId)?.Controls.FirstOrDefault(c => c.Id == controlId)?.ClickAction.ActionMethod)) return;
            await StopAsync();
            if (_runTask is not null) await _runTask;
            await _dispatcher.InvokeAsync(async () =>
            {
                var control = Machine.Screens.FirstOrDefault(s => s.Id == screenId)?.Controls.FirstOrDefault(c => c.Id == controlId);
                if (control is null || control.Kind != "button") return;
                _executionCancellation.Dispose();
                _executionCancellation = new();
                try { await InvokeActionAsync(control.ClickAction); }
                catch (Exception ex) { AddLog("UI", ex.Message, "error"); throw; }
                finally { Notify(); }
            });
        }
        finally
        {
            if (resume && !_disposed) await StartAsync();
            _interfaceActionLock.Release();
        }
    }
}
