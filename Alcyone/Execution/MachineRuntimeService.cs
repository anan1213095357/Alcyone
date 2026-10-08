namespace StateMachine.Execution;
public sealed class MachineRuntimeService(IServiceProvider services, IWebHostEnvironment environment, StateMachine.Automation.IColorProbeScanner scanner) : IAsyncDisposable
{
    private readonly Dictionary<string, MachineSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    public MachineSession GetSession(string key, MachineModel machine)
    {
        lock (_sessions) { if (!_sessions.TryGetValue(key, out var session)) _sessions[key] = session = new(services, environment, scanner, machine); return session; }
    }
    public async Task RemoveAsync(string key)
    {
        MachineSession? session;
        lock (_sessions) { _sessions.Remove(key, out session); }
        if (session is not null) await session.DisposeAsync();
    }
    public async ValueTask DisposeAsync() { foreach (var session in _sessions.Values) await session.DisposeAsync(); }
}
