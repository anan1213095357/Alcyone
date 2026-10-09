using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StateMachine.Localization;

public sealed class UiText(LanguagePreferences preferences)
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();
    private static readonly (Regex Pattern, string Translation)[] Messages = English
        .Where(pair => pair.Key.Contains("{0}", StringComparison.Ordinal))
        .OrderByDescending(pair => pair.Key.Length)
        .Select(pair => (new Regex("^" + Regex.Replace(Regex.Escape(pair.Key), @"\\\{\d+}", "(.*?)") + "$",
            RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(50)), pair.Value))
        .ToArray();
    public string Language => preferences.Language;
    public event Action? Changed
    {
        add => preferences.Changed += value;
        remove => preferences.Changed -= value;
    }
    public string this[string? source]
    {
        get
        {
            if (source is null) return "";
            if (Language != "en") return source;
            if (English.TryGetValue(source, out var translated)) return translated;
            if (source.Length > 8192) return source;
            foreach (var (pattern, translation) in Messages)
            {
                Match match;
                try { match = pattern.Match(source); }
                catch (RegexMatchTimeoutException) { continue; }
                if (match.Success)
                    return string.Format(CultureInfo.InvariantCulture, translation,
                        match.Groups.Cast<Group>().Skip(1).Select(group => (object)group.Value).ToArray());
            }
            return source;
        }
    }
    public string Format(string source, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, this[source], args);
    public Task ChangeAsync(string language) => preferences.SetAsync(language);

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("Alcyone.Localization.en.json")
            ?? throw new InvalidOperationException("Missing English UI resources.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
