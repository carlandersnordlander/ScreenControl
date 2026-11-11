namespace SkarmStyrning;

public enum Presence { Present, Absent, Unknown }

public sealed record PresenceInfo(Presence State, string Reason);

/// <summary>
/// "Smart utöka": utökar bara till skärmar som faktiskt visar den här datorn och lägger dem
/// där de satt senast. Känner av avstängda skärmar och skärmar som bytt till en annan källa.
/// </summary>
public static class Smart
{
    static readonly object Gate = new();
    /// <summary>Ingångar per skärm (från capabilities), fylls i av fönstret.</summary>
    public static readonly Dictionary<string, List<byte>> KnownCaps = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> excluded = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> absentStreak = new(StringComparer.OrdinalIgnoreCase);
    static string? lastActiveKey;
    static bool layoutEnforced;

    public static string? ExcludedReason(string dev)
    {
        lock (Gate) return excluded.GetValueOrDefault(dev);
    }

    static string Key(IEnumerable<DisplayTarget> ts) => string.Join("|", ts.Select(t => t.DevicePath).OrderBy(x => x));

    static List<IReadOnlyList<DisplayTarget>> Singles(IEnumerable<DisplayTarget> ts) =>
        ts.Select(t => (IReadOnlyList<DisplayTarget>)new[] { t }).ToList();

    /// <summary>Avgör för varje aktiv skärm om den visar den här datorn.</summary>
    public static Dictionary<string, PresenceInfo> Check(List<DisplayTarget> active)
    {
        // Skärmar som inte rapporterar rätt ingång: använd ingången appen senast bytte till.
        var raw = active.Select(t =>
        {
            var s = Ddc.ReadStatus(t.GdiName, t.PhysIndex);
            return (t, s: s with { Input = Settings.EffectiveInput(t.DevicePath, s.Input) });
        }).ToList();

        bool Healthy((DisplayTarget t, Ddc.DdcStatus s) x) =>
            x.s.Responded && x.s.PowerOn != false && (x.s.Input is not byte i || Settings.IsHome(x.t, i) != false);

        var result = new Dictionary<string, PresenceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in raw)
        {
            var (t, s) = x;
            // Slutsatser av tystnad/standby dras bara om någon annan skärm mår bra –
            // annars är det troligen datorn som släckt skärmarna (energisparläge).
            bool othersHealthy = raw.Any(o => o.t != t && Healthy(o));

            if (!s.Responded)
            {
                result[t.DevicePath] = Settings.IsCapable(t.DevicePath) && othersHealthy
                    ? new(Presence.Absent, "svarar inte – avstängd eller annan källa")
                    : new(Presence.Unknown, "");
                continue;
            }

            Settings.MarkCapable(t.DevicePath);
            if (s.Input is byte learned)
            {
                List<byte>? caps;
                lock (Gate) caps = KnownCaps.GetValueOrDefault(t.DevicePath);
                Settings.LearnHome(t, learned, caps);
            }

            if (s.PowerOn == false && othersHealthy)
                result[t.DevicePath] = new(Presence.Absent, "avstängd / standby");
            else if (s.Input is byte inp && Settings.IsHome(t, inp) == false)
                result[t.DevicePath] = new(Presence.Absent, "visar " + Ddc.InputName(inp));
            else
                result[t.DevicePath] = new(Presence.Present, "");
        }
        return result;
    }

    /// <summary>
    /// Utökar till alla skärmar som visar den här datorn.
    /// userInitiated = användaren klickade: prova även skärmar som tidigare utelämnats.
    /// Returnerar ett meddelande om något ändrades, annars null.
    /// </summary>
    public static string? Extend(bool userInitiated)
    {
        lock (Gate)
        {
            var ts = DisplayConfig.GetTargets();
            var available = ts.Select(t => t.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var k in excluded.Keys.Where(k => !available.Contains(k)).ToList())
                excluded.Remove(k); // urkopplad – behandlas som ny när den kommer tillbaka
            if (userInitiated) { excluded.Clear(); absentStreak.Clear(); }

            var active = ts.Where(t => t.Active).ToList();
            var pres = Check(active);

            var absent = new List<DisplayTarget>();
            foreach (var t in active)
            {
                if (pres[t.DevicePath].State == Presence.Absent)
                {
                    int n = absentStreak[t.DevicePath] = absentStreak.GetValueOrDefault(t.DevicePath) + 1;
                    if (userInitiated || n >= 2) absent.Add(t); // automatiskt: kräv två observationer i rad
                }
                else absentStreak.Remove(t.DevicePath);
            }
            if (absent.Count == active.Count) absent.Clear(); // stäng aldrig av allt
            foreach (var a in absent) excluded[a.DevicePath] = pres[a.DevicePath].Reason;

            var candidates = ts.Where(t => !t.Active && !excluded.ContainsKey(t.DevicePath)).ToList();
            var desired = active.Except(absent).Concat(candidates).ToList();
            if (desired.Count == 0) return null;

            string activeKey = Key(active);
            if (activeKey != lastActiveKey) { lastActiveKey = activeKey; layoutEnforced = false; }
            bool allExtended = active.Select(t => (t.Adapter.Value, t.SourceId)).Distinct().Count() == active.Count;

            bool apply = absent.Count > 0 || candidates.Count > 0 || !allExtended;
            if (!apply && (userInitiated || !layoutEnforced))
            {
                // Samma skärmar, men har Windows flyttat om dem? Lägg tillbaka dem där de ska sitta.
                apply = !DisplayConfig.LayoutMatches(Singles(desired));
            }
            layoutEnforced = true;

            if (!apply)
                return userInitiated ? "Alla tillgängliga skärmar är redan utökade." : null;

            DisplayConfig.ApplyGroups(Singles(desired));

            // Skärmar som just slogs på: visar de verkligen den här datorn?
            if (candidates.Count > 0)
            {
                List<DisplayTarget> nowActive = new();
                var newAbsent = new List<DisplayTarget>();
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Thread.Sleep(attempt == 0 ? 3000 : 1500);
                    nowActive = DisplayConfig.GetTargets().Where(t => t.Active).ToList();
                    var p2 = Check(nowActive);
                    newAbsent = nowActive
                        .Where(t => candidates.Any(c => c.DevicePath == t.DevicePath) && p2[t.DevicePath].State == Presence.Absent)
                        .ToList();
                    foreach (var a in newAbsent) excluded[a.DevicePath] = p2[a.DevicePath].Reason;
                    if (newAbsent.Count == 0) break;
                }
                var remaining = nowActive.Where(t => !newAbsent.Contains(t)).ToList();
                if (newAbsent.Count > 0 && remaining.Count > 0)
                    DisplayConfig.ApplyGroups(Singles(remaining));
            }

            var final = DisplayConfig.GetTargets();
            lastActiveKey = Key(final.Where(t => t.Active));
            layoutEnforced = true;

            var msg = "Utökat: " + string.Join(", ", final.Where(t => t.Active).Select(t => t.Label)) + ".";
            var left = final.Where(t => !t.Active && excluded.ContainsKey(t.DevicePath)).ToList();
            if (left.Count > 0)
                msg += " Utelämnade: " + string.Join(", ", left.Select(t => $"{t.Label} ({excluded[t.DevicePath]})")) + ".";
            return msg;
        }
    }

    /// <summary>Körs regelbundet i bakgrunden när automatisk anpassning är på.</summary>
    public static string? AutoTick()
    {
        if (!Settings.AutoAdapt || !Settings.ExtendIntent) return null;
        return Extend(userInitiated: false);
    }
}
