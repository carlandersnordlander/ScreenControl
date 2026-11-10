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
        net.Changed += () => BeginInvoke(async () => await PushState());
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
    (string? owner, string how) ResolveOwner(DisplayTarget t)
    {
        var manual = Settings.MonitorOwner(t.DevicePath);
        if (manual == "server") return (null, "manual");
        if (manual != null) return (manual, "manual");

        var reporting = Reporting(t);
        byte? cur = inputs.TryGetValue(t.Id, out var info) && info.Current is uint c ? (byte)c : null;
        if (cur is byte code)
        {
            bool? home = Settings.IsHome(t, code);
            if (home == false)
            {
                var mapped = Settings.InputOwner(t.DevicePath, code);
                if (mapped != null) return (mapped, "input");
                if (reporting.Count == 1)
                {
                    // Bara en annan dator sitter på skärmen – då måste det vara den. Kom ihåg ingången.
                    Settings.SetInputOwner(t.DevicePath, code, reporting[0].Id);
                    return (reporting[0].Id, "input");
                }
                return ("other", "input");
            }
            if (home == true && t.Active) return (null, "input");
        }
        if (!t.Active && Smart.ExcludedReason(t.DevicePath) != null && reporting.Count == 1) return (reporting[0].Id, "guess");
        return (null, t.Active ? "default" : "off");
    }

    List<ClientSession> Reporting(DisplayTarget t) =>
        net == null || t.EdidKey == "" ? new() :
        net.Sessions().Where(s => s.Info?.Monitors.Any(m => m.Key == t.EdidKey) == true).ToList();

    /// <summary>Bygger den gemensamma ytan som kroken använder.</summary>
    void RebuildRegions()
    {
        if (router == null || net == null) return;
        var list = new List<Region>();
        foreach (var p in Placed())
        {
            var t = targets.FirstOrDefault(x => string.Equals(x.Id, p.id, StringComparison.OrdinalIgnoreCase));
            if (t == null) continue;
            var master = new Rectangle(p.x, p.y, p.w, p.h);
            Rectangle? sr = t.Active && t.HasGeometry ? new Rectangle(t.X, t.Y, t.Width, t.Height) : null;
            var (owner, _) = ResolveOwner(t);
            if (owner == null) { if (sr != null) list.Add(new Region(t.Id, master, null, sr, null)); continue; }
            if (owner == "other") continue;
            var s = net.Session(owner);
            var monitors = s?.Info?.Monitors;
            var cm = monitors?.FirstOrDefault(m => m.Key == t.EdidKey) ?? (monitors?.Count == 1 ? monitors[0] : null);
            if (cm == null) continue;
            list.Add(new Region(t.Id, master, owner, sr, new Rectangle(cm.X, cm.Y, cm.W, cm.H)));
        }
        lastRegions = list.ToArray();
        router.Update(lastRegions);
    }

    // ---------------- Tillstånd till gränssnittet ----------------

    object? OwnerState(DisplayTarget? t)
    {
        if (t == null || net == null) return null;
        var (owner, how) = ResolveOwner(t);
        string kind = owner == null ? "server" : owner == "other" ? "other" : "client";
        string? name = kind == "client" ? (net.Session(owner!)?.Name ?? net.PairedClients().FirstOrDefault(p => p.Id == owner)?.Name ?? "Okänd dator") : null;
        bool connected = kind != "client" || net.Session(owner!) != null;
        bool reachable = lastRegions.Any(r => r.Dev == t.Id && r.Owner == owner && owner != null);
        return new
        {
            kind,
            id = kind == "client" ? owner : null,
            name = kind == "server" ? "Den här datorn" : kind == "other" ? "Annan källa" : name,
            how,
            manual = Settings.MonitorOwner(t.DevicePath) ?? "auto",
            connected,
            reachable, // mus och tangentbord kan gå hit
        };
    }

    object ComputersOn(DisplayTarget? t)
    {
        var list = new List<object>();
        if (t == null) return list;
        list.Add(new { id = (string?)null, name = "Den här datorn", input = Settings.HomeInput(t.DevicePath) is byte h ? Ddc.InputName(h) : null });
        foreach (var s in Reporting(t))
        {
            var code = InputOf(t, s.Id);
            list.Add(new { id = (string?)s.Id, name = s.Name, input = code is byte c ? Ddc.InputName(c) : null });
        }
        return list;
    }

    static byte? InputOf(DisplayTarget t, string clientId)
    {
        for (int c = 1; c < 0x20; c++)
            if (Settings.InputOwner(t.DevicePath, (byte)c) == clientId) return (byte)c;
        return null;
    }

    string? ComputerOnInput(DisplayTarget t, byte code)
    {
        if (Settings.HomeInput(t.DevicePath) == code) return "Den här datorn";
        var owner = Settings.InputOwner(t.DevicePath, code);
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
            var shownOn = targets.Where(t => ResolveOwner(t).owner == p.Id).Select(t => t.Label).ToList();
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
        if (net == null) throw new InvalidOperationException("Nätverksdelen är inte igång.");
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
                var t = Find(Str("id"));
                string owner = Str("owner");
                Settings.SetMonitorOwner(t.DevicePath, owner == "auto" ? null : owner);
                // Om användaren väljer en dator för skärmen och vi vet ingången: kom ihåg kopplingen.
                if (owner != "auto" && owner != "server" && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c
                    && Settings.IsHome(t, (byte)c) == false)
                    Settings.SetInputOwner(t.DevicePath, (byte)c, owner);
                await PushState();
                return (true, null);
            }

            case "setInputOwner":
            {
                var t = Find(Str("id"));
                byte code = (byte)(a?["code"]?.GetValue<int>() ?? 0);
                string owner = Str("owner");
                if (owner == "server") Settings.SetHomeInput(t.DevicePath, code);
                else Settings.SetInputOwner(t.DevicePath, code, owner == "" ? null : owner);
                await PushState();
                return (true, null);
            }

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
