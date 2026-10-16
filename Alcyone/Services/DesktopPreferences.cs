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
    public int EditorScale { get; private set; } = 100;
    public int DesktopScale { get; private set; } = 100;
    public int BoardScale { get; private set; } = 25;
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
            EditorScale = Math.Clamp(saved.EditorScale ?? saved.TextScale ?? 100, 80, 160);
            DesktopScale = Math.Clamp(saved.DesktopScale ?? saved.TextScale ?? 100, 80, 160);
            BoardScale = Math.Clamp(saved.BoardScale ?? 25, 10, 100);
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
        settings = settings with { WallpaperTheme = settings.WallpaperTheme ?? WallpaperTheme, EditorScale = settings.EditorScale ?? EditorScale, DesktopScale = settings.DesktopScale ?? DesktopScale, BoardScale = settings.BoardScale ?? BoardScale };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, _path, true);
        DesktopAiEnabled = settings.DesktopAiEnabled;
        WallpaperTheme = settings.WallpaperTheme!;
        EditorScale = settings.EditorScale!.Value;
        DesktopScale = settings.DesktopScale!.Value;
        BoardScale = settings.BoardScale!.Value;
        FilePackingAnimation = settings.FilePackingAnimation; RestoreDesktop = settings.RestoreDesktop; DesktopEnabled = settings.DesktopEnabled; ConfigKey = settings.ConfigKey;
        Changed?.Invoke();
    }
    public void SetWallpaperTheme(string id) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled, id)); }
    public void SetEditorScale(int percent) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled, WallpaperTheme, EditorScale: Math.Clamp(percent, 80, 160))); }
    public void SetDesktopScale(int percent) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled, WallpaperTheme, DesktopScale: Math.Clamp(percent, 80, 160))); }
    public void SetBoardScale(int percent) { lock (_gate) Save(new(RestoreDesktop, DesktopEnabled, ConfigKey, FilePackingAnimation, DesktopAiEnabled, WallpaperTheme, BoardScale: Math.Clamp(percent, 10, 100))); }
    private sealed record Settings(bool RestoreDesktop, bool DesktopEnabled, string ConfigKey, bool FilePackingAnimation = true, bool DesktopAiEnabled = true, string? WallpaperTheme = null, int? TextScale = null, int? EditorScale = null, int? DesktopScale = null, int? BoardScale = null);
}
