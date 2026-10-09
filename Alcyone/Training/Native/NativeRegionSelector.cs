using System.Drawing;
using System.Runtime.InteropServices;

namespace FastColorFinder.Native;

/// <summary>
/// 不依赖 WinForms/WPF 的纯 Win32 屏幕框选层。
/// </summary>
public sealed class NativeRegionSelector
{
    public Task<Rectangle?> SelectAsync()
    {
        var tcs = new TaskCompletionSource<Rectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(SelectorWindow.Run()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "FastColorFinder.RegionSelector"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed class SelectorWindow
    {
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int LWA_ALPHA = 0x00000002;
        private const int SW_SHOW = 5;
        private const int WM_DESTROY = 0x0002;
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONUP = 0x0202;
        private const int VK_ESCAPE = 0x1B;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int PS_SOLID = 0;
        private const int TRANSPARENT = 1;

        private static readonly Dictionary<IntPtr, SelectorWindow> Instances = new();
        private static readonly WndProcDelegate WndProcKeepAlive = WndProc;

        private IntPtr _hwnd;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private Rectangle? _result;
        private readonly int _vx;
        private readonly int _vy;
        private readonly int _vw;
        private readonly int _vh;

        private SelectorWindow()
        {
            _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            _vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            _vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }

        public static Rectangle? Run()
        {
            var instance = new SelectorWindow();
            return instance.MessageLoop();
        }

        private Rectangle? MessageLoop()
        {
            string className = "FastColorFinderRegionSelector_" + Guid.NewGuid().ToString("N");
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcKeepAlive),
                hInstance = GetModuleHandleW(null),
                lpszClassName = className,
                hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32515)) // cross
            };

            ushort atom = RegisterClassW(ref wc);
            if (atom == 0) throw new InvalidOperationException("RegisterClassW 失败。");

            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
                className,
                "FastColorFinder Region Selector",
                WS_POPUP,
                _vx, _vy, _vw, _vh,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowExW 失败。");

            lock (Instances) Instances[_hwnd] = this;
            SetLayeredWindowAttributes(_hwnd, 0, 112, LWA_ALPHA);
            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);
            SetFocus(_hwnd);

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            lock (Instances) Instances.Remove(_hwnd);
            UnregisterClassW(className, wc.hInstance);
            return _result;
        }

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            SelectorWindow? self;
            lock (Instances) Instances.TryGetValue(hwnd, out self);
            if (self is null) return DefWindowProcW(hwnd, msg, wParam, lParam);

            switch ((int)msg)
            {
                case WM_KEYDOWN:
                    if ((int)wParam == VK_ESCAPE)
                    {
                        self._result = null;
                        DestroyWindow(hwnd);
                        return IntPtr.Zero;
                    }
                    break;

                case WM_LBUTTONDOWN:
                    self._dragging = true;
                    self._start = GetPoint(lParam);
                    self._current = self._start;
                    SetCapture(hwnd);
                    InvalidateRect(hwnd, IntPtr.Zero, false);
                    return IntPtr.Zero;

                case WM_MOUSEMOVE:
                    if (self._dragging)
                    {
                        self._current = GetPoint(lParam);
                        InvalidateRect(hwnd, IntPtr.Zero, false);
                    }
                    return IntPtr.Zero;

                case WM_LBUTTONUP:
                    if (self._dragging)
                    {
                        self._dragging = false;
                        self._current = GetPoint(lParam);
                        ReleaseCapture();
                        var local = Normalize(self._start, self._current);
                        if (local.Width >= 3 && local.Height >= 3)
                            self._result = new Rectangle(local.X + self._vx, local.Y + self._vy, local.Width, local.Height);
                        DestroyWindow(hwnd);
                    }
                    return IntPtr.Zero;

                case WM_ERASEBKGND:
                    // The buffered paint supplies the complete background.
                    return new IntPtr(1);

                case WM_PAINT:
                    self.Paint(hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void Paint(IntPtr hwnd)
        {
            BeginPaint(hwnd, out var ps);
            try
            {
                using var target = Graphics.FromHdc(ps.hdc);
                using var buffer = BufferedGraphicsManager.Current.Allocate(target, new Rectangle(0, 0, _vw, _vh));
                var g = buffer.Graphics;
                g.Clear(Color.FromArgb(24, 28, 38));

                using var titleFont = new Font("Segoe UI", 16, FontStyle.Bold);
                using var bodyFont = new Font("Segoe UI", 10, FontStyle.Regular);
                using var white = new SolidBrush(Color.White);
                using var muted = new SolidBrush(Color.FromArgb(210, 220, 235));
                g.DrawString("拖动框选区域", titleFont, white, 28, 24);
                g.DrawString("松开鼠标确认 · ESC 取消", bodyFont, muted, 30, 58);

                if (_dragging || _start != _current)
                {
                    var r = Normalize(_start, _current);
                    using var fill = new SolidBrush(Color.FromArgb(72, 66, 133, 255));
                    using var pen = new Pen(Color.FromArgb(125, 176, 255), 2f);
                    g.FillRectangle(fill, r);
                    g.DrawRectangle(pen, r);

                    string text = $"{r.Width} × {r.Height}   X {_vx + r.X}   Y {_vy + r.Y}";
                    var size = g.MeasureString(text, bodyFont);
                    var tx = Math.Clamp(r.Left, 10, Math.Max(10, _vw - (int)size.Width - 24));
                    var ty = r.Top > 40 ? r.Top - 30 : r.Bottom + 8;
                    using var pill = new SolidBrush(Color.FromArgb(230, 11, 16, 27));
                    g.FillRectangle(pill, tx - 8, ty - 4, size.Width + 16, size.Height + 8);
                    g.DrawString(text, bodyFont, white, tx, ty);
                }
                // Present only the finished frame, without exposing the clear/draw steps.
                buffer.Render(target);
            }
            finally
            {
                EndPaint(hwnd, ref ps);
            }
        }

        private static Rectangle Normalize(Point a, Point b)
        {
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private static Point GetPoint(IntPtr lParam)
        {
            long v = lParam.ToInt64();
            int x = unchecked((short)(v & 0xFFFF));
            int y = unchecked((short)((v >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            [MarshalAs(UnmanagedType.Bool)] public bool fErase;
            public int left, top, right, bottom;
            [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
            [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    }
}
