using System.Diagnostics;
using System.Drawing;
using FastTextFinderRuntime;

namespace StateMachine.Automation;

internal static class RecognitionSearch
{
    private static readonly object DictionaryCacheLock = new();
    private static readonly Dictionary<string, string> DictionaryCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> DictionaryCacheOrder = new();

    public static string DictionaryWeights(string data)
    {
        lock (DictionaryCacheLock)
        {
            if (DictionaryCache.TryGetValue(data, out var cached)) return cached;
        }
        var weights = TextModelCodec.Encode(TextDictionaryFile.Read(data));
        lock (DictionaryCacheLock)
        {
            if (DictionaryCache.TryGetValue(data, out var cached)) return cached;
            // Bound retained dictionary data; edits naturally use a different key.
            while (DictionaryCache.Count >= 8)
                DictionaryCache.Remove(DictionaryCacheOrder.Dequeue());
            DictionaryCache.Add(data, weights);
            DictionaryCacheOrder.Enqueue(data);
        }
        return weights;
    }

    public static async Task<ColorProbeResult> FindAsync(ColorProbeSettings settings, CancellationToken token, double seconds)
    {
        var range = new Rectangle(settings.X, settings.Y, settings.Width, settings.Height);
        if (settings.DictionaryMode == "text")
        {
            var hit = await FastTextSearch.FindAsync(range, DictionaryWeights(settings.WeightString),
                string.IsNullOrWhiteSpace(settings.Query) ? null : settings.Query,
                (int)Math.Ceiling(settings.Similarity), settings.ColorDeviation, seconds,
                cancellationToken: token, step: settings.ScanStep);
            return new(hit.Found, hit.X, hit.Y, hit.Similarity, Text: hit.Text);
        }

        var watch = Stopwatch.StartNew();
        var weights = DictionaryWeights(settings.WeightString);
        ColorProbeResult result;
        do
        {
            token.ThrowIfCancellationRequested();
            result = await Task.Run(() =>
            {
                using var frame = TextScreenCapture.Capture(range);
                var ocr = FastTextSearch.Recognize(frame, weights, (int)Math.Ceiling(settings.Similarity),
                    settings.ColorDeviation, settings.ScanStep, maxResults: 512, cancellationToken: token).Offset(range.X, range.Y);
                var found = ocr.Matches.Count > 0 && (string.IsNullOrWhiteSpace(settings.Query)
                    || ocr.Text.Contains(settings.Query, StringComparison.Ordinal));
                var first = ocr.Matches.FirstOrDefault();
                return new ColorProbeResult(found, first is null ? -1 : first.X,
                    first is null ? -1 : first.Y,
                    ocr.Matches.Count == 0 ? 0 : ocr.Matches.Min(m => m.Similarity), Text: ocr.Text);
            }, token);
            if (result.Found || watch.Elapsed.TotalSeconds >= seconds) return result;
            await Task.Delay(50, token);
        } while (watch.Elapsed.TotalSeconds < seconds);
        return result;
    }

}
