using StateMachine.Automation;
using FastColorFinder.Core;
using FastTextFinderRuntime;

namespace StateMachine.Training;

public sealed class TrainingResult
{
    public string Name { get; set; } = "";
    public string? TargetId { get; set; }
    public ColorProbeSettings Settings { get; set; } = new();

    public ColorProbeSettings ValidatedSettings()
    {
        var copy = Settings.Snapshot();
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidOperationException("请填写识别项目名称。");
        if (copy.Width <= 0 || copy.Height <= 0) throw new InvalidOperationException("请先设置搜索范围。");
        if (!double.IsFinite(copy.Similarity) || copy.Similarity is < 0 or > 100 ||
            copy.ColorDeviation is < 0 or > 255 || copy.ScanStep is < 1 or > 32)
            throw new InvalidOperationException("识别参数超出有效范围。");
        if (copy.Mode == "feature") copy.WeightString = ColorModelCodec.Encode(ColorModelCodec.Decode(copy.WeightString));
        else if (copy.Mode == "dictionary")
        {
            var dictionary = TextDictionaryFile.Read(copy.WeightString);
            if (dictionary.Glyphs.Count == 0) throw new InvalidOperationException("请先训练或导入文字模板。");
            var ids = new HashSet<string>();
            foreach (var glyph in dictionary.Glyphs)
                if (string.IsNullOrEmpty(glyph.Id) || !ids.Add(glyph.Id))
                { glyph.Id = Guid.NewGuid().ToString("N"); ids.Add(glyph.Id); }
            dictionary.Name = Name.Trim();
            copy.WeightString = TextModelCodec.Encode(dictionary);
            if (copy.DictionaryMode is not ("ocr" or "text")) throw new InvalidOperationException("字典识别方式无效。");
        }
        else throw new InvalidOperationException("不支持的识别类型。");
        return copy;
    }
}
