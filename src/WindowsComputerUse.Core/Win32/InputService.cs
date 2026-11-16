using System.Runtime.InteropServices;
using WindowsComputerUse.Core.Dpi;

namespace WindowsComputerUse.Core.Win32;

public enum MouseActionKind
{
    Move,
    Click,
    DoubleClick,
    RightClick,
    Drag,
    Scroll
}

public enum KeyboardActionKind
{
    TypeText,
    KeyPress,
    Hotkey
}

/// <summary>
/// Sends physical mouse and keyboard input via Win32 SendInput.
/// </summary>
public static class InputService
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfLeftDown = 0x0002;
    private const uint MouseEventfLeftUp = 0x0004;
    private const uint MouseEventfRightDown = 0x0008;
    private const uint MouseEventfRightUp = 0x0010;
    private const uint MouseEventfWheel = 0x0800;
    private const uint MouseEventfHWheel = 0x1000;
    private const uint MouseEventfVirtualDesk = 0x4000;
    private const uint MouseEventfAbsolute = 0x8000;

    private const uint KeyEventfExtendedKey = 0x0001;
    private const uint KeyEventfKeyUp = 0x0002;
    private const uint KeyEventfUnicode = 0x0004;

    private const ushort VkReturn = 0x0D;

    public static void MoveMouse(int screenX, int screenY, CancellationToken ct = default, Action<int, int>? onMove = null)
    {
        SmoothMoveTo(screenX, screenY, ct, onMove);
    }

    public static void Click(int screenX, int screenY, bool rightButton = false, CancellationToken ct = default, Action<int, int>? onMove = null)
    {
        SmoothMoveTo(screenX, screenY, ct, onMove);

        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();
        var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(screenX, screenY, virtualBounds);

        uint downFlag = rightButton ? MouseEventfRightDown : MouseEventfLeftDown;
        uint upFlag = rightButton ? MouseEventfRightUp : MouseEventfLeftUp;

        SendInputs([
            CreateMouseInput(nx, ny, 0, downFlag | MouseEventfAbsolute | MouseEventfVirtualDesk),
            CreateMouseInput(nx, ny, 0, upFlag | MouseEventfAbsolute | MouseEventfVirtualDesk)
        ]);
    }

    public static void DoubleClick(int screenX, int screenY, CancellationToken ct = default, Action<int, int>? onMove = null)
    {
        SmoothMoveTo(screenX, screenY, ct, onMove);

        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();
        var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(screenX, screenY, virtualBounds);

        SendInputs([
            CreateMouseInput(nx, ny, 0, MouseEventfLeftDown | MouseEventfAbsolute | MouseEventfVirtualDesk),
            CreateMouseInput(nx, ny, 0, MouseEventfLeftUp | MouseEventfAbsolute | MouseEventfVirtualDesk)
        ]);
        Thread.Sleep(30);
        ct.ThrowIfCancellationRequested();
        SendInputs([
            CreateMouseInput(nx, ny, 0, MouseEventfLeftDown | MouseEventfAbsolute | MouseEventfVirtualDesk),
            CreateMouseInput(nx, ny, 0, MouseEventfLeftUp | MouseEventfAbsolute | MouseEventfVirtualDesk)
        ]);
    }

    public static void Drag(int startX, int startY, int endX, int endY, int steps = 12, CancellationToken ct = default, Action<int, int>? onMove = null)
    {
        SmoothMoveTo(startX, startY, ct, onMove);

        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();
        var (snx, sny) = CoordinateTransform.ScreenToSendInputNormalized(startX, startY, virtualBounds);
        var (enx, eny) = CoordinateTransform.ScreenToSendInputNormalized(endX, endY, virtualBounds);

        SendInputs([
            CreateMouseInput(snx, sny, 0, MouseEventfLeftDown | MouseEventfAbsolute | MouseEventfVirtualDesk)
        ]);

        int clampedSteps = Math.Clamp(steps, 1, 60);
        for (int i = 1; i <= clampedSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            double t = i / (double)clampedSteps;
            int ix = (int)Math.Round(snx + (enx - snx) * t);
            int iy = (int)Math.Round(sny + (eny - sny) * t);
            SendInputs([CreateMouseInput(ix, iy, 0, MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk)]);
            onMove?.Invoke(
                (int)Math.Round(startX + (endX - startX) * t),
                (int)Math.Round(startY + (endY - startY) * t));
            Thread.Sleep(6);
        }

        SendInputs([
            CreateMouseInput(enx, eny, 0, MouseEventfLeftUp | MouseEventfAbsolute | MouseEventfVirtualDesk)
        ]);
    }

    public static void Scroll(int screenX, int screenY, int delta, bool horizontal = false, CancellationToken ct = default, Action<int, int>? onMove = null)
    {
        SmoothMoveTo(screenX, screenY, ct, onMove);

        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();
        var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(screenX, screenY, virtualBounds);

        uint wheelFlag = horizontal ? MouseEventfHWheel : MouseEventfWheel;
        SendInputs([
            CreateMouseInput(nx, ny, unchecked((uint)delta), wheelFlag)
        ]);
    }

    /// <summary>
    /// Smoothly animates the physical cursor from its current position to (targetX, targetY)
    /// so the user can visually follow where the agent is moving.
    /// </summary>
    private static void SmoothMoveTo(int targetX, int targetY, CancellationToken ct, Action<int, int>? onMove)
    {
        RectI virtualBounds = DpiAwareness.GetVirtualScreenBounds();

        if (!GetCursorPos(out NativePoint current))
        {
            var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(targetX, targetY, virtualBounds);
            SendInputs([CreateMouseInput(nx, ny, 0, MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk)]);
            onMove?.Invoke(targetX, targetY);
            return;
        }

        double dx = targetX - current.X;
        double dy = targetY - current.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist <= 4.0)
        {
            var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(targetX, targetY, virtualBounds);
            SendInputs([CreateMouseInput(nx, ny, 0, MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk)]);
            onMove?.Invoke(targetX, targetY);
            return;
        }

        int steps = Math.Clamp((int)Math.Round(dist / 120.0), 6, 12);
        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            double linear = i / (double)steps;
            double eased = linear * linear * (3.0 - 2.0 * linear);

            int sx = (int)Math.Round(current.X + dx * eased);
            int sy = (int)Math.Round(current.Y + dy * eased);

            var (nx, ny) = CoordinateTransform.ScreenToSendInputNormalized(sx, sy, virtualBounds);
            SendInputs([CreateMouseInput(nx, ny, 0, MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk)]);
            onMove?.Invoke(sx, sy);
            Thread.Sleep(4);
        }
    }

    public static void TypeText(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new List<Input>(text.Length * 2);
        foreach (char ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\r')
                continue;

            if (ch == '\n')
            {
                inputs.Add(CreateKeyInput(VkReturn, 0, 0));
                inputs.Add(CreateKeyInput(VkReturn, 0, KeyEventfKeyUp));
            }
            else
            {
                inputs.Add(CreateKeyInput(0, ch, KeyEventfUnicode));
                inputs.Add(CreateKeyInput(0, ch, KeyEventfUnicode | KeyEventfKeyUp));
            }
        }

        if (inputs.Count > 0)
            SendInputs(inputs.ToArray());
    }

    public static void KeyPress(string keyName)
    {
        if (!TryParseVirtualKey(keyName, out ushort vk, out bool extended))
            throw new ArgumentException($"Unknown key name: '{keyName}'.", nameof(keyName));

        uint extFlag = extended ? KeyEventfExtendedKey : 0;
        SendInputs([
            CreateKeyInput(vk, 0, extFlag),
            CreateKeyInput(vk, 0, extFlag | KeyEventfKeyUp)
        ]);
    }

    public static void Hotkey(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            throw new ArgumentException("At least one key is required for hotkey.", nameof(keys));

        var parsed = new List<(ushort Vk, bool Extended)>(keys.Count);
        foreach (string key in keys)
        {
            if (!TryParseVirtualKey(key, out ushort vk, out bool ext))
                throw new ArgumentException($"Unknown key name in hotkey: '{key}'.", nameof(keys));
            parsed.Add((vk, ext));
        }

        var inputs = new List<Input>(parsed.Count * 2);
        foreach (var (vk, ext) in parsed)
        {
            inputs.Add(CreateKeyInput(vk, 0, ext ? KeyEventfExtendedKey : 0));
        }

        for (int i = parsed.Count - 1; i >= 0; i--)
        {
            var (vk, ext) = parsed[i];
            inputs.Add(CreateKeyInput(vk, 0, (ext ? KeyEventfExtendedKey : 0) | KeyEventfKeyUp));
        }

        SendInputs(inputs.ToArray());
    }

    public static bool TryParseVirtualKey(string keyName, out ushort vk, out bool extended)
    {
        extended = false;
        vk = 0;

        if (string.IsNullOrWhiteSpace(keyName))
            return false;

        string norm = keyName.Trim().ToUpperInvariant();

        switch (norm)
        {
            case "CTRL":
            case "CONTROL":
            case "LCTRL":
                vk = 0xA2; return true;
            case "RCTRL":
                vk = 0xA3; extended = true; return true;
            case "SHIFT":
            case "LSHIFT":
                vk = 0xA0; return true;
            case "RSHIFT":
                vk = 0xA1; return true;
            case "ALT":
            case "LALT":
                vk = 0xA4; return true;
            case "RALT":
                vk = 0xA5; extended = true; return true;
            case "WIN":
            case "LWIN":
            case "WINDOWS":
            case "META":
                vk = 0x5B; extended = true; return true;
            case "RWIN":
                vk = 0x5C; extended = true; return true;
            case "ENTER":
            case "RETURN":
                vk = 0x0D; return true;
            case "TAB":
                vk = 0x09; return true;
            case "ESC":
            case "ESCAPE":
                vk = 0x1B; return true;
            case "SPACE":
                vk = 0x20; return true;
            case "BACKSPACE":
            case "BACK":
                vk = 0x08; return true;
            case "DELETE":
            case "DEL":
                vk = 0x2E; extended = true; return true;
            case "INSERT":
            case "INS":
                vk = 0x2D; extended = true; return true;
            case "HOME":
                vk = 0x24; extended = true; return true;
            case "END":
                vk = 0x23; extended = true; return true;
            case "PAGEUP":
            case "PGUP":
                vk = 0x21; extended = true; return true;
            case "PAGEDOWN":
            case "PGDN":
                vk = 0x22; extended = true; return true;
            case "LEFT":
                vk = 0x25; extended = true; return true;
            case "UP":
                vk = 0x26; extended = true; return true;
            case "RIGHT":
                vk = 0x27; extended = true; return true;
            case "DOWN":
                vk = 0x28; extended = true; return true;
            case "PRINTSCREEN":
            case "PRTSC":
                vk = 0x2C; extended = true; return true;
            case "PAUSE":
                vk = 0x13; return true;
            case "CAPSLOCK":
                vk = 0x14; return true;
            case "APPS":
            case "MENU":
                vk = 0x5D; extended = true; return true;
        }

        if (norm.Length == 1)
        {
            char c = norm[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                return true;
            }
        }

        if (norm.StartsWith('F') && int.TryParse(norm[1..], out int fNum) && fNum is >= 1 and <= 24)
        {
            vk = (ushort)(0x6F + fNum);
            return true;
        }

        return false;
    }

    private static void SendInputs(Input[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput sent {sent}/{inputs.Length} events (Win32 error {err}).");
        }
    }

    private static Input CreateMouseInput(int dx, int dy, uint mouseData, uint flags) => new()
    {
        Type = InputMouse,
        Union = new InputUnion
        {
            Mouse = new MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = mouseData,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    private static Input CreateKeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                Vk = vk,
                Scan = scan,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }
}
