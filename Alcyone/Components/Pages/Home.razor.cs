using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using StateMachine.Scripting;
using StateMachine.Automation;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace StateMachine.Components.Pages;

public partial class Home
{
    private string ConfigDirectory =>
    Path.Combine(HostEnvironment.ContentRootPath, "StateMachineConfigs");
    private static readonly string[] Operators = { "==", "!=", ">", ">=", "<", "<=", "contains", "startsWith", "endsWith" };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    [Inject] private IWebHostEnvironment HostEnvironment { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private MachineRuntimeService RuntimeService { get; set; } = default!;
    private MachineSession? _session;
    private bool _disposed;
    [Inject] private IConfiguration Configuration { get; set; } = default!;

    private MachineModel Machine { get; set; } = new();
    private RuntimeModel Runtime { get; set; } = new();
    private List<LogEntry> Logs { get; set; } = new();
    private StateScriptContext _scriptContext = null!;
    private StateScriptHost? _scriptHost;
    private readonly Dictionary<string, ReflectedActionDescriptor> _actionMethods = new(StringComparer.Ordinal);
    private IEnumerable<ReflectedActionDescriptor> ActionMethods => _actionMethods.Values
        .OrderBy(x => x.Group, StringComparer.Ordinal)
        .ThenBy(x => x.DisplayName, StringComparer.Ordinal);
    private DotNetObjectReference<JsBridge>? _dotNetRef;
    private JsBridge? _jsBridge;
    private bool _jsReady;
    private bool _scrollLog;
    private bool _executing => _session?.Snapshot.Executing ?? false;
    private readonly Dictionary<string, ColorProbeResult> _colorResults = new(StringComparer.Ordinal);
    private string _configKey = "default";
    private string _newConfigKey = "";
    private string? _savedConfigKey, _savedConfigJson;
    private bool _configMenuOpen;
    private bool _autoSaveReady, _switchingConfig;
    private readonly SemaphoreSlim _configSaveLock = new(1, 1);
    private List<string> _configKeys = new();
    private static readonly HttpClient AiHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private bool _aiDialogOpen;
    private bool _aiBusy;
    private string _aiScope = "both";
    private bool _startingRuntime, _steppingRuntime, _resettingRuntime, _stoppingRuntime;
    private bool RuntimeControlsBusy => _session is null || Runtime.Running || _executing
        || _startingRuntime || _steppingRuntime || _resettingRuntime || _stoppingRuntime;
    private string RuntimeStatusText => _session is null ? "准备中" : _stoppingRuntime ? "停止中" : _resettingRuntime ? "重置中" : _steppingRuntime ? "单步中" : Runtime.Running || _startingRuntime ? "运行中" : _executing ? "执行中" : "就绪";
    private string RuntimeStatusClass => RuntimeControlsBusy ? (_stoppingRuntime ? "stopping" : "busy") : "ready";
    private bool CanStopRuntime => _session is not null && !_stoppingRuntime && !_resettingRuntime
        && (Runtime.Running || _executing || _startingRuntime || _steppingRuntime);

    private string _aiInput = string.Empty;
    private string _aiError = string.Empty;

    private AiMachinePlan? _pendingAiPlan;

    private readonly List<AiChatItem> _aiMessages = new();

    private string? SelectedRegionId { get; set; }
    private string? SelectedStateId { get; set; }
    private string? SelectedEdgeId { get; set; }
    private PendingConnectionModel? PendingConnection { get; set; }

    private StateModel? SelectedState => string.IsNullOrWhiteSpace(SelectedStateId) ? null : GetState(SelectedStateId!);

    protected override void OnInitialized()
    {
        if (IsWallpaperView) AttachWallpaperSession();
        else CreateDemo();
        _desktopLayoutPending = _desktopMode;
        Desktop.Changed += OnDesktopChanged;
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            if (IsWallpaperView)
            {
                // Read-only subscribers never load/save files, discover actions or
                // create another runtime. Every display observes the applied session.
                await JS.InvokeVoidAsync("industrialStateMachineUi.init", null, true);
                _jsReady = true;
                _desktopLayoutPending = true;
                StateHasChanged();
                return;
            }
            _jsBridge = new JsBridge(StateMoved, SelectStateFromJs, SelectEdgeFromJs, ClearSelectionFromJs, EscapeFromJs, DeleteSelectionFromJs);
            _jsBridge.SaveGroups = SaveGroupsFromJs;
            _jsBridge.EditSelection = EditSelectionFromJs;
            _dotNetRef = DotNetObjectReference.Create(_jsBridge);
            await JS.InvokeVoidAsync("industrialStateMachineUi.init", _dotNetRef);
            _jsReady = true;
            await RefreshConfigListAsync();
            var lastConfigPath = Path.Combine(ConfigDirectory, "last-config.txt");
            if (File.Exists(lastConfigPath))
            {
                var lastKey = (await File.ReadAllTextAsync(lastConfigPath)).Trim();
                if (_configKeys.Contains(lastKey)) _configKey = lastKey;
            }
            if (File.Exists(GetConfigFilePath(_configKey))) await LoadConfigAsync();
            else AttachSession();
            _autoSaveReady = true;
            await SaveConfigAsync();
            await JS.InvokeVoidAsync("industrialStateMachineUi.setZoom", 0.85, false);
            await JS.InvokeVoidAsync("industrialStateMachineUi.setInitialScroll", 40, 50);
            StateHasChanged();
            return;
        }

        if (_jsReady)
        {
            await SyncJsAsync();
            if (_desktopLayoutPending)
            {
                _desktopLayoutPending = false;
                await JS.InvokeVoidAsync("industrialStateMachineUi.setDesktopView", _desktopMode);
            }
            if (_scrollLog)
            {
                _scrollLog = false;
                await JS.InvokeVoidAsync("industrialStateMachineUi.scrollConsoleToBottom");
            }
        }
    }

    private async Task SyncJsAsync()
    {
        await JS.InvokeVoidAsync("industrialStateMachineUi.sync", new
        {
            edges = Machine.Edges,
            groups = Machine.StateGroups,
            configKey = _configKey,
            currentStateIds = Runtime.Running ? Runtime.CurrentStates.Values.ToArray() : Array.Empty<string>(),
            selectedEdgeId = SelectedEdgeId,
            runtimeRunning = Runtime.Running,
            transitionCounts = Runtime.TransitionCounts.ToDictionary(pair => pair.Key, pair => pair.Value),
            runningEdgeIds = Runtime.CurrentEdgeIds.ToArray(),
            pending = PendingConnection,
            language = L.Language
        });
    }
    private JsonNode? GetRuntimeVariable(string name) =>
        Runtime.Variables.TryGetValue(name, out var value) ? CloneNode(value) : null;

