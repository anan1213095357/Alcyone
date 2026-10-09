using System.Text.Json.Serialization;

namespace FastColorFinder.Core;

public sealed class ColorModel
{
    public string Name { get; set; } = "Model";
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
    public int SampleCount { get; set; }
    public DateTime TrainedAt { get; set; } = DateTime.Now;

    // 锚点自身的时间颜色统计。
    // 当前匹配核心不把它作为硬门槛，只保留用于模型信息/兼容旧权重串。
    public int AnchorMeanY { get; set; }
    public int AnchorMeanCb { get; set; }
    public int AnchorMeanCr { get; set; }
    public int AnchorTolY { get; set; }
    public int AnchorTolCb { get; set; }
    public int AnchorTolCr { get; set; }
    public int AnchorChangeRatePermille { get; set; }

    public List<TrainedPoint> Points { get; set; } = new();

    [JsonIgnore]
    public int TotalRequiredWeight => Points
        .Where(p => !p.IsAnchor)
        .Sum(p => p.RequiredWeight);

    // 兼容旧代码命名。
    [JsonIgnore]
    public int TotalWeight => TotalRequiredWeight;
}

public sealed class TrainedPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public bool IsAnchor { get; set; }

    // 当前颜色是否像训练目标。
    public int MeanDY { get; set; }
    public int MeanDCb { get; set; }
    public int MeanDCr { get; set; }

    public int TolDY { get; set; }
    public int TolDCb { get; set; }
    public int TolDCr { get; set; }

    // 0~1000：训练期间这个点发生明显变化的比例。
    // 0 = 基本静态，1000 = 基本每帧都在变化。
    public int ChangeRatePermille { get; set; }

    // 25~1000：这个点“必须出现”的程度。
    // 静态点接近 1000；动态粒子点会很低。
    public int RequiredWeight { get; set; }

    // 兼容旧代码。如果还有地方访问 Weight，不会编译报错。
    [JsonIgnore]
    public int Weight
    {
        get => RequiredWeight;
        set => RequiredWeight = value;
    }
}

public readonly record struct MatchResult(
    bool Found,
    int AnchorX,
    int AnchorY,
    double Score,
    long ElapsedTicks)
{
    public static MatchResult Empty(long ticks = 0)
        => new(false, -1, -1, 0, ticks);
}
