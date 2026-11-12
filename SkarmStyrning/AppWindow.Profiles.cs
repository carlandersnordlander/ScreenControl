using System.Text.Json.Nodes;

namespace SkarmStyrning;

/// <summary>
/// Profiler: för varje skärm vilken dator den ska visa. Ingången räknas ut via kopplingstabellen,
/// och den här datorn släpper skärmar som visar en annan dator – så kan "bild" och "ingång" aldrig
/// säga emot varandra.
/// </summary>
public sealed partial class AppWindow
{
    string? ClientName(string id) =>
        net?.Session(id)?.Name ?? net?.PairedClients().FirstOrDefault(c => c.Id == id)?.Name;

    /// <summary>Andra datorer som profilen visar på någon skärm, och om de är anslutna.</summary>
    List<(string id, string name, bool connected)> RequiredClients(Profile p)
    {
        var list = new List<(string id, string name, bool connected)>();
        if (net == null || p.Spec == null) return list;
        foreach (var id in p.Spec.Clients())
            list.Add((id, ClientName(id) ?? "en annan dator", net.Session(id) != null));
        return list;
    }

    /// <summary>Varför profilen inte kan användas just nu (null = den kan användas).</summary>
    string? BlockedReason(Profile p)
    {
        var missing = RequiredClients(p).Where(c => !c.connected).Select(c => c.name).ToList();
        if (missing.Count == 0) return null;
        string who = string.Join(" och ", missing);
        return $"Profilen \"{p.Name}\" byter skärmar till {who}, men {(missing.Count > 1 ? "de är" : "den är")} inte ansluten. " +
               $"Starta klienten på {who} först – annars går det inte att byta tillbaka härifrån.";
    }

    string ProfileDescription(Profile p) => p.Spec?.Describe(ClientName) ?? p.Layout?.Description ?? "";

    // ---------------- Omvandling av äldre profiler ----------------