    // =========================================================
    // REFLECTION ACTION SYSTEM
    // =========================================================
    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.Changed -= OnRuntimeChanged;
            _session.ScriptHost.Reloaded -= OnScriptsReloaded;
        }
        _session = RuntimeService.GetSession(_configKey, Machine);
        Machine = _session.Snapshot.Machine;
        _scriptContext = _session.ScriptContext;
        _scriptHost = _session.ScriptHost;
        _session.Changed += OnRuntimeChanged;
        _scriptHost.Reloaded += OnScriptsReloaded;
        ApplyRuntimeSnapshot();
        DiscoverActionMethods();
    }
    private void InitializeScriptHost() { if (_session is null) AttachSession(); }
    private void ApplyRuntimeSnapshot()
    {
        if (_session is null) return;
        var snapshot = _session.Snapshot;
        if (IsWallpaperView) Machine = snapshot.Machine;
        Runtime = snapshot.Runtime; Logs = snapshot.Logs;
        if (!snapshot.Executing && !snapshot.Runtime.Running) _stoppingRuntime = false;
        _colorResults.Clear(); foreach (var pair in snapshot.ColorResults) _colorResults[pair.Key] = pair.Value;
        _recognitionResults.Clear(); foreach (var pair in snapshot.RecognitionResults) _recognitionResults[pair.Key] = pair.Value;
    }
    private void OnRuntimeChanged()
    {
        if (_disposed) return;
        _ = InvokeAsync(() => { if (_disposed) return; ApplyRuntimeSnapshot(); _scrollLog = !IsWallpaperView; StateHasChanged(); });
    }

    private void OnScriptsReloaded(ScriptReloadResult result)
    {
        _ = InvokeAsync(() =>
        {
            if (result.Success)
            {
                DiscoverActionMethods();
                AddLog("SCRIPT", $"热更完成：{result.ScriptCount} 个脚本，{result.ActionClassCount} 个动作类", "good");
            }
            else
            {
                AddLog("SCRIPT", string.Join(" | ", result.Errors), "error");
            }

            StateHasChanged();
        });
    }

    private void DiscoverActionMethods()
    {
        _actionMethods.Clear();

        if (_session is not null) RegisterActionTarget(_session, isHotScript: false);

        if (_scriptHost is not null)
        {
            foreach (var target in _scriptHost.ActionTargets)
                RegisterActionTarget(target, isHotScript: true);
        }

        foreach (var state in Machine.States)
        {
            state.BeforeLeaveAction ??= new StateActionCallModel();
            state.LoopAction ??= new StateActionCallModel();
            state.AfterEnterAction ??= new StateActionCallModel();

            NormalizeActionArguments(state.BeforeLeaveAction);
            NormalizeActionArguments(state.LoopAction);
            NormalizeActionArguments(state.AfterEnterAction);
        }
    }

    private void RegisterActionTarget(object target, bool isHotScript)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<StateMachine.Scripting.Metadata.StateActionAttribute>()
            })
            .Where(x => x.Attribute is not null)
            .ToArray();

        foreach (var item in methods)
        {
            var method = item.Method;
            var attribute = item.Attribute!;

            if (method.ContainsGenericParameters)
                throw new InvalidOperationException($"状态动作不能是泛型方法：{method.Name}");

            if (method.GetParameters().Any(x => x.ParameterType.IsByRef || x.IsOut))
                throw new InvalidOperationException($"状态动作不能包含 ref/out 参数：{method.Name}");

            if (!IsSupportedActionReturnType(method.ReturnType))
                throw new InvalidOperationException($"状态动作返回类型只支持 void、Task、Task<T>、ValueTask、ValueTask<T>：{method.Name}");

            var key = isHotScript
                ? $"{target.GetType().FullName}.{method.Name}"
                : method.Name;

            if (_actionMethods.ContainsKey(key))
                throw new InvalidOperationException($"检测到重复状态动作方法：{key}。带 [StateAction] 的方法不要重载。");

            var parameters = method.GetParameters()
                .Select(CreateActionParameterDescriptor)
                .ToList();

            _actionMethods[key] = new ReflectedActionDescriptor
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(attribute.DisplayName) ? method.Name : attribute.DisplayName,
                Group = string.IsNullOrWhiteSpace(attribute.Group) ? "二次开发" : attribute.Group,
                Method = method,
                Target = target,
                IsHotScript = isHotScript,
                Parameters = parameters
            };
        }
    }

    private static bool IsSupportedActionReturnType(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask)) return true;
        if (typeof(Task).IsAssignableFrom(type)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)) return true;
        return false;
    }

    private static ActionParameterDescriptor CreateActionParameterDescriptor(ParameterInfo parameter)
    {
        var parameterAttribute = parameter.GetCustomAttribute<StateMachine.Scripting.Metadata.StateParameterAttribute>();
        var nullableType = Nullable.GetUnderlyingType(parameter.ParameterType);
        var effectiveType = nullableType ?? parameter.ParameterType;

        return new ActionParameterDescriptor
        {
            Name = parameter.Name ?? $"arg{parameter.Position}",
            DisplayName = parameterAttribute?.DisplayName ?? parameter.Name ?? $"参数 {parameter.Position + 1}",
            ParameterType = parameter.ParameterType,
            EffectiveType = effectiveType,
            IsNullable = nullableType is not null || !parameter.ParameterType.IsValueType,
            EditorKind = GetEditorKind(effectiveType),
            EnumValues = effectiveType.IsEnum ? Enum.GetNames(effectiveType) : Array.Empty<string>(),
            DefaultLiteral = GetDefaultLiteral(parameter, effectiveType)
        };
    }

    private static string GetEditorKind(Type type)
    {
        if (type == typeof(bool)) return "boolean";
        if (type.IsEnum) return "enum";
        if (IsNumericType(type)) return "number";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type == typeof(DateOnly)) return "date";
        if (type == typeof(TimeOnly)) return "time";
        if (type == typeof(Guid)) return "guid";
        if (type == typeof(string) || type == typeof(char)) return "string";
        return "json";
    }

    private static bool IsNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string GetDefaultLiteral(ParameterInfo parameter, Type effectiveType)
    {
        if (parameter.HasDefaultValue && parameter.DefaultValue is not null && parameter.DefaultValue != DBNull.Value)
        {
            if (effectiveType == typeof(bool))
                return (bool)parameter.DefaultValue ? "true" : "false";
            if (effectiveType.IsEnum)
                return parameter.DefaultValue.ToString() ?? string.Empty;
            if (effectiveType == typeof(DateTime))
                return ((DateTime)parameter.DefaultValue).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            return Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (effectiveType == typeof(bool)) return "false";
        if (effectiveType.IsEnum) return Enum.GetNames(effectiveType).FirstOrDefault() ?? string.Empty;
        if (IsNumericType(effectiveType)) return "0";
        if (effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset))
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.FromDateTime(DateTime.Now).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(Guid)) return Guid.Empty.ToString();
        if (effectiveType == typeof(JsonNode) || effectiveType == typeof(JsonElement)) return "null";
        return string.Empty;
    }

    private ReflectedActionDescriptor? GetActionDescriptor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _actionMethods.TryGetValue(key, out var descriptor) ? descriptor : null;
    }

    private static IEnumerable<(string Key, string Title, StateActionCallModel Action)> GetLifecycleActions(StateModel state)
    {
        yield return ("enter", "进入状态", state.BeforeLeaveAction);
        yield return ("loop", "循环", state.LoopAction);
        yield return ("leave", "离开状态", state.AfterEnterAction);
    }

    private StateActionCallModel GetStateAction(StateModel state, string lifecycle)
    {
        return lifecycle switch
        {
            "enter" => state.BeforeLeaveAction,
            "loop" => state.LoopAction,
            "leave" => state.AfterEnterAction,
            _ => throw new InvalidOperationException($"未知状态生命周期：{lifecycle}")
        };
    }

    private StateActionCallModel? GetStateAction(string stateId, string lifecycle)
    {
        var state = GetState(stateId);
        return state is null ? null : GetStateAction(state, lifecycle);
    }

    private ActionArgumentModel? GetActionArgument(StateActionCallModel action, string parameterName) =>
        action.ActionArguments.FirstOrDefault(x => x.ParameterName == parameterName);

    private void NormalizeActionArguments(StateActionCallModel action, ReflectedActionDescriptor? descriptor = null)
    {
        action.ActionMethod ??= string.Empty;
        action.ActionArguments ??= new List<ActionArgumentModel>();

        descriptor ??= GetActionDescriptor(action.ActionMethod);
        if (descriptor is null)
        {
            if (_actionMethods.Count == 0) return;
            action.ActionArguments.Clear();
            return;
        }

        var old = action.ActionArguments
            .GroupBy(x => x.ParameterName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var normalized = new List<ActionArgumentModel>();
        foreach (var parameter in descriptor.Parameters)
        {
            if (!old.TryGetValue(parameter.Name, out var argument))
            {
                argument = new ActionArgumentModel
                {
                    ParameterName = parameter.Name,
                    Source = "literal",
                    LiteralValue = parameter.DefaultLiteral
                };
            }

            argument.ParameterName = parameter.Name;

            if (argument.Source != "literal" && argument.Source != "variable")
                argument.Source = "literal";

            if (argument.Source == "variable" &&
                !GetCompatibleVariables(action, parameter)
                    .Any(x => x.Name == argument.VariableName))
            {
                argument.VariableName =
                    GetCompatibleVariables(action, parameter)
                        .FirstOrDefault()?.Name ?? string.Empty;
            }

            normalized.Add(argument);
        }

        action.ActionArguments = normalized;
    }

    private void ChangeStateActionMethod(string stateId, string lifecycle, string value)
    {
        var action = GetStateAction(stateId, lifecycle);
        if (action is null) return;

        action.ActionMethod = value;
        action.ActionArguments.Clear();
        NormalizeActionArguments(action);
    }

    private void ChangeStateActionArgumentSource(
        string stateId,
        string lifecycle,
        string parameterName,
        string source)
    {
        var action = GetStateAction(stateId, lifecycle);
        if (action is null) return;

        var argument = GetActionArgument(action, parameterName);
        var descriptor = GetActionDescriptor(action.ActionMethod);
        var parameter = descriptor?.Parameters.FirstOrDefault(x => x.Name == parameterName);
        if (argument is null || parameter is null) return;

        argument.Source = source == "variable" ? "variable" : "literal";
        if (argument.Source == "variable")
            argument.VariableName = GetCompatibleVariables(action, parameter).FirstOrDefault()?.Name ?? string.Empty;
    }

    private void ChangeStateActionArgumentVariable(
        string stateId,
        string lifecycle,
        string parameterName,
        string variableName)
    {
        var action = GetStateAction(stateId, lifecycle);
        var argument = action is null ? null : GetActionArgument(action, parameterName);
        if (argument is not null) argument.VariableName = variableName;
    }

    private void ChangeStateActionArgumentLiteral(
        string stateId,
        string lifecycle,
        string parameterName,
        string value)
    {
        var action = GetStateAction(stateId, lifecycle);
        var argument = action is null ? null : GetActionArgument(action, parameterName);
        if (argument is not null) argument.LiteralValue = value;
    }

    private IEnumerable<VariableModel> GetCompatibleVariables(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        var requiredType = parameter.EditorKind switch
        {
            "boolean" => "boolean",
            "number" => "number",
            "json" => "json",
            _ => "string"
        };

        return Machine.Variables.Where(x => x.Type == requiredType);
    }

    private bool CanBindGlobalVariable(
    StateActionCallModel action,
    ActionParameterDescriptor parameter) =>
    GetCompatibleVariables(action, parameter).Any();

    private string GetParameterEditorKind(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        return parameter.EditorKind;
    }

    private string GetParameterTypeLabel(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        return parameter.EffectiveType.Name;
    }

    private object? ConvertLiteralToType(string? text, Type targetType)
    {
        text ??= string.Empty;
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;

        if (nullableType is not null && string.IsNullOrWhiteSpace(text)) return null;
        if (effectiveType == typeof(string)) return text;
        if (effectiveType == typeof(char)) return text.FirstOrDefault();
        if (effectiveType == typeof(bool)) return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, text, ignoreCase: true);
        if (effectiveType == typeof(Guid)) return Guid.Parse(text);
        if (effectiveType == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(JsonNode)) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (effectiveType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
            return document.RootElement.Clone();
        }
        if (effectiveType == typeof(object))
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (IsNumericType(effectiveType))
            return Convert.ChangeType(text, effectiveType, CultureInfo.InvariantCulture);

        return JsonSerializer.Deserialize(text, effectiveType, _jsonOptions);
    }

    private static string FormatActionLogValue(object? value)
    {
        if (value is null) return "null";
        if (value is string s) return $"\"{s}\"";
        if (value is JsonNode node) return node.ToJsonString();
        if (value is JsonElement element) return element.GetRawText();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // =========================================================
    // SYSTEM BUILT-IN ACTIONS
    // =========================================================

    private static string Uid(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private RegionModel CreateRegion(string? name = null) => new()
    {
        Id = Uid("region"),
        Name = string.IsNullOrWhiteSpace(name) ? "状态域" : name
    };

    private VariableModel CreateVariable(string name, string type, JsonNode? value) => new()
    {
        Id = Uid("variable"),
        Name = name,
        Type = type,
        Value = CloneNode(value)
    };

    private InputPortModel CreateInputPort() => new()
    {
        Id = Uid("input"),
        Name = "进入"
    };

    private ConditionModel CreateCondition() => new()
    {
        Id = Uid("condition"),
        Left = Machine.Variables.FirstOrDefault()?.Name ?? string.Empty,
        Operator = "==",
        RightMode = "literal",
        RightType = "boolean",
        RightValue = JsonValue.Create(true)
    };

    private OutputPortModel CreateOutputPort() => new()
    {
        Id = Uid("output"),
        Name = "切换",
        MatchMode = "all",
        Conditions = new List<ConditionModel> { CreateCondition() }
    };

    private StateModel CreateState(string regionId, double x, double y) => new()
    {
        Id = Uid("state"),
        RegionId = regionId,
        Name = "新状态",
        X = x,
        Y = y,
        IsStart = false,
        BeforeLeaveAction = new StateActionCallModel(),
        LoopAction = new StateActionCallModel(),
        AfterEnterAction = new StateActionCallModel(),
        Inputs = new List<InputPortModel> { CreateInputPort() },
        Outputs = new List<OutputPortModel> { CreateOutputPort() }
    };

    private void CreateDemo()
    {
        Machine = new MachineModel();
        var region = CreateRegion("桌面自动化");
        Machine.Regions.Add(region);
        SelectedRegionId = region.Id;
        Machine.Variables.Add(CreateVariable("Enabled", "boolean", JsonValue.Create(true)));
        Machine.Variables.Add(CreateVariable("ExecutionCount", "number", JsonValue.Create(0)));
        var target = new RecognitionItem { Name = "TargetVisible" };
        Machine.Recognitions.Add(target);
        RecognitionLibrary.NormalizeFolders(Machine.Recognitions);

        var waiting = CreateState(region.Id, 140, 150);
        waiting.Name = "等待目标特征";
        waiting.IsStart = true;
        waiting.Outputs[0].Name = "存在且已启用";
        waiting.Outputs[0].Conditions = new()
        {
            NewCondition("Enabled", "==", "literal", "boolean", JsonValue.Create(true)),
            new ConditionModel { Id = Uid("condition"), Kind = "recognition", RecognitionId = target.Id, ExpectedSuccess = true }
        };
        var execute = CreateState(region.Id, 760, 150);
        execute.Name = "执行热更脚本";
        execute.BeforeLeaveAction.ActionMethod = "DesktopActions.RecordFeature";
        execute.Outputs[0].Name = "执行完成";
        execute.Outputs[0].MatchMode = "always";
        var gone = CreateState(region.Id, 760, 640);
        gone.Name = "等待目标消失";
        gone.Outputs[0].Name = "不存在";
        gone.Outputs[0].Conditions = new()
        {
            new ConditionModel { Id = Uid("condition"), Kind = "recognition", RecognitionId = target.Id, ExpectedSuccess = false }
        };
        Machine.States.AddRange(new[] { waiting, execute, gone });
        ConnectRaw(waiting, waiting.Outputs[0], execute, execute.Inputs[0]);
        ConnectRaw(execute, execute.Outputs[0], gone, gone.Inputs[0]);
        ConnectRaw(gone, gone.Outputs[0], waiting, waiting.Inputs[0]);
    }

    private ConditionModel NewCondition(string left, string op, string rightMode, string rightType, JsonNode? rightValue) => new()
    {
        Id = Uid("condition"),
        Left = left,
        Operator = op,
        RightMode = rightMode,
        RightType = rightType,
        RightValue = CloneNode(rightValue)
    };

    private void ConnectRaw(StateModel fromState, OutputPortModel output, StateModel toState, InputPortModel input)
    {
        Machine.Edges.Add(new EdgeModel
        {
            Id = Uid("edge"),
            FromStateId = fromState.Id,
            FromPortId = output.Id,
            ToStateId = toState.Id,
            ToPortId = input.Id
        });
    }

    // =========================================================
    // VALUE / CONDITION
    // =========================================================

    private static JsonNode? CloneNode(JsonNode? node)
    {
        if (node is null) return null;
        return JsonNode.Parse(node.ToJsonString());
    }

    private JsonNode? ConvertValue(string? value, string type)
    {
        value ??= string.Empty;
        switch (type)
        {
            case "number":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantNumber))
                    return JsonValue.Create(invariantNumber);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var localNumber))
                    return JsonValue.Create(localNumber);
                return JsonValue.Create(0d);
            case "boolean":
                return JsonValue.Create(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
            case "json":
                try { return JsonNode.Parse(value); }
                catch { return null; }
            default:
                return JsonValue.Create(value);
        }
    }


    private JsonNode? ConvertNodeValue(JsonNode? node, string type)
    {
        var primitive = Primitive(node);
        switch (type)
        {
            case "number":
                if (TryNumber(primitive, out var number)) return JsonValue.Create(number);
                return JsonValue.Create(0d);
            case "boolean":
                if (primitive is bool b) return JsonValue.Create(b);
                if (primitive is not null && TryNumber(primitive, out var n) && Math.Abs(n - 1d) < 1e-12) return JsonValue.Create(true);
                var text = Convert.ToString(primitive, CultureInfo.InvariantCulture) ?? string.Empty;
                return JsonValue.Create(text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase));
            case "json":
                if (node is JsonObject or JsonArray) return CloneNode(node);
                try { return JsonNode.Parse(DisplayNode(node)); } catch { return null; }
            default:
                return JsonValue.Create(JsString(primitive));
        }
    }

    private static string DisplayNode(JsonNode? node, string? type = null)
    {
        if (node is null) return string.Empty;
        if (type == "json") return node.ToJsonString();
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        return node.ToJsonString();
    }

    private static string NodeString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s ?? string.Empty;
        return DisplayNode(node);
    }

    private static bool NodeBool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<double>(out var d)) return Math.Abs(d) > double.Epsilon;
            if (value.TryGetValue<string>(out var s)) return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static object? Primitive(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<string>(out var s)) return s;
        }
        return node.ToJsonString();
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is null) { number = 0; return false; }
        if (value is bool b) { number = b ? 1 : 0; return true; }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private bool EvaluateCondition(ConditionModel condition) => Runtime.ConditionResults.Values
        .SelectMany(outputs => outputs.Values).Any(output => output.Conditions.GetValueOrDefault(condition.Id));

    private static string JsString(object? value) => value switch
    {
        null => "undefined",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private OutputEvaluation EvaluateOutputDetailed(OutputPortModel output) => Runtime.ConditionResults.Values
        .SelectMany(outputs => outputs).FirstOrDefault(pair => pair.Key == output.Id).Value ?? new();
    private void RefreshCurrentConditionResults() { }

    private OutputEvaluation? GetConditionResult(string stateId, string outputId)
    {
        if (Runtime.ConditionResults.TryGetValue(stateId, out var outputs) && outputs.TryGetValue(outputId, out var detail))
            return detail;
        return null;
    }

    // =========================================================
    // MACHINE LOOKUPS
    // =========================================================
    private RegionModel? GetRegion(string id) => Machine.Regions.FirstOrDefault(x => x.Id == id);
    private StateModel? GetState(string id) => Machine.States.FirstOrDefault(x => x.Id == id);
    private StateModel? GetCurrentState(string regionId) => Runtime.CurrentStates.TryGetValue(regionId, out var id) ? GetState(id) : null;
    private IEnumerable<StateModel> GetStatesInRegion(string regionId) => Machine.States.Where(x => x.RegionId == regionId);
    private EdgeModel? GetEdgeFromPort(string stateId, string portId) => Machine.Edges.FirstOrDefault(x => x.FromStateId == stateId && x.FromPortId == portId);
    private EdgeModel? GetEdgeToPort(string stateId, string portId) => Machine.Edges.FirstOrDefault(x => x.ToStateId == stateId && x.ToPortId == portId);

    // =========================================================
    // REGION / VARIABLE
    // =========================================================
    private void SelectRegion(string regionId) => SelectedRegionId = regionId;
    private void RenameRegion(string regionId, string name)
    {
        var region = GetRegion(regionId);
        if (region is null) return;

        region.Name = name;
    }
    private async Task AddRegion()
    {
        var region = CreateRegion($"状态域 {Machine.Regions.Count + 1}");
        Machine.Regions.Add(region);
        SelectedRegionId = region.Id;

        var point = _jsReady
            ? await JS.InvokeAsync<CanvasPoint>(
                "industrialStateMachineUi.getAddStatePosition")
            : new CanvasPoint { X = 280, Y = 140 };

        var state = CreateState(region.Id, point.X, point.Y);
        state.IsStart = true;

        Machine.States.Add(state);

        SelectedStateId = state.Id;
        SelectedEdgeId = null;

        Runtime.CurrentStates[region.Id] = state.Id;
        RefreshCurrentConditionResults();
    }
    private void DeleteRegion(string regionId)
    {
        var stateIds = Machine.States.Where(x => x.RegionId == regionId).Select(x => x.Id).ToHashSet();
        Machine.Edges.RemoveAll(x => stateIds.Contains(x.FromStateId) || stateIds.Contains(x.ToStateId));
        Machine.States.RemoveAll(x => x.RegionId == regionId);
        Machine.Regions.RemoveAll(x => x.Id == regionId);
        Runtime.CurrentStates.Remove(regionId);
        Runtime.EnteredStates.Remove(regionId);
        if (SelectedRegionId == regionId) SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
        if (SelectedStateId is not null && stateIds.Contains(SelectedStateId)) SelectedStateId = null;
        RefreshCurrentConditionResults();
    }

    private void AddVariable()
    {
        var variable = CreateVariable($"Variable{Machine.Variables.Count + 1}", "number", JsonValue.Create(0d));
        Machine.Variables.Add(variable);
        Runtime.Variables[variable.Name] = CloneNode(variable.Value);
        RefreshCurrentConditionResults();
    }

    private void RenameVariable(string variableId, string newName)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;

        var oldName = variable.Name;
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) newName = "Variable";
        if (newName == oldName) return;
        if (Machine.Variables.Any(v => v.Id != variableId && v.Name == newName)) return;
        variable.Name = newName;

        if (Runtime.Variables.TryGetValue(oldName, out var runtimeValue))
        {
            Runtime.Variables[newName] = runtimeValue;
            Runtime.Variables.Remove(oldName);
        }

        foreach (var state in Machine.States)
        {
            foreach (var output in state.Outputs)
            {
                foreach (var condition in output.Conditions)
                {
                    if (condition.Left == oldName)
                        condition.Left = newName;

                    if (condition.RightMode == "variable" &&
                        NodeString(condition.RightValue) == oldName)
                    {
                        condition.RightValue = JsonValue.Create(newName);
                    }
                }
            }

            foreach (var action in new[]
            {
                state.BeforeLeaveAction,
                state.LoopAction,
                state.AfterEnterAction
            })
            {
                foreach (var argument in action.ActionArguments)
                {
                    if (argument.VariableName == oldName)
                        argument.VariableName = newName;
                }
            }
        }

        RefreshCurrentConditionResults();
    }

    private void ChangeVariableType(string variableId, string type)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        variable.Type = type;
        variable.Value = ConvertNodeValue(variable.Value, type);
        Runtime.Variables[variable.Name] = CloneNode(variable.Value);
        RefreshCurrentConditionResults();
    }

    private void ChangeVariableValue(string variableId, string value)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;

        SetRuntimeVariable(
            variable.Name,
            ConvertValue(value, variable.Type));
    }

    private void DeleteVariable(string variableId)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        Machine.Variables.Remove(variable);
        Runtime.Variables.Remove(variable.Name);
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // STATE / PORT / CONDITION EDITING
    // =========================================================
    private async Task AddStateAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRegionId))
        {
            if (Machine.Regions.Count == 0)
            {
                var region = CreateRegion("状态域 1");
                Machine.Regions.Add(region);
                SelectedRegionId = region.Id;
            }
            else
            {
                SelectedRegionId = Machine.Regions[0].Id;
            }
        }

        var point = _jsReady
            ? await JS.InvokeAsync<CanvasPoint>("industrialStateMachineUi.getAddStatePosition")
            : new CanvasPoint { X = 280, Y = 140 };
        var state = CreateState(SelectedRegionId!, point.X, point.Y);
        if (!GetStatesInRegion(SelectedRegionId!).Any()) state.IsStart = true;
        Machine.States.Add(state);
        SelectedStateId = state.Id;
        SelectedEdgeId = null;
    }

    private void RenameState(string stateId, string name)
    {
        var state = GetState(stateId);
        if (state is not null) state.Name = name;
    }

    private void ChangeStateRegion(string stateId, string newRegionId)
    {
        var state = GetState(stateId);
        if (state is null || state.RegionId == newRegionId) return;
        Machine.Edges.RemoveAll(x => x.FromStateId == state.Id || x.ToStateId == state.Id);
        state.RegionId = newRegionId;
        state.IsStart = false;
        if (!GetStatesInRegion(newRegionId).Any(x => x.Id != state.Id && x.IsStart)) state.IsStart = true;
        ResetRuntimeCore(logInitialStates: true);
    }

    private void ChangeStateStart(string stateId, bool isStart)
    {
        var state = GetState(stateId);
        if (state is null) return;
        if (isStart)
        {
            foreach (var item in GetStatesInRegion(state.RegionId)) item.IsStart = false;
            state.IsStart = true;
        }
        else
        {
            state.IsStart = false;
        }
    }

    private void AddInput(string stateId)
    {
        var state = GetState(stateId);
        state?.Inputs.Add(CreateInputPort());
    }

    private void QuickAddInput(string stateId)
    {
        AddInput(stateId);
        SelectedStateId = stateId;
        SelectedEdgeId = null;
    }

    private void AddOutput(string stateId)
    {
        var state = GetState(stateId);
        state?.Outputs.Add(CreateOutputPort());
        RefreshCurrentConditionResults();
    }

    private void QuickAddOutput(string stateId)
    {
        AddOutput(stateId);
        SelectedStateId = stateId;
        SelectedEdgeId = null;
    }

    private void RenameInput(string stateId, string inputId, string name)
    {
        var input = GetState(stateId)?.Inputs.FirstOrDefault(x => x.Id == inputId);
        if (input is not null) input.Name = name;
    }

    private void RenameOutput(string stateId, string outputId, string name)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is not null) output.Name = name;
    }

    private void RemoveInput(string stateId, string inputId)
    {
        var state = GetState(stateId);
        if (state is null) return;
        Machine.Edges.RemoveAll(x => x.ToStateId == state.Id && x.ToPortId == inputId);
        state.Inputs.RemoveAll(x => x.Id == inputId);
    }

    private void RemoveOutput(string stateId, string outputId)
    {
        var state = GetState(stateId);
        if (state is null) return;
        Machine.Edges.RemoveAll(x => x.FromStateId == state.Id && x.FromPortId == outputId);
        state.Outputs.RemoveAll(x => x.Id == outputId);
        RefreshCurrentConditionResults();
    }

    private void MoveOutput(string stateId, string outputId, int direction)
    {
        var state = GetState(stateId);
        if (state is null) return;
        var index = state.Outputs.FindIndex(x => x.Id == outputId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= state.Outputs.Count) return;
        (state.Outputs[index], state.Outputs[target]) = (state.Outputs[target], state.Outputs[index]);
        RefreshCurrentConditionResults();
    }

    private void ChangeOutputMatchMode(string stateId, string outputId, string mode)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.MatchMode = mode;
        RefreshCurrentConditionResults();
    }

    private void AddCondition(string stateId, string outputId)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.Conditions.Add(CreateCondition());
        RefreshCurrentConditionResults();
    }

    private void RemoveCondition(string stateId, string outputId, string conditionId)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.Conditions.RemoveAll(x => x.Id == conditionId);
        RefreshCurrentConditionResults();
    }

    private ConditionModel? FindCondition(string stateId, string outputId, string conditionId) =>
        GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId)?.Conditions.FirstOrDefault(x => x.Id == conditionId);

    private void ChangeConditionLeft(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.Left = value;
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionOperator(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.Operator = value;
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightMode(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightMode = value;
        if (value == "variable") condition.RightValue = JsonValue.Create(Machine.Variables.FirstOrDefault()?.Name ?? string.Empty);
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightType(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightType = value;
        if (value == "boolean") condition.RightValue = JsonValue.Create(false);
        else condition.RightValue = ConvertNodeValue(condition.RightValue, value);
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightValue(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightValue = condition.RightMode == "variable" ? JsonValue.Create(value) : ConvertValue(value, condition.RightType);
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // CONNECTION
    // =========================================================
    private async Task OnOutputPortClickAsync(string stateId, string portId)
    {
        var existing = GetEdgeFromPort(stateId, portId);
        if (existing is not null)
        {
            SelectedEdgeId = existing.Id;
            SelectedStateId = null;
            PendingConnection = null;
            return;
        }
        PendingConnection = new PendingConnectionModel { StateId = stateId, PortId = portId };
        SelectedEdgeId = null;
        if (_jsReady) await SyncJsAsync();
    }

    private async Task OnInputPortClickAsync(string stateId, string portId)
    {
        if (PendingConnection is not null)
        {
            var fromState = GetState(PendingConnection.StateId);
            var toState = GetState(stateId);
            if (fromState is null || toState is null)
            {
                PendingConnection = null;
                return;
            }
            if (fromState.RegionId != toState.RegionId)
            {
                AddLog("CONNECT", "不同状态域不能直接连接，请通过全局变量通信。", "error");
                PendingConnection = null;
                return;
            }
            if (fromState.Id == toState.Id)
            {
                AddLog("CONNECT", "当前版本不允许状态直接连接自己。", "warn");
                PendingConnection = null;
                return;
            }
            if (GetEdgeFromPort(PendingConnection.StateId, PendingConnection.PortId) is not null)
            {
                AddLog("CONNECT", "右侧插头已经被占用。", "error");
                PendingConnection = null;
                return;
            }
            if (GetEdgeToPort(stateId, portId) is not null)
            {
                AddLog("CONNECT", "左侧插头已经被占用。", "error");
                PendingConnection = null;
                return;
            }

            Machine.Edges.Add(new EdgeModel
            {
                Id = Uid("edge"),
                FromStateId = PendingConnection.StateId,
                FromPortId = PendingConnection.PortId,
                ToStateId = stateId,
                ToPortId = portId
            });
            AddLog("CONNECT", $"{fromState.Name} → {toState.Name}", "good");
            PendingConnection = null;
            RefreshCurrentConditionResults();
            return;
        }

        var existing = GetEdgeToPort(stateId, portId);
        if (existing is not null)
        {
            SelectedEdgeId = existing.Id;
            SelectedStateId = null;
        }
        await Task.CompletedTask;
    }

    private async Task ChangeEdgeDelayAsync(EdgeModel edge, ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var delay))
        {
            edge.DelayMilliseconds = Math.Clamp(delay, 0, 86400000);
            await SaveConfigAsync();
        }
    }

    private void DeleteSelectedEdge()
    {
        if (string.IsNullOrWhiteSpace(SelectedEdgeId)) return;
        Machine.Edges.RemoveAll(x => x.Id == SelectedEdgeId);
        SelectedEdgeId = null;
        RefreshCurrentConditionResults();
    }

    private void DeleteSelectedState()
    {
        if (string.IsNullOrWhiteSpace(SelectedStateId)) return;
        var stateId = SelectedStateId!;
        var state = GetState(stateId);
        Machine.Edges.RemoveAll(x => x.FromStateId == stateId || x.ToStateId == stateId);
        Machine.States.RemoveAll(x => x.Id == stateId);
        if (state is not null &&
            Runtime.CurrentStates.TryGetValue(state.RegionId, out var current) &&
            current == stateId)
        {
            Runtime.CurrentStates.Remove(state.RegionId);
            Runtime.EnteredStates.Remove(state.RegionId);
        }
        SelectedStateId = null;
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // RUNTIME
    // =========================================================
    private async Task ResetRuntime()
    {
        if (RuntimeControlsBusy || _session is null) return;
        _resettingRuntime = true;
        try { await _session.ResetAsync(); ApplyRuntimeSnapshot(); }
        finally { _resettingRuntime = false; }
    }
    private void ResetRuntimeCore(bool logInitialStates) { if (_session is not null) _ = _session.ConfigureAsync(Machine); }
    private async Task RunMachineAsync()
    {
        if (RuntimeControlsBusy || _session is null) return;
        _startingRuntime = true;
        try { await _session.StartAsync(); ApplyRuntimeSnapshot(); }
        finally { _startingRuntime = false; }
    }
    private async Task StepAsync()
    {
        if (RuntimeControlsBusy || _session is null) return;
        _steppingRuntime = true;
        try { await _session.StepOnceAsync(); ApplyRuntimeSnapshot(); }
        finally { _steppingRuntime = false; }
    }
    private async Task StopRuntime()
    {
        if (!CanStopRuntime || _session is null) return;
        _stoppingRuntime = true;
        try { await _session.StopAsync(); ApplyRuntimeSnapshot(); }
        catch { _stoppingRuntime = false; throw; }
    }

    private void ChangeRuntimeVariable(string variableId, string value)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;

        SetRuntimeVariable(
            variable.Name,
            ConvertValue(value, variable.Type));
    }

    private void SetRuntimeVariable(string name, JsonNode? value)
    { if (_session is not null) _ = _session.SetVariableAsync(name, value); }

    private async Task ConfigSelectionChanged(ChangeEventArgs e)
    {
        var key = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(key) || key == _configKey) return;
        await SwitchConfigAsync(key);
    }
    private async Task SwitchConfigAsync(string key)
    {
        await SaveConfigAsync();
        if (_savedConfigKey != _configKey || _savedConfigJson != JsonSerializer.Serialize(Machine, _jsonOptions)) return;
        _switchingConfig = true;
        var previousKey = _configKey;
        try
        {
            var path = GetConfigFilePath(key);
            var loaded = JsonSerializer.Deserialize<MachineModel>(await File.ReadAllTextAsync(path, Encoding.UTF8), _jsonOptions)
                ?? throw new InvalidOperationException("配置内容无效。");
            _configKey = key; Machine = loaded; NormalizeMachine();
            _recognitionManagerOpen = false;
            SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
            SelectedStateId = SelectedEdgeId = null; PendingConnection = null;
            AttachSession();
            _savedConfigKey = key; _savedConfigJson = null;
        }
        catch (Exception ex) { _configKey = previousKey; AddLog("CONFIG", ex.Message, "error"); }
        finally { _switchingConfig = false; }
        await SaveConfigAsync();
    }
    private async Task CreateConfigAsync()
    {
        var key = _newConfigKey.Trim();
        if (string.IsNullOrWhiteSpace(key)) return;
        try
        {
            var path = GetConfigFilePath(key);
            if (File.Exists(path)) { await SwitchConfigAsync(key); return; }
            await SaveConfigAsync();
            if (_savedConfigKey != _configKey || _savedConfigJson != JsonSerializer.Serialize(Machine, _jsonOptions)) return;
            _configKey = key; _newConfigKey = "";
            AttachSession();
            await SaveConfigAsync();
        }
        catch (Exception ex) { AddLog("CONFIG", ex.Message, "error"); }
    }

    private string GetConfigFilePath(string key)
    {
        key = key.Trim();

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("配置 Key 不能为空。");

        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            key.Contains("..") ||
            key.Contains('/') ||
            key.Contains('\\'))
        {
            throw new InvalidOperationException("配置 Key 包含非法字符。");
        }

        Directory.CreateDirectory(ConfigDirectory);

        return Path.Combine(ConfigDirectory, $"{key}.json");
    }

    private Task RefreshConfigListAsync(string? selected = null)
    {
        Directory.CreateDirectory(ConfigDirectory);

        _configKeys = Directory
            .GetFiles(ConfigDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(selected))
            _configKey = selected;

        return Task.CompletedTask;
    }

    private Task SaveConfigAsync() => SaveConfigCoreAsync(false);

    private async Task SaveConfigCoreAsync(bool reportFailure)
    {
        if (!_autoSaveReady || _switchingConfig)
        {
            if (reportFailure) throw new InvalidOperationException("配置尚未加载完成，请稍后再保存识别项目。");
            return;
        }
        await _configSaveLock.WaitAsync();
        try
        {
            var key = _configKey.Trim();
            var json = JsonSerializer.Serialize(Machine, _jsonOptions);
            if (_savedConfigKey == key && _savedConfigJson == json) return;
            var path = GetConfigFilePath(key);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            _savedConfigKey = key; _savedConfigJson = json;
            if (_session is not null) await _session.ConfigureAsync(Machine);
            await RefreshConfigListAsync();
            await File.WriteAllTextAsync(Path.Combine(ConfigDirectory, "last-config.txt"), key, Encoding.UTF8);

        }
        catch (Exception ex)
        {

            AddLog("SAVE", ex.Message, "error");
            if (reportFailure) throw;
        }
        finally { _configSaveLock.Release(); }
    }

    private async Task LoadConfigAsync()
    {
        var key = _configKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            AddLog("LOAD", "请输入配置 Key。", "error");
            return;
        }

        try
        {
            var path = GetConfigFilePath(key);

            if (!File.Exists(path))
            {
                AddLog("LOAD", $"没有找到配置：{key}", "warn");
                return;
            }

            var json = await File.ReadAllTextAsync(
                path,
                Encoding.UTF8);

            Machine = JsonSerializer.Deserialize<MachineModel>(
                          json,
                          _jsonOptions)
                      ?? new MachineModel();

            NormalizeMachine();

            SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;

            AttachSession();

            await RefreshConfigListAsync(key);

            AddLog("LOAD", $"已读取配置：{key}", "good");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }
    private async Task DeleteConfigAsync()
    {
        var key = _configKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            AddLog("CONFIG", "请输入配置 Key。", "error");
            return;
        }

        try
        {
            var path = GetConfigFilePath(key);

            if (!File.Exists(path))
            {
                AddLog("CONFIG", $"配置不存在：{key}", "warn");
                return;
            }

            if (_session is not null)
            {
                _session.Changed -= OnRuntimeChanged;
                _session.ScriptHost.Reloaded -= OnScriptsReloaded;
                _session = null;
            }
            if (Desktop.Presentation?.ConfigKey == key) await Desktop.SetDesktopAsync(false);
            await RuntimeService.RemoveAsync(key);
            File.Delete(path);

            await RefreshConfigListAsync();

            if (_configKeys.Count > 0) { _configKey = _configKeys[0]; await LoadConfigAsync(); }
            else { _configKey = "default"; CreateDemo(); AttachSession(); }
            _savedConfigKey = null; _savedConfigJson = null;

            AddLog("CONFIG", $"已删除配置：{key}", "warn");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private async Task ExportConfigAsync()
    {
        var key = string.IsNullOrWhiteSpace(_configKey) ? "state-machine" : _configKey.Trim();
        var json = JsonSerializer.Serialize(Machine, _jsonOptions);
        await JS.InvokeVoidAsync("industrialStateMachineUi.downloadText", $"{key}.json", json, "application/json");
    }

    private async Task ImportConfigAsync(InputFileChangeEventArgs e)
    {
        try
        {
            var file = e.File;
            await using var stream = file.OpenReadStream(256 * 1024 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var machine = JsonSerializer.Deserialize<MachineModel>(json, _jsonOptions);
            if (machine is null) throw new InvalidOperationException("状态机 JSON 格式错误");
            Machine = machine;
            NormalizeMachine();
            SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;
            _configKey = Path.GetFileNameWithoutExtension(file.Name);
            AttachSession();
            AddLog("IMPORT", "导入成功", "good");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private void NormalizeMachine()
    {
        Machine.Regions ??= new List<RegionModel>();
        Machine.Variables ??= new List<VariableModel>();
        Machine.States ??= new List<StateModel>();
        Machine.StateGroups ??= new List<StateGroupModel>();
        var groupedStates = new HashSet<string>(StringComparer.Ordinal);
        Machine.StateGroups = Machine.StateGroups.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id)
            && double.IsFinite(g.X) && double.IsFinite(g.Y)).DistinctBy(g => g.Id).ToList();
        foreach (var group in Machine.StateGroups)
            group.StateIds = (group.StateIds ?? new()).Where(id => Machine.States.Any(s => s.Id == id) && groupedStates.Add(id)).ToList();
        Machine.StateGroups.RemoveAll(g => g.StateIds.Count == 0);
        Machine.Edges ??= new List<EdgeModel>();
        Machine.Settings ??= new MachineSettings();
        Machine.Recognitions ??= new();

        if (Machine.Settings.CycleDelay <= 0)
            Machine.Settings.CycleDelay = 350;

        foreach (var region in Machine.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Id))
                region.Id = Uid("region");

            region.Name ??= "状态域";
        }

        foreach (var variable in Machine.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Id))
                variable.Id = Uid("variable");

            variable.Name ??= "Variable";
            variable.Type ??= "number";

        }

        foreach (var state in Machine.States)
        {
            if (string.IsNullOrWhiteSpace(state.Id))
                state.Id = Uid("state");

            state.Name ??= "新状态";
            state.RegionId ??= Machine.Regions.FirstOrDefault()?.Id ?? string.Empty;

            state.BeforeLeaveAction ??= new StateActionCallModel();
            state.LoopAction ??= new StateActionCallModel();
            state.AfterEnterAction ??= new StateActionCallModel();

            state.Inputs ??= new List<InputPortModel>();
            state.Outputs ??= new List<OutputPortModel>();

            foreach (var input in state.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Id))
                    input.Id = Uid("input");

                input.Name ??= "进入";
            }

            foreach (var output in state.Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Id))
                    output.Id = Uid("output");

                output.Name ??= "切换";
                output.MatchMode ??= "all";
                output.Conditions ??= new List<ConditionModel>();

                // 仅用于兼容旧配置。新版运行时不再执行输出口动作。
                output.ActionMethod ??= string.Empty;
                output.ActionArguments ??= new List<ActionArgumentModel>();
                output.ActionPath ??= string.Empty;
                output.ActionArgs ??= "[]";

                foreach (var condition in output.Conditions)
                {
                    if (string.IsNullOrWhiteSpace(condition.Id))
                        condition.Id = Uid("condition");

                    condition.Left ??= Machine.Variables.FirstOrDefault()?.Name ?? string.Empty;
                    condition.Operator ??= "==";
                    condition.RightMode ??= "literal";
                    condition.RightType ??= "boolean";
                    condition.RightValue ??= JsonValue.Create(false);
                }
            }

            // 旧配置如果把动作挂在输出口上，迁移第一个动作到“进入脚本”。
            if (string.IsNullOrWhiteSpace(state.BeforeLeaveAction.ActionMethod))
            {
                var legacyAction = state.Outputs
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ActionMethod));

                if (legacyAction is not null)
                {
                    state.BeforeLeaveAction.ActionMethod = legacyAction.ActionMethod;
                    state.BeforeLeaveAction.ActionArguments = legacyAction.ActionArguments
                        .Select(CloneActionArgument)
                        .ToList();
                }
            }

            NormalizeActionArguments(state.BeforeLeaveAction);
            NormalizeActionArguments(state.LoopAction);
            NormalizeActionArguments(state.AfterEnterAction);
        }

        MigrateLegacyRecognitionVariables();
        foreach (var item in Machine.Recognitions)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Uid("recognition");
            item.Name ??= "识别项目";
            item.Settings ??= new();
            if (item.Settings.Mode is "text" or "ocr")
            {
                item.Settings.DictionaryMode = item.Settings.Mode;
                item.Settings.Mode = "dictionary";
            }
        }
        Machine.Recognitions.RemoveAll(i => i.Settings.Mode is not ("feature" or "dictionary"));
        RecognitionLibrary.NormalizeFolders(Machine.Recognitions);

        foreach (var edge in Machine.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id))
                edge.Id = Uid("edge");
        }
    }

    private static ActionArgumentModel CloneActionArgument(ActionArgumentModel source) => new()
    {
        ParameterName = source.ParameterName,
        Source = source.Source,
        LiteralValue = source.LiteralValue,
        VariableName = source.VariableName
    };

    // =========================================================
    // JS CALLBACKS / SELECTION / CANVAS
    // =========================================================
    private async Task StateMoved(string stateId, double x, double y)
    {
        var state = GetState(stateId);
        if (state is not null)
        {
            state.X = Math.Max(0, x);
            state.Y = Math.Max(0, y);
            SelectedStateId = state.Id;
            SelectedRegionId = state.RegionId;
            SelectedEdgeId = null;
        }
        await SaveConfigAsync();
        await InvokeAsync(StateHasChanged);
    }

    private Task SelectStateFromJs(string stateId)
    {
        var state = GetState(stateId);
        if (state is not null)
        {
            SelectedStateId = state.Id;
            SelectedRegionId = state.RegionId;
            SelectedEdgeId = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    private Task SelectEdgeFromJs(string edgeId)
    {
        if (Machine.Edges.Any(x => x.Id == edgeId))
        {
            SelectedEdgeId = edgeId;
            SelectedStateId = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    private Task ClearSelectionFromJs()
    {
        SelectedStateId = null;
        SelectedEdgeId = null;
        return InvokeAsync(StateHasChanged);
    }

    private Task EscapeFromJs()
    {
        PendingConnection = null;
        SelectedStateId = null;
        SelectedEdgeId = null;
        return InvokeAsync(StateHasChanged);
    }

    private Task DeleteSelectionFromJs()
    {
        if (!string.IsNullOrWhiteSpace(SelectedEdgeId)) DeleteSelectedEdge();
        else if (!string.IsNullOrWhiteSpace(SelectedStateId)) DeleteSelectedState();
        return InvokeAsync(StateHasChanged);
    }

    private Task ZoomInAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.adjustZoom", 0.1, true).AsTask();
    private Task ZoomOutAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.adjustZoom", -0.1, true).AsTask();
    private Task FitCanvasAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.fitCanvas").AsTask();

    private void AddLog(string type, string message, string level = "info")
    {
        if (_session is not null) { _ = _session.LogAsync(type, message, level); return; }
        Logs.Add(new() { Id = Uid("log"), Time = DateTime.Now.ToString("HH:mm:ss"), Type = type, Message = message, Level = level });
        _scrollLog = true;
    }
    private Task ClearLogs() => _session?.ClearLogsAsync() ?? Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _disposed = true;
        Desktop.Changed -= OnDesktopChanged;
        if (_session is not null)
        {
            _session.Changed -= OnRuntimeChanged;
            _session.ScriptHost.Reloaded -= OnScriptsReloaded;
        }
        if (_jsReady)
        {
            try { await JS.InvokeVoidAsync("industrialStateMachineUi.dispose"); }
            catch (JSDisconnectedException) { }
        }
        _dotNetRef?.Dispose();
    }

    public sealed class JsBridge
    {
        public Func<string, List<StateGroupModel>, List<CanvasStatePosition>, Task>? SaveGroups { get; set; }
        public Func<string, string, List<string>, List<CanvasStatePosition>, Task<CanvasEditResult>>? EditSelection { get; set; }
        [JSInvokable] public Task SaveGroupsFromJs(string configKey, List<StateGroupModel> groups, List<CanvasStatePosition> positions) => SaveGroups?.Invoke(configKey, groups, positions) ?? Task.CompletedTask;
        [JSInvokable] public Task<CanvasEditResult> EditSelectionFromJs(string configKey, string command, List<string> ids, List<CanvasStatePosition> positions) => EditSelection!(configKey, command, ids, positions);
        private readonly Func<string, double, double, Task> _stateMoved;
        private readonly Func<string, Task> _selectState;
        private readonly Func<string, Task> _selectEdge;
        private readonly Func<Task> _clearSelection;
        private readonly Func<Task> _escape;
        private readonly Func<Task> _deleteSelection;

        public JsBridge(
            Func<string, double, double, Task> stateMoved,
            Func<string, Task> selectState,
            Func<string, Task> selectEdge,
            Func<Task> clearSelection,
            Func<Task> escape,
            Func<Task> deleteSelection)
        {
            _stateMoved = stateMoved;
            _selectState = selectState;
            _selectEdge = selectEdge;
            _clearSelection = clearSelection;
            _escape = escape;
            _deleteSelection = deleteSelection;
        }

        [JSInvokable] public Task StateMoved(string stateId, double x, double y) => _stateMoved(stateId, x, y);
        [JSInvokable] public Task SelectStateFromJs(string stateId) => _selectState(stateId);
        [JSInvokable] public Task SelectEdgeFromJs(string edgeId) => _selectEdge(edgeId);
        [JSInvokable] public Task ClearSelectionFromJs() => _clearSelection();
        [JSInvokable] public Task EscapeFromJs() => _escape();
        [JSInvokable] public Task DeleteSelectionFromJs() => _deleteSelection();
    }

    // Compatibility for existing scripts that explicitly reference the former attribute location.
    public sealed class StateActionAttribute(string displayName, string group = "二次开发")
        : StateMachine.Scripting.Metadata.StateActionAttribute(displayName, group);
    public sealed class StateParameterAttribute(string displayName)
        : StateMachine.Scripting.Metadata.StateParameterAttribute(displayName);

    private sealed class ReflectedActionDescriptor
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public MethodInfo Method { get; set; } = null!;
        public object Target { get; set; } = null!;
        public bool IsHotScript { get; set; }
        public List<ActionParameterDescriptor> Parameters { get; set; } = new();
    }

    private sealed class ActionParameterDescriptor
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Type ParameterType { get; set; } = typeof(string);
        public Type EffectiveType { get; set; } = typeof(string);
        public bool IsNullable { get; set; }
        public string EditorKind { get; set; } = "string";
        public string[] EnumValues { get; set; } = Array.Empty<string>();
        public string DefaultLiteral { get; set; } = string.Empty;
    }
    // =========================================================
    // AI STATE MACHINE BUILDER
    // =========================================================

    private void OpenAiDialog()
    {
        _aiDialogOpen = true;
        _aiError = string.Empty;
    }

    private void CloseAiDialog()
    {
        _aiDialogOpen = false;
    }

    private void ClearAiConversation()
    {
        _aiMessages.Clear();
        _pendingAiPlan = null;
        _aiError = string.Empty;
        _aiInput = string.Empty;
    }

    private void SetAiExample(string text)
    {
        _aiInput = text;
        _aiError = string.Empty;
    }

    private async Task GenerateAiPlanAsync()
    {
        if (_aiBusy)
            return;

        var prompt = _aiInput.Trim();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            _aiError = "请输入需要 AI 完成的流程或 C# 热更需求。";
            return;
        }

        _aiBusy = true;
        _aiError = string.Empty;
        _pendingAiPlan = null;

        _aiMessages.Add(new AiChatItem
        {
            Role = "user",
            Text = prompt
        });

        _aiInput = string.Empty;

        try
        {
            var apiKey = Configuration["AI:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "没有配置 AI:ApiKey 或 DASHSCOPE_API_KEY。");

            var endpoint =
                Configuration["AI:Endpoint"]
                ?? "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

            var model =
                Configuration["AI:Model"]
                ?? "qwen3.8-max";

            var actionCatalog = ActionMethods
                .Select(action => new
                {
                    key = action.Key,
                    name = action.DisplayName,
                    group = action.Group,
                    hotScript = action.IsHotScript,
                    parameters = action.Parameters
                        .Select(parameter => new
                        {
                            name = parameter.Name,
                            displayName = parameter.DisplayName,
                            type = parameter.EffectiveType.FullName ?? parameter.EffectiveType.Name,
                            editorKind = parameter.EditorKind,
                            nullable = parameter.IsNullable,
                            defaultValue = parameter.DefaultLiteral
                        })
                        .ToArray()
                })
                .ToArray();

            var actionJson = JsonSerializer.Serialize(actionCatalog, _jsonOptions);
            var currentMachineJson = JsonSerializer.Serialize(new
            {
                Machine.Regions, Machine.Variables, Machine.States, Machine.Edges, Machine.Settings,
                Recognitions = Machine.Recognitions.Select(item => new
                {
                    item.Id, item.Name, item.ParentId, item.IsFolder,
                    Settings = new { item.Settings.Mode, item.Settings.DictionaryMode, item.Settings.Query,
                        item.Settings.X, item.Settings.Y, item.Settings.Width, item.Settings.Height,
                        item.Settings.Similarity, item.Settings.ColorDeviation, item.Settings.ScanStep },
                    Texts = DictionaryTextsForAi(item)
                })
            }, _jsonOptions);
            var currentScriptsJson = BuildAiCurrentScriptsJson();

            var systemPrompt = $$"""
你是桌面自动化状态机与 C# 脚本开发助手。
本产品使用多点找色、字典 OCR、固定文字查找判断当前画面，再执行键鼠脚本。不要生成 PLC、输送线、洗衣机或机器人控制示例。
本次修改范围：{{(_aiScope == "script" ? "仅脚本：applyMachine=false，只修改 csx 文件。" : _aiScope == "machine" ? "仅流程：applyMachine=true，scripts 必须为空，只调用现有动作。" : "按用户需求修改流程与脚本。")}}
状态机和持续识别运行在独立后台服务，页面刷新不会停止运行。不要在 loopAction 里自行写无限 OCR/找色循环，持续检测由出口识别条件负责。
多点色库根项 isFolder=true 只是库，不能作为 recognitionId；必须引用其 parentId 关联下的实际记录（isFolder=false）。脚本按“库名/记录名”或真实 ID 查找，避免同名混淆。

你的任务不是只做流程编排。你同时负责：
1. 状态机流程：regions、variables、states、transitions。
2. HotScripts 目录中的 .csx C# 热更动作。

你必须根据用户需求判断：
- 只改流程：applyMachine=true，scripts=[]。
- 只写/改 C# 热更：applyMachine=false，四个流程数组可以为空。
- 流程和 C# 都需要：applyMachine=true，同时返回 scripts。

==================================================
C# 热更运行环境
==================================================

所有 csx 会被当成普通 C# 源文件一起编译，不是顶层脚本语句。
因此必须声明 class，动作类必须继承 StateScript。

宿主已经自动提供这些 using：
System
System.Collections.Generic
System.Linq
System.Text.Json
System.Text.Json.Nodes
System.Threading
System.Threading.Tasks
StateMachine.Scripting

宿主已经提供：

public abstract class StateScript
{
    protected StateScriptContext Api { get; }
    protected StateVariables Vars { get; }
    protected T Service<T>() where T : notnull;
    protected void Log(string message, string type = "SCRIPT", string level = "info");
}

StateVariables 常用 API：
- Vars.Get<T>(name, defaultValue)
- Vars.Get(name)
- Vars.Set(name, value)

Api 常用 API：
- Api.Delay(milliseconds)
- Api.GetNode(name)
- Api.SetNode(name, value)
- Api.Log(...)
- Api.Service<T>()
- Api.Services
- Api.Input.MoveTo(x, y) / Click(x, y, "left") / DoubleClickAsync(x, y)
- Api.Input.Press("ENTER") / Hotkey("CTRL", "A") / TypeText("文字")
- Api.Input.KeyDown(key) / KeyUp(key) / MouseDown() / MouseUp() / Scroll(delta) / DragAsync(x1,y1,x2,y2)
- Api.Recognition.FindAsync("识别项目名", seconds: 0) 返回 Found、X、Y、Similarity、Text、Error
- Api.Recognition.Last("识别项目名") 获取本轮结果（可能为空）；点击前必须检查 Found
- Api.CancellationToken：长循环需检查取消；Api.Delay 自动响应停止

普通全局变量仅用于脚本/手动数据。识别库 recognitions 只保存多点色库（settings.mode="feature"）和字典（settings.mode="dictionary"）。字典条件必须在 recognition.dictionaryMode 中选择 "ocr"（识别结果判断）或 "text"（查找固定文字）；固定文字填 recognition.searchText，不能为空。OCR 识别文字的比较使用 recognition.text/textOperator。每条条件独立设置用途，不能直接把字典名当布尔值。
识别条件使用 kind="recognition", recognitionId=识别库现有 ID, recognition={success:true/false/null,similarity:90,similarityOperator:">=",x:100,xOperator:">=",y:200,yOperator:"<=",text:"确定",textOperator:"contains"}。
识别的 success、similarity、x、y、text 可以同时填写，全部按 AND 判断，null 或空白不参与判断。识别库应保留，不能编造字典或特征。
不要编造训练特征串，必须使用用户提供的 FastColorFinder 输出。

动作声明规则：

[StateAction("显示名称", "分组")]
public void MethodName(
    [StateParameter("参数名称")] string value)
{
}

允许返回：void、Task、Task<T>、ValueTask、ValueTask<T>。
禁止 ref/out 参数。
带 [StateAction] 的方法不要重载。

推荐脚本模式：
public sealed class ScreenActions : StateScript
{
    [StateAction("点击识别结果", "桌面自动化")]
    public void ClickRecognized([StateParameter("识别项目") ] string name)
    {
        var hit = Api.Recognition.Last(name);
        if (hit is null || !hit.Found || hit.Error is not null) return;
        Api.Input.Click(hit.X, hit.Y);
    }
    [StateAction("输入并确认", "桌面自动化")]
    public async Task TypeAndConfirm([StateParameter("文字")] string text, [StateParameter("等待毫秒")] int wait = 150)
    {
        Api.Input.TypeText(text);
        Api.Input.Press("ENTER");
        await Api.Delay(wait);
    }
}

如果需要主程序已注册服务，使用 Service<T>()。
除非用户明确给出真实类型/库，禁止凭空编造不存在的服务类型、NuGet 包或 DLL。

==================================================
csx 文件规则
==================================================

scripts 数组每项：
- fileName：HotScripts 下的相对路径，必须以 .csx 结尾。
- operation：只允许 upsert 或 delete。
- content：upsert 时必须是完整文件内容；delete 时必须为空字符串。

修改已有 csx 时，必须返回修改后的完整文件，不能只返回代码片段。
新建 csx 时也必须返回完整可编译文件。
禁止使用绝对路径。
禁止使用 .. 跳出 HotScripts。
禁止修改 lib 目录 DLL。

如果新生成的 csx 动作同时被状态机引用，action key 必须严格使用：
Type.FullName + "." + MethodName

例如全局命名空间：
UserActions.AddNumber

例如 namespace MyFactory.Actions：
MyFactory.Actions.UserActions.AddNumber

==================================================
状态机规则
==================================================

1. 一个状态机包含：regions、variables、states、transitions。
2. 不同 Region 之间禁止直接连线，只能通过全局变量通信。
3. 每个 Region 必须至少有一个 State。
4. 每个 Region 只能有一个 isStart=true。
5. transition.from 和 transition.to 使用 State.key，不使用 GUID。
6. stateId、regionId、inputId、outputId、edgeId、conditionId、x、y 全部由 C# 自动创建。

operator 只允许：
== != > >= < <= contains startsWith endsWith

matchMode 只允许：all、any、always。
rightMode 只允许：literal、variable。
变量类型只允许：number、string、boolean、json。

生命周期：
- enterAction：刚进入当前状态时执行一次。
- loopAction：状态持续期间每个周期执行一次。
- leaveAction：准备离开当前状态、切换到下一个状态之前执行一次。

动作调用：
- 可以调用“当前可调用动作”中的动作。
- 也可以调用“本次 scripts 中新建/修改后确定会存在”的动作。
- 除这两类以外禁止编造动作。
- action 字段优先使用动作 key。
- 无动作时 action=""，arguments=[]。

argument.source：
- literal：固定值。
- variable：参数值从全局变量读取。

特别注意：variableName / 变量Key / 变量名 这类参数，是告诉动作“操作哪个变量”，通常 source 必须是 literal。
例如 AddNumber(variableName="Count", value=1)，variableName 应该是 literal: Count。

==================================================
当前可调用动作
==================================================

{{actionJson}}

==================================================
当前状态机
==================================================

{{currentMachineJson}}

==================================================
当前 HotScripts csx 文件
==================================================

{{currentScriptsJson}}

==================================================
修改原则
==================================================

用户说创建/重新做/重新生成/新建流程时，生成新的完整状态机。
用户说增加/修改/删除/在当前流程基础上时，参考当前状态机并返回修改后的完整状态机。

用户如果只要求新增或修改 C# 动作，不要擅自重做状态机，此时 applyMachine=false。
用户如果要求某个流程能力，但当前动作目录没有相应动作，而这个能力适合用 C# 动作实现，你应该同时生成 csx，并在流程中引用新动作。

==================================================
输出格式
==================================================

最终只能输出一个合法 JSON 对象。
不要 Markdown，不要 ```json，不要解释文字。

结构必须是：

{
  "reply": "本次改动的简短说明",
  "applyMachine": true,
  "regions": [
    { "key": "motor", "name": "电机" }
  ],
  "variables": [
    { "name": "MotorStart", "source": "global", "type": "boolean", "value": "false" }
  ],
  "states": [
    {
      "key": "stopped",
      "regionKey": "motor",
      "name": "电机停止",
      "isStart": true,
      "enterAction": { "action": "", "arguments": [] },
      "loopAction": { "action": "", "arguments": [] },
      "leaveAction": { "action": "", "arguments": [] }
    }
  ],
  "transitions": [
    {
      "from": "stopped",
      "to": "running",
      "name": "启动",
      "matchMode": "all",
      "conditions": [
        {
          "left": "MotorStart",
          "operator": "==",
          "rightMode": "literal",
          "rightType": "boolean",
          "rightValue": "true"
        }
      ]
    }
  ],
  "scripts": [
    {
      "fileName": "UserActions.csx",
      "operation": "upsert",
      "content": "public sealed class UserActions : StateScript\n{\n    ...\n}"
    }
  ]
}

regions、variables、states、transitions、scripts 必须始终存在。
所有 value 和 rightValue 都用字符串表示。
每个 state 必须始终包含 enterAction、loopAction、leaveAction。
每个 transition 必须始终包含 conditions。

如果 applyMachine=false：
- regions=[]
- variables=[]
- states=[]
- transitions=[]
- scripts 至少包含一个变更。

如果 applyMachine=true：
- 必须生成完整状态机，不允许只返回局部流程差异。

reply 尽量简短。
状态域、状态、变量、transitions、csx 文件数量不做人工限制。
必须保证 JSON 完整闭合，不能输出到一半。
""";

            systemPrompt += L.Language == "en"
                ? "\nWrite user-facing explanations in English. Preserve existing user-defined names, recognition search text, identifiers and JSON field names."
                : "\n使用简体中文说明方案。保留已有的用户命名、识别查找文字、标识符和 JSON 字段名。";

            var aiConversation = _aiMessages
                .Select(message => (object)new
                {
                    role = message.Role == "assistant" ? "assistant" : "user",
                    content = message.Text
                })
                .ToList();

            // 当前 prompt 已经在 _aiMessages 末尾，system 放最前面即可。
            aiConversation.Insert(0, new
            {
                role = "system",
                content = systemPrompt
            });

            var requestBody = new
            {
                model,
                messages = aiConversation,
                temperature = 0.1
            };
            var requestJson = JsonSerializer.Serialize(requestBody, _jsonOptions);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            request.Content =
                new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await AiHttpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"AI请求失败 {(int)response.StatusCode}：{LimitText(responseText, 1500)}");
            }

            using var document = JsonDocument.Parse(responseText);
            var choice = document.RootElement.GetProperty("choices")[0];


            var content = choice
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("AI没有返回任何数据。");

            var json = ExtractAiJson(content);

            var plan = JsonSerializer.Deserialize<AiMachinePlan>(json, _jsonOptions)
                ?? throw new InvalidOperationException("AI返回数据无法解析。");

            plan.Scripts ??= new List<AiScriptPlan>();
            plan.Regions ??= new List<AiRegionPlan>();
            plan.Variables ??= new List<AiVariablePlan>();
            plan.States ??= new List<AiStatePlan>();
            plan.Transitions ??= new List<AiTransitionPlan>();

            ValidateAiScripts(plan.Scripts);

            if (_aiScope == "script" && plan.ApplyMachine) throw new InvalidOperationException("当前选择仅脚本，生成结果不能修改流程。");
            if (_aiScope == "machine" && plan.Scripts.Count > 0) throw new InvalidOperationException("当前选择仅流程，生成结果不能修改脚本。");
            if (plan.ApplyMachine)
                ValidateAiPlan(plan, validateActions: false);
            else if (plan.Scripts.Count == 0)
                throw new InvalidOperationException("AI没有生成任何可应用的流程或 C# 热更改动。");

            _pendingAiPlan = plan;

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text = BuildAiPlanPreview(plan)
            });
        }
        catch (Exception ex)
        {
            _aiError = ex.GetBaseException().Message;

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text = $"生成失败：{_aiError}"
            });
        }
        finally
        {
            _aiBusy = false;
        }
    }

    private async Task ApplyPendingAiPlanAsync()
    {
        if (_pendingAiPlan is null || _aiBusy || Runtime.Running || _executing)
            return;

        _aiBusy = true;
        _aiError = string.Empty;

        var plan = _pendingAiPlan;
        List<AiScriptBackup>? scriptBackups = null;

        try
        {
            if (plan.Scripts.Count > 0)
            {
                if (_scriptHost is null)
                    throw new InvalidOperationException("C# 热更宿主尚未初始化。");

                scriptBackups = await ApplyAiScriptFilesAsync(plan.Scripts);

                var reload = _scriptHost.Reload();
                if (!reload.Success)
                {
                    throw new InvalidOperationException(
                        "C# 热更编译失败：" + string.Join(" | ", reload.Errors));
                }

                // 新脚本编译成功后，先刷新反射动作目录。
                DiscoverActionMethods();
            }

            if (plan.ApplyMachine)
            {
                // 到这里新 csx 动作已经存在，可以做完整动作校验。
                ValidateAiPlan(plan, validateActions: true);
                ApplyAiPlan(plan);
            }

            var changes = new List<string>();

            if (plan.ApplyMachine)
                changes.Add($"流程 {plan.States.Count} 个状态 / {plan.Transitions.Count} 条连线");

            if (plan.Scripts.Count > 0)
                changes.Add($"C# 热更 {plan.Scripts.Count} 个文件");

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text = "已应用：" + string.Join("，", changes) + "。"
            });

            _pendingAiPlan = null;
            _aiDialogOpen = false;

            await InvokeAsync(StateHasChanged);
            await Task.Delay(80);

            if (_jsReady && plan.ApplyMachine)
            {
                await SyncJsAsync();
                await FitCanvasAsync();
            }
        }
        catch (Exception ex)
        {
            if (scriptBackups is not null && scriptBackups.Count > 0)
            {
                try
                {
                    await RestoreAiScriptFilesAsync(scriptBackups);

                    if (_scriptHost is not null)
                    {
                        var rollbackReload = _scriptHost.Reload();
                        if (rollbackReload.Success)
                            DiscoverActionMethods();
                    }
                }
                catch (Exception rollbackEx)
                {
                    _aiError =
                        $"{ex.GetBaseException().Message} | 脚本回滚失败：{rollbackEx.GetBaseException().Message}";
                    return;
                }
            }

            _aiError = ex.GetBaseException().Message;
        }
        finally
        {
            _aiBusy = false;
        }
    }

    private static string[] DictionaryTextsForAi(RecognitionItem item)
    {
        if (item.Settings.Mode != "dictionary") return Array.Empty<string>();
        try { return FastTextFinderRuntime.TextDictionaryFile.Read(item.Settings.WeightString).Glyphs.Where(g => g.Enabled).Select(g => g.Text).Distinct().ToArray(); }
        catch (FormatException) { return Array.Empty<string>(); }
    }

    private string BuildAiCurrentScriptsJson()
    {
        if (_scriptHost is null || !Directory.Exists(_scriptHost.ScriptDirectory))
            return "[]";

        var root = Path.GetFullPath(_scriptHost.ScriptDirectory);

        var scripts = Directory
            .EnumerateFiles(root, "*.csx", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                fileName = Path.GetRelativePath(root, path).Replace('\\', '/'),
                content = File.ReadAllText(path, Encoding.UTF8)
            })
            .ToArray();

        return JsonSerializer.Serialize(scripts, _jsonOptions);
    }

    private static string ExtractAiJson(string content)
    {
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd >= 0)
                text = text[(firstLineEnd + 1)..];

            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0)
                text = text[..lastFence];

            text = text.Trim();
        }

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');

        if (firstBrace < 0 || lastBrace < firstBrace)
            throw new InvalidOperationException("AI没有返回合法 JSON 对象。");

        return text[firstBrace..(lastBrace + 1)];
    }

    private void ValidateAiScripts(IReadOnlyList<AiScriptPlan> scripts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            var relative = NormalizeAiScriptRelativePath(script.FileName);

            if (!seen.Add(relative))
                throw new InvalidOperationException($"AI重复修改同一个 csx 文件：{relative}");

            script.FileName = relative;
            script.Operation = (script.Operation ?? "upsert").Trim().ToLowerInvariant();

            if (script.Operation is not ("upsert" or "delete"))
                throw new InvalidOperationException($"不支持的 csx 操作：{script.Operation}");

            if (script.Operation == "upsert" && string.IsNullOrWhiteSpace(script.Content))
                throw new InvalidOperationException($"csx 文件内容为空：{relative}");

            if (script.Operation == "delete")
                script.Content = string.Empty;
        }
    }

    private string NormalizeAiScriptRelativePath(string? fileName)
    {
        var relative = (fileName ?? string.Empty)
            .Trim()
            .Replace('\\', '/');

        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidOperationException("csx 文件名不能为空。");

        if (!relative.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"热更文件必须是 .csx：{relative}");

        if (Path.IsPathRooted(relative) ||
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x == ".." || x == "."))
        {
            throw new InvalidOperationException($"非法 csx 路径：{relative}");
        }

        if (relative.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AI 不允许修改 HotScripts/lib 目录。");

        return relative;
    }

    private string GetAiScriptFullPath(string relativePath)
    {
        if (_scriptHost is null)
            throw new InvalidOperationException("C# 热更宿主尚未初始化。");

        var root = Path.GetFullPath(_scriptHost.ScriptDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var combined = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var rootPrefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combined.StartsWith(rootPrefix, comparison))
            throw new InvalidOperationException($"非法 csx 路径：{relativePath}");

        return combined;
    }

    private async Task<List<AiScriptBackup>> ApplyAiScriptFilesAsync(
        IReadOnlyList<AiScriptPlan> scripts)
    {
        ValidateAiScripts(scripts);

        var backups = new List<AiScriptBackup>(scripts.Count);

        foreach (var script in scripts)
        {
            var fullPath = GetAiScriptFullPath(script.FileName);
            var existed = File.Exists(fullPath);
            var oldContent = existed
                ? await File.ReadAllTextAsync(fullPath, Encoding.UTF8)
                : null;

            backups.Add(new AiScriptBackup
            {
                FileName = script.FileName,
                Existed = existed,
                Content = oldContent
            });
        }

        try
        {
            foreach (var script in scripts)
            {
                var fullPath = GetAiScriptFullPath(script.FileName);

                if (script.Operation == "delete")
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    continue;
                }

                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllTextAsync(
                    fullPath,
                    script.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return backups;
        }
        catch
        {
            await RestoreAiScriptFilesAsync(backups);
            throw;
        }
    }

    private async Task RestoreAiScriptFilesAsync(
        IReadOnlyList<AiScriptBackup> backups)
    {
        foreach (var backup in backups)
        {
            var fullPath = GetAiScriptFullPath(backup.FileName);

            if (!backup.Existed)
            {
                if (File.Exists(fullPath))
                    File.Delete(fullPath);

                continue;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(
                fullPath,
                backup.Content ?? string.Empty,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void ApplyAiPlan(
        AiMachinePlan plan)
    {
        ValidateAiPlan(plan);

        var oldMachine = Machine;

        try
        {
            var next =
                new MachineModel
                {
                    Recognitions = oldMachine.Recognitions,
                    Settings =
                        new MachineSettings
                        {
                            CycleDelay =
                                oldMachine.Settings.CycleDelay
                        }
                };

            Machine = next;

            // =====================================================
            // REGION
            // =====================================================

            foreach (var source in plan.Regions)
            {
                next.Regions.Add(
                    new RegionModel
                    {
                        Id = Uid("region"),
                        Name = source.Name.Trim()
                    });
            }

            var regionMap =
                plan.Regions
                    .Select((planRegion, index) => new
                    {
                        planRegion.Key,
                        Region = next.Regions[index]
                    })
                    .ToDictionary(
                        x => x.Key,
                        x => x.Region,
                        StringComparer.Ordinal);

            // =====================================================
            // VARIABLES
            // =====================================================

            foreach (var variable in plan.Variables)
            {
                var value =
                    ConvertValue(
                        variable.Value,
                        variable.Type);

                next.Variables.Add(
                    new VariableModel
                    {
                        Id = Uid("variable"),
                        Name = variable.Name.Trim(),
                        Source = variable.Source == "color" ? "color" : "global",
                        Color = variable.Color ?? new ColorProbeSettings(),
                        Type = variable.Type,
                        Value = CloneNode(value)
                    });
            }

            // =====================================================
            // STATES
            // =====================================================

            var stateMap =
                new Dictionary<string, StateModel>(
                    StringComparer.Ordinal);

            var yCursor = 130d;

            foreach (var regionPlan in plan.Regions)
            {
                var region =
                    regionMap[regionPlan.Key];

                var statePlans =
                    plan.States
                        .Where(x =>
                            x.RegionKey ==
                            regionPlan.Key)
                        .ToArray();

                var startAlreadyAssigned = false;

                for (var index = 0;
                     index < statePlans.Length;
                     index++)
                {
                    var statePlan =
                        statePlans[index];

                    var column =
                        index % 4;

                    var row =
                        index / 4;

                    var start =
                        statePlan.IsStart &&
                        !startAlreadyAssigned;

                    if (start)
                        startAlreadyAssigned = true;

                    var state =
                        new StateModel
                        {
                            Id = Uid("state"),

                            RegionId =
                                region.Id,

                            Name =
                                statePlan.Name.Trim(),

                            X =
                                180 +
                                column * 650,

                            Y =
                                yCursor +
                                row * 390,

                            IsStart =
                                start,

                            BeforeLeaveAction =
                                BuildAiAction(
                                    statePlan.EnterAction),

                            LoopAction =
                                BuildAiAction(
                                    statePlan.LoopAction),

                            AfterEnterAction =
                                BuildAiAction(
                                    statePlan.LeaveAction),

                            Inputs =
                                new List<InputPortModel>(),

                            Outputs =
                                new List<OutputPortModel>()
                        };

                    next.States.Add(state);

                    stateMap.Add(
                        statePlan.Key,
                        state);
                }

                var statesInRegion =
                    next.States
                        .Where(x =>
                            x.RegionId ==
                            region.Id)
                        .ToArray();

                if (!statesInRegion.Any(x => x.IsStart) &&
                    statesInRegion.Length > 0)
                {
                    statesInRegion[0].IsStart = true;
                }

                var rows =
                    Math.Max(
                        1,
                        (statePlans.Length + 3) / 4);

                yCursor +=
                    Math.Max(
                        650,
                        rows * 390 + 260);
            }

            // =====================================================
            // TRANSITIONS / PORTS / EDGES
            // =====================================================

            foreach (var transition
                     in plan.Transitions)
            {
                var from =
                    stateMap[transition.From];

                var to =
                    stateMap[transition.To];

                if (from.RegionId !=
                    to.RegionId)
                {
                    throw new InvalidOperationException(
                        $"AI产生非法跨状态域连线：{from.Name} → {to.Name}");
                }

                var output =
                    new OutputPortModel
                    {
                        Id = Uid("output"),

                        Name =
                            string.IsNullOrWhiteSpace(
                                transition.Name)
                                ? $"{from.Name} → {to.Name}"
                                : transition.Name.Trim(),

                        MatchMode =
                            transition.MatchMode,

                        Conditions =
                            new List<ConditionModel>()
                    };

                if (transition.MatchMode !=
                    "always")
                {
                    foreach (var condition
                             in transition.Conditions)
                    {
                        JsonNode? rightValue;

                        if (condition.RightMode ==
                            "variable")
                        {
                            rightValue =
                                JsonValue.Create(
                                    condition.RightValue);
                        }
                        else
                        {
                            rightValue =
                                ConvertValue(
                                    condition.RightValue,
                                    condition.RightType);
                        }

                        output.Conditions.Add(
                            new ConditionModel
                            {
                                Kind = condition.Kind,
                                Recognition = condition.Recognition,
                                RecognitionId = condition.RecognitionId,
                                RecognitionField = condition.RecognitionField,
                                ExpectedSuccess = condition.ExpectedSuccess,
                                Id =
                                    Uid("condition"),

                                Left =
                                    condition.Left,

                                Operator =
                                    condition.Operator,

                                RightMode =
                                    condition.RightMode,

                                RightType =
                                    condition.RightType,

                                RightValue =
                                    CloneNode(
                                        rightValue)
                            });
                    }
                }

                var input =
                    new InputPortModel
                    {
                        Id = Uid("input"),

                        Name =
                            string.IsNullOrWhiteSpace(
                                transition.Name)
                                ? $"来自 {from.Name}"
                                : transition.Name.Trim()
                    };

                from.Outputs.Add(output);
                to.Inputs.Add(input);

                ConnectRaw(
                    from,
                    output,
                    to,
                    input);
            }

            // 没有任何入口的状态仍然显示一个输入端口
            foreach (var state in
                     next.States)
            {
                if (state.Inputs.Count == 0)
                {
                    state.Inputs.Add(
                        new InputPortModel
                        {
                            Id = Uid("input"),
                            Name = "进入"
                        });
                }
            }

            NormalizeMachine();

            SelectedRegionId =
                next.Regions
                    .FirstOrDefault()?.Id;

            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;

            ResetRuntimeCore(
                logInitialStates: true);

            DiscoverActionMethods();

            AddLog(
                "AI",
                $"AI编排完成：{next.States.Count} 个状态，{next.Edges.Count} 条连线",
                "good");
        }
        catch
        {
            Machine = oldMachine;
            throw;
        }
    }
    private sealed class AiChatItem
    {
        public string Role { get; set; } =
            string.Empty;

        public string Text { get; set; } =
            string.Empty;
    }

    private sealed class AiMachinePlan
    {
        public string Reply { get; set; } =
            string.Empty;

        public bool ApplyMachine { get; set; } = true;

        public List<AiScriptPlan> Scripts { get; set; } = new();

        public List<AiRegionPlan> Regions
        {
            get;
            set;
        } = new();

        public List<AiVariablePlan> Variables
        {
            get;
            set;
        } = new();

        public List<AiStatePlan> States
        {
            get;
            set;
        } = new();

        public List<AiTransitionPlan> Transitions
        {
            get;
            set;
        } = new();
    }

    private sealed class AiScriptPlan
    {
        public string FileName { get; set; } = string.Empty;
        public string Operation { get; set; } = "upsert";
        public string Content { get; set; } = string.Empty;
    }

    private sealed class AiScriptBackup
    {
        public string FileName { get; set; } = string.Empty;
        public bool Existed { get; set; }
        public string? Content { get; set; }
    }

    private sealed class AiRegionPlan
    {
        public string Key { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;
    }

    private sealed class AiVariablePlan
    {
        public string Name { get; set; } =
            string.Empty;

        public string Source { get; set; } = "global";
        public ColorProbeSettings? Color { get; set; }

        public string Type { get; set; } =
            "number";

        public string Value { get; set; } =
            "0";
    }

    private sealed class AiStatePlan
    {
        public string Key { get; set; } =
            string.Empty;

        public string RegionKey { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public bool IsStart { get; set; }

        public AiActionPlan EnterAction
        {
            get;
            set;
        } = new();

        public AiActionPlan LoopAction
        {
            get;
            set;
        } = new();

        public AiActionPlan LeaveAction
        {
            get;
            set;
        } = new();
    }

    private sealed class AiActionPlan
    {
        public string Action { get; set; } =
            string.Empty;

        public List<AiActionArgumentPlan> Arguments
        {
            get;
            set;
        } = new();
    }

    private sealed class AiActionArgumentPlan
    {
        public string Name { get; set; } =
            string.Empty;

        public string Source { get; set; } =
            "literal";

        public string Value { get; set; } =
            string.Empty;
    }

    private sealed class AiTransitionPlan
    {
        public string From { get; set; } =
            string.Empty;

        public string To { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public string MatchMode { get; set; } =
            "all";

        public List<AiConditionPlan> Conditions
        {
            get;
            set;
        } = new();
    }

    private sealed class AiConditionPlan
    {
        public RecognitionCriteria? Recognition { get; set; }
        public string Kind { get; set; } = "variable";
        public string RecognitionId { get; set; } = "";
        public string RecognitionField { get; set; } = "status";
        public bool ExpectedSuccess { get; set; } = true;
        public string Left { get; set; } =
            string.Empty;

        public string Operator { get; set; } =
            "==";

        public string RightMode { get; set; } =
            "literal";

        public string RightType { get; set; } =
            "boolean";

        public string RightValue { get; set; } =
            "false";
    }
    private StateActionCallModel BuildAiAction(
        AiActionPlan source)
    {
        if (source is null ||
            string.IsNullOrWhiteSpace(
                source.Action))
        {
            return new StateActionCallModel();
        }

        var descriptor =
            ResolveAiActionDescriptor(
                source.Action);

        var action =
            new StateActionCallModel
            {
                ActionMethod =
                    descriptor.Key
            };

        // 先生成完整默认参数列表
        NormalizeActionArguments(
            action,
            descriptor);

        foreach (var sourceArgument
                 in source.Arguments)
        {
            var parameter =
                descriptor.Parameters
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.Name,
                            sourceArgument.Name,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.DisplayName,
                            sourceArgument.Name,
                            StringComparison.OrdinalIgnoreCase));

            if (parameter is null)
            {
                throw new InvalidOperationException(
                    $"动作 {descriptor.DisplayName} 不存在参数：{sourceArgument.Name}");
            }

            var argument =
                GetActionArgument(
                    action,
                    parameter.Name)!;

            if (sourceArgument.Source ==
                "variable")
            {
                var variable =
                    Machine.Variables
                        .FirstOrDefault(x =>
                            x.Name ==
                            sourceArgument.Value);

                if (variable is null)
                {
                    throw new InvalidOperationException(
                        $"动作 {descriptor.DisplayName} 引用了不存在的变量：{sourceArgument.Value}");
                }

                var compatible =
                    GetCompatibleVariables(
                            action,
                            parameter)
                        .Any(x =>
                            x.Name ==
                            variable.Name);

                if (!compatible)
                {
                    throw new InvalidOperationException(
                        $"变量 {variable.Name} 无法绑定到动作参数 {parameter.DisplayName}");
                }

                argument.Source =
                    "variable";

                argument.VariableName =
                    variable.Name;
            }
            else
            {
                // 这里顺便验证 AI 生成的参数是否能转换成真实 C# 类型
                _ =
                    ConvertLiteralToType(
                        sourceArgument.Value,
                        parameter.ParameterType);

                argument.Source =
                    "literal";

                argument.LiteralValue =
                    sourceArgument.Value;
            }
        }

        return action;
    }

    private ReflectedActionDescriptor
        ResolveAiActionDescriptor(
            string action)
    {
        action = action.Trim();

        if (_actionMethods.TryGetValue(
                action,
                out var descriptor))
        {
            return descriptor;
        }

        var matches =
            ActionMethods
                .Where(x =>
                    string.Equals(
                        x.DisplayName,
                        action,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (matches.Length == 1)
            return matches[0];

        throw new InvalidOperationException(
            $"AI调用了不存在的动作：{action}");
    }

    private void ValidateAiPlan(
        AiMachinePlan plan,
        bool validateActions = true)
    {
        if (plan.Regions.Count == 0)
            throw new InvalidOperationException(
                "AI没有生成任何状态域。");

        if (plan.States.Count == 0)
            throw new InvalidOperationException(
                "AI没有生成任何状态。");

        var duplicateRegion =
            plan.Regions
                .GroupBy(
                    x => x.Key,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateRegion is not null)
            throw new InvalidOperationException(
                $"AI生成重复状态域 Key：{duplicateRegion.Key}");

        var duplicateState =
            plan.States
                .GroupBy(
                    x => x.Key,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateState is not null)
            throw new InvalidOperationException(
                $"AI生成重复状态 Key：{duplicateState.Key}");

        var duplicateVariable =
            plan.Variables
                .GroupBy(
                    x => x.Name,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateVariable is not null)
            throw new InvalidOperationException(
                $"AI生成重复变量：{duplicateVariable.Key}");

        var regionKeys =
            plan.Regions
                .Select(x => x.Key)
                .ToHashSet(
                    StringComparer.Ordinal);

        var stateKeys =
            plan.States
                .Select(x => x.Key)
                .ToHashSet(
                    StringComparer.Ordinal);

        var variableNames =
            plan.Variables
                .Select(x => x.Name)
                .ToHashSet(
                    StringComparer.Ordinal);

        foreach (var variable
                 in plan.Variables)
        {
            if (variable.Type is not
                ("number" or
                 "string" or
                 "boolean" or
                 "json"))
            {
                throw new InvalidOperationException(
                    $"不支持的变量类型：{variable.Type}");
            }
        }

        foreach (var state
                 in plan.States)
        {
            if (!regionKeys.Contains(
                    state.RegionKey))
            {
                throw new InvalidOperationException(
                    $"状态 {state.Name} 引用了不存在的状态域：{state.RegionKey}");
            }

            if (validateActions)
            {
                ValidateAiAction(
                    state.EnterAction);

                ValidateAiAction(
                    state.LoopAction);

                ValidateAiAction(
                    state.LeaveAction);
            }
        }

        foreach (var region
                 in plan.Regions)
        {
            if (!plan.States.Any(x =>
                    x.RegionKey ==
                    region.Key))
            {
                throw new InvalidOperationException(
                    $"状态域 {region.Name} 没有状态。");
            }
        }

        foreach (var transition
                 in plan.Transitions)
        {
            if (!stateKeys.Contains(
                    transition.From))
            {
                throw new InvalidOperationException(
                    $"连线起点不存在：{transition.From}");
            }

            if (!stateKeys.Contains(
                    transition.To))
            {
                throw new InvalidOperationException(
                    $"连线终点不存在：{transition.To}");
            }

            if (transition.MatchMode is not
                ("all" or "any" or "always"))
            {
                throw new InvalidOperationException(
                    $"错误 MatchMode：{transition.MatchMode}");
            }

            var from =
                plan.States.First(x =>
                    x.Key ==
                    transition.From);

            var to =
                plan.States.First(x =>
                    x.Key ==
                    transition.To);

            if (from.RegionKey !=
                to.RegionKey)
            {
                throw new InvalidOperationException(
                    $"禁止跨状态域直接连线：{from.Name} → {to.Name}");
            }

            foreach (var condition
                     in transition.Conditions)
            {
                if (condition.Kind == "recognition")
                {
                    if (!Machine.Recognitions.Any(i => !i.IsFolder && i.Id == condition.RecognitionId))
                        throw new InvalidOperationException("AI引用了不存在的识别项目。");
                    continue;
                }
                if (!variableNames.Contains(
                        condition.Left))
                {
                    throw new InvalidOperationException(
                        $"条件引用不存在变量：{condition.Left}");
                }

                if (!Operators.Contains(
                        condition.Operator))
                {
                    throw new InvalidOperationException(
                        $"不支持条件操作符：{condition.Operator}");
                }

                if (condition.RightMode is not
                    ("literal" or "variable"))
                {
                    throw new InvalidOperationException(
                        $"错误 RightMode：{condition.RightMode}");
                }

                if (condition.RightMode ==
                        "variable" &&
                    !variableNames.Contains(
                        condition.RightValue))
                {
                    throw new InvalidOperationException(
                        $"条件引用不存在变量：{condition.RightValue}");
                }
            }
        }
    }

    private void ValidateAiAction(
        AiActionPlan action)
    {
        if (action is null ||
            string.IsNullOrWhiteSpace(
                action.Action))
        {
            return;
        }

        var descriptor =
            ResolveAiActionDescriptor(
                action.Action);

        foreach (var argument
                 in action.Arguments)
        {
            var exists =
                descriptor.Parameters.Any(x =>
                    string.Equals(
                        x.Name,
                        argument.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        x.DisplayName,
                        argument.Name,
                        StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                throw new InvalidOperationException(
                    $"动作 {descriptor.DisplayName} 不存在参数：{argument.Name}");
            }
        }
    }

    private string BuildAiPlanPreview(
        AiMachinePlan plan)
    {
        var text = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(plan.Reply))
        {
            text.AppendLine(plan.Reply.Trim());
            text.AppendLine();
        }

        if (plan.ApplyMachine)
        {
            text.AppendLine($"状态域：{plan.Regions.Count}");
            text.AppendLine($"变量：{plan.Variables.Count}");
            text.AppendLine($"状态：{plan.States.Count}");
            text.AppendLine($"连线：{plan.Transitions.Count}");

            foreach (var state in plan.States)
            {
                text.Append("● ");
                text.AppendLine(state.Name);

                AppendAiActionPreview(text, "进入", state.EnterAction);
                AppendAiActionPreview(text, "循环", state.LoopAction);
                AppendAiActionPreview(text, "离开", state.LeaveAction);
            }

            if (plan.Transitions.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("连线：");

                foreach (var transition in plan.Transitions)
                {
                    text.Append("  ");
                    text.Append(transition.From);
                    text.Append(" → ");
                    text.Append(transition.To);

                    if (!string.IsNullOrWhiteSpace(transition.Name))
                    {
                        text.Append(" · ");
                        text.Append(transition.Name);
                    }

                    text.AppendLine();
                }
            }
        }
        else
        {
            text.AppendLine("状态机：保持当前编排不变");
        }

        if (plan.Scripts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("C# 热更：");

            foreach (var script in plan.Scripts)
            {
                text.Append("  ");
                text.Append(script.Operation == "delete" ? "删除 " : "写入 ");
                text.AppendLine(script.FileName);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendAiActionPreview(
        StringBuilder text,
        string lifecycle,
        AiActionPlan action)
    {
        if (action is null ||
            string.IsNullOrWhiteSpace(
                action.Action))
        {
            return;
        }

        text.Append("   ");
        text.Append(lifecycle);
        text.Append("：");
        text.Append(action.Action);
        text.Append("(");

        text.Append(
            string.Join(
                ", ",
                action.Arguments.Select(
                    x =>
                        $"{x.Name}={x.Value}")));

        text.AppendLine(")");
    }

    private static string LimitText(
        string value,
        int length)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length <= length)
        {
            return value;
        }

        return value[..length] + "...";
    }

    // =========================================================
    // MODELS
    // =========================================================
}
