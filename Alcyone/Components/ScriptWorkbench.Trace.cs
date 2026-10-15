using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using StateMachine.Scripting;
using System.Text.Json.Nodes;

namespace StateMachine.Components;

public partial class ScriptWorkbench
{
    [Parameter] public IReadOnlyDictionary<string, JsonNode?>? Globals { get; set; }
    private bool _traceEnabled;
    private ScriptTrace.DebugSession? _debugSession;
    private readonly Dictionary<string, SortedSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private string _watchInput = "";
    private readonly List<string> _watches = new();
    private bool _breakOnException = true;
    private string _lastPausedLocation = "";
    private void SetBreakOnException(bool enabled) { _breakOnException = enabled; if (_debugSession is not null) _debugSession.BreakOnException = enabled; }
    private IEnumerable<ScriptTrace.TraceView> TraceStack(ScriptTrace.TraceView trace)
    {
        for (var depth = 0; depth < 32; depth++)
        {
            yield return trace;
            var parent = _traceFrames.FirstOrDefault(t => t.Id == trace.ParentId);
            if (parent is null) break;
            trace = parent;
        }
    }
    private string WatchValue(string name, ScriptTrace.TraceView? trace)
    {
        if (trace is not null && trace.Values.TryGetValue(name, out var local)) return local;
        if (trace is not null && trace.Globals.TryGetValue(name, out var global)) return global;
        return Globals is not null && Globals.TryGetValue(name, out var value) ? value?.ToJsonString() ?? "null" : Text["当前作用域不可用"];
    }
    private void LoadBreakpoints()
    {
        try
        {
            var path = Path.Combine(Host.ScriptDirectory, ".debug-breakpoints.json");
            if (!File.Exists(path)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(path));
            if (saved is not null) foreach (var entry in saved) _breakpoints[entry.Key] = new(entry.Value.Where(l => l > 0));
        }
        catch (Exception ex) { Report(ex.Message); }
    }
    private void SaveBreakpoints()
    {
        try { File.WriteAllText(Path.Combine(Host.ScriptDirectory, ".debug-breakpoints.json"), System.Text.Json.JsonSerializer.Serialize(_breakpoints)); }
        catch (Exception ex) { Report(ex.Message); }
    }
    [JSInvokable] public async Task ToggleBreakpoint(string file, int line)
    {
        if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase) || line < 1) return;
        if (_dirty.Contains(file)) { Report(Text["请先保存并编译，再设置断点。"]); return; }
        if (!_breakpoints.TryGetValue(file, out var lines)) _breakpoints[file] = lines = new();
        if (!lines.Remove(line))
        {
            var source = await File.ReadAllTextAsync(FilePath(file));
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
            var candidates = ScriptTraceInstrumentation.BreakableLines(tree, line);
            var bound = candidates.FirstOrDefault(l => l >= line);
            if (bound == 0) { Report(Text["此行没有可暂停的脚本语句。"]); return; }
            lines.Add(bound);
        }
        _debugSession?.SetBreakpoints(FilePath(file), lines);
        SaveBreakpoints();
        if (_editor is not null) await _editor.InvokeVoidAsync("breakpoints", file, lines.ToArray());
        StateHasChanged();
    }
    [JSInvokable] public Task DebugShortcut(string command) => InvokeAsync(() => DebugCommand(command));
    private void DebugCommand(string command)
    {
        if (!_traceEnabled) { if (command == "continue") ToggleTrace(); return; }
        if (_debugSession is null) return;
        _debugSession.BreakOnException = _breakOnException;
        _debugSession.Command(command);
    }
    private async Task ClearBreakpointsAsync()
    {
        foreach (var file in _breakpoints.Keys.ToArray())
        {
            if (_files.Contains(file)) _debugSession?.SetBreakpoints(FilePath(file), []);
            if (_editor is not null) await _editor.InvokeVoidAsync("breakpoints", file, Array.Empty<int>());
        }
        _breakpoints.Clear(); SaveBreakpoints();
    }
    private async Task GoToBreakpointAsync(string file, int line)
    {
        if (_editor is null || _busy || !_files.Contains(file)) return;
        await OpenFileAsync(file);
        await _editor.InvokeVoidAsync("reveal", line, 1);
    }
    private void AddWatch()
    {
        var name = _watchInput.Trim();
        if (name.Length > 0 && !_watches.Contains(name)) _watches.Add(name);
        _watchInput = "";
    }
    private IDisposable? _traceSubscription;
    private CancellationTokenSource? _traceCancellation;
    private ScriptTrace.TraceView[] _traceFrames = [];
    private long _traceSelected;
    private void ToggleTrace()
    {
        if (_traceEnabled) { StopTrace(); return; }
        if (_dirty.Count > 0) { Report(Text["请先保存并编译，再启用调试。"]); return; }
        if (!Host.SupportsScriptDebugging) { Report(Text["当前脚本未生成调试信息，请先保存并编译。"]); return; }
        try { _debugSession = ScriptTrace.StartDebug(Host.ScriptDirectory); }
        catch (Exception ex) { Report(ex.Message); return; }
        foreach (var entry in _breakpoints.Where(e => _files.Contains(e.Key))) _debugSession.SetBreakpoints(FilePath(entry.Key), entry.Value);
        _debugSession.BreakOnException = _breakOnException;
        _debugSession.SetGlobalNames(Globals?.Keys ?? Enumerable.Empty<string>());
        _traceEnabled = true;
        _traceSubscription = ScriptTrace.Subscribe();
        _traceCancellation = new();
        _ = RefreshTraceAsync(_traceCancellation.Token);
    }
    private void StopTrace()
    {
        _traceEnabled = false;
        _debugSession?.Dispose(); _debugSession = null;
        _lastPausedLocation = "";
        _traceSubscription?.Dispose(); _traceSubscription = null;
        _traceCancellation?.Cancel(); _traceCancellation?.Dispose(); _traceCancellation = null;
        if (_editor is not null && !_disposed)
        {
            _ = _editor.InvokeVoidAsync("traceLine", 0);
            _ = _editor.InvokeVoidAsync("readonly", _busy);
        }
    }
    private async Task RefreshTraceAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            do
            {
                await InvokeAsync(async () =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    var directory = Path.GetFullPath(Host.ScriptDirectory) + Path.DirectorySeparatorChar;
                    _traceFrames = ScriptTrace.Snapshot().Where(t => Path.GetFullPath(t.File).StartsWith(directory, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var paused = _traceFrames.FirstOrDefault(t => t.Id == _debugSession?.PausedId);
                    var current = paused ?? _traceFrames.FirstOrDefault(t => t.Id == _traceSelected) ?? _traceFrames.FirstOrDefault();
                    if (paused is not null && _lastPausedLocation != $"{paused.Id}:{paused.Line}" && !_busy)
                    {
                        _lastPausedLocation = $"{paused.Id}:{paused.Line}";
                        await SelectTraceAsync(paused);
                    }
                    if (_editor is not null)
                    {
                        await _editor.InvokeVoidAsync("readonly", true);
                        await _editor.InvokeVoidAsync("traceLine", current is not null && !_dirty.Contains(_active ?? "") && string.Equals(Path.GetRelativePath(Host.ScriptDirectory, current.File).Replace('\\', '/'), _active, StringComparison.OrdinalIgnoreCase) ? current.Line : 0);
                    }
                    StateHasChanged();
                });
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task SelectTraceAsync(ScriptTrace.TraceView trace)
    {
        if (_editor is null || _busy) return;
        var file = Path.GetRelativePath(Host.ScriptDirectory, trace.File).Replace('\\', '/');
        if (!_files.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
        _traceSelected = trace.Id;
        await OpenFileAsync(file);
        if (trace.Line > 0) await _editor.InvokeVoidAsync("reveal", trace.Line, 1);
    }
}
