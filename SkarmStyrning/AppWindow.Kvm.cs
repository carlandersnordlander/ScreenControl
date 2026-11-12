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

        // "Utöka" ska inte slå på skärmar som visar en annan dator.
        Smart.ShowsOtherComputer = dev =>
        {
            if (Settings.LastInput(dev) is not byte code) return false;
            return ComputerForInput(dev, null, code) is string who && who != "server";
        };

        // Profiler byter ingångar via kopplingstabellen och den dator skärmen visar just nu.
        Actions.Hooks = new Actions.ProfileHooks
        {
            InputFor = InputForComputer,
            Current = dev => CurrentInput(dev, targets.FirstOrDefault(x => string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase))),
            SetInput = (dev, code) => SetInputRouted(dev, code).GetAwaiter().GetResult(),
            ClientName = id => net?.Session(id)?.Name ?? net?.PairedClients().FirstOrDefault(c => c.Id == id)?.Name,
        };

        // Delat urklipp: kopiera här → finns på de andra datorerna, och tvärtom.
        clipboard = new Net.ClipboardWatcher();
        clipboard.TextChanged += text =>
        {
            if (!Settings.ShareClipboard) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            foreach (var s in net.Sessions()) s.Post(Net.MsgType.Clipboard, bytes);
        };
        net.SessionStarted += s => s.Post(Net.MsgType.ClipboardPolicy, new[] { (byte)(Settings.ShareClipboard ? 1 : 0) });
        net.SessionStarted += s => s.Post(Net.MsgType.KeepAwake, new[] { (byte)(Settings.KeepAwake ? 1 : 0) });
        StartAudio();
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
    readonly AudioHub audioHub = new();

    /// <summary>Ljud från anslutna datorer spelas upp i den här datorns högtalare.</summary>
    void StartAudio()
    {
        if (net == null) return;
        net.SessionStarted += SendAudioConfig;
        net.Disconnected += id => audioHub.Remove(id);
        net.AudioUdp += audioHub.OnUdp;
        net.AudioTcp += (s, p) => audioHub.OnTcp(s.Id, p);
        net.AudioStatus += (s, st) =>
        {
            audioHub.OnStatus(s.Id, st);
            // Kommer inga UDP-paket fram (t.ex. via VPN)? Be klienten skicka via TCP i stället.
            if (st.Sending && st.Transport == "udp")
                _ = Task.Delay(2500).ContinueWith(_ =>
                {
                    if (audioHub.Stream(s.Id) is { UdpSeen: false } && net.Session(s.Id) == s) s.Post(MsgType.AudioUseTcp);
                });
        };
        audioHub.Changed += () => BeginInvoke(async () => await PushState());
    }

    void SendAudioConfig(ClientSession s)
    {
        var cfg = audioHub.Configure(s.Id, Settings.ShareAudio, Settings.AudioVolume(s.Id));
        s.Post(MsgType.AudioConfig, Proto.ToJson(cfg));
    }

    object? AudioState(string clientId)
    {
        var st = audioHub.Stream(clientId);
        return new
        {
            enabled = Settings.ShareAudio,
            sending = st?.Status.Sending == true && DateTime.UtcNow - st.LastPacket < TimeSpan.FromSeconds(3),
            device = st?.Status.Device,
            transport = st == null ? null : st.UdpSeen ? "UDP" : st.Status.Transport?.ToUpperInvariant(),
            volume = Settings.AudioVolume(clientId),
        };
    }

    void BroadcastClipboardPolicy()
    {
        if (net == null) return;
        foreach (var s in net.Sessions()) s.Post(Net.MsgType.ClipboardPolicy, new[] { (byte)(Settings.ShareClipboard ? 1 : 0) });
    }

    void StopKvm()
    {
        router?.Dispose();
        clipboard?.Dispose();
        audioHub.Dispose();
        net?.Dispose();
    }

    // ---------------- Vem visas på skärmen? ----------------

    /// <summary>
    /// Vilken ingång skärmen visar just nu – appens enda sanning:
    /// 1) en avläsning som går att lita på (skärmar som rapporterar rätt – härifrån eller från en
    ///    ansluten dator som ser skärmen),
    /// 2) annars den ingång appen senast bytte till, eller som du angett under "Visar just nu".
    /// </summary>
    byte? CurrentInput(string dev, DisplayTarget? t)
    {
        if (!Settings.IsInputUnreliable(dev))
        {
            if (t is { Active: true } && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c) return (byte)c;
            foreach (var (_, m) in Seen(dev, t)) if (m.Input is int ci) return (byte)ci;
        }
        return Settings.LastInput(dev);
    }

    /// <summary>Vilken dator sitter på den här ingången enligt kopplingarna? "server", ett klient-id eller null (okänt).</summary>
    string? ComputerForInput(string dev, DisplayTarget? t, byte code)
    {
        var home = Settings.HomeInput(dev);
        if (home == code) return "server";
        if (home == null && t != null && Settings.IsHome(t, code) == true) return "server"; // t.ex. DVI-kabel → DVI 1
        return Settings.InputOwner(dev, code);
    }

    /// <summary>Vilken ingång en dator sitter på enligt kopplingarna (null = okänt).</summary>
    byte? InputForComputer(string dev, string computer)
    {
        if (computer == "server") return Settings.HomeInput(dev);
        for (int c = 1; c < 0x20; c++)
            if (Settings.InputOwner(dev, (byte)c) == computer) return (byte)c;
        return null;
    }

    /// <summary>
    /// Vilken dator skärmen visar just nu: null = den här, ett klient-id, "other" (en ingång som inte
    /// är kopplad till någon dator) eller "none" (den här datorn ser inte skärmen och inget annat är känt).
    /// Bygger bara på ingången och kopplingstabellen – appen gissar inte och lär sig inget i det tysta.
    /// </summary>
    /// <param name="dev">Skärmens device path här.</param>
    /// <param name="t">Skärmen som Windows ser den här (null = den här datorn ser den inte alls just nu).</param>
    (string? owner, string how) ResolveOwner(string dev, DisplayTarget? t)
    {
        if (PositionMemory.IsClientScreen(dev)) return (PositionMemory.ClientOf(dev), "client-screen");
        var manual = Settings.MonitorOwner(dev);
        if (manual == "server") return t == null ? ("none", "manual") : (null, "manual");
        if (manual != null) return (manual, "manual");

        if (CurrentInput(dev, t) is byte code)
        {
            var who = ComputerForInput(dev, t, code);
            if (who == "server") return t == null ? ("none", "input") : (null, "input");
            if (who != null) return (who, "input");
            return ("other", "input");
        }
        return t == null ? ("none", "off") : (null, t.Active ? "default" : "off");
    }

    /// <summary>Avläsningar som går att lita på blir den nya sanningen (även när skärmen sedan inte nås).</summary>
    void ObserveInputs()
    {
        foreach (var t in targets.Where(t => t.Active))
            if (!Settings.IsInputUnreliable(t.DevicePath) && inputs.TryGetValue(t.Id, out var info) && info.Current is uint c)
                Settings.ObserveInput(t.DevicePath, (byte)c);
        if (net == null) return;
        foreach (var p in Placed().Where(p => !PositionMemory.IsClientScreen(p.id)))
        {
            if (Settings.IsInputUnreliable(p.id)) continue;
            var t = targets.FirstOrDefault(x => x.Id == p.id);
            if (t is { Active: true } && inputs.TryGetValue(t.Id, out var i2) && i2.Current != null) continue; // här ifrån gäller först
            foreach (var (_, m) in Seen(p.id, t))
                if (m.Input is int ci) { Settings.ObserveInput(p.id, (byte)ci); break; }
        }
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
        var seen = Seen(dev, t);
        // Gå via den dator skärmen visar just nu – dess kabel når skärmen säkert. Visar den en annan
        // dator och vi skickar härifrån kan skärmen säga "ok" utan att byta (t.ex. VG252Q via DisplayPort).
        var (owner, _) = ResolveOwner(dev, t);
        bool viaClientFirst = owner is not (null or "other" or "none") && seen.Any(x => x.s.Id == owner);

        async Task<string?> Direct()
        {
            if (t is not { Active: true }) return "den här datorn ser inte skärmen";
            try { await Task.Run(() => Ddc.SetInput(t.GdiName, t.PhysIndex, code)); return null; }
            catch (Exception ex) { return ex.Message; }
        }
        async Task<string?> ViaClients()
        {
            if (seen.Count == 0) return "ingen ansluten dator ser skärmen";
            string? last = null;
            // Den dator skärmen visar först, sedan de som kan läsa skärmens ingång.
            foreach (var (s, m) in seen.OrderByDescending(x => x.s.Id == owner).ThenByDescending(x => x.m.Input != null))
            {
                var r = await s.SetInput(m.Key, code, TimeSpan.FromSeconds(6)); // klientens egen EDID-kod
                if (r.Ok) return null;
                last = $"via {s.Name}: {r.Error}";
            }
            return last;
        }

        var first = viaClientFirst ? await ViaClients() : await Direct();
        if (first == null) { Commanded(dev, code); return null; }
        var second = viaClientFirst ? await Direct() : await ViaClients();
        if (second == null) { Commanded(dev, code); return null; }
        return t is not { Active: true } && seen.Count == 0
            ? "Den här datorn når inte skärmen, och ingen ansluten dator ser den."
            : first;
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

    /// <summary>
    /// Efter ett ingångsbyte: visar skärmen en annan dator släpper den här datorn den (så att
    /// t.ex. en skärm här och två på laptopen fungerar utan "osynliga" ytor); byts den tillbaka
    /// hit tas den med i den utökade layouten igen. Körs i bakgrunden. Returnerar en kort text.
    /// </summary>
    string FollowInput(string dev, byte code)
    {
        var who = ComputerForInput(dev, null, code);
        if (who == null) return "";
        var ts = DisplayConfig.GetTargets();
        var t = ts.FirstOrDefault(x => string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
        List<IReadOnlyList<DisplayTarget>> GroupsOf(IEnumerable<DisplayTarget> xs) =>
            xs.GroupBy(x => x.GdiName ?? x.Id).Select(g => (IReadOnlyList<DisplayTarget>)g.ToList()).ToList();

        if (who != "server")
        {
            string name = ClientName(who) ?? "den andra datorn";
            Smart.SetExcluded(dev, "visar " + name);
            if (t is not { Active: true }) return "";
            var rest = ts.Where(x => x.Active && x != t).ToList();
            if (rest.Count == 0) return $" Den här datorn har ingen annan skärm, så den behåller {t.Label}.";
            DisplayConfig.ApplyGroups(GroupsOf(rest));
            return $" Den här datorn släppte skärmen – den används av {name}.";
        }

        // Tillbaka hit: vänta tills Windows ser skärmen igen och utöka dit.
        Smart.SetExcluded(dev, null);
        for (int i = 0; i < 10 && t is not { Active: true }; i++)
        {
            if (t != null)
            {
                var active = ts.Where(x => x.Active).ToList();
                DisplayConfig.ApplyGroups(GroupsOf(active).Append(new[] { t }).ToList());
                return " Den här datorn använder skärmen igen.";
            }
            Thread.Sleep(800);
            ts = DisplayConfig.GetTargets();
            t = ts.FirstOrDefault(x => string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
        }
        return "";
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
            // En skärm som bara sitter på klienten (laptopens egen) får aldrig låna en annan skärms plats –
            // är locket stängt finns den helt enkelt inte.
            var cm = Seen(p.id, t).FirstOrDefault(x => x.s.Id == owner).m
                ?? (monitors?.Count == 1 && !PositionMemory.IsClientScreen(p.id) ? monitors[0] : null);
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
            connections = ConnectionsState(dev, t),
            // Klientens eget läge: alla skärmar på samma plats = duplicerat.
            clientMode = session?.Info?.Monitors is { Count: > 1 } cms
                ? (cms.All(c => c.X == cms[0].X && c.Y == cms[0].Y) ? "clone" : "extend") : session != null ? "extend" : null,
            clientScreens = session?.Info?.Monitors.Count ?? 0,
        };
    }

    /// <summary>Kopplingstabellen för en skärm: vilken ingång varje dator sitter på.</summary>
    object? ConnectionsState(string dev, DisplayTarget? t)
    {
        if (PositionMemory.IsClientScreen(dev) || net == null) return null;
        List<byte> codes;
        lock (Smart.KnownCaps) codes = Smart.KnownCaps.TryGetValue(dev, out var k) ? k.Where(c => c is > 0 and < 0x20).ToList() : new List<byte>();
        if (codes.Count == 0) codes = new List<byte> { 0x0F, 0x10, 0x11, 0x12, 0x1B };
        var rows = new List<object> { new { computer = "server", name = "Den här datorn", code = InputForComputer(dev, "server") is byte h ? (int?)h : null } };
        foreach (var c in net.PairedClients())
            rows.Add(new { computer = c.Id, name = c.Name, code = InputForComputer(dev, c.Id) is byte b ? (int?)b : null });
        return new { rows, options = codes.Distinct().Select(c => new { code = (int)c, name = Ddc.InputName(c) }).ToList() };
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
                audio = s != null ? AudioState(p.Id) : null,
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

            case "setClientMode":
            {
                // Utöka/duplicera på den andra datorn (som Win+P där).
                var s = net.Session(Str("client")) ?? throw new InvalidOperationException("Datorn är inte ansluten.");
                bool clone = Str("mode") == "clone";
                s.Post(MsgType.DisplayMode, new[] { (byte)(clone ? 2 : 1) });
                Toast($"{s.Name}: {(clone ? "duplicerar" : "utökar")} skärmarna.", "success");
                return (true, null);
            }

            case "setOwner":
            {
                // "Visar just nu: X" – du talar om vad skärmen visar. Finns en koppling blir dess ingång
                // sanningen; annars sparas det som en upplysning tills ingången byts nästa gång.
                string dev = Str("id");
                string owner = Str("owner");
                if (owner == "auto") Settings.SetMonitorOwner(dev, null);
                else if (InputForComputer(dev, owner) is byte code) { Settings.SetMonitorOwner(dev, null); Settings.ObserveInput(dev, code); }
                else Settings.SetMonitorOwner(dev, owner);
                await PushState();
                return (true, null);
            }

            case "setConnection":
            {
                // Kopplingstabellen: dator X sitter på ingång Y på den här skärmen (code < 0 = ingen/okänd).
                string dev = Str("id");
                string computer = Str("computer");
                int code = a?["code"]?.GetValue<int>() ?? -1;
                Settings.SetConnection(dev, computer, code < 0 ? null : (byte)code);
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

            case "setKeepAwake":
                Settings.KeepAwake = a?["value"]?.GetValue<bool>() ?? false;
                Net.KeepAwake.Enabled = Settings.KeepAwake;
                foreach (var s in net.Sessions()) s.Post(Net.MsgType.KeepAwake, new[] { (byte)(Settings.KeepAwake ? 1 : 0) });
                await PushState();
                return (true, null);

            case "setShareAudio":
                Settings.ShareAudio = a?["value"]?.GetValue<bool>() ?? true;
                foreach (var s in net.Sessions()) SendAudioConfig(s);
                await PushState();
                return (true, null);

            case "setAudioVolume":
            {
                string id = Str("id");
                float v = (float)(a?["value"]?.GetValue<double>() ?? 1.0);
                Settings.SetAudioVolume(id, v);
                audioHub.SetVolume(id, v);
                return (true, null);
            }

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
