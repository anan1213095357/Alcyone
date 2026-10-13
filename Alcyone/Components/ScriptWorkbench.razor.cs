using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using StateMachine.Scripting;
using System.Text.RegularExpressions;

namespace StateMachine.Components;

public partial class ScriptWorkbench
{
    [Parameter, EditorRequired] public StateScriptHost Host { get; set; } = default!;
    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback<ScriptAiContext> OnAi { get; set; }
    [Parameter] public bool StartWithConditionTemplate { get; set; }
    [Parameter] public bool StartWithActionTemplate { get; set; }
    private ElementReference _editorElement, _cursorElement;
    private IJSObjectReference? _module, _editor;
    private DotNetObjectReference<ScriptWorkbench>? _reference;
    private bool _ready, _busy, _disposed, _docsOpen = true;
    private string[] _files = [];
    private readonly List<string> _tabs = [];
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    [Inject] private StateMachine.Localization.UiText Text { get; set; } = default!;
    private string? _deleteFile;
    private string? _active, _confirm;
    private string _fileQuery = "", _docQuery = "", _docCategory = "全部";
    private string _status = "编辑器就绪。Ctrl+S / F8 保存全部修改并编译；Ctrl+F 查找。", _outputTime = "READY";
    private List<CompileError> _errors = [];
    private bool _newScriptOpen;
    private string _newScriptName = "MyActions", _newScriptError = "";
    private string _newScriptTemplate = "action";
    private string _aiScriptKind = "auto";

    protected override void OnInitialized() => Host.DependencyProgress += OnDependencyProgress;
    private void OnDependencyProgress(string status)
    {
        if(!_disposed)_=InvokeAsync(()=>{Report(status);StateHasChanged();});
    }
    private async Task RequestAiAsync()
    {
        if (!_ready || _editor is null || _busy) return;
        var draft = await _editor.InvokeAsync<EditorAiDraft>("aiContext");
        var kind = _newScriptOpen ? _newScriptTemplate : _aiScriptKind;
        if (kind == "auto")
        {
            var method = Regex.Match(draft.Source, @"\[StateCondition(?:Attribute)?\([^\]]*\)\]\s*public\s+(?<type>bool|Task\s*<\s*bool\s*>|ValueTask\s*<\s*bool\s*>|async\s+Task\s*<\s*bool\s*>|async\s+ValueTask\s*<\s*bool\s*>)");
            kind = method.Success && !draft.Source.Contains("[StateAction") ? (method.Groups["type"].Value == "bool" ? "condition" : "async-condition")
                : draft.Source.Contains("[StateAction") && !draft.Source.Contains("[StateCondition") ? "action" : "auto";
        }
        var file = _newScriptOpen ? _newScriptName.Trim() : _active ?? "";
        if (_newScriptOpen && !file.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) file += ".csx";
        await OnAi.InvokeAsync(new ScriptAiContext(kind, file, _newScriptOpen ? "" : draft.Source, _newScriptOpen ? "" : draft.Selection, _errors.Select(e => e.Message).ToArray()));
    }

    public async Task AcceptAiChangesAsync(IEnumerable<string> files)
    {
        if (_editor is null || _disposed) return;
        _files = Directory.GetFiles(Host.ScriptDirectory, "*.csx", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Host.ScriptDirectory, p).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        string? first = null;
        foreach (var file in files)
        {
            var path = file.Replace('\\', '/');
            if (!_files.Contains(path)) continue;
            first ??= path;
            await _editor.InvokeVoidAsync("reload", path, await File.ReadAllTextAsync(FilePath(path)));
            _dirty.Remove(path);
        }
        _newScriptOpen = false;
        _errors.Clear();
        await _editor.InvokeVoidAsync("diagnostics", _errors);
        if (first is not null) await OpenFileAsync(first);
        Report("AI 代码已应用，编译通过。");
        StateHasChanged();
    }

    private sealed class EditorAiDraft
    {
        public string Source { get; set; } = "";
        public string Selection { get; set; } = "";
    }

    private void RequestNewScript()
    {
        ChangeScriptTemplate(StartWithConditionTemplate ? "condition" : "action");
        _newScriptError = "";
        _newScriptOpen = true;
    }

    private void ChangeScriptTemplate(string template)
    {
        _newScriptTemplate = template;
        if (_newScriptName is "MyActions" or "MyCondition" or "MyAsyncCondition")
            _newScriptName = template switch { "condition" => "MyCondition", "async-condition" => "MyAsyncCondition", _ => "MyActions" };
        _newScriptError = "";
    }

