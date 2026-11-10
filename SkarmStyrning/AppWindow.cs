using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SkarmStyrning;

/// <summary>
/// Huvudfönstret: ett WebView2-gränssnitt (ui/index.html) ovanpå samma logik som förut.
/// Gränssnittet skickar kommandon {id, cmd, args}; fönstret svarar och skickar nytt tillstånd.
/// </summary>
public sealed partial class AppWindow : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray = new() { Text = "Skärmstyrning", Visible = true };
    readonly System.Windows.Forms.Timer autoTimer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 1500 };
    readonly SemaphoreSlim op = new(1, 1);
    readonly Dictionary<string, InputInfo> inputs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<byte>> caps = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Profile> profiles = ProfileStore.Load();
    List<DisplayTarget> targets = new();
    string? busyText;
    int refreshGen;
    bool uiReady;

    // Globala kortkommandon
    readonly Dictionary<int, Func<Task>> hotkeyActions = new();
    readonly HashSet<string> failedHotkeys = new(StringComparer.OrdinalIgnoreCase);
    bool hotkeysSuspended;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly string? screenshotPath, screenshotScript;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly Icon AppIcon = CreateAppIcon();

    public AppWindow(string? screenshotPath = null, string? theme = null, string? script = null)
    {
        this.screenshotPath = screenshotPath;
        screenshotScript = script;
        if (theme != null && screenshotPath != null) themeOverride = theme;
        if (screenshotPath != null)
        {
            // Felsökningsläge: osynligt och klickgenomskinligt, så det inte stör den som sitter vid datorn.
            Opacity = 0;
            ShowInTaskbar = false;
            tray.Visible = false;
        }

        Text = "Skärmstyrning";
        Icon = AppIcon;
        tray.Icon = AppIcon;
        MinimumSize = new Size(940, 660);
        Size = new Size(1240, 840);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = IsDark() ? Color.FromArgb(21, 23, 28) : Color.FromArgb(243, 244, 247);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        // Hamnade fönstret ändå med en annan skalning än beräknat? Rätta storleken en gång.
        Shown += (_, _) =>
        {
            uint real = GetDpiForWindow(Handle);
            if (real != 0 && real != fittedDpi) FitToScreen(Screen.FromControl(this), real);
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && screenshotPath == null) Hide(); };

        // Ändringar utanför appen (Windows inställningar, Win+P, skärmar som kopplas in/ur).
        displayTimer.Tick += async (_, _) => { displayTimer.Stop(); if (!await AutoTick()) await RefreshAsync(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        autoTimer.Tick += async (_, _) =>
        {
            if (op.CurrentCount > 0) { try { await Task.Run(DisplayConfig.Observe); } catch { } }
            await AutoTick();
        };

        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            autoTimer.Dispose();
            displayTimer.Dispose();
            tray.Dispose();
            StopKvm();
        };
    }

    void OnDisplayChanged(object? s, EventArgs e) => BeginInvoke(() => { displayTimer.Stop(); displayTimer.Start(); });
    void OnUserPreferenceChanged(object? s, Microsoft.Win32.UserPreferenceChangedEventArgs e) => BeginInvoke(ApplyTheme);

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyTitleBar();
        FitToScreen();
        if (screenshotPath == null)
        {
            // Profiler som aldrig fått något kortkommando får Ctrl+Shift+Alt+1…9.
            bool changed = false;
            foreach (var p in profiles.Where(p => p.Hotkey == null))
            {
                p.Hotkey = Hotkey.Suggest(profiles.Select(x => x.Hotkey)) ?? "";
                changed = true;
            }
            if (changed) ProfileStore.Save(profiles);
            RegisterHotkeys();
            StartKvm();
        }
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkarmStyrning", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kunde inte starta gränssnittet (Microsoft Edge WebView2 saknas?).\n\n" + ex.Message,
                "Skärmstyrning", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("skarm.app", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        ApplyTheme();
        core.WebMessageReceived += (_, a) => _ = HandleMessage(a.WebMessageAsJson);
        core.Navigate("https://skarm.app/index.html");
        autoTimer.Start();
    }

    /// <summary>Storlek i "logiska" pixlar, skalad efter skärmens DPI (t.ex. 150 % på en 4K-skärm).</summary>
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    uint fittedDpi;

    void FitToScreen(Screen? target = null, uint? dpi = null)
    {
        var screen = target ?? Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        if (dpi == null)
        {
            var mon = MonitorFromPoint(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2), 2);
            dpi = GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 ? dx : (uint)DeviceDpi;
        }
        fittedDpi = dpi.Value;
        float s = dpi.Value / 96f;
        MinimumSize = new Size((int)(940 * s), (int)(660 * s));
        int w = Math.Min((int)(1240 * s), (int)(area.Width * 0.94));
        int h = Math.Min((int)(840 * s), (int)(area.Height * 0.94));
        MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    // ---------------- Kortkommandon ----------------

    void UnregisterHotkeys()
    {
        foreach (var id in hotkeyActions.Keys) UnregisterHotKey(Handle, id);
        hotkeyActions.Clear();
    }

    void RegisterHotkeys()
    {
        UnregisterHotkeys();
        failedHotkeys.Clear();
        if (hotkeysSuspended) return;
        int next = 1;
        void Reg(string? combo, Func<Task> action)
        {
            if (string.IsNullOrWhiteSpace(combo)) return;
            if (!Hotkey.TryParse(combo, out uint mods, out uint vk)) { failedHotkeys.Add(combo); return; }
            int id = next++;
            if (RegisterHotKey(Handle, id, mods | Hotkey.MOD_NOREPEAT, vk)) hotkeyActions[id] = action;
            else failedHotkeys.Add(combo); // upptaget av ett annat program (eller dubblett)
        }
        Reg(Settings.HotkeyNextProfile, NextProfile);
        Reg(Settings.HotkeyExtend, async () =>
        {
            if (op.CurrentCount == 0) { OsdOverlay.Show("Vänta lite", "En ändring pågår redan …"); return; }
            OsdOverlay.Show("Utöka alla", "Söker skärmar som visar den här datorn …");
            bool ok = await Quietly("extend", null);
            OsdOverlay.Show("Utöka alla", ok ? "klar ✓" : "gick inte helt – se appen");
        });
        foreach (var p in profiles)
        {
            var name = p.Name;
            Reg(p.Hotkey, () => ApplyProfileFromHotkey(name));
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && hotkeyActions.TryGetValue((int)m.WParam, out var action))
        {
            _ = action();
            return;
        }
        base.WndProc(ref m);
    }

    async Task ApplyProfileFromHotkey(string name)
    {
        if (op.CurrentCount == 0) { OsdOverlay.Show("Vänta lite", "En ändring pågår redan …"); return; }
        int i = profiles.FindIndex(p => p.Name == name);
        string pos = profiles.Count > 1 ? $"{i + 1} av {profiles.Count} · " : "";
        OsdOverlay.Show($"Profil: {name}", pos + "byter …");
        bool ok = await Quietly("applyProfile", new JsonObject { ["name"] = name });
        OsdOverlay.Show($"Profil: {name}", pos + (ok ? "klar ✓" : "gick inte helt – se appen"));
    }

    bool quiet; // inga ballonger i systemfältet när skärmrutan redan visar vad som händer

    async Task<bool> Quietly(string cmd, JsonNode? args)
    {
        quiet = true;
        lastOpFailed = false;
        try { await Dispatch(cmd, args); return !lastOpFailed; }
        catch (Exception ex) { Toast(ex.Message, "error"); return false; }
        finally { quiet = false; }
    }

    bool lastOpFailed;

    /// <summary>Alla kortkommandon i appen: ägare → (namn, kombination).</summary>
    IEnumerable<(string owner, string name, string combo)> AllHotkeys()
    {
        yield return ("next", "Nästa profil", Settings.HotkeyNextProfile);
        yield return ("extend", "Utöka alla", Settings.HotkeyExtend);
        foreach (var p in profiles) yield return ("profile:" + p.Name, $"profilen \"{p.Name}\"", p.Hotkey ?? "");
    }

    string CheckHotkey(string? value, string owner)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Hotkey.TryParse(value, out _, out _))
            throw new InvalidOperationException("Kortkommandot måste ha Ctrl, Alt eller Win och en vanlig tangent.");
        var clash = AllHotkeys().FirstOrDefault(h => h.owner != owner && string.Equals(h.combo, value, StringComparison.OrdinalIgnoreCase));
        if (clash.owner != null)
            throw new InvalidOperationException($"{Hotkey.Label(value)} används redan av {clash.name}.");
        return value;
    }

    void ReportHotkey(string value)
    {
        if (value == "") Toast("Kortkommandot är borttaget.", "success");
        else if (failedHotkeys.Contains(value)) Toast($"{Hotkey.Label(value)} är upptaget av ett annat program – välj ett annat.", "error");
        else Toast($"{Hotkey.Label(value)} är sparat och fungerar i hela Windows.", "success");
    }

    /// <summary>Växlar till nästa profil i listan (efter den som användes senast).</summary>
    async Task NextProfile()
    {
        if (profiles.Count == 0) { OsdOverlay.Show("Inga profiler", "Skapa en profil i Skärmstyrning först."); return; }
        int last = profiles.FindIndex(p => p.Name == Settings.LastProfile);
        var next = profiles[(last + 1) % profiles.Count];
        await ApplyProfileFromHotkey(next.Name);
    }

    // ---------------- Tema ----------------

    string? themeOverride;
    string Theme => themeOverride ?? Settings.Theme;

    bool IsDark()
    {
        if (Theme == "dark") return true;
        if (Theme == "light") return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyTitleBar()
    {
        bool dark = IsDark();
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref v, 4);                 // mörk titelrad
        int caption = dark ? 0x001C1715 : 0x00F7F4F3;               // samma färg som appens bakgrund
        DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        int text = dark ? 0x00F3EEEC : 0x00221D1B;
        DwmSetWindowAttribute(Handle, 36, ref text, 4);
    }

    void ApplyTheme()
    {
        ApplyTitleBar();
        BackColor = IsDark() ? Color.FromArgb(21, 23, 28) : Color.FromArgb(243, 244, 247);
        web.DefaultBackgroundColor = BackColor;
        if (web.CoreWebView2 == null) return;
        web.CoreWebView2.Profile.PreferredColorScheme = Theme switch
        {
            "light" => CoreWebView2PreferredColorScheme.Light,
            "dark" => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    // ---------------- Brygga ----------------

    void Post(object o)
    {
        if (web.CoreWebView2 == null || IsDisposed) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(o, Json));
    }

    void Toast(string text, string kind = "info")
    {
        Post(new { type = "toast", text, kind });
        if (kind == "error") lastOpFailed = true;
        if (!Visible && screenshotPath == null && !quiet)
            tray.ShowBalloonTip(3500, "Skärmstyrning", text, kind == "error" ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    void SetBusy(string? text)
    {
        busyText = text;
        Post(new { type = "busy", text });
    }

    Task PushState()
    {
        RebuildRegions();
        if (uiReady) Post(new { type = "state", state = BuildState() });
        return Task.CompletedTask;
    }

    async Task HandleMessage(string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); } catch { return; }
        int id = msg?["id"]?.GetValue<int>() ?? 0;
        string cmd = msg?["cmd"]?.GetValue<string>() ?? "";
        try
        {
            var result = await Dispatch(cmd, msg?["args"]);
            Post(new { type = "reply", id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { type = "reply", id, ok = false, error = ex.Message });
            Toast(ex.Message, "error");
        }
    }

    DisplayTarget Find(string id) =>
        targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Skärmen är inte ansluten.");

    async Task<object?> Dispatch(string cmd, JsonNode? a)
    {
        string Str(string k) => a?[k]?.GetValue<string>() ?? "";
        bool Bool(string k) => a?[k]?.GetValue<bool>() ?? false;

        switch (cmd)
        {
            case "ready":
                uiReady = true;
                await PushState();
                await RefreshAsync();
                if (screenshotPath != null) _ = TakeScreenshot();
                return null;

            case "refresh":
                await RefreshAsync();
                return null;

            case "extend":
                await DisplayOp("Utökar till skärmarna som visar den här datorn", () =>
                {
                    Settings.ExtendIntent = true;
                    return Smart.Extend(userInitiated: true);
                });
                return null;

            case "clone":
                await DisplayOp("Duplicerar alla skärmar", () =>
                {
                    Settings.ExtendIntent = false;
                    Actions.CloneAll();
                    return "Alla skärmar visar nu samma bild.";
                });
                return null;

            case "only":
            {
                var t = Find(Str("id"));
                await DisplayOp($"Visar endast {t.Label}", () =>
                {
                    Settings.ExtendIntent = false;
                    Actions.Only(t);
                    return $"Visar endast {t.Label}.";
                });
                return null;
            }

            case "applyModes":
            {
                var groups = (a?["groups"] as JsonArray ?? new())
                    .Select(g => (IReadOnlyList<DisplayTarget>)(g as JsonArray ?? new()).Select(x => Find(x!.GetValue<string>())).ToList())
                    .Where(g => g.Count > 0).ToList();
                await DisplayOp("Ändrar skärmläge", () =>
                {
                    Settings.ExtendIntent = false;
                    DisplayConfig.ApplyGroups(groups);
                    return "Skärmläget är ändrat.";
                });
                return null;
            }

            case "setInput":
            {
                var t = Find(Str("id"));
                byte code = (byte)(a?["code"]?.GetValue<int>() ?? 0);
                await DisplayOp($"Byter {t.Label} till {Ddc.InputName(code)}", () =>
                {
                    Ddc.SetInput(t.GdiName, t.PhysIndex, code);
                    return $"{t.Label} visar nu {Ddc.InputName(code)}.";
                });
                return null;
            }

            case "setHome":
            {
                var t = Find(Str("id"));
                byte code = (byte)(a?["code"]?.GetValue<int>() ?? 0);
                Settings.SetHomeInput(t.DevicePath, code);
                Toast($"{t.Label}: den här datorn sitter på {Ddc.InputName(code)}.", "success");
                await PushState();
                return null;
            }

            case "saveLayout":
            {
                var positions = (a?["positions"] as JsonArray ?? new())
                    .Select(p => (p!["id"]!.GetValue<string>(), (int)Math.Round(p["x"]!.GetValue<double>()), (int)Math.Round(p["y"]!.GetValue<double>())))
                    .ToList();
                string? primary = a?["primary"]?.GetValue<string>();
                PositionMemory.SetPositions(positions, primary);
                if (!Bool("apply"))
                {
                    Toast("Placeringen är sparad och används nästa gång skärmarna ändras.", "success");
                    await PushState();
                    return null;
                }
                var groups = targets.Where(t => t.Active)
                    .GroupBy(t => t.GdiName ?? t.Id)
                    .Select(g => (IReadOnlyList<DisplayTarget>)g.ToList()).ToList();
                await DisplayOp("Flyttar skärmarna", () =>
                {
                    DisplayConfig.ApplyGroups(groups);
                    return "Skärmarna sitter nu där du placerade dem.";
                });
                return null;
            }

            case "learnLayout":
                await Task.Run(DisplayConfig.LearnCurrent);
                Toast("Placeringen är hämtad från Windows.", "success");
                await PushState();
                return null;

            case "forget":
                PositionMemory.Forget(Str("id"));
                await PushState();
                return null;

            case "identify":
                Identify();
                return null;

            case "saveProfile":
                await SaveProfile(Str("name"), Bool("layout"), Bool("inputs"));
                return null;

            case "applyProfile":
            {
                var p = profiles.FirstOrDefault(x => x.Name == Str("name")) ?? throw new InvalidOperationException("Profilen finns inte.");
                Settings.LastProfile = p.Name;
                await DisplayOp($"Använder profilen \"{p.Name}\"", () =>
                {
                    if (p.Layout != null) Settings.ExtendIntent = false;
                    Actions.ApplyProfile(p);
                    return $"Profilen \"{p.Name}\" är aktiv.";
                });
                return null;
            }

            case "deleteProfile":
                profiles.RemoveAll(x => x.Name == Str("name"));
                ProfileStore.Save(profiles);
                RegisterHotkeys();
                BuildTrayMenu();
                await PushState();
                return null;

            case "setProfileHotkey":
            {
                var p = profiles.FirstOrDefault(x => x.Name == Str("name")) ?? throw new InvalidOperationException("Profilen finns inte.");
                p.Hotkey = CheckHotkey(a?["hotkey"]?.GetValue<string>(), "profile:" + p.Name);
                ProfileStore.Save(profiles);
                hotkeysSuspended = false;
                RegisterHotkeys();
                BuildTrayMenu();
                await PushState();
                ReportHotkey(p.Hotkey);
                return null;
            }

            case "setHotkey":
            {
                string which = Str("which");
                string value = CheckHotkey(a?["hotkey"]?.GetValue<string>(), which == "next" ? "next" : "extend");
                if (which == "next") Settings.HotkeyNextProfile = value; else Settings.HotkeyExtend = value;
                hotkeysSuspended = false;
                RegisterHotkeys();
                BuildTrayMenu();
                await PushState();
                ReportHotkey(value);
                return null;
            }

            case "suspendHotkeys":
                // Medan man spelar in ett nytt kortkommando ska de gamla inte slå till.
                hotkeysSuspended = true;
                UnregisterHotkeys();
                return null;

            case "resumeHotkeys":
                hotkeysSuspended = false;
                RegisterHotkeys();
                await PushState();
                return null;

            case "copyCommand":
                Clipboard.SetText($"\"{Environment.ProcessPath}\" --profile \"{Str("name")}\"");
                Toast("Kommandot är kopierat – klistra in det som mål i en Windows-genväg.", "success");
                return null;

            case "setAuto":
                Settings.AutoAdapt = Bool("value");
                await PushState();
                return null;

            case "setTheme":
                Settings.Theme = Str("value") is "light" or "dark" ? Str("value") : "system";
                themeOverride = null;
                ApplyTheme();
                await PushState();
                return null;

            case "openWindowsSettings":
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
                return null;

            default:
                var (handled, result) = await KvmDispatch(cmd, a);
                if (handled) return result;
                throw new InvalidOperationException("Okänt kommando: " + cmd);
        }
    }

    /// <summary>Kör en skärmändring i bakgrunden, en i taget, och uppdaterar sedan allt.</summary>
    async Task DisplayOp(string what, Func<string?> action)
    {
        await op.WaitAsync();
        try
        {
            SetBusy(what);
            var msg = await Task.Run(action);
            await Task.Delay(1200);
            Toast(msg ?? what + " – klart.", "success");
        }
        catch (Exception ex)
        {
            Toast(ex.Message, "error");
        }
        finally
        {
            SetBusy(null);
            op.Release();
        }
        await RefreshAsync();
    }

    async Task<bool> AutoTick()
    {
        if (!Settings.AutoAdapt || !Settings.ExtendIntent || op.CurrentCount == 0) return false;
        await op.WaitAsync();
        string? msg;
        try { msg = await Task.Run(Smart.AutoTick); }
        catch (Exception ex) { msg = "Automatisk anpassning misslyckades: " + ex.Message; }
        finally { op.Release(); }
        if (msg == null) return false;
        Toast(msg, "info");
        await RefreshAsync();
        return true;
    }

    async Task RefreshAsync()
    {
        int gen = ++refreshGen;
        try { targets = await Task.Run(DisplayConfig.GetTargets); }
        catch (Exception ex) { Toast("Kunde inte läsa skärmarna: " + ex.Message, "error"); return; }
        BuildTrayMenu();
        await PushState();

        foreach (var t in targets.Where(t => t.Active).ToList())
        {
            caps.TryGetValue(t.Id, out var known);
            var info = await Task.Run(() => Ddc.Read(t.GdiName, t.PhysIndex, known));
            if (gen != refreshGen) return; // en nyare uppdatering har startat
            if (info.CapsFromMonitor)
            {
                caps[t.Id] = info.Supported;
                lock (Smart.KnownCaps) Smart.KnownCaps[t.DevicePath] = info.Supported;
            }
            inputs[t.Id] = info;
            await PushState();
        }
    }

    async Task SaveProfile(string name, bool withLayout, bool withInputs)
    {
        name = name.Trim();
        if (name == "") throw new InvalidOperationException("Ge profilen ett namn.");
        if (!withLayout && !withInputs) throw new InvalidOperationException("Välj minst en sak att spara.");

        var p = new Profile { Name = name };
        if (withLayout) p.Layout = await Task.Run(DisplayConfig.TakeSnapshot);
        if (withInputs)
        {
            p.Inputs = new();
            p.InputLabels = new();
            foreach (var t in targets.Where(t => t.Active))
            {
                caps.TryGetValue(t.Id, out var known);
                var info = await Task.Run(() => Ddc.Read(t.GdiName, t.PhysIndex, known));
                if (info.Current is not uint c) continue;
                p.Inputs[t.DevicePath] = (byte)c;
                p.InputLabels[t.DevicePath] = $"{t.Label} → {Ddc.InputName((byte)c)}";
            }
        }
        int i = profiles.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            p.Hotkey = profiles[i].Hotkey; // behåll kortkommandot när en profil skrivs över
            profiles[i] = p;
        }
        else
        {
            p.Hotkey = Hotkey.Suggest(AllHotkeys().Select(h => h.combo)) ?? "";
            profiles.Add(p);
        }
        ProfileStore.Save(profiles);
        RegisterHotkeys();
        BuildTrayMenu();
        Toast($"Profilen \"{name}\" är sparad.", "success");
        await PushState();
    }

    // ---------------- Tillstånd till gränssnittet ----------------

    /// <summary>Alla skärmar i huvudlayouten + anslutna som inte finns där, med plats och nummer.</summary>
    List<(string id, string name, int x, int y, int w, int h, int number)> Placed()
    {
        var list = PositionMemory.Items().Select(i => (id: i.Dev, name: i.Name, x: i.X, y: i.Y, w: Math.Max(i.Width, 640), h: Math.Max(i.Height, 360))).ToList();
        foreach (var t in targets.Where(t => !list.Any(p => string.Equals(p.id, t.Id, StringComparison.OrdinalIgnoreCase))))
        {
            int right = list.Count > 0 ? list.Max(p => p.x + p.w) : 0;
            list.Add((t.Id, t.Name, right, 0, t.HasGeometry ? t.Width : 1920, t.HasGeometry ? t.Height : 1080));
        }
        return list.OrderBy(p => p.x).ThenBy(p => p.y).Select((p, i) => (p.id, p.name, p.x, p.y, p.w, p.h, i + 1)).ToList();
    }

    object BuildState()
    {
        var placed = Placed();
        var monitors = placed.Select(p =>
        {
            var t = targets.FirstOrDefault(x => string.Equals(x.Id, p.id, StringComparison.OrdinalIgnoreCase));
            string? cloneOf = null;
            if (t is { Active: true })
            {
                int idx = targets.IndexOf(t);
                cloneOf = targets.Take(idx).FirstOrDefault(o => o.Active && o.GdiName == t.GdiName)?.Id;
            }
            return new
            {
                id = p.id,
                name = t?.Name ?? (string.IsNullOrWhiteSpace(p.name) ? "Skärm" : p.name),
                label = t?.Label ?? p.name,
                connector = t?.Connector,
                state = t == null ? "disconnected" : t.Active ? "on" : "off",
                excluded = t is { Active: false } ? Smart.ExcludedReason(t.DevicePath) : null,
                number = p.number,
                windowsNumber = t?.DisplayNumber,
                layout = new { x = p.x, y = p.y, w = p.w, h = p.h },
                primary = p.x == 0 && p.y == 0,
                width = t?.HasGeometry == true ? t.Width : p.w,
                height = t?.HasGeometry == true ? t.Height : p.h,
                hz = t?.Hz ?? 0,
                cloneOf,
                input = InputState(t),
                owner = OwnerState(t),
                computers = ComputersOn(t),
            };
        }).ToList();

        return new
        {
            monitors,
            profiles = profiles.Select(p => new
            {
                name = p.Name,
                layout = p.Layout?.Description,
                inputs = p.InputLabels?.Values.ToList() ?? new List<string>(),
                hotkey = HotkeyState(p.Hotkey),
                active = p.Name == Settings.LastProfile,
            }),
            settings = new
            {
                autoAdapt = Settings.AutoAdapt,
                extendIntent = Settings.ExtendIntent,
                theme = Settings.Theme,
                hotkeys = new { next = HotkeyState(Settings.HotkeyNextProfile), extend = HotkeyState(Settings.HotkeyExtend) },
                shareInput = Settings.ShareInput,
            },
            computers = ComputersState(),
            net = NetState(),
            remote = activeRemote == null ? null : net?.Session(activeRemote)?.Name,
            remoteId = activeRemote,
            busy = busyText,
        };
    }

    object HotkeyState(string? combo) => new
    {
        value = combo ?? "",
        keys = string.IsNullOrEmpty(combo) ? Array.Empty<string>() : Hotkey.Label(combo).Split(" + "),
        failed = !string.IsNullOrEmpty(combo) && failedHotkeys.Contains(combo),
    };

    object? InputState(DisplayTarget? t)
    {
        if (t is not { Active: true }) return null;
        if (!inputs.TryGetValue(t.Id, out var info)) return new { loading = true };
        var codes = info.Supported.Count > 0 ? info.Supported.ToList() : Ddc.CommonInputs.ToList();
        if (info.Current is uint c && !codes.Contains((byte)c)) codes.Insert(0, (byte)c);
        bool? isHome = info.Current is uint cc ? Settings.IsHome(t, (byte)cc) : null;
        return new
        {
            loading = false,
            current = info.Current,
            error = info.Error,
            fromMonitor = info.CapsFromMonitor,
            home = Settings.HomeInput(t.DevicePath),
            isHome,
            options = codes.Select(x => new { code = x, name = Ddc.InputName(x), kind = Kind(x), computer = ComputerOnInput(t, x) }),
        };
    }

    static string Kind(byte c) => c switch
    {
        0x01 or 0x02 => "vga",
        0x03 or 0x04 => "dvi",
        0x0F or 0x10 => "dp",
        0x11 or 0x12 => "hdmi",
        0x1B => "usbc",
        _ => "other",
    };

    // ---------------- Systemfält, identifiera, skärmdump ----------------

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void BuildTrayMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Öppna Skärmstyrning", null, (_, _) => ShowFromTray());
        m.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem Item(string text, string? hotkey, Func<Task> action)
        {
            var item = new ToolStripMenuItem(text, null, async (_, _) => await action());
            if (!string.IsNullOrEmpty(hotkey)) item.ShortcutKeyDisplayString = Hotkey.Label(hotkey);
            return item;
        }
        m.Items.Add(Item("Utöka alla", Settings.HotkeyExtend, () => Quietly("extend", null)));
        m.Items.Add(Item("Duplicera alla", null, () => Quietly("clone", null)));
        foreach (var t in targets)
        {
            var id = t.Id;
            m.Items.Add(Item("Endast " + t.Label, null, () => Quietly("only", new JsonObject { ["id"] = id })));
        }
        if (profiles.Count > 0)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Nästa profil", Settings.HotkeyNextProfile, NextProfile));
            foreach (var p in profiles)
            {
                var name = p.Name;
                var item = Item("Profil: " + name, p.Hotkey, () => Quietly("applyProfile", new JsonObject { ["name"] = name }));
                item.Checked = name == Settings.LastProfile;
                m.Items.Add(item);
            }
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Avsluta", null, (_, _) => Close());
        var old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = m;
        old?.Dispose();
        tray.Text = Settings.AutoAdapt && Settings.ExtendIntent ? "Skärmstyrning – anpassar automatiskt" : "Skärmstyrning";
    }

    void Identify()
    {
        var numbers = Placed().ToDictionary(p => p.id, p => p.number, StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets.Where(t => t is { Active: true, GdiName: not null }))
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, t.GdiName, StringComparison.OrdinalIgnoreCase));
            if (screen == null) continue;
            var f = new IdentifyOverlay(numbers.GetValueOrDefault(t.Id), t.Label)
            {
                Location = new Point(screen.Bounds.Left + 56, screen.Bounds.Top + 56),
            };
            f.Show();
        }
    }

    async Task TakeScreenshot()
    {
        await Task.Delay(2500);
        if (screenshotScript != null)
        {
            await web.CoreWebView2.ExecuteScriptAsync(screenshotScript);
            await Task.Delay(900);
        }
        var metrics = await web.CoreWebView2.ExecuteScriptAsync("innerWidth + 'x' + innerHeight + ' @' + devicePixelRatio");
        File.WriteAllText(screenshotPath + ".txt", $"bounds={Bounds} client={ClientSize} dpi={DeviceDpi} web={metrics}");
        using (var fs = File.Create(screenshotPath!))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        Close();
    }

    static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = RoundRect(new RectangleF(2, 2, 60, 60), 15);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(59, 130, 246), Color.FromArgb(139, 92, 246), 45f);
            g.FillPath(brush, bg);
            using var white = new SolidBrush(Color.White);
            using var back = RoundRect(new RectangleF(20, 13, 32, 21), 4);
            using var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
            g.FillPath(dim, back);
            using var front = RoundRect(new RectangleF(11, 22, 34, 22), 4);
            g.FillPath(white, front);
            g.FillRectangle(white, 25, 44, 6, 6);
            using var foot = RoundRect(new RectangleF(18, 49, 20, 4), 2);
            g.FillPath(white, foot);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Stor siffra som visas en kort stund på varje skärm.</summary>
sealed class IdentifyOverlay : Form
{
    readonly int number;
    readonly string label;

    public IdentifyOverlay(int number, string label)
    {
        this.number = number;
        this.label = label;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Size = new Size(240, 190);
        BackColor = Color.FromArgb(24, 26, 32);
        DoubleBuffered = true;
        var timer = new System.Windows.Forms.Timer { Interval = 3200 };
        timer.Tick += (_, _) => { timer.Dispose(); Close(); };
        Shown += (_, _) => timer.Start();
        FormClosed += (_, _) => Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // rundade hörn i Windows 11
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var accent = new LinearGradientBrush(ClientRectangle, Color.FromArgb(59, 130, 246), Color.FromArgb(139, 92, 246), 45f);
        g.FillRectangle(accent, 0, 0, Width, 6);
        using var big = new Font("Segoe UI Variable Display", 72, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(number.ToString(), big, Brushes.White, new RectangleF(0, 18, Width, 110), fmt);
        using var muted = new SolidBrush(Color.FromArgb(190, 196, 208));
        g.DrawString(label, small, muted, new RectangleF(10, 128, Width - 20, 40), fmt);
    }
}
