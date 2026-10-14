namespace StateMachine.Components.Pages;

public partial class Home
{
    private bool _scriptEditorOpen;
    private bool _startWithConditionTemplate;
    private bool _startWithActionTemplate;
    private ScriptWorkbench? _scriptWorkbench;
    private ScriptAiContext? _scriptAiContext;
    private Task OpenScriptEditorAsync() { _startWithConditionTemplate = false; _startWithActionTemplate = false; _scriptEditorOpen = _scriptHost is not null; return Task.CompletedTask; }
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
