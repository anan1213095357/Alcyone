using StateMachine.Automation;
namespace StateMachine.Execution;
public sealed record MachineSnapshot(MachineModel Machine, RuntimeModel Runtime, List<LogEntry> Logs,
    Dictionary<string, ColorProbeResult> ColorResults,
    Dictionary<(string Id, string Mode, string Query), ColorProbeResult> RecognitionResults, bool Executing);
