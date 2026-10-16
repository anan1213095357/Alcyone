using System.Text.Json;

namespace StateMachine;

public sealed record WallpaperTheme(string Id, string Name, string Description, string? Module = null);

public sealed class WallpaperThemes
{
    public IReadOnlyList<WallpaperTheme> All { get; }
    public WallpaperThemes(IWebHostEnvironment environment, ILogger<WallpaperThemes> logger)
    {
        var themes = new List<WallpaperTheme> { new("default", "星河", "默认星空壁纸") };
        var root = Path.Combine(environment.WebRootPath, "wallpapers");
        if (Directory.Exists(root))
            foreach (var directory in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
            {
                try
                {
                    var manifest = Path.Combine(directory, "theme.json");
                    if (!File.Exists(manifest)) continue;
                    var theme = JsonSerializer.Deserialize<WallpaperTheme>(File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var id = Path.GetFileName(directory);
                    if (theme is null || theme.Id != id || string.IsNullOrWhiteSpace(theme.Name) || theme.Module != "theme.js" || !File.Exists(Path.Combine(directory, "theme.js")) || themes.Any(t => t.Id == id)) continue;
                    themes.Add(theme with { Module = $"./wallpapers/{Uri.EscapeDataString(id)}/theme.js" });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { logger.LogWarning(ex, "Cannot load wallpaper theme {Directory}", directory); }
            }
        All = themes.AsReadOnly();
    }
    public WallpaperTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
