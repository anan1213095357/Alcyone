using Microsoft.Win32;
using System.Text.Json;

namespace StateMachine;

public sealed class DesktopPreferences
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _path;
    private readonly object _gate = new();
    public event Action? Changed;
    public bool DesktopAiEnabled { get; private set; } = true;
    public bool FilePackingAnimation { get; private set; } = true;
    public bool RestoreDesktop { get; private set; } = true;
    public bool DesktopEnabled { get; private set; }
    public string WallpaperTheme { get; private set; } = "default";
    public string ConfigKey { get; private set; } = "default";
    public bool AutoStart
    {
        get { if (!OperatingSystem.IsWindows()) return false; using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("Alcyone") is string; }
    }
    public DesktopPreferences(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alcyone", "desktop.json");
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path));
            if (saved is null) return;
            WallpaperTheme = saved.WallpaperTheme ?? "default";
            DesktopAiEnabled = saved.DesktopAiEnabled; FilePackingAnimation = saved.FilePackingAnimation; RestoreDesktop = saved.RestoreDesktop; DesktopEnabled = saved.DesktopEnabled; ConfigKey = saved.ConfigKey;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled) { key.DeleteValue("Alcyone", false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用路径。");
        var command = $"\"{executable}\"";
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command += $" \"{Path.Combine(AppContext.BaseDirectory, "Alcyone.dll")}\"";
        key.SetValue("Alcyone", command + " --startup");
    }
    public void SetRestore(bool enabled) { lock (_gate) Save(new(enabled, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled)); }
    public void RememberDesktop(bool enabled, string? configKey = null) { lock (_gate) Save(new(RestoreDesktop, enabled, configKey ?? ConfigKey, FilePackingAnimation, DesktopAiEnabled)); }
    public void SetFilePacking(bool enabled) { lock(_gate) Save(new(RestoreDesktop,DesktopEnabled,ConfigKey,enabled,DesktopAiEnabled)); }
    public void SetDesktopAi(bool enabled) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, enabled)); }
    private void Save(Settings settings)
    {
        settings = settings with { WallpaperTheme = settings.WallpaperTheme ?? WallpaperTheme };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, _path, true);
        DesktopAiEnabled = settings.DesktopAiEnabled;
        WallpaperTheme = settings.WallpaperTheme!;
        FilePackingAnimation = settings.FilePackingAnimation; RestoreDesktop = settings.RestoreDesktop; DesktopEnabled = settings.DesktopEnabled; ConfigKey = settings.ConfigKey;
        Changed?.Invoke();
    }
    public void SetWallpaperTheme(string id) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled, id)); }
    private sealed record Settings(bool RestoreDesktop, bool DesktopEnabled, string ConfigKey, bool FilePackingAnimation = true, bool DesktopAiEnabled = true, string? WallpaperTheme = null);
}
