using System.Collections.Concurrent;
using FastColorFinder.Services;

namespace StateMachine.Training;

// Each embedded editor has its own captures; HTTP frame requests look up that circuit's session.
public sealed class TrainingSessions
{
    private readonly ConcurrentDictionary<string, TrainingSession> _sessions = new();
    internal void Add(TrainingSession session) => _sessions[session.Id] = session;
    internal void Remove(string id) => _sessions.TryRemove(id, out _);
    public byte[]? Frame(string id, bool text) => _sessions.TryGetValue(id, out var session)
        ? session.Frame(text) : null;
}

public sealed class TrainingSession : IDisposable
{
    private readonly TrainingSessions _sessions;
    private readonly object _gate = new();
    private bool _disposed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public FinderWorkspace Color { get; } = new();
    public TextFinderWorkspace Text { get; } = new();
    public TrainingSession(TrainingSessions sessions) { _sessions = sessions; sessions.Add(this); }
    internal byte[]? Frame(bool text)
    {
        lock (_gate) return _disposed ? null : text ? Text.CapturePreviewPng() : Color.CapturePreviewPng();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _sessions.Remove(Id); Color.Dispose(); Text.Dispose();
        }
    }
}
