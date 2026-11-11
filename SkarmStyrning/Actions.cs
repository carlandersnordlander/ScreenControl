using System.Text.Json;

namespace SkarmStyrning;

/// <summary>
/// En skärm i en profil. Show = vilken dator skärmen ska visa:
/// "server" (den här datorn, se Mode), ett klient-id (den datorn – den här datorn släpper skärmen),
/// "off" (används inte här, ingången rörs inte) eller "keep" (ändra inte).
/// </summary>
public sealed class ProfileMonitor
{
    public string Dev { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Show { get; set; }
    /// <summary>Den här datorns bild: "extend" | "clone" | "off" | "keep" (som nu).</summary>
    public string Mode { get; set; } = "extend";
    public string? CloneOf { get; set; }
    /// <summary>Eget läge i profilen. null = skärmens önskade läge (det bästa den haft / det du låst).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Hz { get; set; }
}

/// <summary>Redigerbar skärmlayout för en profil. Placeringen kommer från huvudlayouten.</summary>
public sealed class ProfileLayout
{
    public List<ProfileMonitor> Monitors { get; set; } = new();
    public string? Primary { get; set; }

    public string Describe() => Describe(_ => null);

    /// <summary>T.ex. "Samsung + VG252Q · BenQ → WPP-W-PF56CXDT".</summary>
    public string Describe(Func<string, string?> clientName)
    {
        var here = Monitors.Where(m => (m.Show ?? "server") == "server" && m.Mode is "extend" or "clone").ToList();
        var away = Monitors.Where(m => m.Show is not (null or "server" or "off" or "keep")).ToList();
        var parts = new List<string>();
        if (here.Count > 0)
            parts.Add((here.Any(m => m.Mode == "clone") ? "dupl. " : "") + string.Join(" + ", here.Select(m => m.Name + (m.Width is int w ? $" {w}×{m.Height}" : ""))));
        foreach (var g in away.GroupBy(m => m.Show!))
            parts.Add(string.Join(" + ", g.Select(m => m.Name)) + " → " + (clientName(g.Key) ?? "annan dator"));
        return parts.Count == 0 ? "Ändrar inget" : string.Join(" · ", parts);
    }

    /// <summary>Andra datorer profilen visar på någon skärm.</summary>
    public IEnumerable<string> Clients() =>
        Monitors.Select(m => m.Show).Where(s => s is not (null or "server" or "off" or "keep")).Distinct()!;
}

public sealed class Profile
{
    public string Name { get; set; } = "";
    /// <summary>Äldre format: en exakt ögonblicksbild av Windows skärmkonfiguration.</summary>
    public LayoutSnapshot? Layout { get; set; }
    /// <summary>Nyare, redigerbart format.</summary>
    public ProfileLayout? Spec { get; set; }
    /// <summary>Skärmens device path → input-kod (VCP 0x60).</summary>
    public Dictionary<string, byte>? Inputs { get; set; }
    public Dictionary<string, string>? InputLabels { get; set; }
    /// <summary>Globalt kortkommando, t.ex. "Ctrl+Shift+Alt+D1". null = aldrig satt, "" = borttaget.</summary>
    public string? Hotkey { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Spec != null) parts.Add("Layout: " + Spec.Describe());
        else if (Layout != null) parts.Add("Layout: " + Layout.Description);
        if (InputLabels is { Count: > 0 }) parts.Add("Input: " + string.Join(", ", InputLabels.Values));
        return parts.Count == 0 ? Name : $"{Name}   —   {string.Join("   |   ", parts)}";
    }
}

public static class ProfileStore
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkarmStyrning");
    static readonly string FilePath = Path.Combine(Dir, "profiler.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static List<Profile> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(FilePath), Opts) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(List<Profile> profiles)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, Opts));
    }
}

/// <summary>Åtgärder som används både av fönstret, ikonen i systemfältet och kommandoraden.</summary>
public static class Actions
{
    static void WithFallback(Action primary, Action fallback)
    {
        try { primary(); }
        catch (Exception first)
        {
            try { fallback(); }
            catch { throw first; }
        }
    }

    public static void ExtendAll()
    {
        var ts = DisplayConfig.GetTargets();
        WithFallback(
            () => DisplayConfig.ApplyGroups(ts.Select(t => (IReadOnlyList<DisplayTarget>)new[] { t }).ToList()),
            () => DisplayConfig.ApplyTopology(DisplayConfig.SDC_TOPOLOGY_EXTEND));
    }

