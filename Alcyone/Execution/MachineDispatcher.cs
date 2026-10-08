using System.Collections.Concurrent;
namespace StateMachine.Execution;
// One independent execution context per configuration. Continuations never depend on a UI circuit.
internal sealed class MachineDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    public MachineDispatcher()
    {
        _thread = new Thread(() => { SetSynchronizationContext(this); foreach (var action in _queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "StateMachine runtime" };
        _thread.Start();
    }
    public override void Post(SendOrPostCallback callback, object? state) { if (!_queue.IsAddingCompleted) _queue.Add(() => callback(state)); }
    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return Task.CompletedTask; });
    public Task InvokeAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ => { try { await action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } }, null);
        return completion.Task;
    }
    public void Dispose() { _queue.CompleteAdding(); if (Thread.CurrentThread != _thread) _thread.Join(); _queue.Dispose(); }
}
