namespace StateMachine.Execution;

public sealed class InterfaceScreenModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Interface";
    public bool IsPlaced { get; set; }
    public double X { get; set; } = 160;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 640;
    public double BackgroundOpacity { get; set; } = 0.25;
    public bool TransparentBackground { get; set; }
    public string Background { get; set; } = "#101b2b";
    public List<InterfaceControlModel> Controls { get; set; } = new();
}
public sealed class InterfaceControlModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int ZIndex { get; set; } = 10;
    public string VariableName { get; set; } = "";
    public string Kind { get; set; } = "button";
    public string Name { get; set; } = "control";
    public string Text { get; set; } = "Button";
    public double X { get; set; } = 32;
    public double Y { get; set; } = 32;
    public double Width { get; set; } = 160;
    public double Height { get; set; } = 40;
    public string Color { get; set; } = "#d9e9fa";
    public double BackgroundOpacity { get; set; } = 0.25;
    public bool TransparentBackground { get; set; }
    public string Background { get; set; } = "#203b56";
    public List<string> Options { get; set; } = new();
    public StateActionCallModel ClickAction { get; set; } = new();
}
public sealed record InterfaceActionOption(string Key, string Name, string Group, List<InterfaceParameterOption> Parameters);
public sealed record InterfaceParameterOption(string Name, string Label, string DefaultValue, string[] Choices);

public sealed record InterfaceValueChange(string ScreenId,string ControlId,string Value);
