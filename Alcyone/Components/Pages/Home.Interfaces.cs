using StateMachine.Execution;
using Microsoft.JSInterop;
namespace StateMachine.Components.Pages;
public partial class Home
{
    private static void NormalizeInterfaces(List<InterfaceScreenModel> screens)
    {
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in screens)
        {
            if (string.IsNullOrWhiteSpace(screen.Id)) screen.Id=Guid.NewGuid().ToString("N");
            if (!ids.Add(screen.Id)) throw new InvalidOperationException("界面 ID 重复。");
            screen.Width=double.IsFinite(screen.Width) ? Math.Clamp(screen.Width,240,7680) : 960;
            screen.Height=double.IsFinite(screen.Height) ? Math.Clamp(screen.Height,160,4320) : 640;
            screen.X=double.IsFinite(screen.X) ? Math.Clamp(screen.X,0,Math.Max(0,4000-screen.Width)) : 0;
            screen.Y=double.IsFinite(screen.Y) ? Math.Clamp(screen.Y,0,Math.Max(0,2500-screen.Height)) : 0;
            screen.BackgroundOpacity=double.IsFinite(screen.BackgroundOpacity) ? Math.Clamp(screen.BackgroundOpacity,0,1) : 0.25;
            screen.Background=InterfaceColor(screen.Background,"#101b2b");
            screen.Controls ??=new();
            // Older configurations put every control on the same layer. Keep panels behind interactive controls.
            if(screen.Controls.Count>0 && screen.Controls.All(c=>c.ZIndex==10))
                foreach(var panel in screen.Controls.Where(c=>c.Kind=="panel")) panel.ZIndex=0;
            var controls=new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in screen.Controls)
            {
                if (string.IsNullOrWhiteSpace(c.Id)) c.Id=Guid.NewGuid().ToString("N");
                if (!controls.Add(c.Id)) throw new InvalidOperationException("控件 ID 重复。");
                if (!new[]{"button","label","input","checkbox","select","panel"}.Contains(c.Kind)) throw new InvalidOperationException("不支持的控件类型："+c.Kind);
                c.Width=double.IsFinite(c.Width) ? Math.Clamp(c.Width,24,screen.Width) : 160;
                c.Height=double.IsFinite(c.Height) ? Math.Clamp(c.Height,24,screen.Height) : 40;
                c.X=double.IsFinite(c.X) ? Math.Clamp(c.X,0,screen.Width-c.Width) : 0;
                c.Y=double.IsFinite(c.Y) ? Math.Clamp(c.Y,0,screen.Height-c.Height) : 0;
                c.BackgroundOpacity=double.IsFinite(c.BackgroundOpacity) ? Math.Clamp(c.BackgroundOpacity,0,1) : 0.25;
                c.Color=InterfaceColor(c.Color,"#d9e9fa"); c.Background=InterfaceColor(c.Background,"#203b56");
                c.Options ??=new(); c.ClickAction ??=new(); c.ClickAction.ActionArguments ??=new();
            }
        }
    }
    private static string InterfaceColor(string? value,string fallback) => value is { Length:7 } && value[0]=='#' && value.Skip(1).All(Uri.IsHexDigit) ? value : fallback;
    private Microsoft.JSInterop.IJSObjectReference? _interfaceCanvasModule;
    private Microsoft.JSInterop.DotNetObjectReference<Home>? _interfaceCanvasReference;
    private async Task InitializeInterfaceCanvasAsync()
    {
        _interfaceCanvasModule=await JS.InvokeAsync<Microsoft.JSInterop.IJSObjectReference>("import","./js/interface-canvas.js");
        _interfaceCanvasReference=Microsoft.JSInterop.DotNetObjectReference.Create(this);
        await _interfaceCanvasModule.InvokeVoidAsync("attach",_interfaceCanvasReference);
    }
    [Microsoft.JSInterop.JSInvokable] public async Task PlaceInterface(string configKey,string id,double x,double y)
    {
        if(configKey!=_configKey || _switchingConfig || !double.IsFinite(x) || !double.IsFinite(y)) return;
        var screen=Machine.Screens.FirstOrDefault(s=>s.Id==id); if(screen is null) return;
        if(IsWallpaperView && _session is not null)
        {
            await _session.UpdateInterfacePlacementAsync(GetConfigFilePath(_configKey),id,x,y);
            ApplyRuntimeSnapshot();
        }
        else { screen.IsPlaced=true; screen.X=Math.Clamp(x,0,Math.Max(0,4000-screen.Width)); screen.Y=Math.Clamp(y,0,Math.Max(0,2500-screen.Height)); await SaveConfigAsync(); }
        await InvokeAsync(StateHasChanged);
    }
    private async Task RemovePlacedInterfaceAsync(string id)
    {
        var screen=Machine.Screens.FirstOrDefault(s=>s.Id==id); if(screen is null) return;
        screen.IsPlaced=false; await SaveConfigAsync();
    }
    private async Task SetInterfaceValueAsync(InterfaceValueChange change)
    {
        if(_session is not null) { await _session.SetInterfaceValueAsync(change); ApplyRuntimeSnapshot(); }
    }
    private async Task InvokePlacedInterfaceAsync(string id,InterfaceControlModel control)
    {
        if(!IsWallpaperView) await SaveConfigAsync();
        if(_session is not null) await _session.InvokeInterfaceActionAsync(id,control.Id);
    }
    private bool _interfaceMode;
    private string? _interfaceId;
    private InterfaceScreenModel? CurrentInterface => Machine.Screens.FirstOrDefault(s => s.Id == _interfaceId);
    private List<InterfaceActionOption> InterfaceActions => ActionMethods.Select(a => new InterfaceActionOption(a.Key,a.DisplayName,a.Group,a.Parameters.Select(p => new InterfaceParameterOption(p.Name,p.DisplayName,p.DefaultLiteral,p.EnumValues.Length>0 ? p.EnumValues : p.EditorKind=="boolean" ? new[]{"true","false"} : Array.Empty<string>())).ToList())).ToList();
    private async Task AddInterfaceAsync()
    {
        var screen=new InterfaceScreenModel { Name=L["界面"]+" "+(Machine.Screens.Count+1) };
        Machine.Screens.Add(screen); SelectInterface(screen.Id); await SaveConfigAsync();
    }
    private void SelectInterface(string id) { _configMenuOpen=false; _appSettingsOpen=false; _interfaceId=id; _interfaceMode=true; }
    private async Task DeleteInterfaceAsync(string id) { Machine.Screens.RemoveAll(s=>s.Id==id); if(_interfaceId==id) { _interfaceId=Machine.Screens.FirstOrDefault()?.Id; _interfaceMode=_interfaceId is not null; } await SaveConfigAsync(); }
    private void OpenInterfaceAi() { OpenAiDialog(); _aiScope="both"; }
    private void OpenInterfaceScript() { _startWithConditionTemplate=false; _startWithActionTemplate=true; _scriptEditorOpen=_scriptHost is not null; }
    private async Task InvokeInterfaceControlAsync(InterfaceControlModel control)
    {
        await SaveConfigAsync();
        if (_session is not null && CurrentInterface is { } screen) await _session.InvokeInterfaceActionAsync(screen.Id,control.Id);
    }
}
