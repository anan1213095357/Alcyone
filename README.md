# Alcyone

Windows / .NET 10 / Blazor Server。通过全局变量和屏幕识别条件决定状态转移，拖线编排 C# 热重载脚本。

## 使用

```powershell
dotnet run --project StateMachine/Alcyone.csproj
```

打开控制台输出的浏览器地址。截图和键鼠操作作用于服务所在的 Windows 桌面会话。

1. 在状态机左侧打开“识别库 → 管理 / 导入”。在左侧新建或选中字典、多点色库，再点击统一的“编辑 / 训练当前库”；界面自动根据当前库的类型提供完整截图、标定、训练和测试功能，不需要另外启动 FastColorFinder。训练完成点击“保存到识别库”，当前结果和搜索参数直接保存到当前流程；“另存为新项 / 新字典”会追加记录。选中已有项目后点击“编辑 / 训练当前库”，可继续编辑字典点阵、颜色、权重，或调整多点颜色特征与搜索参数；保存保留项目 ID，已有出口条件仍然引用同一项。
2. 状态机左侧点击“识别库 → 管理 / 导入”。字典直接点击“导入字典文件”，支持工具导出的 TXT / JSON 或 FTF1，文件上限与原工具一致为 64 MB。多点色库使用粘贴录入：在工具训练完成后点击“复制调用代码”，新建多点色库，将完整代码粘贴到“粘贴工具输出”中，点击“解析并录入”。会解析出权重字符串、查找范围、色偏和相似度；代码只作为数据解析。也支持单独粘贴 FCF2 / FCF3 权重串，此时保留当前查找参数。成功数量和错误原因显示在弹窗顶部。字典可选择找字或 OCR 识别方式。
3. 管理页使用带列名的列表。字典按“名称、文字矩阵、字符串、查找范围、操作”展示工具字库中的全部模板；多点色库对应展示“名称、颜色坐标描述、字符串、查找范围、操作”。字典字符串为点阵对应的文字，多点色库字符串为工具训练的 FCF2 / FCF3 特征。同一字典的模板共用搜索范围，X/Y/宽/高可在列表中直接修改。字库和色库每行都提供“框选查找范围”，点击后拖动框选桌面区域，自动保存；Esc 取消保留原范围。每行“二次编辑”打开原项目，字库行打开原有字典训练界面并载入整个已有字典，颜色行恢复原训练数据；保存仍更新原项目 ID，不影响出口条件引用。
4. 选中状态，右侧出口添加条件，在同一个名称选择框中选择全局变量、多点色库名称或字典库名称，三类名称分组显示。选全局变量后显示变量比较，选库中名称后显示识别判断。同一个识别条件中可以同时填写成功／失败、相似度、X、Y 和文字；填写项全部满足才成立，数值和文字留空、结果选“不判断”时跳过该项。相似度、X、Y 各自可设置比较符。多个条件仍可用 ALL / ANY 组合。
   选择字典后，每条出口条件分别选择“字典用途”：OCR 结果判断识别搜索区全部文字，再比较识别文字等结果；查找固定文字必须填写要找的字符串，并判断找到／未找到及匹配坐标。同一个字典可同时用于 OCR、查找“确定”和查找“取消”，结果独立；库管理页的方式与文字用于测试。
5. 拖线连接下一状态，配置进入、循环或离开脚本及参数，然后运行或单步。

左侧识别库只显示名称和类型。管理弹窗的表格同时显示同类库的多条记录，点击简介会定位、高亮对应行，并激活右侧该项的参数。点击表格行只切换参数，不滚动或抢走编辑焦点。多点色库每行拥有独立名称、颜色描述、字符串和查找范围；字符串编辑完成后自动应用，无需保存按钮。右侧“解析并录入新项”连续追加色库记录，每次成功后清空粘贴框，已有表格行通过自动保存来修改。左侧导入字典文件会追加新字典，右侧文件入口用于更新当前选中的字典。格式错误时显示原因并保留上次有效值。每轮只检测当前状态出口引用的识别项目。识别库和条件随流程配置一起保存、导入和导出。旧版颜色变量自动迁移成独立识别项目及成功／失败条件。

普通全局变量支持数值、布尔、文本和 JSON，由脚本或手动更新。识别结果独立保存，不会写成普通布尔变量。字典格式错误、截图失败等执行错误会中止本轮并记录日志；正常未找到才属于识别失败。

编译时保留同级 `FastColorFinder` 文件夹。状态机只读链接其截图、框选、训练和识别运行库；工具目录内的源码完全不改。训练界面副本及适配代码位于状态机项目的 `Components/Training`、`Training`、`wwwroot/training`，作为同一进程中的内嵌页面运行；每次打开有独立截图会话，样式与状态机画布隔离。发布后的程序无需旁边放置工具程序。

