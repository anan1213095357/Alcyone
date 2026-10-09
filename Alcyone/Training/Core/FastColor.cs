using System.Runtime.CompilerServices;

namespace FastColorFinder.Core;

public readonly record struct Ycc(int Y, int Cb, int Cr);

public static class FastColor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Ycc FromBgr(byte b, byte g, byte r)
    {
        // 整数近似 BT.601。全部是移位和整数乘法，适合高频匹配。
        int y = (77 * r + 150 * g + 29 * b) >> 8;
        int cb = 128 + ((-43 * r - 85 * g + 128 * b) >> 8);
        int cr = 128 + ((128 * r - 107 * g - 21 * b) >> 8);
        return new Ycc(y, cb, cr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Abs(int v) => v < 0 ? -v : v;
}
