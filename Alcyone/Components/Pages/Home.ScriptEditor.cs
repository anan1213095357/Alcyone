namespace StateMachine.Components.Pages;

public partial class Home
{
    private bool _scriptEditorOpen;
    private Task OpenScriptEditorAsync() { _scriptEditorOpen = _scriptHost is not null; return Task.CompletedTask; }
    private void OpenScriptAi() { _aiScope = "script"; OpenAiDialog(); }
}
