using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static StateMachine.DesktopNative;

namespace StateMachine;

// A normal top-level EDIT owns keyboard/IME input; wallpaper WebViews remain shell children.
public sealed class DesktopTextInput : IDisposable
{
    public sealed record Update(string Id, string Text, bool Submit, bool Active);
    private sealed record Request(string Id, string Text, double Left, double Top, double Width, double Height, double Ratio, double FontSize = 14, string FontFamily = "Segoe UI", string TextColor = "", string BackgroundColor = "", string ValueKind = "string", bool TransparentSurface = false);
    public event Action<Update>? Changed;
    private readonly string _class = "Alcyone.Input." + Guid.NewGuid().ToString("N");
    private readonly WindowProc _windowProc, _editProc;
    private nint _host, _edit, _originalProc, _font, _brush, _surface;
    private string _id = "";
    private uint _textColor=0x00fff3ed, _backgroundColor=0x00291b13;
    private string _valueKind="string";
    private bool _composing, _active, _registered;
    private long _imeCommitAt;
    public DesktopTextInput() { _windowProc = WindowMessage; _editProc = EditMessage; }
    public void Open(nint surface, string json)
    {
        var request = JsonSerializer.Deserialize<Request>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (request is null || request.Id.Length > 64 || request.Text.Length > 16000 || request.Ratio is <= 0 or > 8
            || !double.IsFinite(request.Left + request.Top + request.Width + request.Height + request.Ratio)
            || request.Width is <= 0 or > 2000 || request.Height is <= 0 or > 200 || !GetWindowRect(surface, out var bounds)) return;
        EnsureWindow();
        _surface = surface; _id = request.Id; _composing = false; _imeCommitAt = 0;
        var width = (int)Math.Ceiling(request.Width * request.Ratio);
        var height = (int)Math.Ceiling(request.Height * request.Ratio);
        SetWindowPos(_host, 0, bounds.Left + (int)(request.Left * request.Ratio), bounds.Top + (int)(request.Top * request.Ratio), width, height, 0x0010);
        _valueKind=request.ValueKind;
        _textColor=ParseColor(request.TextColor,0x00fff3ed);
        _backgroundColor=request.TransparentSurface ? 0x00010101u : ParseColor(request.BackgroundColor,0x00291b13);
        var style=GetWindowLongPtrW(_host,-20).ToInt64();
        SetWindowLongPtrW(_host,-20,(nint)(request.TransparentSurface ? style|0x80000 : style&~0x80000));
        if(request.TransparentSurface) SetLayeredWindowAttributes(_host,_backgroundColor,255,1);
        if(_brush!=0) DeleteObject(_brush); _brush=CreateSolidBrush(_backgroundColor);
        var fontSize=double.IsFinite(request.FontSize) ? Math.Clamp(request.FontSize,8,72) : 14;
        var font = CreateFontW(-(int)Math.Round(fontSize * request.Ratio), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, string.IsNullOrWhiteSpace(request.FontFamily) || request.FontFamily.Length>100 ? "Segoe UI" : request.FontFamily);
        SendMessageW(_edit, 0x0030, (nuint)font, 1);
        if (_font != 0) DeleteObject(_font); _font = font;
        SetWindowPos(_edit, 0, 0, 0, width, height, 0x0014);
        if(request.TransparentSurface)
        {
            var dc=GetDC(_edit); var oldFont=SelectObject(dc,_font);
            GetTextExtentPoint32W(dc,"Mg",2,out var textSize); SelectObject(dc,oldFont); ReleaseDC(_edit,dc);
            var rectangle=new InputRect { Left=0,Top=Math.Max(0,(height-textSize.Height)/2),Right=width,Bottom=height };
            var buffer=Marshal.AllocHGlobal(Marshal.SizeOf<InputRect>());
            try { Marshal.StructureToPtr(rectangle,buffer,false); SendMessageW(_edit,0x00b3,0,buffer); }
            finally { Marshal.FreeHGlobal(buffer); }
            SendMessageW(_edit,0x00d3,3,0);
        }
        SetWindowTextW(_edit, request.Text);
        _active = true;
        ShowWindow(_host, 5);
        SetForegroundWindow(_host);
        SetFocus(_edit);
        SendMessageW(_edit, 0x00b1, (nuint)request.Text.Length, request.Text.Length);
        Publish();
    }
    public void Resize(nint surface, string json)
    {
        var request = JsonSerializer.Deserialize<Request>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (!_active || surface != _surface || request is null || request.Id != _id || request.Ratio is <= 0 or > 8
            || !double.IsFinite(request.Left + request.Top + request.Width + request.Height) || request.Width is <= 0 or > 2000 || request.Height is <= 0 or > 200 || !GetWindowRect(surface, out var bounds)) return;
        var width = (int)Math.Ceiling(request.Width * request.Ratio);
        var height = (int)Math.Ceiling(request.Height * request.Ratio);
        SetWindowPos(_host, 0, bounds.Left + (int)(request.Left * request.Ratio), bounds.Top + (int)(request.Top * request.Ratio), width, height, 0x0014);
        SetWindowPos(_edit, 0, 0, 0, width, height, 0x0014);
    }
    public void Close(nint surface = 0)
    {
        if (!_active || surface != 0 && surface != _surface) return;
        _active = false; ShowWindow(_host, 0); Publish();
    }
    private void Publish(bool submit = false)
    {
        var text = new StringBuilder(GetWindowTextLengthW(_edit) + 1); GetWindowTextW(_edit, text, text.Capacity);
        Changed?.Invoke(new(_id, text.ToString(), submit, _active));
    }
    private static uint ParseColor(string value,uint fallback)
    {
        if(string.IsNullOrEmpty(value)) return fallback;
        var parts=System.Text.RegularExpressions.Regex.Matches(value,"[0-9]+");
        if(parts.Count<3 || !byte.TryParse(parts[0].Value,out var r) || !byte.TryParse(parts[1].Value,out var g) || !byte.TryParse(parts[2].Value,out var b)) return fallback;
        return (uint)(r | g<<8 | b<<16);
    }
    private void EnsureWindow()
    {
        if (_host != 0) return;
        _brush = CreateSolidBrush(0x00291b13);
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _windowProc, Instance = GetModuleHandleW(null), ClassName = _class, Background = 0 };
        if (RegisterClassExW(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
        _registered = true;
        _host = CreateWindowExW(0x80, _class, "Alcyone 输入", 0x80000000, 0, 0, 1, 1, 0, 0, cls.Instance, 0);
        _edit = CreateWindowExW(0, "EDIT", "", 0x50001044, 0, 0, 1, 1, _host, 0, cls.Instance, 0);
        if (_host == 0 || _edit == 0) throw new System.ComponentModel.Win32Exception();
        SendMessageW(_edit, 0x00c5, 16000, 0);
        _originalProc = SetWindowLongPtrW(_edit, -4, Marshal.GetFunctionPointerForDelegate(_editProc));
    }
    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0111 && (wParam >> 16 & 0xffff) == 0x0300 && _active) Publish();
        if (message == 0x0006 && (wParam & 0xffff) == 0) Close();
        if (message == 0x0133) { SetTextColor((nint)wParam, _textColor); SetBkColor((nint)wParam, _backgroundColor); return _brush; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private nint EditMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if(message==0x0102 && wParam>=32 && wParam<127)
        {
            var character=(char)wParam;
            if(_valueKind=="number" && !char.IsDigit(character) && !".+-eE".Contains(character)) return 0;
            if(_valueKind=="boolean" && !"truefalsTRUEFALS".Contains(character)) return 0;
        }
        if (message == 0x010d) _composing = true;
        if (message == 0x010e) { _composing = false; _imeCommitAt = Environment.TickCount64; }
        if (message == 0x0100 && wParam == 13 && Environment.TickCount64 - _imeCommitAt < 80) return 0;
        if (message == 0x0100 && !_composing && wParam == 65 && (GetKeyState(17) & 0x8000) != 0)
        { SendMessageW(_edit, 0x00b1, 0, -1); return 0; }
        if (message == 0x0100 && !_composing && (wParam == 27 || (wParam == 13 && (GetKeyState(16) & 0x8000) == 0)))
        { var submit = wParam == 13; _active = false; ShowWindow(_host, 0); Publish(submit); return 0; }
        if (message == 0x0102 && (wParam == 27 || (wParam == 13 && (GetKeyState(16) & 0x8000) == 0))) return 0;
        if (message == 0x0102 && wParam == 1 && (GetKeyState(17) & 0x8000) != 0) return 0;
        return CallWindowProcW(_originalProc, hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        Close(); if (_host != 0) DestroyWindow(_host); _host = 0; _edit = 0;
        if (_font != 0) DeleteObject(_font); if (_brush != 0) DeleteObject(_brush);
        if (_registered) UnregisterClassW(_class, GetModuleHandleW(null));
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window,uint color,byte alpha,uint flags);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [StructLayout(LayoutKind.Sequential)] private struct TextSize { public int Width, Height; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(nint dc, string text, int count, out TextSize size);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(nint dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int height, int width, int escape, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
}
