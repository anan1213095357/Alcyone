namespace StateMachine.Components.Pages;

public partial class Home
{
    private StateMachine.Execution.ConditionModel? _pendingScriptCondition;
    private string _pendingConditionTarget = "";
    private HashSet<string> _conditionKeysBeforeEdit = new(StringComparer.Ordinal);
    private void CloseScriptEditor()
    {
        _scriptEditorOpen = false;
        _scriptInitialMethod = null;
        _pendingScriptCondition = null;
        _conditionKeysBeforeEdit.Clear();
    }
    private void BindCreatedCondition(string file)
    {
        var condition = _pendingScriptCondition;
        if (condition is null) return;
        if (!Machine.States.SelectMany(s => s.Outputs).SelectMany(o => o.Conditions).Any(c => ReferenceEquals(c, condition)) || ConditionTarget(condition) != _pendingConditionTarget)
        { _pendingScriptCondition = null; return; }
        var name = Path.GetFileNameWithoutExtension(file);
        var options = ScriptConditionOptions.ToList();
        var matching = options.Where(o => o.Value.StartsWith("script:" + name + ".", StringComparison.Ordinal) || o.Value.Contains("." + name + ".", StringComparison.Ordinal)).ToList();
        var created = matching.FirstOrDefault(o => !_conditionKeysBeforeEdit.Contains(o.Value)) ?? (matching.Count == 1 ? matching[0] : null);
        if (created is null)
        {
            var added = options.Where(o => !_conditionKeysBeforeEdit.Contains(o.Value)).ToList();
            if (added.Count == 1) created = added[0];
        }
        if (created is null) return;
        ChangeConditionTarget(condition, created.Value);
        _pendingScriptCondition = null;
        _conditionKeysBeforeEdit.Clear();
    }
    private string? _scriptInitialMethod;
    private async Task OpenConditionScriptOptionAsync(string value)
    {
        if (_scriptHost is null) return;
        _pendingScriptCondition = null;
        _startWithConditionTemplate = false;
        _startWithActionTemplate = false;
        _scriptInitialMethod = value.StartsWith("script:", StringComparison.Ordinal) ? value[7..] : value;
        if (_scriptEditorOpen && _scriptWorkbench is not null)
            await _scriptWorkbench.OpenMethodAsync(_scriptInitialMethod);
        else _scriptEditorOpen = true;
    }
    private bool _scriptEditorOpen;
    private bool _startWithConditionTemplate;
    private bool _startWithActionTemplate;
    private ScriptWorkbench? _scriptWorkbench;
    private ScriptAiContext? _scriptAiContext;
    private Task OpenScriptEditorAsync() { _scriptInitialMethod = null; _pendingScriptCondition = null; _startWithConditionTemplate = false; _startWithActionTemplate = false; _scriptEditorOpen = _scriptHost is not null; return Task.CompletedTask; }
    private void OpenScriptAi(ScriptAiContext context)
    {
        if (_scriptAiContext?.File != context.File || _scriptAiContext?.Kind != context.Kind)
            ClearAiConversation();
        OpenAiDialog();
        _aiEditInterfaces=false; _aiEditMachine=false; _aiEditScripts=true;
        _scriptAiContext = context;
    }
    private static string ScriptAiKindLabel(string kind) => kind switch { "condition" => "条件函数", "async-condition" => "异步条件函数", "auto" => "自动判断脚本类型", _ => "动作脚本" };
}
