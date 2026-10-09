using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FastColorFinder.Core;

/// <summary>
/// 高速屏幕捕获：
/// 持久 MemoryDC + top-down DIBSection。
/// 捕获循环中不创建 Graphics、不创建 Bitmap、不重新分配像素缓冲。
/// </summary>
public sealed class Win32ScreenCapture : IDisposable
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    private const uint DIB_RGB_COLORS = 0;
    private const int BI_RGB = 0;

    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;

    private Bitmap? _frame;
    private bool _disposed;

    public Rectangle Region { get; }

    public Bitmap Frame =>
        _frame ??
        throw new ObjectDisposedException(
            nameof(Win32ScreenCapture));

    public int Width => Region.Width;

    public int Height => Region.Height;

    internal IntPtr Bits => !_disposed
        ? _bits
        : throw new ObjectDisposedException(nameof(Win32ScreenCapture));

    internal int Stride => Region.Width * 4;

    public Win32ScreenCapture(Rectangle region)
    {
        if (region.Width <= 0 ||
            region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(region));
        }

        Region = region;

        try
        {
            CreateSurface(region.Width, region.Height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// 更新当前屏幕像素到复用缓冲区。
    /// 返回的始终是同一个 Bitmap 实例。
    /// </summary>
    public Bitmap Capture()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(Win32ScreenCapture));
        }

        // 屏幕 DC 在当前捕获线程取得、释放；MemoryDC 和像素缓冲继续复用。
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC 失败。");

        try
        {
            bool ok = BitBlt(
                _memoryDc, 0, 0, Region.Width, Region.Height,
                screenDc, Region.Left, Region.Top, SRCCOPY | CAPTUREBLT);

            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt 屏幕捕获失败。");

            // CPU 直接访问 DIB 前必须完成当前线程的 GDI 批处理。
            if (!GdiFlush())
                throw new InvalidOperationException("GdiFlush 屏幕捕获同步失败。");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return Frame;
    }

    /// <summary>
    /// 只有确实需要独立 Bitmap 生命周期时才使用。
    /// 高频实时循环不要调用这个方法。
    /// </summary>
    public Bitmap CaptureClone()
    {
        Capture();

        return new Bitmap(Frame);
    }

    private void CreateSurface(int width, int height)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC 失败。");

        try
        {
            _memoryDc = CreateCompatibleDC(screenDc);
            if (_memoryDc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC 失败。");

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = (uint)(width * height * 4)
                }
            };

            _dib = CreateDIBSection(screenDc, ref bmi, DIB_RGB_COLORS,
                out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection 失败。");

            _oldBitmap = SelectObject(_memoryDc, _dib);
            if (_oldBitmap == IntPtr.Zero || _oldBitmap == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SelectObject 失败。");

            _frame = new Bitmap(width, height, width * 4,
                PixelFormat.Format32bppRgb, _bits);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _frame?.Dispose();
        _frame = null;

        if (_memoryDc != IntPtr.Zero &&
            _oldBitmap != IntPtr.Zero &&
            _oldBitmap != new IntPtr(-1))
        {
            SelectObject(
                _memoryDc,
                _oldBitmap);
        }

        if (_dib != IntPtr.Zero)
        {
            DeleteObject(
                _dib);
        }

        if (_memoryDc != IntPtr.Zero)
        {
            DeleteDC(
                _memoryDc);
        }

        _oldBitmap = IntPtr.Zero;
        _dib = IntPtr.Zero;
        _memoryDc = IntPtr.Zero;
        _bits = IntPtr.Zero;

        GC.SuppressFinalize(this);
    }

    ~Win32ScreenCapture()
    {
        Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RGBQUAD
    {
        public byte rgbBlue;
        public byte rgbGreen;
        public byte rgbRed;
        public byte rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public RGBQUAD bmiColors;
    }

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern IntPtr GetDC(
        IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(
        IntPtr hWnd,
        IntPtr hDC);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(
        IntPtr hdc);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern bool DeleteDC(
        IntPtr hdc);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern IntPtr SelectObject(
        IntPtr hdc,
        IntPtr hgdiobj);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern bool DeleteObject(
        IntPtr hObject);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint offset);

    [DllImport(
        "gdi32.dll",
        SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}
