using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using StateMachine.Scripting;
using System.Text.RegularExpressions;

namespace StateMachine.Components;

public partial class ScriptWorkbench
{
    [Parameter, EditorRequired] public StateScriptHost Host { get; set; } = default!;
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnAi { get; set; }
    private ElementReference _editorElement, _cursorElement;
    private IJSObjectReference? _module, _editor;
    private DotNetObjectReference<ScriptWorkbench>? _reference;
    private bool _ready, _busy, _disposed, _docsOpen = true;
    private string[] _files = [];
    private readonly List<string> _tabs = [];
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private string? _active, _confirm;
    private string _fileQuery = "", _docQuery = "", _docCategory = "全部";
    private string _status = "编辑器就绪。Ctrl+S / F8 保存全部修改并编译；Ctrl+F 查找。", _outputTime = "READY";
    private List<CompileError> _errors = [];
    private IEnumerable<ApiDoc> FilteredDocs => Docs.Where(d => (_docCategory == "全部" || d.Category == _docCategory) &&
        (d.Title + d.Description + d.Signature).Contains(_docQuery, StringComparison.OrdinalIgnoreCase));

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _files = Directory.GetFiles(Host.ScriptDirectory, "*.csx", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Host.ScriptDirectory, p).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./editor/workbench.js");
            if (_disposed) return;
            _reference = DotNetObjectReference.Create(this);
            _editor = await _module.InvokeAsync<IJSObjectReference>("create", _editorElement, _cursorElement, _reference,
                Docs.Select(d => new { d.Title, d.Description, d.Signature, d.Prefix, Insert = d.Example }));
            _ready = true;
            if (_files.Length > 0) await OpenFileAsync(_files[0]);
        }
        catch (Exception ex) { Report("编辑器加载失败：" + ex.Message); }
        if (!_disposed) StateHasChanged();
    }

    private string FilePath(string relative)
    {
        if (!_files.Contains(relative, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("未知脚本文件。");
        var root = Path.GetFullPath(Host.ScriptDirectory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("无效脚本路径。");
        return full;
    }

    private async Task OpenFileAsync(string file)
    {
        if (_editor is null || _busy) return;
        try
        {
            var source = _tabs.Contains(file) ? "" : await File.ReadAllTextAsync(FilePath(file));
            await _editor.InvokeVoidAsync("open", file, source);
            _active = file;
            if (!_tabs.Contains(file)) _tabs.Add(file);
            await _editor.InvokeVoidAsync("diagnostics", _errors);
        }
        catch (Exception ex) { Report(ex.Message); }
    }

    [JSInvokable] public Task DirtyChanged(string path, bool dirty) => InvokeAsync(() =>
    {
        if (_disposed) return;
        if (dirty) _dirty.Add(path); else _dirty.Remove(path);
        StateHasChanged();
    });
    [JSInvokable] public Task CompileShortcut() => InvokeAsync(CompileAsync);
    private async Task EditorActionAsync(string id) { if (_editor is not null) await _editor.InvokeVoidAsync("action", id); }
    private async Task InsertExampleAsync(ApiDoc doc) { if (_editor is not null) await _editor.InvokeVoidAsync("insert", doc.Example); }
    private void Report(string message) { _status = message; _outputTime = DateTime.Now.ToString("HH:mm:ss"); }

    private async Task CompileAsync()
    {
        if (_editor is null || _busy) return;
        _busy = true;
        _errors.Clear();
        Report("正在保存脚本并编译…");
        try
        {
            await _editor.InvokeVoidAsync("readonly", true);
            var drafts = await _editor.InvokeAsync<ScriptDraft[]>("dirtyFiles");
            // Check every conflict before writing any file, including edits made by the AI panel.
            foreach (var draft in drafts)
                if (await File.ReadAllTextAsync(FilePath(draft.Path)) != draft.Original)
                    throw new InvalidOperationException($"{draft.Path} 已被外部修改。请备份当前编辑内容，点击重新读取后合并修改。");
            foreach (var draft in drafts)
            {
                await File.WriteAllTextAsync(FilePath(draft.Path), draft.Source);
                await _editor.InvokeVoidAsync("markSaved", draft.Path, draft.Source);
                _dirty.Remove(draft.Path);
            }
            var result = await Task.Run(Host.Reload);
            if (result.Success) Report($"编译成功 · {result.ScriptCount} 个脚本 / {result.ActionClassCount} 个动作类 · 已热重载");
            else
            {
                _errors = result.Errors.Select(ParseError).ToList();
                Report($"编译失败 · {_errors.Count} 个错误 · 仍使用上次成功的动作。点击错误定位代码。");
            }
            await _editor.InvokeVoidAsync("diagnostics", _errors);
        }
        catch (Exception ex) { Report("操作失败：" + ex.Message); _errors.Add(new("", 1, 1, ex.Message)); }
        finally
        {
            _busy = false;
            if (!_disposed) { await _editor.InvokeVoidAsync("readonly", false); StateHasChanged(); }
        }
    }

    private static CompileError ParseError(string message)
    {
        var match = Regex.Match(message, @"^(.*)\((\d+),(\d+)\)\s*(.*)$");
        return match.Success ? new(match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), match.Groups[4].Value) : new("", 1, 1, message);
    }
    private async Task GoToErrorAsync(CompileError error)
    {
        var file = _files.FirstOrDefault(f => f == error.File || Path.GetFileName(f) == error.File);
        if (file is null || _editor is null) return;
        await OpenFileAsync(file);
        await _editor.InvokeVoidAsync("reveal", error.Line, error.Column);
    }
    private void RequestReload() { if (_active is not null) _confirm = "reload"; }
    private async Task RequestCloseAsync()
    {
        if (_editor is not null && (await _editor.InvokeAsync<ScriptDraft[]>("dirtyFiles")).Length > 0) _confirm = "close";
        else await OnClose.InvokeAsync();
    }
    private async Task DiscardAsync()
    {
        if (_confirm == "close") { await OnClose.InvokeAsync(); return; }
        _confirm = null;
        if (_active is null || _editor is null) return;
        try
        {
            await _editor.InvokeVoidAsync("reload", _active, await File.ReadAllTextAsync(FilePath(_active)));
            _dirty.Remove(_active); Report("已重新读取 " + _active);
        }
        catch (Exception ex) { Report(ex.Message); }
    }
    private async Task SaveAndCloseAsync()
    {
        _confirm = null;
        await CompileAsync();
        if (_errors.Count == 0 && _dirty.Count == 0) await OnClose.InvokeAsync();
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_editor is not null) { await _editor.InvokeVoidAsync("dispose"); await _editor.DisposeAsync(); }
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        finally { _reference?.Dispose(); }
    }
    public sealed record ScriptDraft(string Path, string Source, string Original);
    private sealed record CompileError(string File, int Line, int Column, string Message);
    private sealed record ApiDoc(string Category, string Title, string Description, string Signature, string Notes, string Example, string Prefix = "Api");
}
