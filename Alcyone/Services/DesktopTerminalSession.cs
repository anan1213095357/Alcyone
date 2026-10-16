using System.Diagnostics;
using System.Text;

namespace StateMachine;

public sealed class DesktopTerminalSession : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DesktopTerminalSession, byte> Sessions = new();
    private Process? _process;
    private TaskCompletionSource<string>? _done;
    private string _marker = "__ALCYONE_" + Guid.NewGuid().ToString("N") + ":";
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _outputGate = new();
    private readonly StringBuilder _output = new();
    private bool _disposed;
    public string Directory { get; private set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private void Start()
    {
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        _marker = "__ALCYONE_" + Guid.NewGuid().ToString("N") + ":";
        var marker = _marker;
        var worker = """
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            $OutputEncoding = [Text.Encoding]::UTF8
            $ProgressPreference = 'SilentlyContinue'
            while ($null -ne ($__alcyoneLine = [Console]::ReadLine())) {
                try {
                    $__alcyoneCode = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($__alcyoneLine))
                    . ([scriptblock]::Create($__alcyoneCode)) *>&1 | Out-String -Stream | ForEach-Object { [Console]::WriteLine($_) }
                } catch { [Console]::WriteLine(($_ | Out-String)) }
                [Console]::WriteLine()
                [Console]::WriteLine('__ALCYONE_MARKER__' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path)))
            }
            """.Replace("__ALCYONE_MARKER__", marker, StringComparison.Ordinal);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = System.IO.Directory.Exists(Directory) ? Directory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(worker)) }) start.ArgumentList.Add(arg);
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var process = _process;
        _process.Exited += (_, _) => { if (ReferenceEquals(_process, process)) _done?.TrySetException(new InvalidOperationException("PowerShell 会话已结束。")); };
        _process.Start(); Sessions[this] = 0;
        _ = ReadAsync(_process.StandardOutput, process, marker); _ = ReadAsync(_process.StandardError, process, marker);
    }
    private async Task ReadAsync(StreamReader reader, Process process, string marker)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!ReferenceEquals(_process, process)) return;
                if (line.StartsWith(marker, StringComparison.Ordinal)) { _done?.TrySetResult(line[marker.Length..]); continue; }
                lock (_outputGate) { _output.AppendLine(line); if (_output.Length > 100000) _output.Remove(0, _output.Length - 100000); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public string Output { get { lock (_outputGate) return _output.ToString(); } }
    public async Task ExecuteAsync(string command)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopTerminalSession));
        if (command.Length > 32000) throw new InvalidOperationException("命令过长。");
        await _gate.WaitAsync();
        try
        {
            lock (_outputGate) _output.Clear();
            _done = new(TaskCreationOptions.RunContinuationsAsynchronously); Start();
            await _process!.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(command)));
            await _process.StandardInput.FlushAsync();
            var path = await _done.Task;
            Directory = Encoding.UTF8.GetString(Convert.FromBase64String(path));
        }
        finally { _gate.Release(); }
    }
    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { _done?.TrySetException(ex); }
        _done?.TrySetException(new OperationCanceledException("命令已停止，会话将在下次执行时重建。"));
    }
    public static void StopAll() { foreach (var session in Sessions.Keys) session.Stop(); }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); Sessions.TryRemove(this, out _); _process?.Dispose(); }
}
