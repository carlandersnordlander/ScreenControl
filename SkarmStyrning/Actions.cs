using System.Text.Json;

namespace SkarmStyrning;

/// <summary>En skärm i en profil: av, på (utökad) eller duplicerar en annan skärm, och ev. ett eget läge.</summary>
public sealed class ProfileMonitor
{
    public string Dev { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "extend";   // "off" | "extend" | "clone"
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

    public string Describe()
    {
        var on = Monitors.Where(m => m.Mode != "off").ToList();
        if (on.Count == 0) return "Inga skärmar";
        bool cloned = on.Any(m => m.Mode == "clone");
        return (cloned ? "dupl. " : "") + string.Join(" + ", on.Select(m => m.Name + (m.Width is int w ? $" {w}×{m.Height}" : "")));
    }
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

    /// <summary>
    /// Byter ingång på en skärm: direkt härifrån om det går, annars via en ansluten dator som når
    /// skärmen (t.ex. när skärmen visar laptopen och den här datorn har tappat kontakten med den).
    /// Sätts av fönstret när nätverksdelen är igång. Returnerar null vid lyckat byte, annars felet.
    /// </summary>
    public static Func<string, byte, string?>? SetInputAnyRoute;

    static string? SetInputDirect(string dev, byte code)
    {
        var t = DisplayConfig.GetTargets().FirstOrDefault(x => x.Active && string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
        if (t == null) return "skärmen är inte på i Windows";
        try { Ddc.SetInput(t.GdiName, t.PhysIndex, code); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Byter först ingångar (så att skärmar som visar en annan dator kommer tillbaka), väntar in
    /// skärmarna och lägger sedan på layouten.
    /// </summary>
    public static void ApplyProfile(Profile p)
    {
        var errors = new List<string>();
        if (p.Inputs is { Count: > 0 })
        {
            foreach (var (dev, code) in p.Inputs)
            {
                string label = p.InputLabels?.GetValueOrDefault(dev) ?? dev;
                var err = SetInputAnyRoute != null ? SetInputAnyRoute(dev, code) : SetInputDirect(dev, code);
                if (err != null) errors.Add($"{label}: {err}");
            }
            if (p.Layout != null || p.Spec != null) Thread.Sleep(3000); // låt skärmar som bytt tillbaka hinna dyka upp i Windows
        }
        if (p.Spec != null)
            errors.AddRange(ApplySpec(p.Spec, validateOnly: false));
        else if (p.Layout != null)
        {
            try { DisplayConfig.RestoreSnapshot(p.Layout); }
            catch (Exception ex) { errors.Add("Layout: " + ex.Message); }
        }
        if (errors.Count > 0) throw new InvalidOperationException("Profilen lades på, men inte allt gick:\n" + string.Join("\n", errors));
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
