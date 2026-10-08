using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StateMachine.Automation;

/// <summary>Physical desktop coordinates; SendInput sends to the foreground application.</summary>
public sealed class DesktopInput
{
    private readonly Func<CancellationToken> _token;
    private readonly HashSet<ushort> _heldKeys = new();
    private readonly HashSet<string> _heldButtons = new(StringComparer.OrdinalIgnoreCase);
    public DesktopInput(Func<CancellationToken> token) => _token = token;

    public static void InitializeDpiAwareness()
    {
        if (OperatingSystem.IsWindows()) SetProcessDpiAwarenessContext(new IntPtr(-4));
    }

    public void MoveTo(int x, int y)
    {
        Check();
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (x < left || y < top || x >= (long)left + width || y >= (long)top + height)
            throw new ArgumentOutOfRangeException(nameof(x), "鼠标坐标超出虚拟桌面。");
        Send(Mouse(0x8000 | 0x4000 | 0x0001,
            (int)(((long)x - left) * 65536 / width), (int)(((long)y - top) * 65536 / height)));
    }

    public void Click(int x, int y, string button = "left")
    {
        MoveTo(x, y);
        var (down, up) = ButtonFlags(button);
        _heldButtons.Add(button);
        Send(Mouse(down), Mouse(up));
        _heldButtons.Remove(button);
    }

    public async Task DoubleClickAsync(int x, int y, string button = "left", int interval = 80)
    {
        Click(x, y, button);
        await Task.Delay(Math.Max(0, interval), _token());
        Click(x, y, button);
    }

    public void MouseDown(string button = "left")
    {
        Check();
        var (down, _) = ButtonFlags(button);
        _heldButtons.Add(button);
        Send(Mouse(down));
    }

    public void MouseUp(string button = "left")
    {
        var (_, up) = ButtonFlags(button);
        Send(Mouse(up));
        _heldButtons.Remove(button);
    }

    public void Scroll(int delta)
    {
        Check();
        Send(Mouse(0x0800, data: unchecked((uint)delta)));
    }

    public void KeyDown(string key)
    {
        Check();
        var code = KeyCode(key);
        _heldKeys.Add(code);
        Send(Keyboard(code, false));
    }

    public void KeyUp(string key)
    {
        var code = KeyCode(key);
        Send(Keyboard(code, true));
        _heldKeys.Remove(code);
    }

    public void Press(string key)
    {
        Check();
        var code = KeyCode(key);
        _heldKeys.Add(code);
        Send(Keyboard(code, false), Keyboard(code, true));
        _heldKeys.Remove(code);
    }

    public void Hotkey(params string[] keys)
    {
        Check();
        // Validate the entire chord before holding any keys.
        var codes = keys.Select(KeyCode).ToArray();
        try
        {
            foreach (var code in codes) { _heldKeys.Add(code); Send(Keyboard(code, false)); }
        }
        finally
        {
            foreach (var code in codes.Reverse())
            {
                Send(Keyboard(code, true));
                _heldKeys.Remove(code);
            }
        }
    }

    public void TypeText(string text)
    {
        foreach (var character in text)
        {
            Check();
            Send(Unicode(character, false), Unicode(character, true));
        }
    }

    public async Task DragAsync(int fromX, int fromY, int toX, int toY, int milliseconds = 300)
    {
        MoveTo(fromX, fromY);
        MouseDown();
        try { await Task.Delay(Math.Max(0, milliseconds), _token()); MoveTo(toX, toY); }
        finally { MouseUp(); }
    }

    public void ReleaseAll()
    {
        foreach (var code in _heldKeys.ToArray())
        {
            try { Send(Keyboard(code, true)); } catch { }
            _heldKeys.Remove(code);
        }
        foreach (var button in _heldButtons.ToArray())
        {
            try { MouseUp(button); } catch { _heldButtons.Remove(button); }
        }
    }

    public static ushort KeyCode(string key)
    {
        var text = key.Trim().ToUpperInvariant();
        if (text.Length == 1 && text[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return text[0];
        if (text.StartsWith('F') && int.TryParse(text[1..], out var f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        return text switch
        {
            "CTRL" or "CONTROL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12,
            "WIN" => 0x5B, "ENTER" or "RETURN" => 0x0D, "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20, "TAB" => 0x09, "BACKSPACE" => 0x08, "DELETE" => 0x2E,
            "INSERT" => 0x2D, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            _ => throw new ArgumentException($"不支持的按键：{key}，文字输入请使用 TypeText。")
        };
    }

    private void Check() => _token().ThrowIfCancellationRequested();
    private static (uint Down, uint Up) ButtonFlags(string button) => button.ToLowerInvariant() switch
    {
        "left" => (0x0002, 0x0004), "right" => (0x0008, 0x0010), "middle" => (0x0020, 0x0040),
        _ => throw new ArgumentException("鼠标按钮应为 left / right / middle。")
    };

    private static Input Mouse(uint flags, int x = 0, int y = 0, uint data = 0) =>
        new() { Type = 0, Data = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } };
    private static Input Keyboard(ushort code, bool up) =>
        new() { Type = 1, Data = new() { Keyboard = new() { Vk = code,
            Flags = (up ? 2u : 0u) | (code is >= 0x21 and <= 0x2E or 0x5B ? 1u : 0u) } } };
    private static Input Unicode(char character, bool up) =>
        new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 4u | (up ? 2u : 0u) } } };

    private static void Send(params Input[] inputs)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("键鼠操作需要 Windows。");
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "键鼠输入失败，请检查目标窗口权限。");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
