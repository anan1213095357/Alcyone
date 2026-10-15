namespace StateMachine.Execution;

public sealed partial class MachineSession
{
    private readonly Dictionary<StateActionCallModel, Task> _lifecycleTasks = new();

    private async Task InvokeLifecycleAsync(StateActionCallModel lifecycle)
    {
        Task execution;
        lock (_lifecycleTasks)
        {
            // Avoid overlapping executions of a slow loop across scan cycles.
            if (_lifecycleTasks.TryGetValue(lifecycle, out var pending) && !pending.IsCompleted)
                execution = pending;
            else
            {
                var calls = new[] { lifecycle }.Concat(lifecycle.AdditionalActions ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.ActionMethod)).ToArray();
                execution = Task.Run(async () => await Task.WhenAll(calls.Select(InvokeActionAsync)));
                _lifecycleTasks[lifecycle] = execution;
                _ = execution.ContinueWith(completed =>
                {
                    lock (_lifecycleTasks)
                        if (_lifecycleTasks.TryGetValue(lifecycle, out var current) && ReferenceEquals(current, completed))
                            _lifecycleTasks.Remove(lifecycle);
                }, TaskScheduler.Default);
                if (!lifecycle.WaitForCompletion) _ = ObserveLifecycleAsync(execution);
            }
        }
        if (lifecycle.WaitForCompletion) await execution;
    }

    private async Task ObserveLifecycleAsync(Task execution)
    {
        try { await execution; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("ERROR", ex.Message, "error"); }
    }
}
