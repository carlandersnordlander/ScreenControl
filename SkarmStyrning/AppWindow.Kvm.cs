using System.Text.Json.Nodes;
using SkarmStyrning.Net;

namespace SkarmStyrning;

/// <summary>Andra datorer: parkoppling, vem som visas på vilken skärm, och mus/tangentbord över kanten.</summary>
public sealed partial class AppWindow
{
    NetServer? net;
    InputRouter? router;
    string? activeRemote;
    Region[] lastRegions = Array.Empty<Region>();

    void StartKvm()
    {
        net = new NetServer();
        net.Changed += () => BeginInvoke(async () =>
        {
            BuildTrayMenu(); // profiler som kräver en dator blir valbara/spärrade när den ansluter/kopplar ner
            await PushState();
        });
        net.Paired += name => BeginInvoke(() =>
        {
            Toast($"{name} är parkopplad och ansluter strax.", "success");
            Post(new { type = "paired", name });
        });
        net.Start();
        if (net.ListenError != null) Toast(net.ListenError, "error");

        router = new InputRouter(net) { Enabled = Settings.ShareInput };
        router.ActiveChanged += id => BeginInvoke(async () =>
        {
            activeRemote = id;
            if (id != null && net.Session(id) is { } s)
                OsdOverlay.Show($"Styr {s.Name}", "Ctrl+Shift+Alt + valfri tangent tar tillbaka musen");
            await PushState();
        });
        router.Start();

        // Profiler byter ingång via klienten när den här datorn inte når skärmen.
        Actions.SetInputAnyRoute = (dev, code) => SetInputRouted(dev, code).GetAwaiter().GetResult();

        // Delat urklipp: kopiera här → finns på de andra datorerna, och tvärtom.
        clipboard = new Net.ClipboardWatcher();
        clipboard.TextChanged += text =>
        {
            if (!Settings.ShareClipboard) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            foreach (var s in net.Sessions()) s.Post(Net.MsgType.Clipboard, bytes);
        };
        net.SessionStarted += s => s.Post(Net.MsgType.ClipboardPolicy, new[] { (byte)(Settings.ShareClipboard ? 1 : 0) });
        net.ClipboardReceived += (from, text) => BeginInvoke(() =>
        {
            if (!Settings.ShareClipboard) return;
            clipboard.SetText(text);
            // Vidare till övriga anslutna datorer.
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            foreach (var s in net.Sessions().Where(s => s.Id != from.Id)) s.Post(Net.MsgType.Clipboard, bytes);
        });
    }

    Net.ClipboardWatcher? clipboard;

    void BroadcastClipboardPolicy()
    {
        if (net == null) return;
        foreach (var s in net.Sessions()) s.Post(Net.MsgType.ClipboardPolicy, new[] { (byte)(Settings.ShareClipboard ? 1 : 0) });
    }

    void StopKvm()
    {
        router?.Dispose();
        clipboard?.Dispose();
        net?.Dispose();
    }

    // ---------------- Vem visas på skärmen? ----------------

