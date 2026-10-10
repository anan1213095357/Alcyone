namespace StateMachine.Components;

public partial class ScriptWorkbench
{
    private static readonly ApiDoc[] Docs =
    [
        new("基础", "StateAction", "将公开方法注册为卡片动作", "[StateAction(string displayName, string group = \"二次开发\")]", "标注继承 StateScript 的类中的公开方法。", "[StateAction(\"我的动作\", \"自定义\")]", ""),
        new("基础", "StateParameter", "设置动作参数的显示名称", "[StateParameter(string displayName)]", "放在方法参数前，显示为卡片参数名称。", "[StateParameter(\"参数名称\")]", ""),
        new("基础", "StateScript", "定义可在卡片里调用的动作", "class MyActions : StateScript", "继承 StateScript，用 StateAction 标注公开方法。异步动作返回 Task；参数会显示在状态卡片中。", "public sealed class MyActions : StateScript\n{\n    [StateAction(\"我的动作\", \"自定义\")]\n    public async Task Run()\n    {\n        Log(\"开始执行\");\n        await Api.Delay(300);\n    }\n}", ""),
        new("基础", "Api.Delay", "等待，并响应停止指令", "Task Delay(int milliseconds)", "单位为毫秒。使用 await 等待，避免 Thread.Sleep 阻塞动作线程。", "await Api.Delay(500);", "Api"),
        new("基础", "Api.CancellationToken", "在循环中响应停止", "CancellationToken CancellationToken", "较长循环应检查取消令牌。Delay 和内置识别接口已处理取消。", "Api.CancellationToken.ThrowIfCancellationRequested();", "Api"),
        new("基础", "Log", "向运行日志写入消息", "void Log(string message, string type = \"SCRIPT\", string level = \"info\")", "支持 info、good、error 等日志级别。", "Log(\"动作完成\", level: \"good\");", ""),
        new("基础", "Api.Service<T>", "获取已注册的应用服务", "T Service<T>() where T : notnull", "仅能获取应用依赖注入容器中已经注册的服务；未注册类型会抛出异常。", "var service = Api.Service<StateMachine.Automation.IColorProbeScanner>();", "Api"),
        new("键鼠", "Api.Input.Click", "在桌面坐标单击", "void Click(int x, int y, string button = \"left\")", "使用屏幕物理坐标。按钮可选 left、right、middle。", "Api.Input.Click(640, 360);", "Api.Input"),
        new("键鼠", "Api.Input.MoveTo", "移动鼠标到指定位置", "void MoveTo(int x, int y)", "支持多屏物理坐标，包括位于主屏左侧的负坐标。", "Api.Input.MoveTo(640, 360);", "Api.Input"),
        new("键鼠", "Api.Input.DoubleClickAsync", "双击鼠标", "Task DoubleClickAsync(int x, int y, string button = \"left\", int interval = 80)", "interval 为两次点击间的毫秒间隔，等待可被停止指令取消。", "await Api.Input.DoubleClickAsync(640, 360);", "Api.Input"),
        new("键鼠", "Api.Input.MouseDown", "按住鼠标按钮", "void MouseDown(string button = \"left\")", "与 MouseUp 配对使用。建议在 finally 中释放按钮。", "Api.Input.MouseDown();", "Api.Input"),
        new("键鼠", "Api.Input.MouseUp", "释放鼠标按钮", "void MouseUp(string button = \"left\")", "释放对应的 left、right 或 middle 按钮。", "Api.Input.MouseUp();", "Api.Input"),
        new("键鼠", "Api.Input.Scroll", "滚动鼠标滚轮", "void Scroll(int delta)", "常用滚动步长为 120，负值向下滚动。", "Api.Input.Scroll(-120);", "Api.Input"),
        new("键鼠", "Api.Input.Press", "按下并释放一个按键", "void Press(string key)", "例如 ENTER、ESC、F1、A。操作发送给当前前台窗口。", "Api.Input.Press(\"ENTER\");", "Api.Input"),
        new("键鼠", "Api.Input.Hotkey", "发送组合键", "void Hotkey(params string[] keys)", "传入多个按键名称。完成后按相反顺序释放。", "Api.Input.Hotkey(\"CTRL\", \"A\");", "Api.Input"),
        new("键鼠", "Api.Input.KeyDown", "按住一个按键", "void KeyDown(string key)", "与 KeyUp 配对使用。", "Api.Input.KeyDown(\"SHIFT\");", "Api.Input"),
        new("键鼠", "Api.Input.KeyUp", "释放一个按键", "void KeyUp(string key)", "可在 finally 中释放之前按住的按键。", "Api.Input.KeyUp(\"SHIFT\");", "Api.Input"),
        new("键鼠", "Api.Input.TypeText", "输入 Unicode 文字", "void TypeText(string text)", "向前台窗口输入文字，支持中文。先点击目标输入框。", "Api.Input.TypeText(\"你好，Alcyone\");", "Api.Input"),
        new("键鼠", "Api.Input.DragAsync", "按住鼠标并移动到终点", "Task DragAsync(int fromX, int fromY, int toX, int toY, int milliseconds = 300)", "在起点按住左键，等待指定时间后移到终点并释放。", "await Api.Input.DragAsync(200, 200, 600, 400);", "Api.Input"),
        new("键鼠", "Api.Input.ReleaseAll", "释放脚本持有的按键和鼠标", "void ReleaseAll()", "清理通过当前 Input 实例按住的按钮和按键。", "Api.Input.ReleaseAll();", "Api.Input"),
        new("识别", "Api.Recognition.FindAsync", "查找色库或字典中的目标", "Task<ColorProbeResult> FindAsync(string name, double seconds = 0)", "按识别项目名称、ID 或路径查找。seconds 为最长查找秒数，0 为单次查找。Color 是 Recognition 的别名。", "var hit = await Api.Recognition.FindAsync(\"目标名称\", 3);\nif (hit.Found && hit.Error is null)\n    Api.Input.Click(hit.X, hit.Y);", "Api.Recognition"),
        new("识别", "Api.Recognition.Last", "读取最近一轮识别结果", "ColorProbeResult? Last(string name)", "不会重新截图。结果可能为 null；使用坐标前先检查 Found 和 Error。", "var hit = Api.Recognition.Last(\"目标名称\");\nif (hit is { Found: true, Error: null })\n    Log(hit.Text);", "Api.Recognition"),
        new("识别", "ColorProbeResult", "读取坐标、文字与匹配状态", "Found · X · Y · Similarity · Text · Error", "Found 表示找到目标，Similarity 为相似度，Text 为 OCR 文字。Error 非空时表示识别失败。", "var result = await Api.Recognition.FindAsync(\"字典名称\");\nif (result.Found && long.TryParse(result.Text, out var number))\n    Vars.Set(\"识别数值\", number);", ""),
        new("变量", "Vars.Get<T>", "读取一个全局变量", "T? Get<T>(string name, T? defaultValue = default)", "未找到或类型转换失败时返回默认值。", "var count = Vars.Get<int>(\"次数\", 0);", "Vars"),
        new("变量", "Vars.Set", "写入一个全局变量", "void Set<T>(string name, T value)", "值会序列化为 JSON，可供后续状态和条件使用。", "Vars.Set(\"次数\", 1);", "Vars"),
        new("变量", "Api.GetNode", "以 JSON 节点读取变量", "JsonNode? GetNode(string name)", "返回副本；修改副本后需调用 SetNode 写回。", "var node = Api.GetNode(\"结果\");", "Api"),
        new("变量", "Api.SetNode", "写入 JSON 节点变量", "void SetNode(string name, JsonNode? value)", "可以写入 JSON 对象、数组或 null。", "Api.SetNode(\"结果\", new JsonObject { [\"成功\"] = true });", "Api")
    ];
}
