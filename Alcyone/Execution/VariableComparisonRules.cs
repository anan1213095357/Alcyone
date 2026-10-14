namespace StateMachine.Execution;
public static class VariableComparisonRules
{
    public static string[] Operators(string type) => type switch
    {
        "number" => new[]{"==","!=",">",">=","<","<="},
        "string" => new[]{"==","!=","contains","startsWith","endsWith"},
        _ => new[]{"==","!="}
    };
}
