<div align="center">

![Alcyone — AI 驱动的脚本与状态机自动化](docs/images/banner.svg)

**让简单操作，组成复杂自动化。**

用 AI 编写脚本，用状态机组织逻辑，用屏幕识别连接真实桌面。

[English](README.md) · **简体中文**

![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)
![C%23](https://img.shields.io/badge/Scripting-C%23-65C7C8?style=flat-square)
![Blazor + Photino](https://img.shields.io/badge/Desktop-Blazor%20%2B%20Photino-8B87F8?style=flat-square)

[功能亮点](#功能亮点) · [界面预览](#界面预览) · [快速开始](#快速开始) · [使用指南](docs/guide.zh-CN.md)

</div>

---

Alcyone 是一个面向 Windows 的桌面自动化工具，将 **AI 辅助开发、可视化状态机和 C# 热重载脚本**放进同一个工作空间。点击、输入、等待是基本动作；条件、分支、循环和并行状态域让它们组成可持续运行的流程。

从“找到按钮后点击”，到“识别文字、判断条件、执行脚本、等待界面变化再继续”，每个状态和转移都可以在画布上查看和编辑。

![Alcyone 状态机画布](docs/images/workflow.png)

## 功能亮点

| | 能力 | 可以做什么 |
| :-- | :-- | :-- |
| ✦ | **AI 脚本助手** | 用自然语言描述需求，生成流程、C# 脚本或两者；预览后应用变更。 |
| ◈ | **可视化状态机** | 拖线连接状态，组合 ALL / ANY 条件，按出口优先级处理分支。 |
| ⌘ | **C# 热重载** | 修改 `.csx` 后自动编译，参数直接显示在界面；编译失败保留上次有效版本。 |
| ◎ | **屏幕识别** | 多点颜色、字典 OCR 和固定文字查找，识别结果直接驱动状态转移。 |
| ⑂ | **并行状态域** | 每个域拥有独立的当前状态，通过数值、布尔、文本和 JSON 变量共享数据。 |
| ▷ | **运行与调试** | 连续运行、单步、停止、重置，配合状态高亮和日志观察执行过程。 |

## 从想法到自动化

```text
描述需求 → AI 生成流程 / 脚本 → 预览并应用 → 调整状态与条件 → 运行 / 单步
                                                  ↑              │
                                                  └── 观察与迭代 ─┘
```

也可以直接手工编排流程和编写脚本。AI 是可选的开发助手。

1. **准备识别项目**：导入或训练字库、多点色库，设置查找区域。
2. **组织流程**：添加状态，配置进入、循环、离开动作，连接下一状态。
3. **定义切换条件**：组合变量、识别成功与否、相似度、坐标和文字判断。
4. **检查执行过程**：单步检查逻辑，再连续运行；通过日志定位问题。

## 界面预览

### 用自然语言描述要做的事

AI 助手结合当前流程、识别库和动作目录生成修改，支持“流程与脚本”“仅脚本”“仅流程”三种模式。

![AI 脚本助手](docs/images/ai-assistant.png)

### 把屏幕上的内容变成流程条件

在同一窗口管理字库、多点颜色、查找范围和匹配参数；训练与测试界面内嵌，无需另开识别工具。

![识别库管理](docs/images/recognition.png)

<sub>以上均为当前程序的实际截图。AI 截图展示需求输入界面，未调用模型或生成示例结果。</sub>

### 切换界面语言

点击顶部工具栏的语言选择框，即可在 **简体中文** 和 **English** 之间即时切换，识别训练窗口也提供同样的入口。程序会保存当前 Windows 用户的选择，下次启动自动恢复。切换不重载流程、不打断正在运行的实例；自定义名称、脚本、识别文字和原始日志保持不变。

## 快速开始

### 环境准备

- Windows x64、.NET 10 SDK、WebView2 Runtime。
所需的 `FastColorFinder` 源码已包含在 `Alcyone/Automation`、`Alcyone/Training/Core`、`Alcyone/Training/Services` 和 `Alcyone/Training/Native` 中。本仓库可独立编译，无需准备同级图色助手项目。

### 启动桌面程序

在仓库根目录执行：

```powershell
dotnet restore Alcyone.slnx
dotnet run --project Alcyone/Alcyone.csproj -c Release
```

程序打开独立桌面窗口。如需浏览器调试：

```powershell
dotnet run --project Alcyone/Alcyone.csproj -c Release -- --browser-host
```

打开控制台输出的本机地址。截图、键盘和鼠标操作作用于**运行服务的 Windows 桌面会话**。

### 配置 AI（可选）

应用读取 `AI:ApiKey`、`AI:Endpoint` 和 `AI:Model`；也支持 `DASHSCOPE_API_KEY`。可在同一 PowerShell 窗口设置环境变量后启动：

```powershell
$env:AI__ApiKey = "<your-api-key>"
$env:AI__Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
$env:AI__Model = "<model-id-available-to-your-account>"
dotnet run --project Alcyone/Alcyone.csproj -c Release
```

接口需要兼容 Chat Completions 请求格式。AI 请求会将需求和当前流程上下文发送到配置的服务；返回结果在界面中预览、应用。密钥请保留在本地配置或环境变量中。

## 用 C# 扩展动作

将脚本保存到应用工作目录的 `HotScripts/*.csx`。从源码开发时，可编辑 `Alcyone/HotScripts/` 后重新启动以复制脚本；运行中热重载监听的是可执行文件旁的 `HotScripts/`。

```csharp
public sealed class MyActions : StateScript
{
    [StateAction("识别后点击", "我的脚本")]
    public async Task ClickTarget([StateParameter("识别项目")] string name)
    {
        var hit = await Api.Recognition.FindAsync(name, seconds: 3);
        if (!hit.Found) return;

        Api.Input.Click(hit.X, hit.Y);
        await Api.Delay(200);
        Vars.Set("Finished", true);
        Log("目标已点击");
    }
}
```

动作可以绑定到进入、循环或离开阶段。脚本支持键鼠操作、识别查询、共享变量、日志和可取消的等待。详见 [脚本 API 与操作指南](docs/guide.zh-CN.md#热重载脚本)。

## 构建与发布

```powershell
# 编译
dotnet build Alcyone.slnx -c Release

# 框架依赖的桌面发行目录
dotnet publish Alcyone/Alcyone.csproj -c Release --self-contained false -o Desktop

# 检查脚本、页面与静态资源
dotnet Desktop/Alcyone.dll --check-host
```

运行 `Desktop/Alcyone.exe`，分发时保留完整目录。目标机器需要 .NET 10 ASP.NET Core Runtime 和 WebView2；发布后无需附带 `FastColorFinder` 源码。

<details>
<summary><strong>生成自包含单文件 EXE</strong></summary>

```powershell
dotnet publish Alcyone/Alcyone.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o release-exe
```

自包含版本无需单独安装 .NET Runtime，仍需 WebView2。首次运行可能解压内嵌资源；可编辑脚本和配置位于可执行文件旁。

</details>

## 进一步了解

| 文档 / 目录 | 内容 |
| :-- | :-- |
| [完整使用指南](docs/guide.zh-CN.md) | 识别库导入、训练、出口条件、脚本 API 和运行行为 |
| [English guide](docs/guide.md) | 英文操作与开发说明 |
| [HotScripts](Alcyone/HotScripts) | 内置 C# 动作示例 |
| [Execution](Alcyone/Execution) | 后台状态机运行服务 |
| [Training](Alcyone/Training) | 内嵌识别训练适配 |

页面刷新或切换配置不会停止已运行的实例；使用“停止”明确结束执行。退出程序会释放运行实例，重新启动不会自动恢复执行。

## 参与改进

欢迎通过 [Issues](https://github.com/anan1213095357/Alcyone/issues) 提交问题与想法，或发起 Pull Request。报告问题时请附 Windows / .NET 版本、复现步骤，以及去除敏感内容的日志或最小流程。

喜欢 Alcyone 的方向，可以点一个 Star，让更多人发现它。

<sub>当前仓库尚未提供 LICENSE 文件；使用与分发许可有待项目维护者明确。</sub>
