namespace StateMachine.Components;

public sealed record ScriptAiContext(string Kind, string File, string Source, string Selection, string[] Errors);
