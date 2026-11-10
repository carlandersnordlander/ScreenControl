using System.Text.Json;

namespace SkarmStyrning;

public sealed class Profile
{
    public string Name { get; set; } = "";
    public LayoutSnapshot? Layout { get; set; }
    /// <summary>Skärmens device path → input-kod (VCP 0x60).</summary>
    public Dictionary<string, byte>? Inputs { get; set; }
    public Dictionary<string, string>? InputLabels { get; set; }
    /// <summary>Globalt kortkommando, t.ex. "Ctrl+Shift+Alt+D1". null = aldrig satt, "" = borttaget.</summary>
    public string? Hotkey { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Layout != null) parts.Add("Layout: " + Layout.Description);
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

    /// <summary>Lägger först på layouten, väntar in skärmarna och byter sedan input.</summary>
    public static void ApplyProfile(Profile p)
    {
        if (p.Layout != null)
        {
            DisplayConfig.RestoreSnapshot(p.Layout);
            Thread.Sleep(2500);
        }
        if (p.Inputs is not { Count: > 0 }) return;

        var ts = DisplayConfig.GetTargets();
        var errors = new List<string>();
        foreach (var (dev, code) in p.Inputs)
        {
            var t = ts.FirstOrDefault(x => x.Active && string.Equals(x.DevicePath, dev, StringComparison.OrdinalIgnoreCase));
            string label = p.InputLabels?.GetValueOrDefault(dev) ?? dev;
            if (t == null) { errors.Add($"{label}: skärmen är inte aktiv"); continue; }
            try { Ddc.SetInput(t.GdiName, t.PhysIndex, code); }
            catch (Exception ex) { errors.Add($"{t.Label}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException("Vissa inputbyten misslyckades:\n" + string.Join("\n", errors));
    }
}