    /// <summary>
    /// Vilken dator skärmen visar just nu: null = den här, ett klient-id, eller "other" (en annan
    /// källa som ingen ansluten klient har). Bygger på ingången (DDC/CI), vad klienterna
    /// rapporterar och vad användaren valt.
    /// </summary>
    /// <param name="dev">Skärmens device path här.</param>
    /// <param name="t">Skärmen som Windows ser den här (null = den här datorn ser den inte alls just nu).</param>
    (string? owner, string how) ResolveOwner(string dev, DisplayTarget? t)
    {
        if (PositionMemory.IsClientScreen(dev)) return (PositionMemory.ClientOf(dev), "client-screen");
        var manual = Settings.MonitorOwner(dev);
        if (manual == "server") return t == null ? ("none", "manual") : (null, "manual");
        if (manual != null) return (manual, "manual");

        var seen = Seen(dev, t);
        var reporting = seen.Select(x => x.s).ToList();
        byte? cur = Settings.EffectiveInput(dev,
            t is { Active: true } && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c ? (byte)c : null);

        // Den här datorn når inte skärmen – fråga klienterna, de ser den när den visar dem.
        if (cur == null)
        {
            foreach (var (s, m) in seen)
            {
                if (m.Input is not int ci) continue;
                byte code = (byte)ci;
                bool? home = Settings.HomeInput(dev) is byte h ? h == code : t != null ? Settings.IsHome(t, code) : null;
                if (home == true) return t is { Active: true } ? (null, "client") : ("none", "client");
                if (home == false)
                {
                    var mapped = Settings.InputOwner(dev, code);
                    if (mapped != null) return (mapped, "input");
                    Settings.SetInputOwner(dev, code, s.Id); // klienten ser skärmen på en ingång som inte är vår
                    return (s.Id, "input");
                }
            }
        }

        if (cur is byte cc)
        {
            bool? home = t != null ? Settings.IsHome(t, cc) : Settings.HomeInput(dev) is byte hh ? hh == cc : null;
            if (home == false)
            {
                var mapped = Settings.InputOwner(dev, cc);
                if (mapped != null) return (mapped, "input");
                if (reporting.Count == 1)
                {
                    // Bara en annan dator sitter på skärmen – då måste det vara den. Kom ihåg ingången.
                    Settings.SetInputOwner(dev, cc, reporting[0].Id);
                    return (reporting[0].Id, "input");
                }
                return ("other", "input");
            }
            if (home == true) return (null, "input");
        }
        if (t == null) return reporting.Count > 0 ? (reporting[0].Id, "guess") : ("none", "off");
        if (!t.Active && Smart.ExcludedReason(t.DevicePath) != null && reporting.Count == 1) return (reporting[0].Id, "guess");
        return (null, t.Active ? "default" : "off");
    }

    static string KeyOf(string dev, DisplayTarget? t) => t?.EdidKey is { Length: > 0 } k ? k : PositionMemory.EdidOf(dev) ?? "";

