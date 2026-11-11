using System.Text.Json.Nodes;

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
    }

    void StopKvm()
    {
        router?.Dispose();
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
        var manual = Settings.MonitorOwner(dev);
        if (manual == "server") return t == null ? ("none", "manual") : (null, "manual");
        if (manual != null) return (manual, "manual");

        var reporting = Reporting(KeyOf(dev, t));
        byte? cur = t is { Active: true } && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c ? (byte)c : null;

        // Den här datorn når inte skärmen – fråga klienterna, de ser den när den visar dem.
        if (cur == null)
        {
            foreach (var s in reporting)
            {
                var m = s.Info!.Monitors.First(x => x.Key == KeyOf(dev, t));
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
            bool? home = Settings.IsHome(t!, cc);
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

    List<ClientSession> Reporting(string key) =>
        net == null || key == "" ? new() :
        net.Sessions().Where(s => s.Info?.Monitors.Any(m => m.Key == key) == true).ToList();

    /// <summary>En ansluten dator som ser skärmen och kan läsa dess ingång (null om ingen).</summary>
    (string name, byte? current)? ClientView(string dev, DisplayTarget? t)
    {
        var key = KeyOf(dev, t);
        foreach (var s in Reporting(key))
        {
            var m = s.Info!.Monitors.First(x => x.Key == key);
            if (m.Input is int i) return (s.Name, (byte)i);
        }
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
            try { await Task.Run(() => Ddc.SetInput(t.GdiName, t.PhysIndex, code)); return null; }
            catch (Exception ex) { directError = ex.Message; }
        }
        string key = KeyOf(dev, t);
        var sessions = Reporting(key);
        if (sessions.Count == 0)
            return directError ?? "Den här datorn når inte skärmen, och ingen ansluten dator ser den.";
        string? last = null;
        // Datorer som kan läsa skärmens ingång når den säkert – prova dem först.
        foreach (var s in sessions.OrderByDescending(s => s.Info?.Monitors.FirstOrDefault(m => m.Key == key)?.Input != null))
        {
            var r = await s.SetInput(key, code, TimeSpan.FromSeconds(6));
            if (r.Ok) return null;
            last = $"via {s.Name}: {r.Error}";
        }
        return last;
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
            var s = net.Session(owner);
            var monitors = s?.Info?.Monitors;
            string key = KeyOf(p.id, t);
            var cm = monitors?.FirstOrDefault(m => m.Key == key) ?? (monitors?.Count == 1 ? monitors[0] : null);
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
        return new
        {
            kind,
            id = kind == "client" ? owner : null,
            name = kind switch { "server" => "Den här datorn", "other" => "Annan källa", "none" => "Okänt", _ => name },
            how,
            manual = Settings.MonitorOwner(dev) ?? "auto",
            connected,
            reachable, // mus och tangentbord kan gå hit
        };
    }

    object ComputersOn(string dev, DisplayTarget? t)
    {
        var list = new List<object>();
        if (t != null)
            list.Add(new { id = (string?)null, name = "Den här datorn", input = Settings.HomeInput(dev) is byte h ? Ddc.InputName(h) : null });
        foreach (var s in Reporting(KeyOf(dev, t)))
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

            case "allowFirewall":
                await AllowInFirewall();
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
