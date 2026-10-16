using System.Text.Json;
using System.Text.RegularExpressions;
using StateMachine.Localization;

Check(StateMachine.AiConnectionSettings.NormalizeEndpoint("https://example.com/v1/") == "https://example.com/v1/chat/completions", "AI base URL appends completion suffix");
Check(StateMachine.AiConnectionSettings.NormalizeEndpoint("https://example.com/custom/api") == "https://example.com/custom/api/chat/completions", "Custom provider paths append completion suffix");
Check(StateMachine.AiConnectionSettings.NormalizeEndpoint("https://example.com/v1/chat/completions/") == "https://example.com/v1/chat/completions", "Full completion URL is not duplicated");
Check(StateMachine.AiConnectionSettings.ToBaseEndpoint("https://example.com/v1/chat/completions") == "https://example.com/v1", "Settings field omits fixed suffix");
Check(StateMachine.AiConnectionSettings.NormalizeEndpoint("https://example.com/v1?api-version=1") == "https://example.com/v1/chat/completions?api-version=1", "Provider query parameters are preserved");
// Razor localizers are server objects and must not leak into browser scripts.
var sourceRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
while (sourceRoot is not null && !File.Exists(Path.Combine(sourceRoot.FullName, "Alcyone", "Components", "Pages", "Home.razor"))) sourceRoot = sourceRoot.Parent;
if (sourceRoot is not null)
{
    var pageSource = File.ReadAllText(Path.Combine(sourceRoot.FullName, "Alcyone", "Components", "Pages", "Home.razor"));
    foreach (Match script in Regex.Matches(pageSource, @"<script[^>]*>(.*?)</script>", RegexOptions.Singleline))
        Check(!Regex.IsMatch(script.Groups[1].Value, @"(?<!@)\b(?:L|Text)\["""), "Canvas scripts cannot reference server translation objects");
}

var directory = Path.Combine(Path.GetTempPath(), "Alcyone-language-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "settings.json");
    var desktopPath = Path.Combine(directory, "desktop.json");
    var desktopPreferences = new StateMachine.DesktopPreferences(desktopPath);
    Check(desktopPreferences.WallpaperTheme == "default", "Existing installations retain the default wallpaper");
    desktopPreferences.SetWallpaperTheme("gravity");
    Check(desktopPreferences.EditorScale == 100, "Editor UI scale defaults to 100 percent");
    desktopPreferences.SetEditorScale(125);
    desktopPreferences.SetDesktopAi(false);
    desktopPreferences.SetFilePacking(false);
    desktopPreferences.SetRestore(false);
    desktopPreferences.RememberDesktop(true, "example");
    var restoredDesktop = new StateMachine.DesktopPreferences(desktopPath);
    Check(restoredDesktop.EditorScale == 125, "Editor UI scale survives other settings and restart");
    Check(restoredDesktop.WallpaperTheme == "gravity" && !restoredDesktop.DesktopAiEnabled && restoredDesktop.ConfigKey == "example", "Wallpaper selection survives other settings and restart");
    restoredDesktop.SetWallpaperTheme("default");
    Check(new StateMachine.DesktopPreferences(desktopPath).EditorScale == 125, "Changing wallpaper preserves editor UI scale");
    restoredDesktop.SetEditorScale(999);
    Check(restoredDesktop.EditorScale == 160, "Editor UI scale has a safe upper bound");
    restoredDesktop.SetEditorScale(0);
    Check(restoredDesktop.EditorScale == 80, "Editor UI scale has a safe lower bound");
    restoredDesktop.SetEditorScale(100);
    Check(new StateMachine.DesktopPreferences(desktopPath).EditorScale == 100, "Reset editor UI scale persists");
    Check(new StateMachine.DesktopPreferences(desktopPath).WallpaperTheme == "default", "Switching back to the default persists");
    var preferences = new LanguagePreferences(path);
    var text = new UiText(preferences);
    Check(preferences.Language == "zh-CN" && text["运行"] == "运行", "Chinese default");
    var notifications = 0;
    preferences.Changed += () => notifications++;
    await preferences.SetAsync("en");
    Check(text["运行"] == "Run" && notifications == 1, "Switch notification and translation");
    foreach (var key in new[] { "星舰外观", "桌面与启动", "目标文件", "API 参考", "保存并编译", "条件函数", "自定义", "请选择判断 API", "待执行 · PowerShell", "函数返回", "编译时自动下载缺失库" })
        Check(text[key] != key && !Regex.IsMatch(text[key], "[\u4e00-\u9fff]"), "Remaining UI label translated: " + key);
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

// OCR values must compare numerically without floating point precision loss.
var integerCriteria = new StateMachine.Automation.RecognitionCriteria { IsInteger = true };
Check(integerCriteria.MatchesOcr(" -42 "), "Signed OCR integer");
foreach (var invalid in new[] { "", "1.5", "1e3", "1,000", "12abc", "12\n34" })
    Check(!integerCriteria.MatchesOcr(invalid), "Reject invalid OCR integer: " + invalid);
integerCriteria.IntegerValue = "9007199254740993";
integerCriteria.IntegerOperator = ">";
Check(integerCriteria.MatchesOcr("9007199254740994"), "Exact comparison above double precision");
Check(!integerCriteria.MatchesOcr("9007199254740993"), "Strict numeric comparison");
foreach (var op in new[] { "==", "!=", ">", ">=", "<", "<=" })
{
    integerCriteria.IntegerValue = "10";
    integerCriteria.IntegerOperator = op;
    Check(integerCriteria.MatchesOcr("2") == (op is "!=" or "<" or "<="), "Numeric ordering: " + op);
}
integerCriteria.IntegerValue = "bad";
Check(!integerCriteria.MatchesOcr("10"), "Invalid comparison input cannot match");
integerCriteria.IntegerValue = null;
integerCriteria.IsInteger = false;
Check(integerCriteria.MatchesOcr("abc") && !integerCriteria.MatchesOcr("12"), "Noninteger condition");
Console.WriteLine("OCR integer checks passed.");