    /// <summary>
    /// Äldre profiler sparade råa ingångskoder och "bild" separat. Gör om dem till "vilken dator
    /// visas" via kopplingstabellen. Ingångar som inte går att koppla till en dator blir "Ändra inte".
    /// </summary>
    void MigrateProfiles()
    {
        bool changed = false;
        foreach (var p in profiles)
        {
            if (p.Spec != null && p.Spec.Monitors.All(m => m.Show != null) && p.Inputs == null) continue;
            var spec = p.Spec ?? (p.Layout != null ? DisplayConfig.SpecFromSnapshot(p.Layout) : null);
            bool layoutKnown = spec != null;
            spec ??= new ProfileLayout();
            // Se till att alla kända skärmar finns med.
            foreach (var item in PositionMemory.Items().Where(i => !PositionMemory.IsClientScreen(i.Dev)))
                if (!spec.Monitors.Any(m => string.Equals(m.Dev, item.Dev, StringComparison.OrdinalIgnoreCase)))
                    spec.Monitors.Add(new ProfileMonitor { Dev = item.Dev, Name = item.Name, Mode = layoutKnown ? "off" : "keep" });

            foreach (var m in spec.Monitors.Where(m => m.Show == null))
            {
                byte? code = p.Inputs?.FirstOrDefault(kv => string.Equals(kv.Key, m.Dev, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv ? kv.Value : null;
                string? who = code is byte c ? ComputerForInput(m.Dev, null, c) : null;
                if (who != null && who != "server") { m.Show = who; m.Mode = "off"; }
                else if (!layoutKnown) { m.Show = who == "server" ? "server" : "keep"; m.Mode = "keep"; }
                else m.Show = m.Mode == "off" ? "off" : "server";
            }
            p.Spec = spec;
            p.Layout = null;
            p.Inputs = null;
            p.InputLabels = null;
            changed = true;
        }
        if (changed) ProfileStore.Save(profiles);
    }

    // ---------------- Spara nuvarande ----------------

    /// <summary>
    /// Profil av hur det är just nu: den här datorns skärmlayout, och för varje skärm vilken dator
    /// den visar enligt appens sanning (inte vad skärmen själv påstår).
    /// </summary>
    ProfileLayout SpecFromNow(ProfileLayout spec)
    {
        foreach (var p in Placed().Where(p => !PositionMemory.IsClientScreen(p.id)))
        {
            var t = targets.FirstOrDefault(x => x.Id == p.id);
            var m = spec.Monitors.FirstOrDefault(x => string.Equals(x.Dev, p.id, StringComparison.OrdinalIgnoreCase));
            if (m == null) spec.Monitors.Add(m = new ProfileMonitor { Dev = p.id, Name = t?.Label ?? p.name, Mode = "off" });
            var (owner, _) = ResolveOwner(p.id, t);
            if (owner is not (null or "other" or "none")) { m.Show = owner; m.Mode = "off"; }
            else m.Show = m.Mode == "off" ? "off" : "server";
        }
        return spec;
    }

    /// <summary>
    /// Vad som skiljer sig mellan profilen och hur det är just nu (tom = stämmer). Används för den
    /// aktiva profilen så att man kan spara ändringar i den eller som en ny profil.
    /// </summary>
    List<string> ProfileChanges(Profile p)
    {
        var changes = new List<string>();
        if (p.Spec == null) return changes;
        ProfileLayout now;
        try { now = SpecFromNow(DisplayConfig.SpecFromCurrent()); } catch { return changes; }
        string Who(string? show) => show switch
        {
            "server" => "den här datorn",
            "off" or null => "av",
            var id => ClientName(id) ?? "en annan dator",
        };
        foreach (var pm in p.Spec.Monitors.Where(m => m.Show is not (null or "keep")))
        {
            var nm = now.Monitors.FirstOrDefault(m => string.Equals(m.Dev, pm.Dev, StringComparison.OrdinalIgnoreCase));
            if (nm == null) continue;
            string name = nm.Name ?? pm.Name ?? "Skärm";
            if (pm.Show == "off")
            {
                if (nm.Show == "server") changes.Add($"{name} används av den här datorn");
                continue;
            }
            if (pm.Show != nm.Show) { changes.Add($"{name} visar {Who(nm.Show)} (profilen: {Who(pm.Show)})"); continue; }
            if (pm.Show != "server") continue;
            if (pm.Mode != nm.Mode || (pm.Mode == "clone" && !string.Equals(pm.CloneOf, nm.CloneOf, StringComparison.OrdinalIgnoreCase)))
                changes.Add($"{name}: {(nm.Mode == "clone" ? "duplicerad" : "utökad")} i stället för {(pm.Mode == "clone" ? "duplicerad" : "utökad")}");
            else if (pm.Mode == "extend" && (pm.Width, pm.Height, pm.Hz) != (nm.Width, nm.Height, nm.Hz))
                changes.Add(nm.Width is int nw ? $"{name}: {nw}×{nm.Height}{(nm.Hz is int h2 ? $" @ {h2} Hz" : "")}" : $"{name}: standardupplösning");
        }
        if (p.Spec.Primary is string prim && now.Primary is string nprim && !string.Equals(prim, nprim, StringComparison.OrdinalIgnoreCase)
            && now.Monitors.Any(m => string.Equals(m.Dev, prim, StringComparison.OrdinalIgnoreCase) && m.Show == "server" && m.Mode == "extend"))
            changes.Add($"Huvudskärm: {now.Monitors.FirstOrDefault(m => string.Equals(m.Dev, nprim, StringComparison.OrdinalIgnoreCase))?.Name ?? "annan"}");
        return changes;
    }

    // ---------------- Redigera ----------------

    object GetProfileForEdit(string name)
    {
        var p = profiles.FirstOrDefault(x => x.Name == name) ?? throw new InvalidOperationException("Profilen finns inte.");
        var spec = p.Spec ?? new ProfileLayout();
        var placed = Placed().Where(x => !PositionMemory.IsClientScreen(x.id)).ToList();
        var clients = net?.PairedClients() ?? new List<PairedClient>();
        var monitors = placed.Select(x =>
        {
            var t = targets.FirstOrDefault(t => t.Id == x.id);
            var pm = spec.Monitors.FirstOrDefault(m => string.Equals(m.Dev, x.id, StringComparison.OrdinalIgnoreCase));
            var pref = PositionMemory.Preferred(x.id);
            List<VideoMode> modes = new();
            if (t is { Active: true, GdiName: not null }) try { modes = DisplayModes.List(t.GdiName); } catch { }

            string value = pm?.Show switch
            {
                null => "off",
                "server" => pm.Mode == "clone" && pm.CloneOf != null ? "clone:" + pm.CloneOf : "server",
                var s => s,
            };
            var options = new List<object> { new { value = "server", label = "Den här datorn", needsMapping = false } };
            foreach (var o in placed.Where(o => o.id != x.id))
                options.Add(new { value = "clone:" + o.id, label = $"Den här datorn – samma bild som {targets.FirstOrDefault(t => t.Id == o.id)?.Label ?? o.name}", needsMapping = false });
            foreach (var c in clients)
            {
                var code = InputForComputer(x.id, c.Id);
                options.Add(new { value = c.Id, label = $"{c.Name} – {(code is byte b ? Ddc.InputName(b) : "ingång okänd")}", needsMapping = code == null });
            }
            // Profilens dator finns inte bland de parade (t.ex. borttagen) – visa den ändå så att den inte skrivs över.
            if (pm?.Show is string sh && sh is not ("server" or "off" or "keep") && !clients.Any(c => c.Id == sh))
            {
                var code = InputForComputer(x.id, sh);
                options.Add(new { value = sh, label = $"{ClientName(sh) ?? "Annan dator"} – {(code is byte b ? Ddc.InputName(b) : "ingång okänd")}", needsMapping = code == null });
            }
            options.Add(new { value = "off", label = "Av (används inte här)", needsMapping = false });
            options.Add(new { value = "keep", label = "Ändra inte", needsMapping = false });

            return new
            {
                dev = x.id,
                name = t?.Label ?? x.name,
                number = x.number,
                connected = t != null,
                value,
                options,
                width = pm?.Width,
                height = pm?.Height,
                hz = pm?.Hz,
                preferred = pref == null ? null : $"{pref.Mode.Width}×{pref.Mode.Height} @ {pref.Mode.Hz} Hz",
                resolutions = modes.GroupBy(m => (m.Width, m.Height))
                    .Select(g => new { w = g.Key.Width, h = g.Key.Height, hz = g.Select(v => v.Hz).Distinct().OrderByDescending(v => v).ToList() })
                    .ToList(),
            };
        }).ToList();
        return new { name = p.Name, primary = spec.Primary, monitors };
    }

    async Task SaveProfileEdit(JsonNode a)
    {
        string original = a["original"]?.GetValue<string>() ?? "";
        string name = (a["name"]?.GetValue<string>() ?? "").Trim();
        var p = profiles.FirstOrDefault(x => x.Name == original) ?? throw new InvalidOperationException("Profilen finns inte.");
        if (name == "") throw new InvalidOperationException("Ge profilen ett namn.");
        if (name != original && profiles.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x != p))
            throw new InvalidOperationException($"Det finns redan en profil som heter \"{name}\".");

        var placed = Placed();
        string NameOf(string dev) => targets.FirstOrDefault(t => t.Id == dev)?.Label ?? placed.FirstOrDefault(x => x.id == dev).name ?? "Skärm";

        var spec = new ProfileLayout { Primary = a["primary"]?.GetValue<string>() };
        foreach (var m in a["monitors"] as JsonArray ?? new())
        {
            if (m == null) continue;
            string dev = m["dev"]!.GetValue<string>();
            string value = m["value"]?.GetValue<string>() ?? "keep";
            var pm = new ProfileMonitor { Dev = dev, Name = NameOf(dev) };
            if (value == "server")
                (pm.Show, pm.Mode, pm.Width, pm.Height, pm.Hz) = ("server", "extend", m["width"]?.GetValue<int?>(), m["height"]?.GetValue<int?>(), m["hz"]?.GetValue<int?>());
            else if (value.StartsWith("clone:")) (pm.Show, pm.Mode, pm.CloneOf) = ("server", "clone", value[6..]);
            else if (value is "off" or "keep") (pm.Show, pm.Mode) = (value, value);
            else (pm.Show, pm.Mode) = (value, "off"); // en annan dator
            spec.Monitors.Add(pm);
        }
        bool desktopHasScreen = spec.Monitors.Any(m => (m.Show == "server" && m.Mode is "extend" or "clone") || m.Show == "keep");
        if (!desktopHasScreen) throw new InvalidOperationException("Den här datorn behöver minst en skärm i profilen (eller \"Ändra inte\" på någon).");
        if (spec.Primary == null || spec.Monitors.FirstOrDefault(m => m.Dev == spec.Primary) is not { Show: "server", Mode: "extend" })
            spec.Primary = spec.Monitors.FirstOrDefault(m => m.Show == "server" && m.Mode == "extend")?.Dev;

        // Kolla att Windows godtar layouten (utan att verkställa) och att kopplingarna finns.
        var warnings = new List<string>();
        foreach (var m in spec.Monitors.Where(m => m.Show is not (null or "server" or "off" or "keep")))
            if (InputForComputer(m.Dev, m.Show!) == null)
                warnings.Add($"{m.Name}: ange vilken ingång {ClientName(m.Show!) ?? "datorn"} sitter på (Kopplingar)");
        warnings.AddRange(await Task.Run(() =>
        {
            DisplayConfig.ValidateLog = new();
            try { return Actions.DesktopLayout(spec) is { } d ? Actions.ApplySpec(d, validateOnly: true) : new List<string>(); }
            catch (Exception ex) { return new List<string> { ex.Message }; }
            finally { DisplayConfig.ValidateLog = null; }
        }));

        if (Settings.LastProfile == original) Settings.LastProfile = name;
        p.Name = name;
        p.Spec = spec;
        p.Layout = null;
        p.Inputs = null;
        p.InputLabels = null;
        ProfileStore.Save(profiles);
        RegisterHotkeys();
        BuildTrayMenu();
        await PushState();

        if (warnings.Count > 0)
            Toast($"Profilen \"{name}\" är sparad, men: {string.Join("; ", warnings)}", "info");
        else
            Toast($"Profilen \"{name}\" är sparad.", "success");

        if (a["apply"]?.GetValue<bool>() == true)
        {
            if (BlockedReason(p) is string blocked) Toast(blocked + " Profilen är sparad men används inte nu.", "error");
            else await Dispatch("applyProfile", new JsonObject { ["name"] = name });
        }
    }

    async Task<(bool handled, object? result)> ProfilesDispatch(string cmd, JsonNode? a)
    {
        switch (cmd)
        {
            case "getProfile":
                return (true, GetProfileForEdit(a?["name"]?.GetValue<string>() ?? ""));
            case "saveProfileEdit":
                await SaveProfileEdit(a ?? new JsonObject());
                return (true, null);
        }
        return (false, null);
    }
}
