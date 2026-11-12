using System.Runtime.InteropServices;

namespace SkarmStyrning.Net;

/// <summary>
/// "Håll vaken": hindrar viloläge och skärmsläckning. Var 10:e sekund säger den till Windows att
/// systemet och skärmen behövs, och om datorn varit orörd en stund trycks en osynlig F15
/// (en tangent som nästan inga program använder) – det håller även Teams/skärmlås nöjda.
/// </summary>
public static class KeepAwake
{
    const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
    const ushort VK_F15 = 0x7E;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

    static System.Threading.Timer? timer;
    static readonly object Gate = new();

    public static bool Enabled
    {
        get { lock (Gate) return timer != null; }
        set
        {
            lock (Gate)
            {
                if (value == (timer != null)) return;
                if (value) timer = new System.Threading.Timer(_ => Tick(), null, 0, 10_000);
                else { timer.Dispose(); timer = null; }
            }
        }
    }

    static void Tick()
    {
        try
        {
            SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED); // nollställer vilo- och skärmtimern
            // Bara när ingen använt datorn på en stund – annars stör vi inte.
            var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref li)) return;
            uint idleMs = unchecked((uint)Environment.TickCount - li.dwTime);
            if (idleMs < 9_000) return;
            var inputs = new[]
            {
                new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F15 } } },
                new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F15, dwFlags = KEYEVENTF_KEYUP } } },
            };
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
        catch { }
    }
}
