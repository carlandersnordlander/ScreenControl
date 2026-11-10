using System.Runtime.InteropServices;

namespace SkarmStyrning.Client;

/// <summary>Spelar upp mus och tangentbord från servern som om de vore lokala (SendInput).</summary>
static class Injector
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40,
               XDOWN = 0x80, XUP = 0x100, WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    const uint KEY_EXTENDED = 0x1, KEY_UP = 0x2, KEY_SCANCODE = 0x8;

    static readonly object Gate = new();
    static readonly HashSet<(ushort vk, ushort scan, bool ext)> keysDown = new();
    static readonly HashSet<byte> buttonsDown = new();

    static void Send(INPUT input)
    {
        var arr = new[] { input };
        SendInput(1, arr, Marshal.SizeOf<INPUT>());
    }

    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        if (vw <= 1 || vh <= 1) return;
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOVE | ABSOLUTE | VIRTUALDESK } });
    }

    public static void Button(byte button, bool down)
    {
        uint flags; uint data = 0;
        switch (button)
        {
            case 1: flags = down ? LEFTDOWN : LEFTUP; break;
            case 2: flags = down ? RIGHTDOWN : RIGHTUP; break;
            case 3: flags = down ? MIDDLEDOWN : MIDDLEUP; break;
            case 4: flags = down ? XDOWN : XUP; data = 1; break;
            case 5: flags = down ? XDOWN : XUP; data = 2; break;
            default: return;
        }
        lock (Gate) { if (down) buttonsDown.Add(button); else buttonsDown.Remove(button); }
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } });
    }

    public static void Wheel(short delta, bool horizontal) =>
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = horizontal ? HWHEEL : WHEEL, mouseData = unchecked((uint)delta) } });

    public static void Key(ushort vk, ushort scan, byte flags)
    {
        bool up = (flags & Net.Wire.KeyUp) != 0, ext = (flags & Net.Wire.KeyExtended) != 0;
        lock (Gate) { if (up) keysDown.Remove((vk, scan, ext)); else keysDown.Add((vk, scan, ext)); }
        Send(MakeKey(vk, scan, ext, up));
    }

    static INPUT MakeKey(ushort vk, ushort scan, bool ext, bool up)
    {
        uint f = (up ? KEY_UP : 0) | (ext ? KEY_EXTENDED : 0);
        // Skankod ger rätt tecken enligt den här datorns tangentbordslayout; vk används som reserv.
        var ki = scan != 0
            ? new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = f | KEY_SCANCODE }
            : new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = f };
        return new INPUT { type = 1, ki = ki };
    }

    /// <summary>Släpp allt som är nedtryckt – så inget fastnar när pekaren lämnar.</summary>
    public static void ReleaseAll()
    {
        List<(ushort, ushort, bool)> keys;
        List<byte> buttons;
        lock (Gate)
        {
            keys = keysDown.ToList(); keysDown.Clear();
            buttons = buttonsDown.ToList(); buttonsDown.Clear();
        }
        foreach (var (vk, scan, ext) in keys) Send(MakeKey(vk, scan, ext, true));
        foreach (var b in buttons) Button(b, false);
    }
}
