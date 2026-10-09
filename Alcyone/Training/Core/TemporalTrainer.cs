using System.Drawing.Imaging;

namespace FastColorFinder.Core;

public sealed class TemporalTrainer
{
    /// <summary>
    /// 只负责颜色分布：均值 / 标准差。
    /// 标准差只用于计算“颜色容差”，不再决定点的重要程度。
    /// </summary>
    private sealed class Stat
    {
        public int Count;
        public double MeanA, M2A;
        public double MeanB, M2B;
        public double MeanC, M2C;

        public void Add(int a, int b, int c)
        {
            Count++;
            AddOne(a, Count, ref MeanA, ref M2A);
            AddOne(b, Count, ref MeanB, ref M2B);
            AddOne(c, Count, ref MeanC, ref M2C);
        }

        private static void AddOne(double x, int n, ref double mean, ref double m2)
        {
            double delta = x - mean;
            mean += delta / n;
            m2 += delta * (x - mean);
        }

        public double StdA => Count > 1 ? Math.Sqrt(M2A / (Count - 1)) : 0;
        public double StdB => Count > 1 ? Math.Sqrt(M2B / (Count - 1)) : 0;
        public double StdC => Count > 1 ? Math.Sqrt(M2C / (Count - 1)) : 0;
    }

    /// <summary>
    /// 单独统计“这个点在训练期间有多少帧真的发生了颜色变化”。
    /// 这个比例只用于 RequiredWeight。
    /// </summary>
    private sealed class BehaviorStat
    {
        private bool _hasPrevious;
        private Ycc _previous;

        public int Count { get; private set; }
        public int ChangedCount { get; private set; }

        public void Add(Ycc current)
        {
            if (_hasPrevious)
            {
                int dy = FastColor.Abs(current.Y - _previous.Y);
                int dcb = FastColor.Abs(current.Cb - _previous.Cb);
                int dcr = FastColor.Abs(current.Cr - _previous.Cr);

                // 过滤屏幕采集时 1~2 个色阶的小抖动。
                // 粒子、闪烁、颜色动画会非常容易超过这个门槛。
                bool changed =
                    dy >= 6 ||
                    dcb >= 4 ||
                    dcr >= 4 ||
                    (dy + dcb + dcr) >= 12;

                if (changed)
                    ChangedCount++;
            }

            _previous = current;
            _hasPrevious = true;
            Count++;
        }

        public double ChangeRate
            => Count <= 1
                ? 0d
                : ChangedCount / (double)(Count - 1);

        public int ChangeRatePermille
            => Math.Clamp(
                (int)Math.Round(ChangeRate * 1000d),
                0,
                1000);
    }

    private readonly List<Point> _points;
    private readonly Stat _anchorStat = new();
    private readonly Stat[] _relativeStats;
    private readonly BehaviorStat[] _behaviorStats;

    public int SampleCount { get; private set; }

    public TemporalTrainer(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
            throw new ArgumentException("至少标注 3 个点。", nameof(points));

        _points = points.ToList();
        _relativeStats = points.Select(_ => new Stat()).ToArray();
        _behaviorStats = points.Select(_ => new BehaviorStat()).ToArray();
    }

    public unsafe void AddSample(Bitmap bitmap)
    {
        if (bitmap.PixelFormat != PixelFormat.Format32bppPArgb &&
            bitmap.PixelFormat != PixelFormat.Format32bppArgb &&
            bitmap.PixelFormat != PixelFormat.Format32bppRgb)
        {
            throw new ArgumentException("训练图必须为 32bpp。", nameof(bitmap));
        }

        // 任意标注点越界，这一帧全部不要。
        // 保证所有点拥有完全相同的训练帧数。
        for (int i = 0; i < _points.Count; i++)
        {
            if (!Inside(_points[i], bitmap.Width, bitmap.Height))
                return;
        }

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);

        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;

            Span<Ycc> colors = _points.Count <= 256
                ? stackalloc Ycc[_points.Count]
                : new Ycc[_points.Count];

            // 先读取每个点自己的绝对颜色。
            // 变化率必须基于点自身，而不是基于锚点相对色。
            for (int i = 0; i < _points.Count; i++)
            {
                Point p = _points[i];
                Ycc c = Read(scan0, stride, p.X, p.Y);
                colors[i] = c;
                _behaviorStats[i].Add(c);
            }

            Ycc anchor = colors[0];
            _anchorStat.Add(anchor.Y, anchor.Cb, anchor.Cr);

            // 匹配颜色仍然使用相对锚点差值，保留整体亮暗/色偏的适应能力。
            for (int i = 1; i < _points.Count; i++)
            {
                Ycc c = colors[i];
                _relativeStats[i].Add(
                    c.Y - anchor.Y,
                    c.Cb - anchor.Cb,
                    c.Cr - anchor.Cr);
            }

