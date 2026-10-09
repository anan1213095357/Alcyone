using System.Collections.Concurrent;
using System.Text;

namespace FastColorFinder.Services;

public sealed class TextDictionaryDownloads
{
    public sealed record Download(string FileName, byte[] Bytes, DateTime ExpiresAt);
    private readonly ConcurrentDictionary<string, Download> _files = new();
    public string Register(string fileName, string text)
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _files) if (pair.Value.ExpiresAt <= now) _files.TryRemove(pair.Key, out _);
        if (_files.Count >= 32)
        {
            var oldest = _files.OrderBy(pair => pair.Value.ExpiresAt).First(); _files.TryRemove(oldest.Key, out _);
        }
        var id = Guid.NewGuid().ToString("N");
        _files[id] = new Download(fileName, Encoding.UTF8.GetBytes(text), now.AddMinutes(5));
        return "/api/text/dictionaries/download/" + id;
    }
    public Download? Get(string id) => _files.TryGetValue(id, out var file) && file.ExpiresAt > DateTime.UtcNow ? file : null;
}
