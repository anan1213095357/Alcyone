namespace StateMachine.Components.Pages;
public partial class Home
{
    private string ConditionVariableType(ConditionModel condition)=>Machine.Variables.FirstOrDefault(v=>v.Name==condition.Left)?.Type ?? "string";
    private string[] ConditionOperators(ConditionModel condition)=>VariableComparisonRules.Operators(ConditionVariableType(condition));
    private IEnumerable<VariableModel> ComparisonVariables(ConditionModel condition)=>Machine.Variables.Where(v=>v.Type==ConditionVariableType(condition));
    private void NormalizeVariableComparison(ConditionModel condition)
    {
        if(condition.Kind!="variable") return;
        var type=ConditionVariableType(condition);
        if(!ConditionOperators(condition).Contains(condition.Operator)) condition.Operator="==";
        if(condition.RightMode=="literal" && condition.RightType!=type)
        {
            condition.RightType=type;
            try { condition.RightValue=ConvertNodeValue(condition.RightValue,type); }
            catch { condition.RightValue=type=="boolean" ? System.Text.Json.Nodes.JsonValue.Create(false) : type=="number" ? System.Text.Json.Nodes.JsonValue.Create(0) : System.Text.Json.Nodes.JsonValue.Create(""); }
        }
        if(condition.RightMode=="variable" && !ComparisonVariables(condition).Any(v=>v.Name==NodeString(condition.RightValue)))
            condition.RightValue=System.Text.Json.Nodes.JsonValue.Create(ComparisonVariables(condition).FirstOrDefault()?.Name ?? "");
    }
}
