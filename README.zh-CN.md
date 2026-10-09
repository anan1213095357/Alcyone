<div align="center">

![Alcyone — Give your workflows a universe](docs/images/banner.svg)

### 让桌面自动化，拥有自己的宇宙。

**可视化状态机 · 动态星球 · C# 热重载 · AI 辅助开发**

[English](README.md) · **简体中文**

![Windows x64](https://img.shields.io/badge/Windows-x64-8bbcff?style=flat-square&labelColor=101b2d)
![.NET 10](https://img.shields.io/badge/.NET-10-b9a0ff?style=flat-square&labelColor=101b2d)
![C%23 scripting](https://img.shields.io/badge/Scripting-C%23-7fe0c0?style=flat-square&labelColor=101b2d)
![English + Chinese](https://img.shields.io/badge/UI-English%20%2F%20中文-d9c69e?style=flat-square&labelColor=101b2d)

[探索工作空间](#有层次的工作空间) · [快速开始](#快速开始) · [使用指南](docs/guide.zh-CN.md)

</div>

![英文界面的 Alcyone：状态卡片、星球分组与星空画布](docs/images/workspace-en.jpg)

Alcyone 是一个面向 Windows 的桌面自动化工作空间。把状态连起来，让屏幕识别驱动条件，用 C# 脚本执行动作。流程变复杂时，将相关卡片收纳为有名字的星球，仍然看得清整个流程。

画布有纵深，执行逻辑始终清晰。

## 有层次的工作空间

### 收起复杂，保留逻辑。

框选卡片，点击 **折叠**，让它们汇聚成一颗星球。组内连线随之收起，对外连线仍然保留；展开后回到原来的卡片。星球用于组织画布，不会替换状态或改变执行顺序。

- **围绕选中项操作**：顶部工具栏提供折叠、复制、删除和展开。
- **成组移动**：拖动任意一张选中卡片，整组一起移动。
- **继续收纳**：把选中卡片拖到已有星球上，卡片与连线一起融入。
- **保存布局**：名称、成员、外观、位置和折叠状态随流程配置保存。

![框选卡片与批量操作工具栏](docs/images/selection-en.jpg)

### 为每一组逻辑，指定自己的星球。

海洋、冰川、熔岩、森林、紫色云层与环状星球——**12 种可选外观**，由你指定。通过 **外观** 修改样式和名称；重开配置，或将同一组卡片展开再折叠，仍然保持一致。

空闲时星球缓慢自转，执行到组内状态时加快转动；移出可见画布后暂停绘制。背景的星云、远景星群和前景星点，组成有层次的工作空间。

![英文星球设置面板与十二种外观](docs/images/planet-styles-en.jpg)

### 可视化编排，可编程内核。

| 能力 | 可以做什么 |
| :-- | :-- |
| **状态机** | 配置进入、循环和离开动作；组合 ALL / ANY 条件，按优先级切换。 |
| **C# 热重载** | 编写可复用的 `.csx` 动作，在界面配置参数；编译失败保留上次有效版本。 |
| **屏幕识别** | 多点颜色、字典 OCR 和固定文字查找，配套内嵌训练工具。 |
| **独立状态域** | 每个域有自己的当前状态，共享布尔、数值、文本和 JSON 变量。 |
| **执行控制** | 运行、单步、停止、重置；观察状态高亮、条件结果和日志。 |
| **中英文界面** | 即时切换语言，不重载当前流程。 |

### 描述需求，审阅修改，再运行。

AI 助手会结合当前流程、识别库和动作目录，生成脚本、流程或两者；你可以预览后再应用，也可以完全手工编排。

![英文 AI 助手与待发送的需求示例](docs/images/ai-assistant-en.jpg)

<sub>以上为当前程序本地浏览器宿主的英文界面实拍。展示的是文档演示流程，并非正在操作桌面的任务；AI 截图只展示尚未发送的提示词，没有调用模型或展示生成结果。</sub>

<details>
<summary><strong>打开截图中的演示流程</strong></summary>

将 [orbital-workflow.json](docs/examples/orbital-workflow.json) 复制到可执行文件旁的 `StateMachineConfigs/`，再从配置选择器选择 **orbital-workflow**。源码构建的可执行文件通常位于 `Alcyone/bin/Release/net10.0/win-x64/`。

该示例仅展示布局，不包含鼠标键盘动作或训练数据；转移条件等待 `TargetReady` 变量，初始值为 `false`。

</details>

## 画布操作

| 手势 | 操作 |
| :-- | :-- |
| 空白处左键拖动 | 框选卡片和星球 |
| 右键拖动 | 平移画布 |
| Ctrl + 滚轮 | 缩放 |
| 拖动选中卡片 | 整组移动 |
| 将选中卡片拖到星球上 | 收纳进该星球 |
| 单击 / 双击星球 | 选中 / 展开 |
| 点击 **外观** 或星球名称 | 选择样式、重命名 |

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