            SampleCount++;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public ColorModel Build(
        string name,
        int width,
        int height,
        int toleranceFloor = 10)
    {
        if (SampleCount < 3)
            throw new InvalidOperationException("训练样本太少。至少需要 3 帧。");

        // 锚点统计继续保存到模型，但匹配阶段不再拿它做绝对颜色硬拦截。
        int anchorTolY = Math.Clamp(
            (int)Math.Ceiling(_anchorStat.StdA * 2.5) + 12,
            12,
            64);

        int anchorTolCb = Math.Clamp(
            (int)Math.Ceiling(_anchorStat.StdB * 2.5) + 8,
            8,
            48);

        int anchorTolCr = Math.Clamp(
            (int)Math.Ceiling(_anchorStat.StdC * 2.5) + 8,
            8,
            48);

        var model = new ColorModel
        {
            Name = name,
            RegionWidth = width,
            RegionHeight = height,
            AnchorX = _points[0].X,
            AnchorY = _points[0].Y,
            SampleCount = SampleCount,
            TrainedAt = DateTime.Now,

            AnchorMeanY = (int)Math.Round(_anchorStat.MeanA),
            AnchorMeanCb = (int)Math.Round(_anchorStat.MeanB),
            AnchorMeanCr = (int)Math.Round(_anchorStat.MeanC),

            AnchorTolY = anchorTolY,
            AnchorTolCb = anchorTolCb,
            AnchorTolCr = anchorTolCr,

            AnchorChangeRatePermille = _behaviorStats[0].ChangeRatePermille
        };

        model.Points.Add(new TrainedPoint
        {
            X = _points[0].X,
            Y = _points[0].Y,
            Dx = 0,
            Dy = 0,
            IsAnchor = true,
            ChangeRatePermille = _behaviorStats[0].ChangeRatePermille,
            RequiredWeight = 0,
            TolDY = 255,
            TolDCb = 255,
            TolDCr = 255
        });

        for (int i = 1; i < _points.Count; i++)
        {
            Stat s = _relativeStats[i];
            Point point = _points[i];
            BehaviorStat behavior = _behaviorStats[i];

            // -------------------------------
            // A. 颜色容差：只回答“当前像不像”。
            // 不允许因为点一直变化就把容差无限撑大。
            // -------------------------------
            int tolY = Math.Clamp(
                (int)Math.Ceiling(s.StdA * 2.5) + toleranceFloor + 2,
                toleranceFloor + 2,
                64);

            int tolCb = Math.Clamp(
                (int)Math.Ceiling(s.StdB * 2.5) + toleranceFloor,
                toleranceFloor,
                48);

            int tolCr = Math.Clamp(
                (int)Math.Ceiling(s.StdC * 2.5) + toleranceFloor,
                toleranceFloor,
                48);

            // -------------------------------
            // B. 必须程度：只由 30 秒内的变化率决定。
            // 静态点≈1000；粒子点可能只有几十。
            // -------------------------------
            int requiredWeight = RequiredWeightFromChangeRate(behavior.ChangeRate);

            model.Points.Add(new TrainedPoint
            {
                X = point.X,
                Y = point.Y,
                Dx = point.X - _points[0].X,
                Dy = point.Y - _points[0].Y,

                MeanDY = (int)Math.Round(s.MeanA),
                MeanDCb = (int)Math.Round(s.MeanB),
                MeanDCr = (int)Math.Round(s.MeanC),

                TolDY = tolY,
                TolDCb = tolCb,
                TolDCr = tolCr,

                ChangeRatePermille = behavior.ChangeRatePermille,
                RequiredWeight = requiredWeight,
                IsAnchor = false
            });
        }

        return model;
    }

    private static int RequiredWeightFromChangeRate(double changeRate)
    {
        changeRate = Math.Clamp(changeRate, 0d, 1d);

        // 变化率越高，“没找到这个点”的惩罚越小。
        // 使用平方曲线，让真正静态的点明显比动态点重要。
        //
        // 变化率  0%   -> 1000
        //         10%  -> 815 左右
        //         20%  -> 649 左右
        //         40%  -> 376 左右
        //         60%  -> 181 左右
        //         80%  -> 64  左右
        //         100% -> 25
        double stable = 1d - changeRate;
        double required = 25d + 975d * stable * stable;

        return Math.Clamp(
            (int)Math.Round(required),
            25,
            1000);
    }

    private static bool Inside(Point p, int w, int h)
        => p.X >= 0 && p.Y >= 0 && p.X < w && p.Y < h;

    private static unsafe Ycc Read(byte* scan0, int stride, int x, int y)
    {
        byte* px = scan0 + y * stride + x * 4;
        return FastColor.FromBgr(px[0], px[1], px[2]);
    }
}