取字界面保留多字库切换、TXT 导入导出、逐像素点阵/权重/颜色编辑、实时 OCR、搜索区设置、匹配测试以及调用代码与 C# 类下载。每个字库单独点击“保存到识别库”；下载 TXT 是额外导出。关闭时若存在未保存内容会提示。多点颜色保留实时画布、放大镜、拖动及方向键微调、时间训练、连续测试、搜索范围和调用代码导出。新保存的多点颜色会同时保留训练捕获位置；二次编辑时恢复捕获区及原标点，可拖动调整后重新训练。旧特征串没有捕获位置时，首次重新框选原目标区域即可恢复标点，不必重新逐点标注。查找范围与训练捕获范围独立保存。

从同时包含 `StateMachine` 和 `FastColorFinder` 的目录执行验证：`dotnet run --project StateMachine/Tests/TrainingChecks/TrainingChecks.csproj -c Release`（直接保存、更新 ID、另存、错误回滚、参数与会话隔离），`dotnet run --project StateMachine/Tests/RecognitionImportChecks/RecognitionImportChecks.csproj -c Release`（原有导入和运行服务）。发布程序支持 `--check-host` 检查脚本、主界面和内嵌训练资源，`--browser-host` 用于本地浏览器验证。

## 热重载脚本

编辑 `StateMachine/HotScripts/*.csx` 后自动编译和替换动作，编译失败保留上次有效版本。脚本类继承 `StateScript`，公开方法使用 `[StateAction]` 标记，参数使用 `[StateParameter]` 标记。

```csharp
public sealed class MyDesktopActions : StateScript
{
    [StateAction("识别后点击", "我的脚本")]
    public async Task Execute([StateParameter("识别项目名")] string name)
    {
        var hit = await Api.Recognition.FindAsync(name, seconds: 3);
        if (!hit.Found) return;
        Api.Input.Click(hit.X, hit.Y);
        await Api.Delay(200);
        Api.Input.Hotkey("CTRL", "A");
        Api.Input.TypeText("测试文字");
        Api.Input.Press("ENTER");
        Vars.Set("Finished", true);
    }
}
```

| API | 用途 |
| --- | --- |
| `Vars.Get<T>(name, defaultValue)` / `Vars.Set(name, value)` | 读写全局变量 |
| `Api.Recognition.Last(name)` | 获取最近一次检测结果；可能为空 |
| `await Api.Recognition.FindAsync(name, seconds: 3)` | 按识别库项目的参数查找 |
| `hit.Found` / `X` / `Y` / `Similarity` / `Text` | 成功状态、坐标、相似度及文字 |
| `Api.Input.MoveTo(x, y)` / `Click(x, y, "left")` | 移动和点击 |
| `await Api.Input.DoubleClickAsync(x, y)` | 双击 |
| `Api.Input.Press("ENTER")` / `Hotkey("CTRL", "A")` | 按键及组合键 |
| `Api.Input.TypeText(text)` | Unicode 文字输入 |
| `Api.Input.KeyDown(key)` / `KeyUp(key)` | 按住和释放键 |
| `Api.Input.MouseDown()` / `MouseUp()` / `Scroll(120)` | 鼠标按住、释放及滚轮 |
| `await Api.Input.DragAsync(x1, y1, x2, y2)` | 拖动 |
| `await Api.Delay(milliseconds)` / `Api.CancellationToken` | 可取消的等待及长循环取消 |
| `Log(message)` | 写日志 |

`Api.Color` 保留为 `Api.Recognition` 的兼容别名。使用检测结果点击前检查 `Found`。坐标为虚拟桌面的物理像素，副屏可使用负坐标。

停止和重置会取消内置等待及查找，释放脚本按住的键鼠。自定义长循环需要响应 `Api.CancellationToken`。默认示例使用 `TargetVisible` 识别项；先录入真实特征，然后在执行状态中选择记录或点击动作。

## 编译

```powershell
dotnet build StateMachine/Alcyone.csproj -c Release
```


运行由 `Execution/MachineRuntimeService` 按配置管理。`MachineSession` 拥有状态、识别循环、热重载脚本宿主和独立调度线程；Razor 页面只提交操作并订阅运行快照。刷新、断开页面或切换配置不会销毁已有运行实例；停止由明确的停止操作控制，删除配置会释放对应实例。应用进程退出时服务停止，进程重启后不自动恢复运行中的脚本。
编辑事件和拖动操作沿用当前配置的统一保存入口；运行快照刷新不触发配置序列化与保存。


Photino 桌面版：Windows x64，启动发布目录 `Desktop/Alcyone.exe`，保留整个目录。窗口通过随机的本机回环端口加载现有 Blazor 界面，启动时不打开浏览器；关闭窗口后释放后台识别、状态机和热重载宿主。配置与脚本分别在可执行文件旁的 `StateMachineConfigs` 和 `HotScripts`。
发布命令：`dotnet publish StateMachine/Alcyone.csproj -c Release --self-contained false -o Desktop`。框架依赖版需要 .NET 10 ASP.NET Core Runtime 和 WebView2（与原工具相同的窗口运行组件）。
