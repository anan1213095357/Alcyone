namespace StateMachine.Execution;

public sealed partial class MachineSession
{
    private readonly Dictionary<string,(long At,bool Result)> _conditionScanCache=new();
    private bool ConditionScanDue(ConditionModel condition)
    {
        var interval=Math.Clamp(condition.ScanIntervalMilliseconds,0,3600000);
        return interval==0 || !_conditionScanCache.TryGetValue(condition.Id,out var previous) || Environment.TickCount64-previous.At>=interval;
    }
    private void RecordConditionScan(ConditionModel condition,bool result) => _conditionScanCache[condition.Id]=(Environment.TickCount64,result);
    private bool EvaluateCondition(ConditionModel condition)
    {
        if(condition.Kind is "script" or "recognition")
            return _conditionScanCache.TryGetValue(condition.Id,out var previous) ? previous.Result : EvaluateConditionNow(condition);
        if(!ConditionScanDue(condition)) return _conditionScanCache[condition.Id].Result;
        var result=EvaluateConditionNow(condition); RecordConditionScan(condition,result); return result;
    }
}
