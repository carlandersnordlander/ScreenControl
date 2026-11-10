using System.Diagnostics;
using System.Runtime.InteropServices;
using SkarmStyrning.Net;

namespace SkarmStyrning;

/// <summary>
/// En skärm i den gemensamma ytan. Master = platsen i huvudlayouten (samma koordinater som
/// placeringsvyn). Owner = null betyder den här datorn, annars klientens id.
/// ServerRect = var skärmen sitter på den här datorns skrivbord (om den är på här),
/// ClientRect = var den sitter på klientens skrivbord.
/// </summary>
public sealed record Region(string Dev, Rectangle Master, string? Owner, Rectangle? ServerRect, Rectangle? ClientRect);

/// <summary>
/// Mjukvaru-KVM: lågnivåkrokar för mus och tangentbord. När pekaren går över kanten till en
/// skärm som visar en annan dator fångas all inmatning här och skickas krypterat till den datorn.
/// </summary>
public sealed class InputRouter : IDisposable
{
    readonly NetServer net;
    volatile Region[] regions = Array.Empty<Region>();
    volatile bool enabled;

    // Fjärrläge (används bara på kroktråden)
    string? remote;
    double vx, vy;
    Point pinned;
    readonly HashSet<int> keysDown = new();

    Thread? thread;
    uint threadId;
    IntPtr mouseHook, keyboardHook;
    HookProc? mouseProc, keyboardProc;

    /// <summary>Vilken dator som styrs just nu (null = den här). Anropas från kroktråden.</summary>
    public event Action<string?>? ActiveChanged;
    public string? Active => remote;

    public InputRouter(NetServer net)
    {
        this.net = net;
        net.Disconnected += id => { if (remote == id) PostToHook(() => ExitToServer(null)); };
    }

    public bool Enabled
    {
        get => enabled;
        set { enabled = value; if (!value) PostToHook(() => ExitToServer(null)); }
    }

    /// <summary>Ny gemensam yta (byggs på UI-tråden när skärmar, ingångar eller klienter ändras).</summary>
    public void Update(Region[] r)
    {
        regions = r;
        PostToHook(() =>
        {
            if (remote != null && !r.Any(x => x.Owner == remote)) ExitToServer(null);
        });
    }

    // ---------- Krokar ----------

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const uint WM_QUIT = 0x12, WM_APP_RUN = 0x8001;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
              WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLMHF_INJECTED = 0x1, LLKHF_EXTENDED = 0x1, LLKHF_INJECTED = 0x10;

    readonly System.Collections.Concurrent.ConcurrentQueue<Action> hookWork = new();

