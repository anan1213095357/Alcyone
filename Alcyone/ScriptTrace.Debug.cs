using System.Collections.Concurrent;

namespace StateMachine.Scripting;

public static partial class ScriptTrace
{
    private static readonly ConcurrentDictionary<string, DebugSession> Debuggers = new(StringComparer.OrdinalIgnoreCase);
    public static DebugSession StartDebug(string directory)
    {
        var key = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var session = new DebugSession(key);
        if (!Debuggers.TryAdd(key, session)) throw new InvalidOperationException("此脚本工作区已有调试会话。");
        return session;
    }
    public sealed class DebugSession : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource _resume = Signal();
        private Frame? _paused;
        private string _mode = "continue";
        private long _root;
        private int _depth;
        private bool _pauseRequested, _stopped, _disposed;
        private string[] _globalNames = [];
        public void SetGlobalNames(IEnumerable<string> names) { lock (_gate) _globalNames = names.Take(256).ToArray(); }
        public bool BreakOnException { get; set; } = true;
        public bool IsPaused { get { lock (_gate) return _paused is not null; } }
        public bool IsStopped { get { lock (_gate) return _stopped; } }
        public long PausedId { get { lock (_gate) return _paused?.Id ?? 0; } }
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DebugSession(string directory) { _directory = directory; }
        public void SetBreakpoints(string file, IEnumerable<int> lines)
        {
            lock (_gate) _breakpoints[Path.GetFullPath(file)] = lines.Where(l => l > 0).ToHashSet();
        }
        public void Command(string command)
        {
            TaskCompletionSource? release = null;
            lock (_gate)
            {
                if (_disposed) return;
                if (command == "pause") { if (!_stopped) _pauseRequested = true; return; }
                if (command == "stop")
                {
                    _stopped = true; _mode = "continue";
                    if (_paused is not null) _paused.Paused = false;
                    _paused = null; release = _resume; _resume = Signal();
                }
                else
                {
                    if (_stopped) return;
                    if (_paused is not null)
                    {
                        _root = Root(_paused); _depth = Depth(_paused); _mode = command;
                        _paused.Paused = false; _paused = null;
                        release = _resume; _resume = Signal();
                    }
                    else if (command == "continue")
                    { _pauseRequested = false; _mode = "continue"; release = _resume; _resume = Signal(); }
                }
            }
            release?.TrySetResult();
        }
        private static long Root(Frame frame) { while (frame.Parent is not null) frame = frame.Parent; return frame.Id; }
        private static int Depth(Frame frame) { var depth = 0; while (frame.Parent is not null) { depth++; frame = frame.Parent; } return depth; }
        internal async Task CheckAsync(Frame frame, bool exception)
        {
            while (true)
            {
                Task? wait = null;
                lock (_gate)
                {
                    if (_disposed) return;
                    if (_stopped) throw new OperationCanceledException("脚本调试已停止。");
                    if (_paused is not null)
                    {
                        wait = _resume.Task;
                    }
                    else
                    {
                        var sameFlow = _root == 0 || Root(frame) == _root;
                        var step = sameFlow && (_mode == "into" || _mode == "over" && Depth(frame) <= _depth || _mode == "out" && Depth(frame) < _depth);
                        var breakpoint = _breakpoints.TryGetValue(Path.GetFullPath(frame.File), out var lines) && lines.Contains(frame.Line);
                        if (_pauseRequested || step || breakpoint || exception && BreakOnException)
                        {
                            if (frame.Api is not null)
                            {
                                var globals = new Dictionary<string, string>();
                                foreach (var name in _globalNames)
                                {
                                    try { var text = frame.Api.GetNode(name)?.ToJsonString() ?? "null"; globals[name] = text.Length > 1024 ? text[..1024] + "…" : text; }
                                    catch { globals[name] = "<unavailable>"; }
                                }
                                lock (frame.Gate) frame.Globals = globals;
                            }
                            _pauseRequested = false; _mode = "continue";
                            _paused = frame; frame.Paused = true;
                            wait = _resume.Task;
                        }
                        else if (_mode != "continue" && !sameFlow) wait = _resume.Task;
                    }
                }
                if (wait is null) return;
                await wait.ConfigureAwait(false);
                // A resumed checkpoint must not immediately hit the same breakpoint again.
                lock (_gate)
                {
                    if (_stopped) throw new OperationCanceledException("脚本调试已停止。");
                    if (_disposed || _paused is null && (_mode == "continue" || Root(frame) == _root)) return;
                }
            }
        }
        internal void FrameExited(Frame frame)
        {
            TaskCompletionSource? release = null;
            lock (_gate)
            {
                if (frame.Parent is null && _root == frame.Id && _paused is null)
                { _mode = "continue"; release = _resume; _resume = Signal(); }
            }
            release?.TrySetResult();
        }
        public void Dispose()
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_paused is not null) _paused.Paused = false;
                _paused = null; release = _resume;
            }
            Debuggers.TryRemove(_directory, out _);
            release.TrySetResult();
        }
    }
    private static void DebugFrameExited(Frame frame)
    {
        foreach (var debugger in Debuggers.Values) debugger.FrameExited(frame);
    }
    public static void CloseDebug(string directory)
    {
        if (Debuggers.TryGetValue(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), out var session)) session.Dispose();
    }
    public static Task CheckpointAsync()
    {
        if (!Enabled || Current.Value is not { } frame) return Task.CompletedTask;
        var directory = Path.GetDirectoryName(Path.GetFullPath(frame.File));
        while (directory is not null)
        {
            if (Debuggers.TryGetValue(directory, out var debugger)) return debugger.CheckAsync(frame, false);
            directory = Path.GetDirectoryName(directory);
        }
        return Task.CompletedTask;
    }
    public static void Checkpoint() => CheckpointAsync().GetAwaiter().GetResult();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, object> SeenErrors = new();
    public static async Task ExceptionAsync(Exception error)
    {
        if (error is OperationCanceledException || Current.Value is not { } frame) return;
        lock (frame.Gate) frame.Error = error.ToString();
        lock (SeenErrors)
        {
            if (SeenErrors.TryGetValue(error, out _)) return;
            SeenErrors.Add(error, new object());
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(frame.File));
        while (directory is not null)
        {
            if (Debuggers.TryGetValue(directory, out var debugger)) { await debugger.CheckAsync(frame, true); return; }
            directory = Path.GetDirectoryName(directory);
        }
    }
    public static void Exception(Exception error) => ExceptionAsync(error).GetAwaiter().GetResult();
}
