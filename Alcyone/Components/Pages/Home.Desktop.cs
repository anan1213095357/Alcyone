using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace StateMachine.Components.Pages;

public partial class Home
{
    [Inject] private DesktopWallpaperService Desktop { get; set; } = default!;
    [Inject] private DesktopPreferences DesktopPreferences { get; set; } = default!;
    [Parameter] public string? WallpaperToken { get; set; }
    private bool IsWallpaperView => WallpaperToken is not null;
    private bool _desktopMode => IsWallpaperView;
    private bool _desktopBusy, _desktopLayoutPending;
    private bool _toolbarMenuOpen;
    private bool? _lastAnimationPause;

    private async Task SyncAnimationPauseAsync()
    {
        var paused = Desktop.AnimationsPaused || (!IsWallpaperView && Desktop.EditorHidden);
        if (_lastAnimationPause == paused) return;
        try
        {
            await JS.InvokeVoidAsync("alcyoneAnimation.setNativePaused", paused);
            _lastAnimationPause = paused;
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
    }
    private string? _desktopError;
    private string DesktopSummary => L["每个屏幕显示一份完整画板，双击托盘图标或按 Ctrl+Alt+F10 打开设置。"] + "\n" +
        string.Join("\n", Desktop.Displays.Select(display => $"{display.Device} · {display.Width} × {display.Height} · {display.Dpi * 100 / 96}%"));

    private async Task SetAsDesktopAsync()
    {
        if (IsWallpaperView || _desktopBusy || !_autoSaveReady || _session is null) return;
        _desktopBusy = true;
        _desktopError = null;
        _configMenuOpen = false;
        try
        {
            await SaveConfigCoreAsync(reportFailure: true);
            await Desktop.SetDesktopAsync(true, new(_configKey, _session));
        }
        catch (Exception ex) { _desktopError = ex.Message; }
        finally { _desktopBusy = false; }
    }

    private async Task StopDesktopAsync()
    {
        if (IsWallpaperView || _desktopBusy) return;
        _desktopBusy = true;
        try { await Desktop.SetDesktopAsync(false); }
        catch (Exception ex) { _desktopError = ex.Message; }
        finally { _desktopBusy = false; }
    }

    private void AttachWallpaperSession()
    {
        var presentation = Desktop.GetPresentation(WallpaperToken!);
        if (!ReferenceEquals(_session, presentation?.Session))
        {
            if (_session is not null) _session.Changed -= OnRuntimeChanged;
            _session = presentation?.Session;
            if (_session is not null) _session.Changed += OnRuntimeChanged;
        }
        _configKey = presentation?.ConfigKey ?? "";
        if (_session is not null)
        {
            _scriptContext = _session.ScriptContext;
            _scriptHost = _session.ScriptHost;
            ApplyRuntimeSnapshot();
        }
        else { Machine = new(); Runtime = new(); }
    }

    private void OnDesktopChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            await SyncAnimationPauseAsync();
            if (_disposed) return;
            if (IsWallpaperView) AttachWallpaperSession();
            if (!Desktop.AnimationsPaused) ApplyRuntimeSnapshot();
            _desktopError = Desktop.Error;
            _desktopLayoutPending = IsWallpaperView;
            StateHasChanged();
        });
    }
}
