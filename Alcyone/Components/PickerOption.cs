namespace StateMachine.Components;
public sealed record PickerOption(string Value, string Label, string Group = "", string Description = "", bool CanEdit = false);

