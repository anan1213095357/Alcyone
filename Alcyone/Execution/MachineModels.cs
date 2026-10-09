using System.Text.Json.Nodes;
using StateMachine.Automation;
namespace StateMachine.Execution;

    public sealed class MachineModel
    {
        public List<RegionModel> Regions { get; set; } = new();
        public List<VariableModel> Variables { get; set; } = new();
        public List<RecognitionItem> Recognitions { get; set; } = new();
        public List<StateModel> States { get; set; } = new();
        public List<StateGroupModel> StateGroups { get; set; } = new();
        public List<EdgeModel> Edges { get; set; } = new();
        public MachineSettings Settings { get; set; } = new();
    }

    // Canvas-only organization; execution continues to use the original states and edges.
    public sealed class StateGroupModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "Alcyone";
        public List<string> StateIds { get; set; } = new();
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class MachineSettings
    {
        public int CycleDelay { get; set; } = 50;
    }

    public sealed class RegionModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
    public sealed class VariableModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Source { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ColorProbeSettings? Color { get; set; }
        public string Type { get; set; } = "number";
        public JsonNode? Value { get; set; }
    }

    public sealed class StateModel
    {
        public string Id { get; set; } = string.Empty;
        public string RegionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public bool IsStart { get; set; }

        public StateActionCallModel BeforeLeaveAction { get; set; } = new();
        public StateActionCallModel LoopAction { get; set; } = new();
        public StateActionCallModel AfterEnterAction { get; set; } = new();

        public List<InputPortModel> Inputs { get; set; } = new();
        public List<OutputPortModel> Outputs { get; set; } = new();
    }

    public sealed class StateActionCallModel
    {
        public string ActionMethod { get; set; } = string.Empty;
        public List<ActionArgumentModel> ActionArguments { get; set; } = new();
    }

    public sealed class InputPortModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public sealed class OutputPortModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MatchMode { get; set; } = "all";
        public List<ConditionModel> Conditions { get; set; } = new();

        // 仅用于兼容旧 JSON。新版 UI/运行时不再执行输出口动作。
        public string ActionMethod { get; set; } = string.Empty;
        public List<ActionArgumentModel> ActionArguments { get; set; } = new();
        public string ActionPath { get; set; } = string.Empty;
        public string ActionArgs { get; set; } = "[]";
    }

    public sealed class ActionArgumentModel
    {
        public string ParameterName { get; set; } = string.Empty;
        public string Source { get; set; } = "literal";
        public string LiteralValue { get; set; } = string.Empty;
        public string VariableName { get; set; } = string.Empty;
    }

    public sealed class ConditionModel
    {
        public RecognitionCriteria? Recognition { get; set; }
        public string Kind { get; set; } = "variable";
        public string RecognitionId { get; set; } = "";
        public string RecognitionField { get; set; } = "status";
        public bool ExpectedSuccess { get; set; } = true;
        public string Id { get; set; } = string.Empty;
        public string Left { get; set; } = string.Empty;
        public string Operator { get; set; } = "==";
        public string RightMode { get; set; } = "literal";
        public string RightType { get; set; } = "boolean";
        public JsonNode? RightValue { get; set; }
    }

    public sealed class EdgeModel
    {
        public string Id { get; set; } = string.Empty;
        public int DelayMilliseconds { get; set; }
        public string FromStateId { get; set; } = string.Empty;
        public string FromPortId { get; set; } = string.Empty;
        public string ToStateId { get; set; } = string.Empty;
        public string ToPortId { get; set; } = string.Empty;
    }

    public sealed class PendingConnectionModel
    {
        public string StateId { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
    }

    public sealed class RuntimeModel
    {
        public bool Running { get; set; }
        public bool StopRequested { get; set; }
        public Dictionary<string, JsonNode?> Variables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> CurrentStates { get; } = new();
        public Dictionary<string, string> EnteredStates { get; } = new();
        public HashSet<string> CurrentEdgeIds { get; } = new();
        public Dictionary<string, Dictionary<string, OutputEvaluation>> ConditionResults { get; } = new();
    }

    public sealed class OutputEvaluation
    {
        public bool Matched { get; set; }
        public Dictionary<string, bool> Conditions { get; set; } = new();
    }

    public sealed class LogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "info";
    }

    public sealed class CanvasPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
