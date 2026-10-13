namespace StateMachine.Execution;
public sealed record ConditionTestResult(bool? Matched,string? Error);
public sealed partial class MachineSession
{
    public async Task<ConditionTestResult> TestConditionResultAsync(ConditionModel condition)
    {
        var resume=Snapshot.Runtime.Running;
        await StopAsync();if(_runTask is not null)await _runTask;
        try
        {
            var previous=Snapshot.Logs.Select(log=>log.Id).ToHashSet();
            await TestConditionAsync(condition);
            var result=new ConditionTestResult(null,"测试未完成");
            await DispatchAsync(()=>
            {
                var error=Logs.LastOrDefault(log=>!previous.Contains(log.Id)&&log.Level=="error")?.Message;
                result=new ConditionTestResult(error is null?EvaluateCondition(condition):null,error);
            });
            return result;
        }
        finally {if(resume)await StartAsync();}
    }
}
