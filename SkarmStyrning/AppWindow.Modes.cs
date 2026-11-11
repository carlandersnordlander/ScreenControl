using System.Text.Json.Nodes;

namespace SkarmStyrning;

/// <summary>Upplösning och uppdateringsfrekvens per skärm – väljs i appen och återställs efter byten.</summary>
public sealed partial class AppWindow
{
    string? lastActiveSet;
    string? lastEnforced;

    object ModesState(string dev, DisplayTarget? t)
    {
        var pref = PositionMemory.Preferred(dev);
        List<VideoMode> list = new();
        if (t is { Active: true, GdiName: not null })
            try { list = DisplayModes.List(t.GdiName); } catch { }
        return new
        {
            current = t is { Active: true, HasGeometry: true } ? new { w = t.Width, h = t.Height, hz = t.Hz } : null,
            preferred = pref == null ? null : new { w = pref.Mode.Width, h = pref.Mode.Height, hz = pref.Mode.Hz, pinned = pref.Pinned },
            resolutions = list.GroupBy(m => (m.Width, m.Height))
                .Select(g => new { w = g.Key.Width, h = g.Key.Height, hz = g.Select(x => x.Hz).Distinct().OrderByDescending(x => x).ToList() })
                .ToList(),
        };
    }

    /// <summary>
    /// Efter att en skärm kommit tillbaka (t.ex. bytt tillbaka från laptopen) väljer Windows ofta
    /// ett annat läge, som 1080p i stället för 4K eller 60 Hz i stället för 144 Hz. Sätt tillbaka
    /// skärmarnas önskade lägen – men bara just när uppsättningen skärmar ändrats, så att ett läge
    /// man själv väljer i Windows inställningar inte skrivs över.
    /// </summary>
    async Task EnforcePreferredModes()
    {
        string set = string.Join("|", targets.Where(t => t.Active).Select(t => t.Id).OrderBy(x => x));
        bool setChanged = lastActiveSet != null && set != lastActiveSet;
        lastActiveSet = set;
        if (!setChanged || op.CurrentCount == 0) return;

        Dictionary<string, VideoMode> fromProfile;
        lock (Actions.ProfileModes) fromProfile = new(Actions.ProfileModes, StringComparer.OrdinalIgnoreCase);
        var wrong = targets.Where(t => t is { Active: true, HasGeometry: true }
                && !fromProfile.ContainsKey(t.DevicePath) // profilen har valt ett eget läge
                && PositionMemory.PreferredPlacement(t.DevicePath) != null
                && PositionMemory.Preferred(t.DevicePath) is { } p
                && (p.Mode.Width != t.Width || p.Mode.Height != t.Height || p.Mode.Hz != t.Hz))
            .ToList();
        if (wrong.Count == 0) return;

        string key = set + "#" + string.Join("|", wrong.Select(t => $"{t.Id}{t.Width}x{t.Height}@{t.Hz}"));
        if (key == lastEnforced) return; // redan försökt för exakt det här läget
        lastEnforced = key;

        var groups = targets.Where(t => t.Active).GroupBy(t => t.GdiName ?? t.Id)
            .Select(g => (IReadOnlyList<DisplayTarget>)g.ToList()).ToList();
        string what = string.Join(", ", wrong.Select(t =>
        {
            var m = PositionMemory.Preferred(t.DevicePath)!.Mode;
            return $"{t.Label} → {m.Width}×{m.Height} @ {m.Hz} Hz";
        }));
        await DisplayOp("Återställer upplösning och Hz", () =>
        {
            DisplayConfig.ApplyGroups(groups);
            return "Återställde " + what + ".";
        });
    }

    async Task<(bool handled, object? result)> ModesDispatch(string cmd, JsonNode? a)
    {
        string Str(string k) => a?[k]?.GetValue<string>() ?? "";
        int Int(string k) => a?[k]?.GetValue<int>() ?? 0;
        switch (cmd)
        {
            case "setMode":
            {
                var t = Find(Str("id"));
                if (t.GdiName == null) throw new InvalidOperationException("Skärmen måste vara på för att byta upplösning.");
                var mode = new VideoMode(Int("w"), Int("h"), Int("hz"));
                await DisplayOp($"Byter {t.Label} till {mode.Width}×{mode.Height} @ {mode.Hz} Hz", () =>
                {
                    DisplayModes.Set(t.GdiName, mode);
                    PositionMemory.Pin(t.DevicePath, mode);
                    return $"{t.Label} använder nu {mode.Width}×{mode.Height} @ {mode.Hz} Hz – och får det igen efter byten.";
                });
                return (true, null);
            }

            case "autoMode":
                PositionMemory.Pin(Str("id"), null);
                Toast("Skärmen använder sitt bästa läge automatiskt.", "success");
                await PushState();
                return (true, null);
        }
        return (false, null);
    }
}
