using StateMachine.Execution;


namespace StateMachine.Components.Pages;
public partial class Home
{
    private static string WindowsCategory(string check) => "Windows/" + (check.Split('.')[0] switch { "file" => "文件", "directory" => "文件夹", "disk" => "磁盘", "window" => "窗口", _ => "进程" });
    private IEnumerable<PickerOption> RecognitionTextOptions(ConditionModel condition, bool optional = false)
    {
        var item = Machine.Recognitions.FirstOrDefault(r => r.Id == condition.RecognitionId);
        var values = new List<PickerOption>();
        if(optional) values.Add(new("", "不判断此项"));
        if(!string.IsNullOrWhiteSpace(item?.Settings.Query)) values.Add(new(item.Settings.Query,item.Settings.Query,"项目已有值","已配置的查找文字"));
        if(_colorResults.TryGetValue(condition.RecognitionId,out var result) && !string.IsNullOrWhiteSpace(result.Text)) values.Add(new(result.Text,result.Text,"最近识别结果"));
        values.AddRange(Machine.Variables.Where(v=>v.Type=="string").Select(v=>new PickerOption(NodeString(v.Value),NodeString(v.Value),"变量已有值",v.Name)));
        return values.DistinctBy(v=>v.Value);
    }
    private IEnumerable<PickerOption> ConditionValueOptions(ConditionModel condition) => Machine.Variables.Where(v=>v.Type==condition.RightType).Select(v=>new PickerOption(DisplayNode(v.Value,v.Type),DisplayNode(v.Value,v.Type),"已有值",v.Name)).DistinctBy(o=>o.Value);
    private IEnumerable<PickerOption> ConditionOptions => Machine.Variables.Select(v => new PickerOption("variable:" + v.Id, v.Name, "全局变量/" + v.Type, "比较变量当前值"))
        .Concat(Machine.Recognitions.Where(r => !r.IsFolder).Select(r => new PickerOption("recognition:" + r.Id, r.Name, (r.Settings.Mode == "dictionary" ? "识别/字典" : "识别/多点色库") + (RecognitionPath(r).Contains('/') ? "/" + RecognitionPath(r)[..RecognitionPath(r).LastIndexOf('/')] : ""), RecognitionPath(r))))
        .Concat(new[] { new PickerOption("script:new", "编写条件函数…", "自定义条件", "打开脚本编辑器，新建脚本模板中包含条件函数；保存并编译后可在此选择。") })
        .Concat(ScriptConditionOptions)
        .Concat(WindowsConditions.Checks.Select(c => new PickerOption("windows:" + c.Key, c.Value, WindowsCategory(c.Key), c.Key)));
    private IEnumerable<PickerOption> ScriptConditionOptions => (_scriptHost?.ActionTargets ?? Array.Empty<object>())
        .SelectMany(target => target.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => !m.ContainsGenericParameters && m.GetParameters().Length == 0 &&
                (m.ReturnType == typeof(bool) || m.ReturnType == typeof(Task<bool>) || m.ReturnType == typeof(ValueTask<bool>)))
            .Select(m => (Method: m, Attribute: m.GetCustomAttributes(typeof(StateMachine.Scripting.Metadata.StateConditionAttribute), true).Cast<StateMachine.Scripting.Metadata.StateConditionAttribute>().FirstOrDefault()))
            .Where(e => e.Attribute is not null)
            .Select(e => new PickerOption("script:" + target.GetType().FullName + "." + e.Method.Name, e.Attribute!.DisplayName, "自定义条件/" + e.Attribute.Group, e.Method.Name + "()")));
    private IEnumerable<PickerOption> LifecycleOptions => new[] { new PickerOption("", "不调用动作") }.Concat(ActionMethods.Select(a => new PickerOption(a.Key, L[a.DisplayName], (a.IsHotScript ? "热重载脚本/" : "内置 API/") + L[a.Group], a.Method.Name + "(" + string.Join(", ", a.Parameters.Select(p => L[p.DisplayName])) + ")")));
    private IEnumerable<PickerOption> VariableOptions(IEnumerable<VariableModel> variables) => variables.Select(v => new PickerOption(v.Name,v.Name,"变量/"+v.Type,DisplayNode(v.Value,v.Type)));
    private bool IsReferenceParameter(ActionParameterDescriptor p) => (p.DisplayName.Contains("识别") && (p.DisplayName.Contains("项目") || p.DisplayName.Contains("名称"))) || p.Name.Contains("recognition",StringComparison.OrdinalIgnoreCase) || p.Name.Equals("variableName",StringComparison.OrdinalIgnoreCase) || p.Name is "stateId" or "regionId" || p.DisplayName.Contains("状态名") || p.DisplayName.Contains("状态域");
    private IEnumerable<PickerOption> ParameterOptions(ActionParameterDescriptor p)
    {
        if (p.DisplayName.Contains("识别") || p.Name.Contains("recognition",StringComparison.OrdinalIgnoreCase)) return Machine.Recognitions.Where(r=>!r.IsFolder).Select(r=>new PickerOption(r.Name,r.Name,r.Settings.Mode=="dictionary"?"字典库":"多点色库",RecognitionPath(r)));
        if(p.Name.Equals("variableName",StringComparison.OrdinalIgnoreCase)) return VariableOptions(Machine.Variables.Where(v => p.DisplayName.Contains("布尔") ? v.Type=="boolean" : p.DisplayName.Contains("数值") ? v.Type=="number" : true));
        if(p.Name=="stateId" || p.DisplayName.Contains("状态名")) return Machine.States.Select(s=>new PickerOption(p.Name=="stateId"?s.Id:s.Name,s.Name,Machine.Regions.FirstOrDefault(r=>r.Id==s.RegionId)?.Name??"状态"));
        if(p.Name=="regionId" || p.DisplayName.Contains("状态域"))return Machine.Regions.Select(r=>new PickerOption(p.Name=="regionId"?r.Id:r.Name,r.Name,"状态域"));
        if(p.Name.Equals("button",StringComparison.OrdinalIgnoreCase))return new[]{new PickerOption("left","左键"),new PickerOption("right","右键"),new PickerOption("middle","中键")};
        if(p.Name is "key" or "keys")return new[]{"ENTER","ESC","TAB","SPACE","BACKSPACE","DELETE","UP","DOWN","LEFT","RIGHT","CTRL+A","CTRL+C","CTRL+V","F1","F2","F5"}.Select(k=>new PickerOption(k,k,"常用按键"));
        return [];
    }
    private string ParameterHint(ActionParameterDescriptor p) => IsReferenceParameter(p) ? "从已有项目中选择；输入名称可以搜索。" : p.EditorKind=="number" ? (p.DisplayName.Contains("毫秒")?"时间单位：毫秒，例如 1000 = 1 秒。":p.DisplayName.Contains("秒")?"时间单位：秒，例如 0.5。":p.DisplayName.Contains("屏幕")?"屏幕像素坐标，支持全局变量。":"输入数值，支持小数和全局变量。") : p.EditorKind=="json" ? "输入合法 JSON，例如 {\"count\":1}。" : "先搜索已有值；找不到时再输入自定义值。";
}

