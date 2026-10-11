using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StateMachine.Execution;

public static class WindowsConditions
{
    public static readonly Dictionary<string, string> Checks = new()
    {
        ["file.exists"] = "文件存在", ["directory.exists"] = "文件夹存在",
        ["file.size"] = "文件大小（MB）", ["file.age"] = "文件最后修改距今（秒）",
        ["disk.ready"] = "磁盘可用", ["disk.free"] = "磁盘剩余空间（GB）",
        ["window.exists"] = "窗口标题包含", ["window.foreground"] = "前台窗口标题包含",
        ["process.running"] = "进程正在运行"
    };
    public static bool Numeric(string check) => check is "file.size" or "file.age" or "disk.free";
    public static bool Evaluate(ConditionModel c)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(c.SystemQuery)) return false;
        try
        {
            var query = Environment.ExpandEnvironmentVariables(c.SystemQuery.Trim());
            if (Numeric(c.SystemCheck))
            {
                double value;
                if (c.SystemCheck == "disk.free") { var drive = new DriveInfo(query); if (!drive.IsReady) return false; value = drive.AvailableFreeSpace / 1073741824d; }
                else { var file = new FileInfo(query); if (!file.Exists) return false; value = c.SystemCheck == "file.size" ? file.Length / 1048576d : (DateTime.UtcNow - file.LastWriteTimeUtc).TotalSeconds; }
                return c.Operator switch { "==" => Math.Abs(value-c.SystemValue)<.000001, "!=" => Math.Abs(value-c.SystemValue)>=.000001, ">" => value>c.SystemValue, ">=" => value>=c.SystemValue, "<" => value<c.SystemValue, "<=" => value<=c.SystemValue, _ => false };
            }
            var found = c.SystemCheck switch
            {
                "file.exists" => File.Exists(query), "directory.exists" => Directory.Exists(query),
                "disk.ready" => new DriveInfo(query).IsReady,
                "window.exists" => HasWindow(query), "window.foreground" => TitleMatches(GetForegroundWindow(), query),
                "process.running" => HasProcess(query), _ => false
            };
            return found == c.ExpectedSuccess;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
    private static bool HasProcess(string query)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(query));
        try { return processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    private static bool HasWindow(string query)
    {
        var found = false;
        EnumWindows((window, _) => { if (IsWindowVisible(window) && TitleMatches(window, query)) { found = true; return false; } return true; }, 0);
        return found;
    }
    public static IReadOnlyList<string> WindowTitles()
    {
        var titles = new List<string>();
        if (!OperatingSystem.IsWindows()) return titles;
        EnumWindows((window, _) => { if(IsWindowVisible(window)){var text=new StringBuilder(1024);GetWindowTextW(window,text,text.Capacity);if(text.Length>0)titles.Add(text.ToString());} return true; },0);
        return titles.Distinct().Order().ToArray();
    }
    private static bool TitleMatches(nint window, string query) { var title = new StringBuilder(1024); GetWindowTextW(window, title, title.Capacity); return title.ToString().Contains(query, StringComparison.OrdinalIgnoreCase); }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int length);
}
