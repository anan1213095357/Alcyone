using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using StateMachine.Automation;
using StateMachine.Scripting;
using StateActionAttribute = StateMachine.Scripting.Metadata.StateActionAttribute;
using StateParameterAttribute = StateMachine.Scripting.Metadata.StateParameterAttribute;
namespace StateMachine.Execution;

public sealed class MachineSession : IAsyncDisposable
{
    private readonly MachineDispatcher _dispatcher;
    private readonly IServiceProvider Services;
    private readonly IWebHostEnvironment HostEnvironment;
    private readonly IColorProbeScanner ColorScanner;
    private MachineModel Machine = new();
    private RuntimeModel Runtime = new();
    private readonly List<LogEntry> Logs = new();
    private StateScriptContext _scriptContext = null!;
    private StateScriptHost? _scriptHost;
    private readonly Dictionary<string, ReflectedActionDescriptor> _actionMethods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ColorProbeResult> _colorResults = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, string Mode, string Query), ColorProbeResult> _recognitionResults = new();
    private CancellationTokenSource _executionCancellation = new();
    private readonly SemaphoreSlim _recognitionUpdated = new(0, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate };
    private bool _executing;
    private int _runToken, _recognitionRevision;
    private Task? _runTask;
    private bool _disposed;
    private long _lastNotification;
    private bool _notificationPending;
    private Task? _notificationTask;
    private const int NotificationIntervalMilliseconds = 50;
    private MachineModel _publishedMachine = new();
    private MachineSnapshot _snapshot = new(new(), new(), new(), new(), new(), false);
    public event Action? Changed;
    public MachineSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public StateScriptHost ScriptHost => _scriptHost!;
    public StateScriptContext ScriptContext => _scriptContext;
    public MachineSession(IServiceProvider services, IWebHostEnvironment environment, IColorProbeScanner scanner, MachineModel machine)
    {
        Services = services; HostEnvironment = environment; ColorScanner = scanner;
        _dispatcher = new MachineDispatcher();
        DispatchAsync(() => { Machine = Clone(machine); _publishedMachine = Clone(Machine); InitializeScriptHost(); DiscoverActionMethods(); ResetRuntime(); Notify(); }).GetAwaiter().GetResult();
    }
    private T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, _jsonOptions), _jsonOptions)!;
    private static string Uid(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    private Task DispatchAsync(Action action) => _dispatcher.InvokeAsync(action);
    private void Notify()
    {
        if (_disposed) return;
        var remaining = NotificationIntervalMilliseconds - (Environment.TickCount64 - _lastNotification);
        if (Runtime.Running && _executing && remaining > 0)
        {
            if (!_notificationPending)
            {
                _notificationPending = true;
                _notificationTask = PublishPendingNotificationAsync((int)remaining);
            }
            return;
        }
        _lastNotification = Environment.TickCount64;
        var snapshot = new MachineSnapshot(_publishedMachine, Clone(Runtime), Clone(Logs), new(_colorResults), new(_recognitionResults), _executing);
        Volatile.Write(ref _snapshot, snapshot);
        foreach (var subscriber in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)subscriber)(); }
            catch { /* A disconnected view must not terminate the machine. */ }
        }
    }
    private async Task PublishPendingNotificationAsync(int delay)
    {
        // Resume on the session dispatcher so snapshot creation stays serialized.
        await Task.Delay(delay);
        _notificationPending = false;
        if (!_disposed) Notify();
    }
    public Task ConfigureAsync(MachineModel machine) => DispatchAsync(() => { if (JsonSerializer.Serialize(Machine, _jsonOptions) == JsonSerializer.Serialize(machine, _jsonOptions)) return; Machine = Clone(machine); _publishedMachine = Clone(Machine); _recognitionRevision++; _recognitionResults.Clear(); DiscoverActionMethods(); if (!Runtime.Running && !_executing) ResetRuntime(); Notify(); });
    public Task StartAsync() => DispatchAsync(() => { if (_executing || Runtime.Running) return; _runTask = RunMachineAsync(); Notify(); });
    public Task StopAsync() => DispatchAsync(() => { StopRuntime(); Notify(); });
    public Task ResetAsync() => DispatchAsync(() => { ResetRuntime(); Notify(); });
    public Task StepOnceAsync() => _dispatcher.InvokeAsync(async () => { await StepAsync(); Notify(); });
    public Task TestAsync(RecognitionItem item) => _dispatcher.InvokeAsync(async () => { await TestRecognitionAsync(Machine.Recognitions.First(i => i.Id == item.Id)); Notify(); });
    public Task TestConditionAsync(ConditionModel condition) => _dispatcher.InvokeAsync(async () =>
    {
        if (_executing) return;
        BeginExecution();
        try { await ScanRecognitionConditionAsync(condition); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("RECOGNITION", ex.Message, "error"); }
        finally { EndExecution(); Notify(); }
    });
    public Task LogAsync(string type, string message, string level) => DispatchAsync(() => AddLog(type, message, level));
    public Task ClearLogsAsync() => DispatchAsync(() => { Logs.Clear(); Notify(); });
    public Task SetVariableAsync(string name, JsonNode? value) => DispatchAsync(() => { SetRuntimeVariable(name, value); Notify(); });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        if (_runTask is not null) await _runTask;
        if (_notificationTask is not null) await _notificationTask;
        await DispatchAsync(() => { _scriptHost?.Dispose(); _executionCancellation.Dispose(); });
        _dispatcher.Dispose();
    }
    private JsonNode? GetRuntimeVariable(string name) =>
        Runtime.Variables.TryGetValue(name, out var value) ? CloneNode(value) : null;


    // =========================================================
    // REFLECTION ACTION SYSTEM
    // =========================================================
    private void InitializeScriptHost()
    {

        _scriptContext = new StateScriptContext(

            Services,

            GetRuntimeVariable,

            SetRuntimeVariable,

            AddLog,
            () => _executionCancellation.Token,
            name => Machine.Recognitions.FirstOrDefault(v => !v.IsFolder && (v.Name == name || v.Id == name || RecognitionPath(v) == name))?.Settings,
            name => _colorResults.GetValueOrDefault(name));

        var scriptDirectory = Path.Combine(
            HostEnvironment.ContentRootPath,
            "HotScripts");

        _scriptHost = new StateScriptHost(
            _scriptContext,
            scriptDirectory);

        _scriptHost.Reloaded += OnScriptsReloaded;

        var result = _scriptHost.Reload();

        if (result.Success)
        {
            AddLog(
                "SCRIPT",
                $"热更加载成功：{result.ScriptCount} 个 csx，{result.ActionClassCount} 个动作类，目录：{scriptDirectory}",
                "good");
        }
        else
        {
            AddLog(
                "SCRIPT",
                string.Join(" | ", result.Errors),
                "error");
        }
    }

    private void OnScriptsReloaded(ScriptReloadResult result)
    {
        _ = DispatchAsync(() => { DiscoverActionMethods(); AddLog("SCRIPT", result.Success ? "热更完成" : string.Join(" | ", result.Errors), result.Success ? "good" : "error"); });
    }

    private void DiscoverActionMethods()
    {
        _actionMethods.Clear();

        RegisterActionTarget(this, isHotScript: false);

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
                Attribute = method.GetCustomAttribute<StateActionAttribute>()
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
        var parameterAttribute = parameter.GetCustomAttribute<StateParameterAttribute>();
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


    private ActionArgumentModel? GetActionArgument(StateActionCallModel action, string parameterName) =>
        action.ActionArguments.FirstOrDefault(x => x.ParameterName == parameterName);

    private void NormalizeActionArguments(StateActionCallModel action, ReflectedActionDescriptor? descriptor = null)
    {
        action.ActionMethod ??= string.Empty;
        action.ActionArguments ??= new List<ActionArgumentModel>();

        descriptor ??= GetActionDescriptor(action.ActionMethod);
        if (descriptor is null)
        {
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


    private async Task InvokeActionAsync(StateActionCallModel action)
    {
        if (string.IsNullOrWhiteSpace(action.ActionMethod)) return;

        if (!_actionMethods.TryGetValue(action.ActionMethod, out var descriptor))
            throw new InvalidOperationException($"动作方法不存在：{action.ActionMethod}");

        _executionCancellation.Token.ThrowIfCancellationRequested();
        NormalizeActionArguments(action, descriptor);

        var values = new object?[descriptor.Parameters.Count];
        for (var i = 0; i < descriptor.Parameters.Count; i++)
        {
            var parameter = descriptor.Parameters[i];
            var argument = GetActionArgument(action, parameter.Name)
                           ?? throw new InvalidOperationException($"动作参数不存在：{parameter.DisplayName}");
            values[i] = ResolveActionArgument(action, parameter, argument);
        }

        var callText = string.Join(", ", descriptor.Parameters.Select((p, i) => $"{p.Name}={FormatActionLogValue(values[i])}"));
        AddLog("CALL", $"{descriptor.DisplayName}({callText})", "info");

        object? result;
        try
        {
            result = descriptor.Method.Invoke(descriptor.Target, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            throw new OperationCanceledException(_executionCancellation.Token);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
        }

        if (result is Task task)
        {
            await task;
            _executionCancellation.Token.ThrowIfCancellationRequested();
            return;
        }

        if (result is ValueTask valueTask)
        {
            await valueTask;
            return;
        }

        if (result is not null)
        {
            var resultType = result.GetType();
            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var asTask = resultType
                    .GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(result, null) as Task;

                if (asTask is not null)
                    await asTask;
            }
        }
    }

    private object? ResolveActionArgument(
        StateActionCallModel action,
        ActionParameterDescriptor parameter,
        ActionArgumentModel argument)
    {
        if (argument.Source == "variable")
        {
            if (!Runtime.Variables.TryGetValue(argument.VariableName, out var runtimeValue))
                throw new InvalidOperationException($"全局变量不存在：{argument.VariableName}");

            return ConvertRuntimeNodeToType(runtimeValue, parameter.ParameterType);
        }

        return ConvertLiteralToType(argument.LiteralValue, parameter.ParameterType);
    }
    private object? ConvertRuntimeNodeToType(JsonNode? node, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;

        if (node is null)
        {
            if (nullableType is not null || !targetType.IsValueType) return null;
            return Activator.CreateInstance(effectiveType);
        }

        if (effectiveType == typeof(JsonNode)) return CloneNode(node);
        if (effectiveType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }
        if (effectiveType == typeof(object)) return Primitive(node);
        if (effectiveType == typeof(string)) return NodeString(node);
        if (effectiveType == typeof(char)) return NodeString(node).FirstOrDefault();
        if (effectiveType == typeof(bool)) return NodeBool(node);
        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, NodeString(node), ignoreCase: true);
        if (effectiveType == typeof(Guid)) return Guid.Parse(NodeString(node));
        if (effectiveType == typeof(DateTime)) return DateTime.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (IsNumericType(effectiveType))
        {
            if (!TryNumber(Primitive(node), out var number))
                throw new InvalidOperationException($"无法把变量值转换成 {effectiveType.Name}");
            return Convert.ChangeType(number, effectiveType, CultureInfo.InvariantCulture);
        }

        return JsonSerializer.Deserialize(node.ToJsonString(), effectiveType, _jsonOptions);
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

    [StateAction("写日志", "系统内置")]
    private void SystemLog(
        [StateParameter("内容")] string message)
    {
        AddLog("ACTION", message, "good");
    }

    [StateAction("设置变量", "系统内置")]
    private void SystemSetVariable(
        [StateParameter("目标变量")] string variableName,
        [StateParameter("值")] JsonNode? value)
    {
        _scriptContext.Vars.Set(variableName, value);
    }

    [StateAction("布尔变量取反", "系统内置")]
    private void SystemToggleVariable(
        [StateParameter("布尔变量")] string variableName)
    {
        var current = _scriptContext.Vars.Get<bool>(variableName, false);
        _scriptContext.Vars.Set(variableName, !current);
    }

    [StateAction("数值变量增加", "系统内置")]
    private void SystemAddNumber(
        [StateParameter("数值变量")] string variableName,
        [StateParameter("增加值")] double value)
    {
        var current = _scriptContext.Vars.Get<double>(variableName, 0d);
        _scriptContext.Vars.Set(variableName, current + value);
    }

    [StateAction("数值变量减少", "系统内置")]
    private void SystemSubtractNumber(
        [StateParameter("数值变量")] string variableName,
        [StateParameter("减少值")] double value)
    {
        var current = _scriptContext.Vars.Get<double>(variableName, 0d);
        _scriptContext.Vars.Set(variableName, current - value);
    }


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

    private static bool LooseEquals(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is bool || right is bool || left is double || right is double || left is long || right is long)
        {
            if (TryNumber(left, out var ln) && TryNumber(right, out var rn)) return Math.Abs(ln - rn) < 1e-12;
        }
        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private bool EvaluateCondition(ConditionModel condition)
    {
        if (condition.Kind == "recognition") return EvaluateRecognitionCondition(condition);
        Runtime.Variables.TryGetValue(condition.Left, out var leftNode);
        JsonNode? rightNode;
        if (condition.RightMode == "variable")
        {
            Runtime.Variables.TryGetValue(NodeString(condition.RightValue), out rightNode);
        }
        else
        {
            rightNode = ConvertNodeValue(condition.RightValue, condition.RightType);
        }

        var left = Primitive(leftNode);
        var right = Primitive(rightNode);
        switch (condition.Operator)
        {
            case "==": return LooseEquals(left, right);
            case "!=": return !LooseEquals(left, right);
            case ">": return Compare(left, right) > 0;
            case ">=": return Compare(left, right) >= 0;
            case "<": return Compare(left, right) < 0;
            case "<=": return Compare(left, right) <= 0;
            case "contains": return JsString(left).Contains(JsString(right), StringComparison.Ordinal);
            case "startsWith": return JsString(left).StartsWith(JsString(right), StringComparison.Ordinal);
            case "endsWith": return JsString(left).EndsWith(JsString(right), StringComparison.Ordinal);
            default: return false;
        }
    }

    private static int Compare(object? left, object? right)
    {
        if (TryNumber(left, out var ln) && TryNumber(right, out var rn)) return ln.CompareTo(rn);
        return string.CompareOrdinal(JsString(left), JsString(right));
    }

    private static string JsString(object? value) => value switch
    {
        null => "undefined",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private OutputEvaluation EvaluateOutputDetailed(OutputPortModel output)
    {
        if (output.MatchMode == "always")
            return new OutputEvaluation { Matched = true };

        var result = new OutputEvaluation();
        foreach (var condition in output.Conditions)
            result.Conditions[condition.Id] = EvaluateCondition(condition);

        if (output.Conditions.Count == 0)
            result.Matched = false;
        else if (output.MatchMode == "any")
            result.Matched = result.Conditions.Values.Any(x => x);
        else
            result.Matched = result.Conditions.Values.All(x => x);
        return result;
    }

    private void RefreshCurrentConditionResults()
    {
        Runtime.ConditionResults.Clear();
        foreach (var region in Machine.Regions)
        {
            if (!Runtime.CurrentStates.TryGetValue(region.Id, out var currentStateId)) continue;
            var state = GetState(currentStateId);
            if (state is null) continue;
            var map = new Dictionary<string, OutputEvaluation>();
            foreach (var output in state.Outputs)
                map[output.Id] = EvaluateOutputDetailed(output);
            Runtime.ConditionResults[state.Id] = map;
        }
    }

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

    private void ResetRuntime() => ResetRuntimeCore(logInitialStates: true);

    private void ResetRuntimeCore(bool logInitialStates)

    {

        _runToken++;

        _executionCancellation.Cancel();
        _scriptContext?.Input.ReleaseAll();


        Runtime.Running = false;
        Runtime.StopRequested = false;

        _colorResults.Clear();
        _recognitionResults.Clear();
        _recognitionRevision++;
        Runtime.Variables.Clear();
        Runtime.CurrentStates.Clear();
        Runtime.EnteredStates.Clear();
        Runtime.CurrentEdgeIds.Clear();
        Runtime.ConditionResults.Clear();

        foreach (var variable in Machine.Variables)
            Runtime.Variables[variable.Name] = CloneNode(variable.Value);

        foreach (var region in Machine.Regions)
        {
            var start = GetStatesInRegion(region.Id).FirstOrDefault(x => x.IsStart);
            if (start is null) continue;

            Runtime.CurrentStates[region.Id] = start.Id;

            if (logInitialStates)
                AddLog("STATE", $"{region.Name} → {start.Name}", "good");
        }

        RefreshCurrentConditionResults();
    }

    private async Task<bool> ExecuteRegionStepAsync(RegionModel region)
    {
        _executionCancellation.Token.ThrowIfCancellationRequested();
        if (!Runtime.CurrentStates.TryGetValue(region.Id, out var currentStateId))
            return false;

        var state = GetState(currentStateId);
        if (state is null)
            return false;

        // =====================================================
        // 进入脚本
        // 当前卡片刚准备进入时，只执行一次
        // =====================================================
        if (!Runtime.EnteredStates.TryGetValue(region.Id, out var enteredStateId) ||
            enteredStateId != state.Id)
        {
            await InvokeActionAsync(state.BeforeLeaveAction);
            Runtime.EnteredStates[region.Id] = state.Id;
        }

        // =====================================================
        // 循环脚本
        // 当前卡片存续期间，每个周期执行
        // =====================================================
        await InvokeActionAsync(state.LoopAction);
        _executionCancellation.Token.ThrowIfCancellationRequested();
        if (!Runtime.Running) await RefreshRecognitionsAsync(state.Id);

        foreach (var output in state.Outputs)
        {
            var detail = EvaluateOutputDetailed(output);

            if (!Runtime.ConditionResults.TryGetValue(state.Id, out var resultMap))
            {
                resultMap = new Dictionary<string, OutputEvaluation>();
                Runtime.ConditionResults[state.Id] = resultMap;
            }

            resultMap[output.Id] = detail;

            var edge = GetEdgeFromPort(state.Id, output.Id);
            if (edge is null || !detail.Matched)
                continue;

            var targetState = GetState(edge.ToStateId);
            if (targetState is null)
                continue;

            if (targetState.RegionId != region.Id)
            {
                AddLog(
                    "ERROR",
                    $"非法跨状态域连线：{state.Name} → {targetState.Name}",
                    "error");

                continue;
            }

            // =====================================================
            // 离开脚本
            // 当前卡片即将离开、切换到别的卡片之前执行
            // =====================================================
            if (edge.DelayMilliseconds > 0)
            {
                Runtime.CurrentEdgeIds.Add(edge.Id);
                AddLog("WAIT", $"[{region.Name}] {state.Name} → {targetState.Name} · 等待 {edge.DelayMilliseconds} ms", "good");
                Notify();
                try { await Task.Delay(Math.Clamp(edge.DelayMilliseconds, 0, 86400000), _executionCancellation.Token); }
                finally { Runtime.CurrentEdgeIds.Remove(edge.Id); Notify(); }
            }
            _executionCancellation.Token.ThrowIfCancellationRequested();
            await InvokeActionAsync(state.AfterEnterAction);

            Runtime.CurrentEdgeIds.Add(edge.Id);

            AddLog(
                "SWITCH",
                $"[{region.Name}] {state.Name} → {targetState.Name} · {output.Name}",
                "good");

            Notify();

            // =====================================================
            // 目标卡片的进入脚本
            // 在真正进入目标卡片之前执行
            // =====================================================
            await InvokeActionAsync(targetState.BeforeLeaveAction);
            _executionCancellation.Token.ThrowIfCancellationRequested();

            // 正式进入目标卡片
            Runtime.CurrentStates[region.Id] = targetState.Id;
            Runtime.TransitionCounts[edge.Id] = Runtime.TransitionCounts.GetValueOrDefault(edge.Id) + 1;
            _recognitionRevision++;
            _recognitionResults.Clear();
            Runtime.CurrentEdgeIds.Remove(edge.Id);

            // 标记目标卡片已经执行过“进入脚本”
            // 防止下一周期再次执行
            Runtime.EnteredStates[region.Id] = targetState.Id;

            RefreshCurrentConditionResults();
            Notify();

            return true;
        }

        return false;
    }
    private async Task<bool> ExecuteStepAsync()
    {
        var switched = false;
        RefreshCurrentConditionResults();
        foreach (var region in Machine.Regions.ToArray())
        {
            if (await ExecuteRegionStepAsync(region)) switched = true;
        }
        RefreshCurrentConditionResults();
        Notify();
        return switched;
    }

    private async Task StepAsync()
    {
        if (Runtime.Running || _executing) return;
        if (Runtime.CurrentStates.Count == 0) ResetRuntimeCore(logInitialStates: true);
        BeginExecution();
        try { await ExecuteStepAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("ERROR", ex.Message, "error"); }
        finally { EndExecution(); }
    }

    private async Task RunMachineAsync()
    {
        if (Runtime.Running || _executing) return;
        if (Runtime.CurrentStates.Count == 0) ResetRuntimeCore(logInitialStates: true);
        BeginExecution();
        Runtime.Running = true;
        Runtime.StopRequested = false;
        var token = ++_runToken;
        _recognitionResults.Clear();
        _recognitionRevision++;
        var recognitionLoop = RecognitionLoopAsync(_executionCancellation.Token);
        Notify();

        try
        {
        while (Runtime.Running && !Runtime.StopRequested && token == _runToken)
        {
            try
            {
                await ExecuteStepAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AddLog("ERROR", ex.Message, "error");
                Runtime.Running = false;
                break;
            }
            if (!Runtime.Running || Runtime.StopRequested) break;
            await _recognitionUpdated.WaitAsync(Math.Max(20, Machine.Settings.CycleDelay), _executionCancellation.Token);
        }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _executionCancellation.Cancel();
            try { await recognitionLoop; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AddLog("RECOGNITION", ex.Message, "error"); }
            EndExecution();
        }

        if (token == _runToken) Runtime.Running = false;
        Notify();
    }

    private void StopRuntime()
    {
        Runtime.StopRequested = true;
        Runtime.Running = false;
        _runToken++;
        _executionCancellation.Cancel();
        _scriptContext?.Input.ReleaseAll();
        Runtime.CurrentEdgeIds.Clear();
        AddLog("SYSTEM", "运行停止", "warn");
    }


    private void SetRuntimeVariable(

    string name,

    JsonNode? value)

    {

        var variable =

            Machine.Variables.FirstOrDefault(

                x => x.Name == name);




        if (variable is not null)

        {

            var converted =

                ConvertNodeValue(

                    value,

                    variable.Type);



            variable.Value =

                CloneNode(converted);



            Runtime.Variables[name] =

                CloneNode(converted);





        }

        else

        {

            Runtime.Variables[name] =

                CloneNode(value);

        }



        RefreshCurrentConditionResults();

    }

    // =========================================================
    // CONFIG
    // =========================================================

    private void AddLog(string type, string message, string level = "info")
    {
        Logs.Add(new LogEntry
        {
            Id = Uid("log"),
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Type = type,
            Message = message,
            Level = level
        });
        if (Logs.Count > 1000) Logs.RemoveRange(0, Logs.Count - 1000);
        Notify();
    }


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


    private void WakeRecognitionEvaluation()
    {
        if (_recognitionUpdated.CurrentCount == 0) _recognitionUpdated.Release();
    }

    private async Task RecognitionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && Runtime.Running)
        {
            try { await RefreshRecognitionsAsync(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog("RECOGNITION", ex.Message, "error");
                Runtime.Running = false;
                WakeRecognitionEvaluation();
                return;
            }
            Notify();
            await Task.Delay(30, token);
        }
    }

    private void BeginExecution()
    {
        _executionCancellation.Dispose();
        _executionCancellation = new CancellationTokenSource();
        _executing = true;
    }

    private void EndExecution() { _scriptContext.Input.ReleaseAll(); _executing = false; Notify(); }

    private string RecognitionPath(RecognitionItem item) => item.ParentId is { } parent

        ? $"{Machine.Recognitions.FirstOrDefault(i => i.Id == parent)?.Name}/{item.Name}" : item.Name;


    private async Task TestRecognitionAsync(RecognitionItem item)
    {
        if (_executing) return;
        BeginExecution();
        try
        {
            var settings = item.Settings.Snapshot();
            if (settings.Mode == "dictionary")
            {
                if (settings.DictionaryMode == "ocr") settings.Query = "";
                else if (string.IsNullOrWhiteSpace(settings.Query)) throw new InvalidOperationException("测试固定文字查找时，请先填写要查找的文字。");
            }
            await UpdateRecognitionAsync(item, settings);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("RECOGNITION", ex.Message, "error"); }
        finally { EndExecution(); }
    }

    private async Task ScanRecognitionConditionAsync(ConditionModel condition)
    {
        var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId)
            ?? throw new InvalidOperationException("条件引用的识别项目已删除，请重新选择。");
        var key = RecognitionScanKey(condition, item);
        var settings = item.Settings.Snapshot();
        var revision = _recognitionRevision;
        try
        {
            if (settings.Mode == "dictionary")
            {
                if (key.Mode == "text" && string.IsNullOrWhiteSpace(key.Query))
                    throw new InvalidOperationException($"字典 {item.Name} 的“查找固定文字”条件必须填写要查找的文字。");
                settings.DictionaryMode = key.Mode; settings.Query = key.Query;
            }
            var result = await UpdateRecognitionAsync(item, settings);
            if (revision != _recognitionRevision) return;
            _recognitionResults[key] = result;
            WakeRecognitionEvaluation();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _recognitionResults[key] = new(false, Error: ex.Message); throw; }
        finally { RefreshCurrentConditionResults(); }
    }

    private async Task RefreshRecognitionsAsync(string? stateId = null)
    {
        // Unused library entries are not scanned and can stay incomplete while being edited.
        var conditions = Machine.Regions.Select(r => GetCurrentState(r.Id)).Where(s => s is not null)
            .Where(s => stateId is null || s!.Id == stateId)

            .SelectMany(s => s!.Outputs).Where(o => o.MatchMode != "always")
            .SelectMany(o => o.Conditions).Where(c => c.Kind == "recognition")
            .ToArray();
        var scanned = new HashSet<(string Id, string Mode, string Query)>();
        foreach (var condition in conditions)
        {
            var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId)
                ?? throw new InvalidOperationException("条件引用的识别项目已删除，请重新选择。");
            var key = RecognitionScanKey(condition, item);
            if (!scanned.Add(key)) continue;
            await ScanRecognitionConditionAsync(condition);
        }
    }

    private async Task<ColorProbeResult> UpdateRecognitionAsync(RecognitionItem item, ColorProbeSettings? searchSettings = null)
    {
        var machine = Machine;
        var name = item.Name;
        var settings = searchSettings ?? item.Settings.Snapshot();
        var fingerprint = JsonSerializer.Serialize(item.Settings);
        ColorProbeResult result;
        try { result = await ColorScanner.ProbeAsync(settings, _executionCancellation.Token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result = new(false, Error: ex.Message); }
        _executionCancellation.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(machine, Machine) || !Machine.Recognitions.Contains(item)
            || item.Name != name || fingerprint != JsonSerializer.Serialize(item.Settings))
            throw new OperationCanceledException("检测期间识别库已改变。");
        _colorResults[name] = result;

        _colorResults[item.Id] = result;

        _colorResults[RecognitionPath(item)] = result;
        RefreshCurrentConditionResults();
        if (result.Error is not null) throw new InvalidOperationException($"识别 {name} 失败：{result.Error}");
        return result;
    }

    private RecognitionCriteria RecognitionCriteriaFor(ConditionModel condition)
    {
        if (condition.Recognition is not null) return condition.Recognition;
        var criteria = new RecognitionCriteria { Success = null };
        if (condition.RecognitionField == "status") criteria.Success = condition.ExpectedSuccess;
        else if (condition.RecognitionField == "text")
        { criteria.Text = NodeString(condition.RightValue); criteria.TextOperator = condition.Operator; }
        else if (TryNumber(Primitive(condition.RightValue), out var number))
        {
            switch (condition.RecognitionField)
            {
                case "similarity": criteria.Similarity = number; criteria.SimilarityOperator = condition.Operator; break;
                case "x": criteria.X = number; criteria.XOperator = condition.Operator; break;
                case "y": criteria.Y = number; criteria.YOperator = condition.Operator; break;
            }
        }
        return condition.Recognition = criteria;
    }

    private bool EvaluateRecognitionCondition(ConditionModel condition)
    {
        var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId);
        if (item is null) return false;
        var key = RecognitionScanKey(condition, item);
        if (key.Mode == "text" && string.IsNullOrWhiteSpace(key.Query)) return false;
        if (!_recognitionResults.TryGetValue(key, out var result) || result.Error is not null) return false;
        var criteria = RecognitionCriteriaFor(condition);
        if (criteria.Success.HasValue && result.Found != criteria.Success.Value) return false;
        if ((criteria.X.HasValue || criteria.Y.HasValue || !string.IsNullOrWhiteSpace(criteria.Text)) && !result.Found) return false;
        return OptionalNumberMatches(result.Similarity, criteria.Similarity, criteria.SimilarityOperator)
            && OptionalNumberMatches(result.X, criteria.X, criteria.XOperator)
            && OptionalNumberMatches(result.Y, criteria.Y, criteria.YOperator)
            && (string.IsNullOrWhiteSpace(criteria.Text) || RecognitionTextMatches(result.Text, criteria.Text, criteria.TextOperator));
    }

    private (string Id, string Mode, string Query) RecognitionScanKey(ConditionModel condition, RecognitionItem item)
    {
        if (item.Settings.Mode != "dictionary") return (item.Id, "feature", "");
        var criteria = RecognitionCriteriaFor(condition);
        var mode = criteria.DictionaryMode ?? item.Settings.DictionaryMode;
        var query = mode == "text" ? (criteria.SearchText ?? item.Settings.Query).Trim() : "";
        return (item.Id, mode, query);
    }

    private static bool OptionalNumberMatches(double actual, double? expected, string op) => !expected.HasValue || op switch
    {
        "==" => actual == expected.Value, "!=" => actual != expected.Value,
        ">" => actual > expected.Value, ">=" => actual >= expected.Value,
        "<" => actual < expected.Value, "<=" => actual <= expected.Value, _ => false
    };

    private static bool RecognitionTextMatches(string left, string right, string op) => op switch
        {
            "==" => left == right, "!=" => left != right,
            "contains" => left.Contains(right, StringComparison.Ordinal),
            "startsWith" => left.StartsWith(right, StringComparison.Ordinal),
            "endsWith" => left.EndsWith(right, StringComparison.Ordinal), _ => false
        };

}
