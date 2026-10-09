using System.Globalization;
using System.Text;

namespace FastColorFinder.Core;

/// <summary>
/// 把训练结果压成一行权重字符串。
///
/// FCF3：
/// - 颜色容差与“必须命中程度”完全分开。
/// - 每个点额外保存 ChangeRatePermille + RequiredWeight。
///
/// 同时兼容读取旧 FCF2 字符串。
/// </summary>
public static class ColorModelCodec
{
    private const string PrefixV3 = "FCF3";
    private const string PrefixV2 = "FCF2";

    public static string Encode(ColorModel model)
    {
        var sb = new StringBuilder(320 + model.Points.Count * 56);

        sb.Append(PrefixV3).Append('|')
          .Append(model.RegionWidth).Append(',').Append(model.RegionHeight).Append('|')
          .Append(model.AnchorX).Append(',').Append(model.AnchorY).Append('|')
          .Append(model.SampleCount).Append('|')
          .Append(model.AnchorMeanY).Append(',')
          .Append(model.AnchorMeanCb).Append(',')
          .Append(model.AnchorMeanCr).Append(',')
          .Append(model.AnchorTolY).Append(',')
          .Append(model.AnchorTolCb).Append(',')
          .Append(model.AnchorTolCr).Append(',')
          .Append(model.AnchorChangeRatePermille)
          .Append('|');

        bool first = true;

        foreach (var p in model.Points.Where(p => !p.IsAnchor))
        {
            if (!first)
                sb.Append(';');

            first = false;

            sb.Append(p.Dx).Append(',')
              .Append(p.Dy).Append(',')
              .Append(p.MeanDY).Append(',')
              .Append(p.MeanDCb).Append(',')
              .Append(p.MeanDCr).Append(',')
              .Append(p.TolDY).Append(',')
              .Append(p.TolDCb).Append(',')
              .Append(p.TolDCr).Append(',')
              .Append(p.ChangeRatePermille).Append(',')
              .Append(p.RequiredWeight);
        }

        return sb.ToString();
    }

    public static ColorModel Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("权重字符串为空。");

        string compact = text.Trim();
        string[] blocks = compact.Split('|');

        if (blocks.Length != 6)
            throw new FormatException("权重字符串格式不正确。");

        return blocks[0] switch
        {
            PrefixV3 => DecodeV3(blocks),
            PrefixV2 => DecodeV2(blocks),
            _ => throw new FormatException("权重字符串版本不支持。")
        };
    }

    private static ColorModel DecodeV3(string[] blocks)
    {
        int[] region = ParseInts(blocks[1], 2);
        int[] anchorPos = ParseInts(blocks[2], 2);

        if (!int.TryParse(
                blocks[3],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int samples))
        {
            throw new FormatException("训练帧数字段无效。");
        }

        int[] anchor = ParseInts(blocks[4], 7);

        var model = new ColorModel
        {
            Name = "WeightString",
            RegionWidth = region[0],
            RegionHeight = region[1],
            AnchorX = anchorPos[0],
            AnchorY = anchorPos[1],
            SampleCount = samples,

            AnchorMeanY = anchor[0],
            AnchorMeanCb = anchor[1],
            AnchorMeanCr = anchor[2],
            AnchorTolY = anchor[3],
            AnchorTolCb = anchor[4],
            AnchorTolCr = anchor[5],
            AnchorChangeRatePermille = Math.Clamp(anchor[6], 0, 1000)
        };

        model.Points.Add(new TrainedPoint
        {
            X = model.AnchorX,
            Y = model.AnchorY,
            Dx = 0,
            Dy = 0,
            IsAnchor = true,
            ChangeRatePermille = model.AnchorChangeRatePermille,
            RequiredWeight = 0
        });

        if (!string.IsNullOrWhiteSpace(blocks[5]))
        {
            foreach (string item in blocks[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int[] v = ParseInts(item, 10);

                model.Points.Add(new TrainedPoint
                {
                    Dx = v[0],
                    Dy = v[1],
                    X = model.AnchorX + v[0],
                    Y = model.AnchorY + v[1],

                    MeanDY = v[2],
                    MeanDCb = v[3],
                    MeanDCr = v[4],

                    TolDY = Math.Max(1, v[5]),
                    TolDCb = Math.Max(1, v[6]),
                    TolDCr = Math.Max(1, v[7]),

                    ChangeRatePermille = Math.Clamp(v[8], 0, 1000),
                    RequiredWeight = Math.Clamp(v[9], 1, 1000),
                    IsAnchor = false
                });
            }
        }

        ValidateModel(model);
        return model;
    }

    /// <summary>
    /// 兼容旧 FCF2。
    /// 旧 Weight 直接映射成 RequiredWeight。
    /// </summary>
    private static ColorModel DecodeV2(string[] blocks)
    {
        int[] region = ParseInts(blocks[1], 2);
        int[] anchorPos = ParseInts(blocks[2], 2);

        if (!int.TryParse(
                blocks[3],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int samples))
        {
            throw new FormatException("训练帧数字段无效。");
        }

        int[] anchor = ParseInts(blocks[4], 6);

        var model = new ColorModel
        {
            Name = "WeightString-FCF2",
            RegionWidth = region[0],
            RegionHeight = region[1],
            AnchorX = anchorPos[0],
            AnchorY = anchorPos[1],
            SampleCount = samples,

            AnchorMeanY = anchor[0],
            AnchorMeanCb = anchor[1],
            AnchorMeanCr = anchor[2],
            AnchorTolY = anchor[3],
            AnchorTolCb = anchor[4],
            AnchorTolCr = anchor[5],
            AnchorChangeRatePermille = 0
        };

        model.Points.Add(new TrainedPoint
        {
            X = model.AnchorX,
            Y = model.AnchorY,
            Dx = 0,
            Dy = 0,
            IsAnchor = true,
            RequiredWeight = 0,
            ChangeRatePermille = 0
        });

        if (!string.IsNullOrWhiteSpace(blocks[5]))
        {
            foreach (string item in blocks[5].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int[] v = ParseInts(item, 9);

                model.Points.Add(new TrainedPoint
                {
                    Dx = v[0],
                    Dy = v[1],
                    X = model.AnchorX + v[0],
                    Y = model.AnchorY + v[1],

                    MeanDY = v[2],
                    MeanDCb = v[3],
                    MeanDCr = v[4],

                    TolDY = Math.Max(1, v[5]),
                    TolDCb = Math.Max(1, v[6]),
                    TolDCr = Math.Max(1, v[7]),

                    ChangeRatePermille = 0,
                    RequiredWeight = Math.Clamp(v[8], 1, 1000),
                    IsAnchor = false
                });
            }
        }

        ValidateModel(model);
        return model;
    }

    private static void ValidateModel(ColorModel model)
    {
        // 一个锚点 + 至少两个实际匹配点。
        if (model.Points.Count < 3)
            throw new FormatException("权重字符串中的有效点太少。");

        if (model.RegionWidth <= 0 || model.RegionHeight <= 0)
            throw new FormatException("权重字符串中的区域尺寸无效。");
    }

    private static int[] ParseInts(string value, int count)
    {
        string[] parts = value.Split(',');

        if (parts.Length != count)
            throw new FormatException("权重字符串字段数量不正确。");

        var values = new int[count];

        for (int i = 0; i < count; i++)
        {
            if (!int.TryParse(
                    parts[i],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out values[i]))
            {
                throw new FormatException("权重字符串包含无效数字。");
            }
        }

        return values;
    }
}
