using System.Text.Json.Nodes;

namespace SkarmStyrning;

/// <summary>Redigera profiler utan att lägga på dem.</summary>
public sealed partial class AppWindow
{
    object GetProfileForEdit(string name)
    {
        var p = profiles.FirstOrDefault(x => x.Name == name) ?? throw new InvalidOperationException("Profilen finns inte.");
        var spec = p.Spec ?? (p.Layout != null ? DisplayConfig.SpecFromSnapshot(p.Layout) : null);
        var monitors = Placed().Select(x =>
        {
            var t = targets.FirstOrDefault(t => t.Id == x.id);
            var pm = spec?.Monitors.FirstOrDefault(m => string.Equals(m.Dev, x.id, StringComparison.OrdinalIgnoreCase));
            var pref = PositionMemory.Preferred(x.id);
            List<VideoMode> modes = new();
            if (t is { Active: true, GdiName: not null }) try { modes = DisplayModes.List(t.GdiName); } catch { }
            var codes = caps.TryGetValue(x.id, out var known) && known.Count > 0 ? known.ToList() : Ddc.CommonInputs.ToList();
            byte? input = p.Inputs != null && p.Inputs.FirstOrDefault(kv => string.Equals(kv.Key, x.id, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv2 ? kv2.Value : null;
            if (input is byte b && !codes.Contains(b)) codes.Insert(0, b);
            return new
            {
                dev = x.id,
                name = t?.Label ?? x.name,
                number = x.number,
                connected = t != null,
                mode = pm?.Mode ?? (spec == null ? "extend" : "off"),
                cloneOf = pm?.CloneOf,
                width = pm?.Width,
                height = pm?.Height,
                hz = pm?.Hz,
                preferred = pref == null ? null : $"{pref.Mode.Width}×{pref.Mode.Height} @ {pref.Mode.Hz} Hz",
                resolutions = modes.GroupBy(m => (m.Width, m.Height))
                    .Select(g => new { w = g.Key.Width, h = g.Key.Height, hz = g.Select(v => v.Hz).Distinct().OrderByDescending(v => v).ToList() })
                    .ToList(),
                input,
                inputs = codes.Select(c => new { code = c, name = Ddc.InputName(c), computer = ComputerOnInput(x.id, c) }),
            };
        }).ToList();
        return new { name = p.Name, hasLayout = spec != null, primary = spec?.Primary, monitors };
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
        string NameOf(string dev) => targets.FirstOrDefault(t => t.Id == dev)?.Label
            ?? placed.FirstOrDefault(x => x.id == dev).name ?? "Skärm";

        ProfileLayout? spec = null;
        if (a["hasLayout"]?.GetValue<bool>() == true)
        {
            spec = new ProfileLayout { Primary = a["primary"]?.GetValue<string>() };
            foreach (var m in a["monitors"] as JsonArray ?? new())
            {
                if (m == null) continue;
                string dev = m["dev"]!.GetValue<string>();
                spec.Monitors.Add(new ProfileMonitor
                {
                    Dev = dev,
                    Name = NameOf(dev),
                    Mode = m["mode"]?.GetValue<string>() is "off" or "clone" ? m["mode"]!.GetValue<string>() : "extend",
                    CloneOf = m["cloneOf"]?.GetValue<string>(),
                    Width = m["width"]?.GetValue<int?>(),
                    Height = m["height"]?.GetValue<int?>(),
                    Hz = m["hz"]?.GetValue<int?>(),
                });
            }
            if (!spec.Monitors.Any(m => m.Mode != "off")) throw new InvalidOperationException("Minst en skärm måste vara på i profilen.");
            if (spec.Primary == null || spec.Monitors.FirstOrDefault(m => m.Dev == spec.Primary)?.Mode is not "extend")
                spec.Primary = spec.Monitors.FirstOrDefault(m => m.Mode == "extend")?.Dev ?? spec.Monitors.First(m => m.Mode != "off").Dev;
        }

        var inputsMap = new Dictionary<string, byte>();
        var labels = new Dictionary<string, string>();
        if (a["inputs"] is JsonObject io)
            foreach (var (dev, v) in io)
            {
                if (v == null) continue;
                byte code = (byte)v.GetValue<int>();
                inputsMap[dev] = code;
                labels[dev] = $"{NameOf(dev)} → {Ddc.InputName(code)}";
            }

        // Kolla med Windows (utan att verkställa) att layouten går att lägga på just nu.
        List<string> warnings = new();
        if (spec != null)
            warnings = await Task.Run(() =>
            {
                DisplayConfig.ValidateLog = new();
                try { return Actions.ApplySpec(spec, validateOnly: true); }
                catch (Exception ex) { return new List<string> { ex.Message }; }
                finally { DisplayConfig.ValidateLog = null; }
            });

        if (Settings.LastProfile == original) Settings.LastProfile = name;
        p.Name = name;
        p.Spec = spec;
        p.Layout = null; // äldre format ersätts av det redigerbara
        p.Inputs = inputsMap.Count > 0 ? inputsMap : null;
        p.InputLabels = labels.Count > 0 ? labels : null;
        ProfileStore.Save(profiles);
        RegisterHotkeys();
        BuildTrayMenu();
        await PushState();

        if (warnings.Count > 0)
            Toast($"Profilen \"{name}\" är sparad, men går inte att lägga på helt just nu: {string.Join("; ", warnings)}", "info");
        else
            Toast($"Profilen \"{name}\" är sparad.", "success");

        if (a["apply"]?.GetValue<bool>() == true)
            await Dispatch("applyProfile", new JsonObject { ["name"] = name });
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
