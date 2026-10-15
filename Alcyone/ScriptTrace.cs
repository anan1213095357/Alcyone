using System.Collections.Concurrent;
using System.Diagnostics;

namespace StateMachine.Scripting;

// Tracing never calls user ToString/getters and does not retain script instances.
public static partial class ScriptTrace
{
    private static readonly AsyncLocal<Frame?> Current = new();
    private static readonly ConcurrentDictionary<long, Frame> Active = new();
    private static readonly ConcurrentQueue<TraceView> Finished = new();
    private static long _next;
    private static int _readers;
    public static bool Enabled => Volatile.Read(ref _readers) > 0;
    public static IDisposable Subscribe() { Interlocked.Increment(ref _readers); return new Subscription(); }
    private sealed class Subscription : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _readers); }
    }
    internal sealed class Frame
    {
        public long Id;
        public string File = "", Method = "", Code = "", Error = "";
        public int Line;
        public volatile bool Paused;
        public long Since = Stopwatch.GetTimestamp(), Sample;
        public Dictionary<string, string> Values = new();
        public Frame? Parent;
        public IStateScriptApi? Api;
        public Dictionary<string, string> Globals = new();
        public readonly object Gate = new();
    }
    public sealed record TraceView(long Id, string File, string Method, int Line, string Code, double Seconds, string Status, string Error, Dictionary<string, string> Values, long ParentId, Dictionary<string, string> Globals);
    public sealed class Scope : IDisposable
    {
        private static readonly Scope Disabled = new(null);
        private readonly Frame? _frame;
        private int _disposed;
        private Scope(Frame? frame) { _frame = frame; }
        public void Fail(Exception ex)
        {
            if (_frame is null) return;
            lock (_frame.Gate) _frame.Error = ex.ToString();
        }
        public void Dispose()
        {
            if (_frame is null || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Finished.Enqueue(View(_frame, _frame.Error.Length == 0 ? "completed" : "error"));
            while (Finished.Count > 64) Finished.TryDequeue(out _);
            Active.TryRemove(_frame.Id, out _);
            DebugFrameExited(_frame);
            Current.Value = _frame.Parent;
        }
        internal static Scope Start(string file, string method, IStateScriptApi? api)
        {
            if (!Enabled || Active.Count >= 128) return Disabled;
            var frame = new Frame { Id = Interlocked.Increment(ref _next), File = file, Method = method, Parent = Current.Value, Api = api ?? Current.Value?.Api };
            Current.Value = frame;
            Active[frame.Id] = frame;
            return new Scope(frame);
        }
    }
    public static Scope Enter(string file, string method, IStateScriptApi? api = null) => Scope.Start(file, method, api);
    public static bool Step(int line, string code)
    {
        if (!Enabled || Current.Value is not { } frame) return false;
        var now = Stopwatch.GetTimestamp();
        lock (frame.Gate)
        {
            var changed = frame.Line != line;
            if (changed) { frame.Line = line; frame.Code = code; frame.Since = now; }
            if (!changed && Debuggers.IsEmpty && Stopwatch.GetElapsedTime(frame.Sample, now).TotalMilliseconds < 50) return false;
            frame.Sample = now;
            return true;
        }
    }
    public static void Values(params (string Name, object? Value)[] values)
    {
        if (Current.Value is not { } frame) return;
        var snapshot = new Dictionary<string, string>();
        foreach (var (name, value) in values.Take(48))
        {
            var text = Describe(value);
            snapshot[name] = text.Length > 512 ? text[..512] + "…" : text;
        }
        lock (frame.Gate) frame.Values = snapshot;
    }
    private static string Describe(object? value, int depth = 0)
    {
        if (value is null) return "null";
        if (value is string text) return text.Length > 512 ? text[..512] + "…" : text;
        if (value is char or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid || value.GetType().IsEnum)
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (depth >= 2) return "<" + value.GetType().Name + ">";
        try
        {
            if (value is Array array && array.Rank == 1)
                return "[" + string.Join(", ", array.Cast<object?>().Take(12).Select(v => Describe(v, depth + 1))) + (array.Length > 12 ? ", …]" : "]");
            var fields = value.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(f => !typeof(Delegate).IsAssignableFrom(f.FieldType)).Take(12);
            return value.GetType().Name + " { " + string.Join(", ", fields.Select(f => (f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name) + " = " + Describe(f.GetValue(value), depth + 1))) + " }";
        }
        catch { return "<" + value.GetType().Name + ">"; }
    }
    private static TraceView View(Frame frame, string status)
    {
        lock (frame.Gate) return new(frame.Id, frame.File, frame.Method, frame.Line, frame.Code, Stopwatch.GetElapsedTime(frame.Since).TotalSeconds, frame.Paused ? "paused" : status, frame.Error, new(frame.Values), frame.Parent?.Id ?? 0, new(frame.Globals));
    }
    public static TraceView[] Snapshot() => Active.Values.OrderByDescending(f => f.Paused).Select(f => View(f, "running")).Concat(Finished.Reverse()).Take(192).ToArray();
}