    public static void CloneAll()
    {
        var ts = DisplayConfig.GetTargets();
        WithFallback(
            () => DisplayConfig.ApplyGroups(new[] { (IReadOnlyList<DisplayTarget>)ts }),
            () => DisplayConfig.ApplyTopology(DisplayConfig.SDC_TOPOLOGY_CLONE));
    }

    public static void Only(DisplayTarget t) =>
        DisplayConfig.ApplyGroups(new[] { (IReadOnlyList<DisplayTarget>)new[] { t } });

    public static DisplayTarget FindTarget(string query)
    {
        var ts = DisplayConfig.GetTargets();
        if (int.TryParse(query.TrimStart('#'), out int n) && n >= 1 && n <= ts.Count) return ts[n - 1];
        return ts.FirstOrDefault(t => t.Label.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? ts.FirstOrDefault(t => t.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Hittar ingen skärm som matchar \"{query}\".");
    }

    /// <summary>Hur en profil byter ingångar. Fönstret ger versioner som går via rätt dator; kommandoraden enkla.</summary>
    public sealed class ProfileHooks
    {
        /// <summary>Vilken ingång en dator ("server"/klient-id) sitter på på skärmen (null = okänt).</summary>
        public required Func<string, string, byte?> InputFor;
        /// <summary>Vilken ingång skärmen visar just nu (null = okänt).</summary>
        public required Func<string, byte?> Current;
        /// <summary>Byt ingång. null = det gick, annars felet.</summary>
        public required Func<string, byte, string?> SetInput;
        public Func<string, string?> ClientName = id => null;
    }

    public static ProfileHooks? Hooks;

    static ProfileHooks DefaultHooks() => new()
    {
        InputFor = (dev, computer) =>
        {
            if (computer == "server") return Settings.HomeInput(dev);
            for (int c = 1; c < 0x20; c++) if (Settings.InputOwner(dev, (byte)c) == computer) return (byte)c;
            return null;
        },
        Current = Settings.LastInput,
        SetInput = (dev, code) =>
        {
            var t = DisplayConfig.GetTargets().FirstOrDefault(x => x.Active && string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
            if (t == null) return "skärmen är inte på i Windows";
            try { Ddc.SetInput(t.GdiName, t.PhysIndex, code); Settings.SetLastInput(dev, code); return null; }
            catch (Exception ex) { return ex.Message; }
        },
    };

    /// <summary>
    /// Lägger på en profil: först byts ingångar så att varje skärm visar rätt dator (skärmar som ska
    /// tillbaka hit hinner dyka upp i Windows), sedan den här datorns skärmlayout.
    /// </summary>
    public static void ApplyProfile(Profile p)
    {
        var hooks = Hooks ?? DefaultHooks();
        var errors = new List<string>();
        var spec = p.Spec;
        if (spec == null)
        {
            if (p.Layout != null)
                try { DisplayConfig.RestoreSnapshot(p.Layout); } catch (Exception ex) { errors.Add("Layout: " + ex.Message); }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            return;
        }

        // 1) Ingångar: varje skärm ska visa rätt dator.
        bool switched = false;
        foreach (var m in spec.Monitors)
        {
            string? computer = m.Show switch { null or "off" or "keep" => null, var s => s };
            if (computer == null) continue;
            if (hooks.InputFor(m.Dev, computer) is not byte code)
            {
                if (computer != "server")
                    errors.Add($"{m.Name}: vet inte vilken ingång {hooks.ClientName(computer) ?? "datorn"} sitter på – ange det under Kopplingar.");
                continue;
            }
            if (hooks.Current(m.Dev) == code) continue;
            var err = hooks.SetInput(m.Dev, code);
            if (err != null) errors.Add($"{m.Name}: {err}");
            else switched = true;
        }
        if (switched) Thread.Sleep(3000); // skärmar som bytt hit behöver en stund innan Windows ser dem

        // 2) Den här datorns skärmlayout.
        var desktop = DesktopLayout(spec);
        if (desktop != null) errors.AddRange(ApplySpec(desktop, validateOnly: false));
        if (errors.Count > 0) throw new InvalidOperationException("Profilen lades på, men inte allt gick:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Den här datorns del av profilen: skärmar som visar en annan dator eller är "Av" släpps,
    /// "Ändra inte" blir som nu. null = profilen rör inte layouten alls.
    /// </summary>
    public static ProfileLayout? DesktopLayout(ProfileLayout spec)
    {
        if (spec.Monitors.All(m => m.Show == "keep" || ((m.Show ?? "server") == "server" && m.Mode == "keep"))) return null;
        var ts = DisplayConfig.GetTargets();
        var result = new ProfileLayout { Primary = spec.Primary };
        foreach (var m in spec.Monitors)
        {
            var copy = new ProfileMonitor { Dev = m.Dev, Name = m.Name, Show = m.Show, Mode = m.Mode, CloneOf = m.CloneOf, Width = m.Width, Height = m.Height, Hz = m.Hz };
            if (m.Show is not (null or "server" or "keep")) copy.Mode = "off"; // annan dator eller "Av"
            else if (m.Show == "keep" || m.Mode == "keep")
            {
                var t = ts.FirstOrDefault(x => string.Equals(x.DevicePath, m.Dev, StringComparison.OrdinalIgnoreCase));
                var cloneOf = t is { Active: true } ? ts.FirstOrDefault(o => o != t && o.Active && o.GdiName == t.GdiName && ts.IndexOf(o) < ts.IndexOf(t)) : null;
                copy.Mode = t is not { Active: true } ? "off" : cloneOf != null ? "clone" : "extend";
                copy.CloneOf = cloneOf?.DevicePath;
                copy.Width = copy.Height = copy.Hz = null;
            }
            result.Monitors.Add(copy);
        }
        return result;
    }
    /// <summary>
    /// Läge som en profil valt för en skärm (dev → läge). Gäller tills nästa byte av läge, så att
    /// appens automatiska återställning av upplösning inte skriver över profilens val.
    /// </summary>
    public static readonly Dictionary<string, VideoMode> ProfileModes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lägger på en redigerbar profil-layout. Returnerar det som inte gick (tom = allt gick).</summary>
    public static List<string> ApplySpec(ProfileLayout spec, bool validateOnly)
    {
        var errors = new List<string>();
        var ts = DisplayConfig.GetTargets();
        DisplayTarget? T(string dev) => ts.FirstOrDefault(t => string.Equals(t.DevicePath, dev, StringComparison.OrdinalIgnoreCase));

        var on = spec.Monitors.Where(m => m.Mode != "off").ToList();
        foreach (var m in on.Where(m => T(m.Dev) == null)) errors.Add($"{m.Name}: är inte ansluten just nu");
        on = on.Where(m => T(m.Dev) != null).ToList();
        if (on.Count == 0) { errors.Add("Ingen av profilens skärmar är ansluten just nu."); return errors; }

        // Dupliceringar: skärmar som visar samma bild hamnar i samma grupp.
        var parent = on.ToDictionary(m => m.Dev, m => m.Dev, StringComparer.OrdinalIgnoreCase);
        string Find(string d) { while (!string.Equals(parent[d], d, StringComparison.OrdinalIgnoreCase)) d = parent[d]; return d; }
        foreach (var m in on.Where(m => m.Mode == "clone" && m.CloneOf != null && parent.ContainsKey(m.CloneOf)))
            parent[Find(m.Dev)] = Find(m.CloneOf!);
        var groups = on.GroupBy(m => Find(m.Dev), StringComparer.OrdinalIgnoreCase)
            .Select(g => (IReadOnlyList<DisplayTarget>)g.Select(m => T(m.Dev)!).ToList()).ToList();

        try { DisplayConfig.ApplyGroups(groups, spec.Primary); }
        catch (Exception ex) { errors.Add("Layout: " + ex.Message); return errors; }
        if (validateOnly) return errors;

        // Egna lägen (upplösning/Hz) för utökade skärmar.
        lock (ProfileModes) ProfileModes.Clear();
        var custom = on.Where(m => m.Mode == "extend" && m.Width is int && m.Height is int && m.Hz is int).ToList();
        if (custom.Count == 0) return errors;
        Thread.Sleep(1200);
        ts = DisplayConfig.GetTargets();
        foreach (var m in custom)
        {
            var mode = new VideoMode(m.Width!.Value, m.Height!.Value, m.Hz!.Value);
            lock (ProfileModes) ProfileModes[m.Dev] = mode;
            var t = T(m.Dev);
            if (t is not { Active: true, GdiName: not null }) continue;
            if (t.Width == mode.Width && t.Height == mode.Height && t.Hz == mode.Hz) continue;
            try { DisplayModes.Set(t.GdiName, mode); }
            catch (Exception ex) { errors.Add($"{m.Name}: {ex.Message}"); }
        }
        return errors;
    }
}
