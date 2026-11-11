using System.Drawing.Drawing2D;

namespace SkarmStyrning.Client;

/// <summary>Litet fönster för klienten: parkoppling, status och ikon i systemfältet.</summary>
sealed class ClientForm : Form
{
    readonly ClientLink link = new();
    readonly NotifyIcon tray = new() { Visible = true, Text = "Skärmstyrning klient" };
    readonly Label status = new() { AutoSize = false, Height = 44, Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(30, 0, 0, 0) };
    readonly Panel dot = new() { Size = new Size(12, 12) };
    readonly TextBox address = new() { Width = 260, PlaceholderText = "t.ex. 192.168.1.20" };
    readonly TextBox code = new() { Width = 120, PlaceholderText = "123456", MaxLength = 6 };
    readonly ComboBox found = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
    readonly Label testResult = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(420, 0) };
    readonly Label pairedInfo = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    readonly CheckBox autostart = new() { Text = "Starta automatiskt när jag loggar in", AutoSize = true };
    bool reallyClose;
    readonly Net.ClipboardWatcher clipboard;
    readonly Label audioStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Text = "Ljud: skickas inte (inte ansluten)." };

    static readonly Color Accent = Color.FromArgb(47, 107, 255);

    public ClientForm()
    {
        Text = "Skärmstyrning – klient";
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(246, 247, 250);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Icon = MakeIcon();
        tray.Icon = Icon;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

        var title = new Label { Text = "Skärmstyrning klient", Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        var sub = new Label
        {
            Text = "Låter den här datorn styras med musen och tangentbordet från datorn som kör Skärmstyrning.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 14),
        };
        root.Controls.Add(title);
        root.Controls.Add(sub);

        // Statusrad
        var statusBox = new Panel { Width = 440, Height = 44, BackColor = Color.White, Margin = new Padding(0, 0, 0, 16) };
        statusBox.Paint += (_, e) => Border(e.Graphics, statusBox.ClientRectangle);
        dot.Location = new Point(12, 16);
        dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(dot.Tag is Color c ? c : Color.Gray);
            e.Graphics.FillEllipse(b, 0, 0, 11, 11);
        };
        statusBox.Controls.Add(status);
        statusBox.Controls.Add(dot);
        dot.BringToFront();
        root.Controls.Add(statusBox);

        // Parkoppling
        root.Controls.Add(Heading("Server"));
        var row1 = Row();
        row1.Controls.Add(address);
        row1.Controls.Add(Button("Sök", async (_, _) => await Search()));
        row1.Controls.Add(Button("Testa", async (_, _) => { testResult.Text = "Testar …"; testResult.Text = await ClientLink.Test(address.Text.Trim()); }));
        root.Controls.Add(row1);
        found.SelectedIndexChanged += (_, _) => { if (found.SelectedItem is Found f) address.Text = f.Address; };
        root.Controls.Add(found);
        root.Controls.Add(testResult);

        root.Controls.Add(Heading("Parkoppla"));
        var help = new Label
        {
            Text = "Klicka på \"Lägg till dator\" i Skärmstyrning på den andra datorn och skriv in koden som visas där.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 6),
        };
        root.Controls.Add(help);
        var row2 = Row();
        code.Font = new Font("Segoe UI Semibold", 13f);
        row2.Controls.Add(code);
        var pairBtn = Button("Parkoppla", async (_, _) => await Pair(), primary: true);
        row2.Controls.Add(pairBtn);
        root.Controls.Add(row2);
        AcceptButton = pairBtn;

        root.Controls.Add(Heading("Ljud"));
        root.Controls.Add(audioStatus);
        root.Controls.Add(new Label
        {
            Text = "Allt som spelas upp här skickas till den andra datorns högtalare. Vill du inte höra det här också: " +
                   "välj en ljudutgång utan högtalare (t.ex. en skärm via HDMI) som standard i Windows.",
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 6),
        });

        root.Controls.Add(pairedInfo);
        var row3 = Row();
        row3.Controls.Add(Button("Glöm servern", (_, _) => { link.Forget(); UpdatePaired(); }));
        root.Controls.Add(row3);
        root.Controls.Add(autostart);

        Controls.Add(root);

        autostart.Checked = Autostart.IsEnabled;
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);

        address.Text = link.Config.ServerAddress ?? "";
        link.StatusChanged += (text, connected, controlled) => BeginInvoke(() => SetStatus(text, connected, controlled));

        // Delat urklipp: kopiera här → klistra in på den andra datorn, och tvärtom.
        clipboard = new Net.ClipboardWatcher();
        clipboard.TextChanged += t => _ = link.SendClipboard(t);
        link.ClipboardReceived += t => BeginInvoke(() => clipboard.SetText(t));
        link.AudioChanged += () => BeginInvoke(UpdateAudio);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Öppna", null, (_, _) => ShowWindow());
        menu.Items.Add("Avsluta", null, (_, _) => { reallyClose = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();

        FormClosing += (_, e) =>
        {
            if (!reallyClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // fortsätt köra i systemfältet
                Hide();
                tray.ShowBalloonTip(2500, "Skärmstyrning klient", "Körs vidare i systemfältet.", ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) => { link.Dispose(); tray.Dispose(); clipboard.Dispose(); };

        UpdatePaired();
        SetStatus(link.Config.IsPaired ? "Ansluter …" : "Inte parkopplad", false, false);
        link.Start();
    }

    sealed record Found(string Name, string Address)
    {
        public override string ToString() => $"{Name}  ({Address})";
    }

    async Task Search()
    {
        testResult.Text = "Söker på nätverket …";
        var list = await ClientLink.Discover();
        found.Items.Clear();
        foreach (var (n, a) in list) found.Items.Add(new Found(n, a));
        found.Visible = list.Count > 0;
        if (list.Count > 0) found.SelectedIndex = 0;
        testResult.Text = list.Count > 0
            ? $"Hittade {list.Count} server(ar)."
            : "Hittade ingen server automatiskt (vanligt med VPN). Skriv in adressen som visas i Skärmstyrning.";
    }

    async Task Pair()
    {
        if (address.Text.Trim() == "" || code.Text.Trim().Length != 6)
        {
            testResult.Text = "Fyll i serverns adress och den sexsiffriga koden.";
            return;
        }
        testResult.Text = "Parkopplar …";
        try
        {
            await link.Pair(address.Text.Trim(), code.Text.Trim());
            code.Text = "";
            testResult.Text = "✓ Parkopplad! Den här datorn syns nu i Skärmstyrning.";
            UpdatePaired();
        }
        catch (Exception ex)
        {
            testResult.Text = "✗ " + ex.Message;
            link.Start();
        }
    }

    void UpdateAudio()
    {
        var a = link.Audio;
        if (a == null || a.DeviceName == null) { audioStatus.Text = "Ljud: skickas inte."; return; }
        audioStatus.Text = a.Sending
            ? $"🔊 Ljud skickas till {link.Config.ServerName} ({a.Transport.ToUpperInvariant()}) – från {a.DeviceName}"
            : $"Ljud: redo – skickar när något spelas upp på {a.DeviceName}";
    }

    void UpdatePaired()
    {
        var c = link.Config;
        pairedInfo.Text = c.IsPaired
            ? $"Parkopplad med {c.ServerName} ({c.ServerAddress}). Den här datorn heter \"{c.Name}\"."
            : "Inte parkopplad ännu.";
    }

    void SetStatus(string text, bool connected, bool controlled)
    {
        status.Text = text;
        dot.Tag = controlled ? Accent : connected ? Color.FromArgb(18, 161, 80) : Color.FromArgb(200, 140, 20);
        dot.Invalidate();
        tray.Text = (connected ? "Skärmstyrning – ansluten" : "Skärmstyrning – ej ansluten");
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Color.FromArgb(120, 128, 140),
        Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, 10, 0, 6),
    };

    static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };

    static Button Button(string text, EventHandler click, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(84, 32), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Color.Black,
            Margin = new Padding(6, 0, 0, 0), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(210, 214, 222);
        b.Click += click;
        return b;
    }

    static void Border(Graphics g, Rectangle r)
    {
        using var pen = new Pen(Color.FromArgb(220, 223, 230));
        g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(20, 184, 166), Color.FromArgb(59, 130, 246), 45f);
            g.FillEllipse(brush, 2, 2, 60, 60);
            g.FillRectangle(Brushes.White, 16, 20, 32, 20);
            g.FillRectangle(Brushes.White, 29, 40, 6, 6);
            g.FillRectangle(Brushes.White, 22, 45, 20, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SkarmStyrningKlient";

    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(Name, false);
    }
}