    static string Ident(ClientMonitor m) => m.Key + "|" + m.Name;
    static bool SameName(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    string NameOfMonitor(string dev, DisplayTarget? t) =>
        t?.Name ?? PositionMemory.Items().FirstOrDefault(i => string.Equals(i.Dev, dev, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

    /// <summary>
    /// Anslutna datorer som ser skärmen, och vilken av deras skärmar det är. Matchar i tur och ordning:
    /// ditt val, samma EDID-kod, samma tillverkare + modellnamn, samma modellnamn. (Många skärmar har
    /// olika produktkod på olika ingångar, t.ex. BenQ på DVI och HDMI.)
    /// </summary>
    List<(ClientSession s, ClientMonitor m)> Seen(string dev, DisplayTarget? t)
    {
        var list = new List<(ClientSession, ClientMonitor)>();
        if (net == null) return list;
        if (PositionMemory.IsClientScreen(dev))
        {
            // En skärm som bara sitter på klienten – den är "sin egen" skärm hos den datorn.
            var cs = net.Session(PositionMemory.ClientOf(dev)!);
            var own = cs?.Info?.Monitors.FirstOrDefault(m => dev.EndsWith(":" + Ident(m), StringComparison.Ordinal));
            if (cs != null && own != null) list.Add((cs, own));
            return list;
        }
        string key = KeyOf(dev, t), name = NameOfMonitor(dev, t);
        foreach (var s in net.Sessions())
        {
            var mons = s.Info?.Monitors;
            if (mons == null || mons.Count == 0) continue;
            ClientMonitor? m = Settings.ClientMonitorFor(dev, s.Id) is string chosen ? mons.FirstOrDefault(x => Ident(x) == chosen) : null;
            if (m == null && key != "") m = mons.FirstOrDefault(x => x.Key == key);
            if (m == null && key.Length >= 4 && name != "")
                m = mons.FirstOrDefault(x => x.Key.StartsWith(key[..4], StringComparison.OrdinalIgnoreCase) && SameName(x.Name, name));
            if (m == null && name != "" && mons.Count(x => SameName(x.Name, name)) == 1)
                m = mons.First(x => SameName(x.Name, name));
            if (m != null) list.Add((s, m));
        }
        return list;
    }

    /// <summary>En ansluten dator som ser skärmen och kan läsa dess ingång (null om ingen).</summary>
    (string name, byte? current)? ClientView(string dev, DisplayTarget? t)
    {
        foreach (var (s, m) in Seen(dev, t))
            if (m.Input is int i) return (s.Name, (byte)i);
        return null;
    }

    // ---------------- Byta ingång via rätt dator ----------------

    /// <summary>
    /// Byter ingång härifrån om det går, annars via en ansluten dator som når skärmen.
    /// Körs i bakgrunden. Returnerar null om det gick, annars ett felmeddelande.
    /// </summary>
    async Task<string?> SetInputRouted(string dev, byte code)
    {
        var t = targets.FirstOrDefault(x => string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
        string? directError = null;
        if (t is { Active: true })
        {
            try { await Task.Run(() => Ddc.SetInput(t.GdiName, t.PhysIndex, code)); Commanded(dev, code); return null; }
            catch (Exception ex) { directError = ex.Message; }
        }
        var seen = Seen(dev, t);
        if (seen.Count == 0)
            return directError ?? "Den här datorn når inte skärmen, och ingen ansluten dator ser den.";
        string? last = null;
        // Datorer som kan läsa skärmens ingång når den säkert – prova dem först.
        foreach (var (s, m) in seen.OrderByDescending(x => x.m.Input != null))
        {
            // Klientens egen EDID-kod för skärmen (kan skilja sig från vår).
            var r = await s.SetInput(m.Key, code, TimeSpan.FromSeconds(6));
            if (r.Ok) { Commanded(dev, code); return null; }
            last = $"via {s.Name}: {r.Error}";
        }
        return last;
    }

    /// <summary>
    /// Ingången byttes. Kom ihåg den, och kontrollera efter en stund om skärmen rapporterar rätt:
    /// svarar den fortfarande med den här datorns ingång fast den bytt bort, går det inte att lita på
    /// skärmens svar – då används senaste bytet i stället (t.ex. VG252Q via DisplayPort).
    /// </summary>
    void Commanded(string dev, byte code)
    {
        Settings.SetLastInput(dev, code);
        var home = Settings.HomeInput(dev);
        if (home == null || home == code) return; // går inte att avgöra när vi byter till vår egen ingång
        _ = Task.Run(async () =>
        {
            await Task.Delay(3500);
            var t = DisplayConfig.GetTargets().FirstOrDefault(x => x.Active && string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
            if (t == null) return; // skärmen försvann ur Windows – inget att jämföra med
            var read = Ddc.ReadStatus(t.GdiName, t.PhysIndex).Input;
            if (read == home) Settings.SetInputUnreliable(dev, true);
            else if (read == code) Settings.SetInputUnreliable(dev, false);
            BeginInvoke(async () => await PushState());
        });
    }

    /// <summary>"Alla skärmar hit": byt tillbaka alla skärmar till den här datorns ingång och utöka.</summary>
    async Task AllHome()
    {
        var plan = PositionMemory.Items()
            .Select(i => (dev: i.Dev, name: targets.FirstOrDefault(t => t.DevicePath == i.Dev)?.Label ?? i.Name, home: Settings.HomeInput(i.Dev)))
            .Where(x => x.home != null)
            .Where(x =>
            {
                // Hoppa över skärmar som redan visar den här datorn.
                var t = targets.FirstOrDefault(t => t.DevicePath == x.dev);
                return !(t is { Active: true } && inputs.TryGetValue(t.Id, out var info) && info.Current == x.home);
            })
            .ToList();
        await DisplayOp("Hämtar tillbaka skärmarna", () =>
        {
            var errors = new List<string>();
            foreach (var (dev, name, home) in plan)
                if (SetInputRouted(dev, home!.Value).GetAwaiter().GetResult() is string err) errors.Add($"{name}: {err}");
            if (plan.Count > 0) Thread.Sleep(3000); // låt skärmarna dyka upp i Windows igen
            Settings.ExtendIntent = true;
            var msg = Smart.Extend(userInitiated: true) ?? "Alla skärmar visar den här datorn.";
            if (errors.Count > 0) throw new InvalidOperationException(msg + "\nKunde inte byta: " + string.Join("; ", errors));
            return msg;
        });
    }

    /// <summary>Klientskärmar i minnet som i själva verket är en delad skärm (döljs i layouten).</summary>
    readonly HashSet<string> sharedClientScreens = new(StringComparer.Ordinal);

    /// <summary>
    /// Skärmar som bara sitter på en ansluten dator (t.ex. laptopens inbyggda skärm) får en plats i
    /// layouten, så att man kan placera dem och dra pekaren dit.
    /// </summary>
    void EnsureClientScreens()
    {
        if (net == null || targets.Count == 0) return;
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var realDevs = PositionMemory.Items().Select(i => i.Dev).Where(d => !PositionMemory.IsClientScreen(d))
            .Concat(targets.Select(t => t.Id)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dev in realDevs)
            foreach (var (s, m) in Seen(dev, targets.FirstOrDefault(x => string.Equals(x.Id, dev, StringComparison.OrdinalIgnoreCase))))
                matched.Add(PositionMemory.ClientScreenId(s.Id, Ident(m)));

        sharedClientScreens.Clear();
        foreach (var id in matched) sharedClientScreens.Add(id);
        foreach (var s in net.Sessions())
            foreach (var m in s.Info?.Monitors ?? new())
            {
                var id = PositionMemory.ClientScreenId(s.Id, Ident(m));
                if (!matched.Contains(id)) PositionMemory.EnsureClientScreen(id, m.Name, m.W, m.H);
            }
    }

    /// <summary>Bygger den gemensamma ytan som kroken använder.</summary>
    void RebuildRegions()
    {
        if (router == null || net == null) return;
        var list = new List<Region>();
        foreach (var p in Placed())
        {
            var t = targets.FirstOrDefault(x => string.Equals(x.Id, p.id, StringComparison.OrdinalIgnoreCase));
            var master = new Rectangle(p.x, p.y, p.w, p.h);
            Rectangle? sr = t is { Active: true, HasGeometry: true } ? new Rectangle(t.X, t.Y, t.Width, t.Height) : null;
            var (owner, _) = ResolveOwner(p.id, t);
            if (owner == null) { if (sr != null) list.Add(new Region(p.id, master, null, sr, null)); continue; }
            if (owner is "other" or "none") continue;
            var monitors = net.Session(owner)?.Info?.Monitors;
            var cm = Seen(p.id, t).FirstOrDefault(x => x.s.Id == owner).m ?? (monitors?.Count == 1 ? monitors[0] : null);
            if (cm == null) continue;
            list.Add(new Region(p.id, master, owner, sr, new Rectangle(cm.X, cm.Y, cm.W, cm.H)));
        }
        lastRegions = list.ToArray();
        router.Update(lastRegions);
    }

    // ---------------- Tillstånd till gränssnittet ----------------

    object? OwnerState(string dev, DisplayTarget? t)
    {
        if (net == null) return null;
        var (owner, how) = ResolveOwner(dev, t);
        string kind = owner == null ? "server" : owner is "other" or "none" ? owner : "client";
        string? name = kind == "client" ? (net.Session(owner!)?.Name ?? net.PairedClients().FirstOrDefault(p => p.Id == owner)?.Name ?? "Okänd dator") : null;
        bool connected = kind != "client" || net.Session(owner!) != null;
        bool reachable = lastRegions.Any(r => r.Dev == dev && r.Owner == owner && owner != null);
        // Klientens skärmar – så att man kan välja vilken det är om den inte hittas automatiskt.
        var session = kind == "client" ? net.Session(owner!) : null;
        var match = session == null ? null : Seen(dev, t).FirstOrDefault(x => x.s.Id == session.Id).m;
        return new
        {
            kind,
            id = kind == "client" ? owner : null,
            name = kind switch { "server" => "Den här datorn", "other" => "Annan källa", "none" => "Okänt", _ => name },
            how,
            manual = Settings.MonitorOwner(dev) ?? "auto",
            connected,
            reachable, // mus och tangentbord kan gå hit
            clientMonitors = session?.Info?.Monitors.Select(m => new { ident = Ident(m), label = $"{m.Name} ({m.W}×{m.H})" }).ToList(),
            clientMonitor = match == null ? null : Ident(match),
            clientMonitorChosen = session != null && Settings.ClientMonitorFor(dev, session.Id) != null,
        };
    }

    object ComputersOn(string dev, DisplayTarget? t)
    {
        var list = new List<object>();
        if (t != null)
            list.Add(new { id = (string?)null, name = "Den här datorn", input = Settings.HomeInput(dev) is byte h ? Ddc.InputName(h) : null });
        foreach (var (s, _) in Seen(dev, t))
        {
            var code = InputOf(dev, s.Id);
            list.Add(new { id = (string?)s.Id, name = s.Name, input = code is byte c ? Ddc.InputName(c) : null });
        }
        return list;
    }

    static byte? InputOf(string dev, string clientId)
    {
        for (int c = 1; c < 0x20; c++)
            if (Settings.InputOwner(dev, (byte)c) == clientId) return (byte)c;
        return null;
    }

    string? ComputerOnInput(string dev, byte code)
    {
        if (Settings.HomeInput(dev) == code) return "Den här datorn";
        var owner = Settings.InputOwner(dev, code);
        if (owner == null || net == null) return null;
        return net.Session(owner)?.Name ?? net.PairedClients().FirstOrDefault(p => p.Id == owner)?.Name;
    }

    object ComputersState()
    {
        if (net == null) return Array.Empty<object>();
        var sessions = net.Sessions();
        return net.PairedClients().Select(p =>
        {
            var s = sessions.FirstOrDefault(x => x.Id == p.Id);
            var shownOn = Placed()
                .Where(x => ResolveOwner(x.id, targets.FirstOrDefault(t => t.Id == x.id)).owner == p.Id)
                .Select(x => targets.FirstOrDefault(t => t.Id == x.id)?.Label ?? x.name).ToList();
            return new
            {
                id = p.Id,
                name = s?.Name ?? p.Name,
                connected = s != null,
                address = s?.Address,
                since = s?.Since.ToString("HH:mm"),
                monitors = s?.Info?.Monitors.Select(m => m.Name).ToList() ?? new List<string>(),
                shownOn,
                active = activeRemote == p.Id,
            };
        }).ToList();
    }

    // ---------------- Brandvägg ----------------

    (bool allowed, bool publicNet, DateTime at) firewall = (true, false, DateTime.MinValue);

    /// <summary>Släpper Windows brandvägg in anslutningar till appen på det nätverk som används nu?</summary>
    (bool allowed, bool publicNet) FirewallState()
    {
        if (DateTime.Now - firewall.at < TimeSpan.FromSeconds(15)) return (firewall.allowed, firewall.publicNet);
        bool allowed = false, blocked = false, publicNet = false;
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int current = policy.CurrentProfileTypes;
            publicNet = (current & 4) != 0;
            bool firewallOn = true;
            try
            {
                firewallOn = false;
                foreach (int p in new[] { 1, 2, 4 })
                    if ((current & p) != 0 && (bool)policy.FirewallEnabled[p]) firewallOn = true;
            }
            catch { firewallOn = true; }
            if (!firewallOn) allowed = true;
            string exe = Environment.ProcessPath ?? "";
            foreach (dynamic r in policy.Rules)
            {
                try
                {
                    if (!(bool)r.Enabled || (int)r.Direction != 1 || ((int)r.Profiles & current) == 0) continue;
                    string? app = r.ApplicationName;
                    bool forApp = app != null && string.Equals(Path.GetFullPath(Environment.ExpandEnvironmentVariables(app)), exe, StringComparison.OrdinalIgnoreCase);
                    if (!forApp) continue;
                    if ((int)r.Action == 0) blocked = true; else allowed = true;
                }
                catch { }
            }
        }
        catch { allowed = true; } // kan inte läsa brandväggen – varna inte i onödan
        firewall = (allowed && !blocked, publicNet, DateTime.Now);
        return (firewall.allowed, firewall.publicNet);
    }

    /// <summary>Lägger till brandväggsregler för appen. Windows frågar användaren (UAC) först.</summary>
    async Task AllowInFirewall()
    {
        string exe = (Environment.ProcessPath ?? "").Replace("'", "''");
        string script =
            "Remove-NetFirewallRule -DisplayName 'Skarmstyrning TCP 47811','Skarmstyrning UDP 47812' -ErrorAction SilentlyContinue; " +
            "Get-NetFirewallApplicationFilter -Program '" + exe + "' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object Action -eq 'Block' | Remove-NetFirewallRule; " +
            "New-NetFirewallRule -DisplayName 'Skarmstyrning TCP 47811' -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + Net.Proto.Port + " -Program '" + exe + "' -Profile Any | Out-Null; " +
            "New-NetFirewallRule -DisplayName 'Skarmstyrning UDP 47812' -Direction Inbound -Action Allow -Protocol UDP -LocalPort " + Net.Proto.DiscoveryPort + " -Program '" + exe + "' -Profile Any | Out-Null";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        try
        {
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Toast("Brandväggen ändrades inte (frågan avbröts).", "info");
            return;
        }
        firewall.at = DateTime.MinValue;
        Toast(FirewallState().allowed ? "Brandväggen släpper nu in andra datorer till Skärmstyrning." : "Det gick inte att ändra brandväggen.",
              FirewallState().allowed ? "success" : "error");
        await PushState();
    }

    object? NetState()
    {
        if (net == null) return null;
        var (fwAllowed, publicNet) = FirewallState();
        int left = net.PairCode != null ? Math.Max(0, (int)(net.PairUntil - DateTime.Now).TotalSeconds) : 0;
        return new
        {
            error = net.ListenError,
            port = Net.Proto.Port,
            name = net.ServerName,
            addresses = NetServer.LocalAddresses(),
            pairCode = left > 0 ? net.PairCode : null,
            pairSecondsLeft = left,
            firewallBlocked = !fwAllowed,
            publicNetwork = publicNet,
        };
    }

    // ---------------- Kommandon ----------------

    async Task<(bool handled, object? result)> KvmDispatch(string cmd, JsonNode? a)
    {
        string Str(string k) => a?[k]?.GetValue<string>() ?? "";
        if (net == null) return (false, null); // nätverksdelen är inte igång (t.ex. vid skärmdump för felsökning)
        switch (cmd)
        {
            case "startPairing":
                net.StartPairing();
                await PushState();
                return (true, null);

            case "stopPairing":
                net.StopPairing();
                await PushState();
                return (true, null);

            case "removeComputer":
                net.Remove(Str("id"));
                Toast("Datorn är borttagen. Den behöver parkopplas igen för att ansluta.", "success");
                await PushState();
                return (true, null);

            case "renameComputer":
                if (Str("name").Trim() != "") net.Rename(Str("id"), Str("name").Trim());
                await PushState();
                return (true, null);

            case "setOwner":
            {
                string dev = Str("id");
                var t = targets.FirstOrDefault(x => x.Id == dev);
                string owner = Str("owner");
                Settings.SetMonitorOwner(dev, owner == "auto" ? null : owner);
                if (t == null) { await PushState(); return (true, null); }
                // Om användaren väljer en dator för skärmen och vi vet ingången: kom ihåg kopplingen.
                if (owner != "auto" && owner != "server" && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c
                    && Settings.IsHome(t, (byte)c) == false)
                    Settings.SetInputOwner(t.DevicePath, (byte)c, owner);
                await PushState();
                return (true, null);
            }

            case "setInputOwner":
            {
                string dev = Str("id");
                byte code = (byte)(a?["code"]?.GetValue<int>() ?? 0);
                string owner = Str("owner");
                if (owner == "server") Settings.SetHomeInput(dev, code);
                else Settings.SetInputOwner(dev, code, owner == "" ? null : owner);
                await PushState();
                return (true, null);
            }

            case "allHome":
                await AllHome();
                return (true, null);

            case "mapClientMonitor":
            {
                // Användaren talar om vilken av klientens skärmar det här är ("" = automatiskt).
                string ident = Str("ident");
                Settings.SetClientMonitorFor(Str("id"), Str("client"), ident == "" ? null : ident);
                await PushState();
                return (true, null);
            }

            case "allowFirewall":
                await AllowInFirewall();
                return (true, null);

            case "setShareClipboard":
                Settings.ShareClipboard = a?["value"]?.GetValue<bool>() ?? true;
                BroadcastClipboardPolicy();
                await PushState();
                return (true, null);

            case "setShareInput":
                Settings.ShareInput = a?["value"]?.GetValue<bool>() ?? true;
                if (router != null) router.Enabled = Settings.ShareInput;
                await PushState();
                return (true, null);
        }
        return (false, null);
    }
}