    /// <summary>Kör något på kroktråden (där allt fjärrläge hanteras).</summary>
    void PostToHook(Action a)
    {
        if (threadId == 0) return;
        hookWork.Enqueue(a);
        PostThreadMessage(threadId, WM_APP_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    public void Start()
    {
        if (thread != null) return;
        var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            mouseProc = MouseProc;
            keyboardProc = KeyboardProc;
            var mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, mod, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_APP_RUN)
                    while (hookWork.TryDequeue(out var a)) { try { a(); } catch (Exception ex) { Debug.WriteLine(ex); } }
            }
            UnhookWindowsHookEx(mouseHook);
            UnhookWindowsHookEx(keyboardHook);
        })
        { IsBackground = true, Name = "Skärmstyrning inmatning", Priority = ThreadPriority.Highest };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(2000);
    }

    public void Dispose()
    {
        if (threadId != 0)
        {
            PostToHook(() => ExitToServer(null));
            PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ---------- Mus ----------

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(mouseHook, code, wParam, lParam);
        try
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            if (remote == null)
            {
                if ((m.flags & LLMHF_INJECTED) != 0 || !enabled || msg != WM_MOUSEMOVE) return CallNextHookEx(mouseHook, code, wParam, lParam);
                if (TryEnter(new Point(m.pt.x, m.pt.y))) return (IntPtr)1;
                return CallNextHookEx(mouseHook, code, wParam, lParam);
            }

            var s = net.Session(remote);
            if (s == null) { ExitToServer(null); return CallNextHookEx(mouseHook, code, wParam, lParam); }
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    if ((m.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(mouseHook, code, wParam, lParam);
                    MoveVirtual(m.pt.x - pinned.X, m.pt.y - pinned.Y);
                    break;
                case WM_LBUTTONDOWN: s.Post(MsgType.MouseButton, Wire.Button(1, true)); break;
                case WM_LBUTTONUP: s.Post(MsgType.MouseButton, Wire.Button(1, false)); break;
                case WM_RBUTTONDOWN: s.Post(MsgType.MouseButton, Wire.Button(2, true)); break;
                case WM_RBUTTONUP: s.Post(MsgType.MouseButton, Wire.Button(2, false)); break;
                case WM_MBUTTONDOWN: s.Post(MsgType.MouseButton, Wire.Button(3, true)); break;
                case WM_MBUTTONUP: s.Post(MsgType.MouseButton, Wire.Button(3, false)); break;
                case WM_XBUTTONDOWN: s.Post(MsgType.MouseButton, Wire.Button((byte)((m.mouseData >> 16) == 1 ? 4 : 5), true)); break;
                case WM_XBUTTONUP: s.Post(MsgType.MouseButton, Wire.Button((byte)((m.mouseData >> 16) == 1 ? 4 : 5), false)); break;
                case WM_MOUSEWHEEL: s.Post(MsgType.Wheel, Wire.Wheel((short)(m.mouseData >> 16), false)); break;
                case WM_MOUSEHWHEEL: s.Post(MsgType.Wheel, Wire.Wheel((short)(m.mouseData >> 16), true)); break;
            }
            return (IntPtr)1; // svälj – den här datorn ska inte se det
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }
    }

    /// <summary>Lokalt läge: ska pekaren gå över till en annan dator?</summary>
    bool TryEnter(Point pt)
    {
        var rs = regions;
        if (rs.Length == 0 || !rs.Any(r => r.Owner != null)) return false;

        // a) Pekaren går in på en skärm som Windows har på, men som visar en annan dator.
        foreach (var r in rs)
        {
            if (r.Owner == null || r.ServerRect is not Rectangle sr || !sr.Contains(pt)) continue;
            Enter(r, r.Master.X + (pt.X - sr.X) * (double)r.Master.Width / sr.Width,
                     r.Master.Y + (pt.Y - sr.Y) * (double)r.Master.Height / sr.Height, ServerHome(rs, pt));
            return true;
        }

        // b) Pekaren trycker mot kanten av den här datorns skärm, och på andra sidan (i layouten) sitter en annan dators skärm.
        var cur = rs.FirstOrDefault(r => r.Owner == null && r.ServerRect is Rectangle sr && sr.Contains(pt));
        if (cur == null)
        {
            GetCursorPos(out var cp);
            cur = rs.FirstOrDefault(r => r.Owner == null && r.ServerRect is Rectangle sr && sr.Contains(cp.x, cp.y));
            if (cur == null) return false;
            pt = new Point(Math.Clamp(pt.X, cur.ServerRect!.Value.Left, cur.ServerRect.Value.Right - 1),
                           Math.Clamp(pt.Y, cur.ServerRect.Value.Top, cur.ServerRect.Value.Bottom - 1));
        }
        var s = cur.ServerRect!.Value;
        double mx = cur.Master.X + (pt.X - s.X) * (double)cur.Master.Width / s.Width;
        double my = cur.Master.Y + (pt.Y - s.Y) * (double)cur.Master.Height / s.Height;

        (int dx, int dy)[] dirs =
        {
            (pt.X <= s.Left ? -1 : 0, 0), (pt.X >= s.Right - 1 ? 1 : 0, 0),
            (0, pt.Y <= s.Top ? -1 : 0), (0, pt.Y >= s.Bottom - 1 ? 1 : 0),
        };
        foreach (var (dx, dy) in dirs)
        {
            if (dx == 0 && dy == 0) continue;
            // Har den här datorn själv en skärm där? Då sköter Windows det.
            if (rs.Any(r => r.Owner == null && r.ServerRect is Rectangle o && o.Contains(pt.X + dx, pt.Y + dy))) continue;
            double px = dx < 0 ? cur.Master.Left - 1 : dx > 0 ? cur.Master.Right : mx;
            double py = dy < 0 ? cur.Master.Top - 1 : dy > 0 ? cur.Master.Bottom : my;
            var target = rs.FirstOrDefault(r => r.Owner != null && r.Master.Contains((int)px, (int)py));
            if (target == null) continue;
            Enter(target, px, py, s);
            return true;
        }
        return false;
    }

    static Rectangle ServerHome(Region[] rs, Point pt) =>
        rs.FirstOrDefault(r => r.Owner == null && r.ServerRect is Rectangle sr && sr.Contains(pt))?.ServerRect
        ?? rs.FirstOrDefault(r => r.Owner == null && r.ServerRect != null)?.ServerRect
        ?? Screen.PrimaryScreen!.Bounds;

    void Enter(Region target, double mx, double my, Rectangle parkOn)
    {
        var s = net.Session(target.Owner!);
        if (s == null || target.ClientRect == null) return;
        remote = target.Owner;
        vx = Math.Clamp(mx, target.Master.Left, target.Master.Right - 1);
        vy = Math.Clamp(my, target.Master.Top, target.Master.Bottom - 1);
        // Parkera den här datorns pekare mitt på en skärm så att rörelser aldrig "tar slut" mot en kant.
        pinned = new Point(parkOn.X + parkOn.Width / 2, parkOn.Y + parkOn.Height / 2);
        SetCursorPos(pinned.X, pinned.Y);
        keysDown.Clear();
        var (cx, cy) = ToClient(target, vx, vy);
        s.Post(MsgType.Enter, Wire.Point(cx, cy));
        ActiveChanged?.Invoke(remote);
    }

    void MoveVirtual(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        var rs = regions;
        double nx = vx + dx, ny = vy + dy;
        Region? r = At(rs, nx, ny);
        if (r == null)
        {
            // Glid längs kanten i stället för att fastna.
            if ((r = At(rs, nx, vy)) != null) ny = vy;
            else if ((r = At(rs, vx, ny)) != null) nx = vx;
            else return;
        }
        vx = nx; vy = ny;

        if (r.Owner == null) { ExitToServer(r); return; }
        if (r.Owner != remote)
        {
            net.Session(remote!)?.Post(MsgType.ReleaseAll);
            net.Session(remote!)?.Post(MsgType.Leave);
            var next = net.Session(r.Owner);
            if (next == null || r.ClientRect == null) return;
            remote = r.Owner;
            var (ex, ey) = ToClient(r, vx, vy);
            next.Post(MsgType.Enter, Wire.Point(ex, ey));
            ActiveChanged?.Invoke(remote);
            return;
        }
        var (cx, cy) = ToClient(r, vx, vy);
        net.Session(remote)?.Post(MsgType.MouseMove, Wire.Point(cx, cy));
    }

    /// <summary>Tillbaka till den här datorn. r = skärmen pekaren går in på (null = där den parkerades).</summary>
    void ExitToServer(Region? r)
    {
        if (remote == null) return;
        var s = net.Session(remote);
        s?.Post(MsgType.ReleaseAll);
        s?.Post(MsgType.Leave);
        remote = null;
        keysDown.Clear();
        if (r?.ServerRect is Rectangle sr)
        {
            int x = (int)Math.Round(sr.X + (vx - r.Master.X) * sr.Width / r.Master.Width);
            int y = (int)Math.Round(sr.Y + (vy - r.Master.Y) * sr.Height / r.Master.Height);
            SetCursorPos(Math.Clamp(x, sr.Left, sr.Right - 1), Math.Clamp(y, sr.Top, sr.Bottom - 1));
        }
        else SetCursorPos(pinned.X, pinned.Y);
        ActiveChanged?.Invoke(null);
    }

    static Region? At(Region[] rs, double x, double y)
    {
        foreach (var r in rs)
            if (x >= r.Master.Left && x < r.Master.Right && y >= r.Master.Top && y < r.Master.Bottom
                && (r.Owner == null ? r.ServerRect != null : r.ClientRect != null))
                return r;
        return null;
    }

    static (int x, int y) ToClient(Region r, double x, double y)
    {
        var c = r.ClientRect!.Value;
        return ((int)Math.Round(c.X + (x - r.Master.X) * c.Width / r.Master.Width),
                (int)Math.Round(c.Y + (y - r.Master.Y) * c.Height / r.Master.Height));
    }

    // ---------- Tangentbord ----------

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || remote == null) return CallNextHookEx(keyboardHook, code, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(keyboardHook, code, wParam, lParam);
            int msg = (int)wParam;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            int vk = (int)k.vkCode;

            if (!up)
            {
                // Nödutgång: Ctrl+Shift+Alt + valfri tangent tar tillbaka musen.
                bool ctrl = keysDown.Overlaps(new[] { 0xA2, 0xA3, 0x11 });
                bool shift = keysDown.Overlaps(new[] { 0xA0, 0xA1, 0x10 });
                bool alt = keysDown.Overlaps(new[] { 0xA4, 0xA5, 0x12 });
                bool isModifier = vk is >= 0xA0 and <= 0xA5 or 0x10 or 0x11 or 0x12;
                if (ctrl && shift && alt && !isModifier)
                {
                    ExitToServer(null);
                    return (IntPtr)1;
                }
                keysDown.Add(vk);
            }
            else keysDown.Remove(vk);

            byte flags = (byte)((up ? Wire.KeyUp : 0) | ((k.flags & LLKHF_EXTENDED) != 0 ? Wire.KeyExtended : 0));
            net.Session(remote)?.Post(MsgType.Key, Wire.Key((ushort)vk, (ushort)k.scanCode, flags));
            return (IntPtr)1;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }
    }
}
