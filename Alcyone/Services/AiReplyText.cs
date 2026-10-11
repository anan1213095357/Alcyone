using System.Text.Json;
using System.Text.RegularExpressions;
namespace StateMachine;
public static class AiReplyText
{
    private static readonly Regex Reply = new("""
        "reply"\s*:\s*"((?:\\.|[^"\\])*)
        """, RegexOptions.Compiled);
    public static string FromPartialJson(string content)
    {
        var match = Reply.Match(content);
        if (!match.Success) return "";
        var text = match.Groups[1].Value;
        // A streamed token may end inside an escape sequence.
        while (text.Length > 0)
        {
            try { return JsonSerializer.Deserialize<string>('"' + text + '"') ?? ""; }
            catch (JsonException) { text = text[..^1]; }
        }
        return "";
    }
}
