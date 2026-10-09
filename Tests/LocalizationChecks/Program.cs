using System.Text.Json;
using System.Text.RegularExpressions;
using StateMachine.Localization;

var directory = Path.Combine(Path.GetTempPath(), "Alcyone-language-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "settings.json");
    var preferences = new LanguagePreferences(path);
    var text = new UiText(preferences);
    Check(preferences.Language == "zh-CN" && text["运行"] == "运行", "Chinese default");
    var notifications = 0;
    preferences.Changed += () => notifications++;
    await preferences.SetAsync("en");
    Check(text["运行"] == "Run" && notifications == 1, "Switch notification and translation");
    Check(new LanguagePreferences(path).Language == "en", "Persists across service restarts");
    await preferences.SetAsync("en");
    Check(notifications == 1, "No duplicate notification");
    Check(text["Customer-defined 状态"] == "Customer-defined 状态", "Unknown content preserved");
    Check(text["完成 · 12 帧 · 3 点"] == "Complete · 12 frames · 3 points", "Dynamic training status");
    Check(text["已切换到“自定义字库”。"] == "Switched to “自定义字库”.", "User names remain unchanged");
    Check(text.Format("条件 {0}", 2) == "Condition 2", "Formatted UI text");

    using (var resource = typeof(UiText).Assembly.GetManifestResourceStream("Alcyone.Localization.en.json")!)
    {
        var resources = JsonSerializer.Deserialize<Dictionary<string, string>>(resource)!;
        foreach (var (source, translation) in resources)
        {
            var placeholders = Regex.Matches(source, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Order().ToArray();
            var translated = Regex.Matches(translation, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Order().ToArray();
            Check(placeholders.SequenceEqual(translated), "Placeholder parity: " + source);
            if (placeholders.Length > 0)
            {
                var arguments = Enumerable.Range(0, placeholders.Select(int.Parse).Max() + 1).Select(i => (object)$"value{i}").ToArray();
                Check(text.Format(source, arguments) == string.Format(translation, arguments), "Format: " + source);
                Check(text[string.Format(source, arguments)] == string.Format(translation, arguments), "Existing status: " + source);
            }
        }
    }

    try { await preferences.SetAsync("fr"); throw new Exception("Unsupported language accepted"); }
    catch (ArgumentException) { }
    Check(preferences.Language == "en" && new LanguagePreferences(path).Language == "en", "Invalid choice does not overwrite preferences");
    await Task.WhenAll(preferences.SetAsync("zh-CN"), preferences.SetAsync("en"));
    Check(new LanguagePreferences(path).Language == preferences.Language, "Concurrent writes remain consistent");
    await preferences.SetAsync("zh-CN");
    Check(text["运行"] == "运行" && new LanguagePreferences(path).Language == "zh-CN", "Switch back and persist");

    File.WriteAllText(path, "not json");
    Check(new LanguagePreferences(path).Language == "zh-CN", "Corrupt preferences fallback");
    File.WriteAllText(path, "{\"Language\":\"unknown\"}");
    Check(new LanguagePreferences(path).Language == "zh-CN", "Unsupported saved language fallback");
    File.WriteAllText(path, "null");
    Check(new LanguagePreferences(path).Language == "zh-CN", "Null preferences fallback");

    var blocked = Path.Combine(directory, "blocked");
    File.WriteAllText(blocked, "A file cannot contain a settings directory.");
    var cannotSave = new LanguagePreferences(Path.Combine(blocked, "settings.json"));
    try { await cannotSave.SetAsync("en"); throw new Exception("Expected save failure"); }
    catch (IOException) { }
    Check(cannotSave.Language == "zh-CN", "Failed save preserves active language");
    Console.WriteLine("Localization checks passed: resources, live changes, persistence, invalid/corrupt settings, concurrency, and save failure.");
}
finally { Directory.Delete(directory, recursive: true); }

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
}
