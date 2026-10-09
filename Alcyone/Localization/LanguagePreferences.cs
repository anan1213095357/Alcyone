using System.Text.Json;

namespace StateMachine.Localization;

public sealed class LanguagePreferences
{
    private readonly string _path;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    public string Language { get; private set; } = "zh-CN";
    public event Action? Changed;

    public LanguagePreferences(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (IsSupported(saved?.Language)) Language = saved!.Language;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public static bool IsSupported(string? language) => language is "zh-CN" or "en";

    public async Task SetAsync(string language)
    {
        if (!IsSupported(language)) throw new ArgumentException("Unsupported language.", nameof(language));
        await _saveLock.WaitAsync();
        try
        {
            if (Language == language) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Replace only a completely written file; a failed save leaves the current language intact.
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Settings(language)));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            Language = language;
        }
        finally { _saveLock.Release(); }
        Changed?.Invoke();
    }

    private sealed record Settings(string Language);
}