    private async Task CreateScriptAsync()
    {
        if (_busy || !_ready) return;
        var name = _newScriptName.Trim();
        if (name.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") ||
            !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name) ||
            Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None ||
            Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase))
        {
            _newScriptError = "请输入有效的 C# 类名（英文字母、数字、下划线，不能以数字开头或使用关键字）。";
            return;
        }
        var file = name + ".csx";
        var source = $$"""
            // 保存并编译后，可在状态卡片中选择此动作。
            public sealed class {{name}} : StateScript
            {
                [StateAction("{{name}} 示例动作", "自定义")]
                public async Task Run([StateParameter("等待毫秒")] int milliseconds = 300)
                {
                    Log("开始执行");
                    await Api.Delay(milliseconds);
                    Vars.Set("{{name}}执行次数", Vars.Get<int>("{{name}}执行次数", 0) + 1);
                    Log("动作完成", level: "good");
                }

            }
            """;
        if (_newScriptTemplate == "condition") source = $$"""
            // 1. 修改下面的判断逻辑，true 表示满足条件，false 表示不满足。
            // 2. 保存并编译，在卡片的条件 API → 自定义条件中选择此函数。
            public sealed class {{name}} : StateScript
            {
                [StateCondition("{{name}} 条件判断", "自定义")]
                public bool Check()
                {
                    // 示例：全局变量“就绪”为 true 时满足；变量不存在时默认 false。
                    // 也可使用 Vars.Get<int>("次数", 0) >= 3 等表达式。
                    return Vars.Get<bool>("就绪", false);
                }
            }
            """;
        else if (_newScriptTemplate == "async-condition") source = $$"""
            // 1. 在函数内执行异步操作，再返回判断结果。
            // 2. 状态机会等待函数执行完毕；true 表示满足，false 表示不满足。
            // 3. 保存并编译，在条件 API → 自定义条件中选择此函数。
            public sealed class {{name}} : StateScript
            {
                [StateCondition("{{name}} 异步条件判断", "自定义")]
                public async Task<bool> CheckAsync()
                {
                    // 将这里替换为你的异步操作，等待结束后再判断。
                    await Api.Delay(100);
                    return Vars.Get<bool>("就绪", false);
                }
            }
            """;
        try
        {
            // CreateNew prevents an existing script from being overwritten.
            await using (var stream = new FileStream(Path.Combine(Host.ScriptDirectory, file), FileMode.CreateNew, FileAccess.Write))
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(source);
            _files = _files.Append(file).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _fileQuery = "";
            _newScriptOpen = false;
            await OpenFileAsync(file);
            await CompileAsync();
        }
        catch (IOException ex) { _newScriptError = File.Exists(Path.Combine(Host.ScriptDirectory, file)) ? "同名脚本已存在，请换一个名称。" : ex.Message; }
        catch (Exception ex) { _newScriptError = "新建失败：" + ex.Message; }
    }
    private IEnumerable<ApiDoc> FilteredDocs => Docs.Where(d => (_docCategory == "全部" || d.Category == _docCategory) &&
        (d.Title + Text[d.Description] + d.Signature).Contains(_docQuery, StringComparison.OrdinalIgnoreCase));

    private string? _editorLanguage;
    private object[] LocalizedDocs => Docs.Select(d => (object)new { d.Title, Description = Text[d.Description], d.Signature, d.Prefix, Insert = d.Example }).ToArray();
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_ready && _editor is not null && _editorLanguage != Text.Language)
            {
                _editorLanguage = Text.Language;
                await _editor.InvokeVoidAsync("language", Text.Language, LocalizedDocs);
            }
            return;
        }
        try
        {
            _files = Directory.GetFiles(Host.ScriptDirectory, "*.csx", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Host.ScriptDirectory, p).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./editor/workbench.js");
            if (_disposed) return;
            _reference = DotNetObjectReference.Create(this);
            _editor = await _module.InvokeAsync<IJSObjectReference>("create", _editorElement, _cursorElement, _reference,
                LocalizedDocs, Text.Language);
            _editorLanguage = Text.Language;
            _ready = true;
            if (_files.Length > 0) await OpenFileAsync(_files[0]);
            if (StartWithConditionTemplate || StartWithActionTemplate) RequestNewScript();
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
    private async Task DeleteScriptAsync()
    {
        if (_busy || _editor is null || _deleteFile is null) return;
        var file = _deleteFile;
        _busy = true;
        string? backup = null;
        string? path = null;
        try
        {
            await _editor.InvokeVoidAsync("readonly", true);
            path = FilePath(file);
            var folder = Path.Combine(Host.ScriptDirectory, ".deleted");
            Directory.CreateDirectory(folder);
            backup = Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(file) + ".bak");
            File.Move(path, backup);
            var result = await Task.Run(Host.Reload);
            if (!result.Success)
            {
                File.Move(backup, path);
                backup = null;
                await Task.Run(Host.Reload);
                _errors = result.Errors.Select(ParseError).ToList();
                await _editor.InvokeVoidAsync("diagnostics", _errors);
                Report(Text["删除会导致其他脚本编译失败，已恢复文件。"]);
                return;
            }
            await _editor.InvokeVoidAsync("remove", file);
            _files = _files.Where(f => f != file).ToArray();
            _tabs.Remove(file);
            _dirty.Remove(file);
            if (_active == file) _active = null;
            _errors.Clear();
            await _editor.InvokeVoidAsync("diagnostics", _errors);
            Report(Text["已删除脚本并完成热重载："] + file);
        }
        catch (Exception ex)
        {
            if (backup is not null && path is not null && File.Exists(backup) && !File.Exists(path))
            {
                File.Move(backup, path);
                await Task.Run(Host.Reload);
            }
            Report(ex.Message);
        }
        finally
        {
            _deleteFile = null;
            _busy = false;
            if (!_disposed) await _editor.InvokeVoidAsync("readonly", false);
        }
        if (_active is null && _files.Length > 0) await OpenFileAsync(_tabs.LastOrDefault() ?? _files[0]);
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
    public override async ValueTask DisposeAsync()
    {
        Host.DependencyProgress -= OnDependencyProgress;
        _disposed = true;
        try
        {
            if (_editor is not null) { await _editor.InvokeVoidAsync("dispose"); await _editor.DisposeAsync(); }
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        finally { _reference?.Dispose(); await base.DisposeAsync(); }
    }
    public sealed record ScriptDraft(string Path, string Source, string Original);
    private sealed record CompileError(string File, int Line, int Column, string Message);
    private sealed record ApiDoc(string Category, string Title, string Description, string Signature, string Notes, string Example, string Prefix = "Api");
}
