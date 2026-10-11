using System.Diagnostics;
using System.Text;

namespace StateMachine;

public sealed record DesktopCliResult(int ExitCode, string Output);

public static class DesktopCli
{
    public static async Task<DesktopCliResult> RunAsync(string script, string directory, Func<string, Task> output, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(script) || script.Length > 32000) throw new InvalidOperationException("CLI 命令为空或过长。");
        directory = string.IsNullOrWhiteSpace(directory) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Environment.ExpandEnvironmentVariables(directory);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("CLI 工作目录不存在，请使用完整路径。");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(shell) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';\ntry { & {\n" + script + "\n} *>&1 | Out-String -Stream | ForEach-Object { [Console]::WriteLine($_) } } catch { [Console]::Error.WriteLine(($_ | Out-String)); exit 1 }")) }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var log = new StringBuilder();
        var gate = new SemaphoreSlim(1, 1);
        var clock = Stopwatch.StartNew(); long lastReport = -100;
        async Task Read(StreamReader reader)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                await gate.WaitAsync(timeout.Token);
                try
                {
                    log.Append(buffer, 0, count);
                    if (log.Length > 64000) log.Remove(0, log.Length - 64000);
                    if (clock.ElapsedMilliseconds - lastReport >= 100) { lastReport = clock.ElapsedMilliseconds; await output(log.ToString()); }
                }
                finally { gate.Release(); }
            }
        }
        var reads = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try
        {
            await Task.WhenAll(reads, process.WaitForExitAsync(timeout.Token));
            await output(log.ToString());
            return new(process.ExitCode, log.ToString());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await reads; } catch (OperationCanceledException) { }
            throw new InvalidOperationException(cancellation.IsCancellationRequested ? "CLI 已停止。" : "CLI 执行超过 2 分钟，已停止。");
        }
        finally { gate.Dispose(); }
    }
}
