using System.Drawing;
using FastColorFinderRuntime;

namespace StateMachine.Automation;

public sealed record TrainingCaptureRegion(int X, int Y, int Width, int Height);

public sealed class ColorProbeSettings
{
    public string Mode { get; set; } = "feature";
    public string WeightString { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public int ColorDeviation { get; set; }
    public double Similarity { get; set; } = 90;
    public string Query { get; set; } = "";
    public string DictionaryMode { get; set; } = "ocr";
    public string FileName { get; set; } = "";
    public int ScanStep { get; set; } = 1;
    public TrainingCaptureRegion? TrainingCapture { get; set; }
    public TrainingEditorState? TrainingEditor { get; set; }

    public ColorProbeSettings Snapshot() => (ColorProbeSettings)MemberwiseClone();
}

public sealed record ColorProbeResult(bool Found, int X = -1, int Y = -1,
    double Similarity = 0, string? Error = null, string Text = "");

public sealed class RecognitionItem
{
    public bool IsFolder { get; set; }
    public string? ParentId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新识别项目";
    public ColorProbeSettings Settings { get; set; } = new();

    public static string KindName(string mode) => mode switch
    {
        "feature" => "多点色库", "dictionary" => "字典", _ => "识别"
    };
}

public static class RecognitionLibrary
{
    public static void NormalizeFolders(List<RecognitionItem> items)
    {
        foreach (var entry in items.Where(i => i.Settings.Mode == "feature" && !i.IsFolder && i.ParentId is null).ToArray())
        {
            var folder = new RecognitionItem { Name = entry.Name, IsFolder = true };
            entry.ParentId = folder.Id;
            items.Add(folder);
        }
    }
}

public sealed class RecognitionCriteria
{
    public string? DictionaryMode { get; set; }
    public string? SearchText { get; set; }
    public bool? Success { get; set; } = true;
    public double? Similarity { get; set; }
    public string SimilarityOperator { get; set; } = ">=";
    public double? X { get; set; }
    public string XOperator { get; set; } = "==";
    public double? Y { get; set; }
    public string YOperator { get; set; } = "==";
    public string? Text { get; set; }
    public string TextOperator { get; set; } = "contains";
    public bool? IsInteger { get; set; }
    public string? IntegerValue { get; set; }
    public string IntegerOperator { get; set; } = ">=";

    public bool MatchesOcr(string text)
    {
        var valid = System.Numerics.BigInteger.TryParse(text.Trim(),
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var actual);
        if (IsInteger.HasValue && valid != IsInteger.Value) return false;
        if (string.IsNullOrWhiteSpace(IntegerValue)) return true;
        if (!valid || !System.Numerics.BigInteger.TryParse(IntegerValue.Trim(),
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var expected)) return false;
        return IntegerOperator switch
        {
            "==" => actual == expected, "!=" => actual != expected,
            ">" => actual > expected, ">=" => actual >= expected,
            "<" => actual < expected, "<=" => actual <= expected, _ => false
        };
    }
}

public interface IColorProbeScanner
{
    Task<ColorProbeResult> ProbeAsync(ColorProbeSettings settings, CancellationToken token, double seconds = 0);
}

public sealed class DesktopColorProbeScanner : IColorProbeScanner
{
    public Task<ColorProbeResult> ProbeAsync(ColorProbeSettings settings, CancellationToken token, double seconds = 0) =>
        ColorProbeApi.ProbeAsync(settings, token, seconds);
}

/// <summary>Uses the finder's exported runtime; the training application is not required at runtime.</summary>
public sealed class ColorProbeApi
{
    private readonly Func<string, ColorProbeSettings?> _settings;
    private readonly Func<string, ColorProbeResult?> _last;
    private readonly Func<CancellationToken> _token;
    private readonly IColorProbeScanner _scanner;

    public ColorProbeApi(Func<string, ColorProbeSettings?> settings,
        Func<string, ColorProbeResult?> last, Func<CancellationToken> token, IColorProbeScanner? scanner = null)
    {
        _settings = settings;
        _last = last;
        _token = token;
        _scanner = scanner ?? new DesktopColorProbeScanner();
    }

    public ColorProbeResult? Last(string name) => _last(name);

    public Task<ColorProbeResult> FindAsync(string name, double seconds = 0)
    {
        var settings = _settings(name)?.Snapshot()
            ?? throw new ArgumentException($"识别项目不存在：{name}");
        return _scanner.ProbeAsync(settings, _token(), seconds);
    }

    public Task<ColorProbeResult> FindAsync(int x, int y, int width, int height,
        string weightString, int colorDeviation = 0, double similarity = 90, double seconds = 0) =>
        _scanner.ProbeAsync(new ColorProbeSettings { X = x, Y = y, Width = width, Height = height,
            WeightString = weightString, ColorDeviation = colorDeviation, Similarity = similarity }, _token(), seconds);

    public static async Task<ColorProbeResult> ProbeAsync(ColorProbeSettings settings,
        CancellationToken cancellationToken = default, double seconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("找色需要 Windows 桌面。");
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentException("查找秒数必须为非负有限数值。");
        if (settings.Mode is not ("feature" or "dictionary")) throw new ArgumentException("未知识别模式。");
        if (!double.IsFinite(settings.Similarity)) throw new ArgumentException("相似度必须为有限数值。");
        if (settings.Width <= 0 || settings.Height <= 0 || settings.Similarity is <= 0 or > 100
            || settings.ColorDeviation is < 0 or > 255 || settings.ScanStep is < 1 or > 8)
            throw new ArgumentException("请检查搜索范围、相似度、色偏和扫描步长。");
        if (settings.Mode == "dictionary")
        {
            if (settings.DictionaryMode is not ("text" or "ocr")) throw new ArgumentException("未知字典识别方式。");
            return await RecognitionSearch.FindAsync(settings, cancellationToken, seconds);
        }

        var result = await FastColorSearch.FindAsync(
            new Rectangle(settings.X, settings.Y, settings.Width, settings.Height),
            settings.WeightString.Trim(), settings.ColorDeviation, settings.Similarity,
            null, null, seconds, 50, cancellationToken);
        return result.Success is { } hit
            ? new(true, hit.X, hit.Y, hit.Similarity)
            : new(false, Similarity: result.Failure?.BestSimilarity ?? 0);
    }

}
