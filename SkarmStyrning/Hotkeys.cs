using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SkarmStyrning;

/// <summary>Kortkommandon som text: "Ctrl+Shift+Alt+D1" (modifierare + namn ur Keys).</summary>
public static class Hotkey
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public static bool TryParse(string? combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Keys>(raw, true, out var k)) return false;
                    vk = (uint)k;
                    break;
            }
        }
        return vk != 0 && (mods & (MOD_CONTROL | MOD_ALT | MOD_WIN)) != 0;
    }

    public static string Label(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return "";
        return string.Join(" + ", combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(KeyLabel));
    }

    static string KeyLabel(string k) => k switch
    {
        _ when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => k[1..],
        _ when k.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) => "Num " + k[6..],
        "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓",
        "PageUp" => "PgUp", "PageDown" => "PgDn", "Delete" => "Del", "Insert" => "Ins",
        _ => k,
    };

    /// <summary>Föreslår Ctrl+Shift+Alt+1…9 (Ctrl+Alt ensamt är AltGr på svenskt tangentbord).</summary>
    public static string? Suggest(IEnumerable<string?> taken)
    {
        var used = taken.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!.ToLowerInvariant()).ToHashSet();
        for (int n = 1; n <= 9; n++)
        {
            var c = $"Ctrl+Shift+Alt+D{n}";
            if (!used.Contains(c.ToLowerInvariant())) return c;
        }
        return null;
    }
}

/// <summary>Liten ruta längst ner på skärmen som visar vad ett kortkommando gjorde.</summary>
sealed class OsdOverlay : Form
{
    static OsdOverlay? current;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1800 };
    string title = "", subtitle = "";

    public static void Show(string title, string subtitle)
    {
        if (current == null || current.IsDisposed) current = new OsdOverlay();
        current.title = title;
        current.subtitle = subtitle;
        var screen = Screen.FromPoint(Cursor.Position);
        current.Location = new Point(screen.WorkingArea.Left + (screen.WorkingArea.Width - current.Width) / 2, screen.WorkingArea.Bottom - current.Height - 72);
        current.Invalidate();
        if (!current.Visible) current.Show();
        current.timer.Stop();
        current.timer.Start();
    }

    OsdOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Size = new Size(380, 84);
        BackColor = Color.FromArgb(28, 31, 38);
        DoubleBuffered = true;
        timer.Tick += (_, _) => { timer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x20 /*TRANSPARENT*/;
            return cp;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var icon = new RectangleF(18, 18, 48, 48);
        using (var path = AppWindow.RoundRect(icon, 12))
        using (var brush = new LinearGradientBrush(icon, Color.FromArgb(59, 130, 246), Color.FromArgb(139, 92, 246), 45f))
            g.FillPath(brush, path);
        using (var white = new SolidBrush(Color.White))
        {
            using var scr = AppWindow.RoundRect(new RectangleF(29, 29, 26, 17), 3);
            g.FillPath(white, scr);
            g.FillRectangle(white, 39, 46, 6, 5);
            g.FillRectangle(white, 34, 51, 16, 3);
        }

        using var big = new Font("Segoe UI Semibold", 15, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 12.5f, GraphicsUnit.Pixel);
        using var muted = new SolidBrush(Color.FromArgb(170, 178, 192));
        var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(title, big, Brushes.White, new RectangleF(80, 21, Width - 96, 24), fmt);
        g.DrawString(subtitle, small, muted, new RectangleF(80, 45, Width - 96, 22), fmt);
    }
}
