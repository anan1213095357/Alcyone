namespace StateMachine.Scripting.Metadata;
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class StateActionAttribute(string displayName, string group = "二次开发") : Attribute
{
    public string DisplayName { get; } = displayName;
    public string Group { get; } = group;
}
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public class StateParameterAttribute(string displayName) : Attribute
{
    public string DisplayName { get; } = displayName;
}
