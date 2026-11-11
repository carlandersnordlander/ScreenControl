namespace SkarmStyrning;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) return RunCommandLine(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new AppWindow());
        return 0;
    }

    /// <summary>
    /// SkarmStyrning.exe --extend | --clone | --only "BenQ" | --profile "Spel" | --input "BenQ" HDMI1 | --list
    /// Praktiskt för genvägar och kortkommandon.
    /// </summary>
    static int RunCommandLine(string[] args)
    {
        string cmd = args[0].ToLowerInvariant();
        string Arg(int i) => args.Length > i ? args[i] : throw new ArgumentException($"{args[0]} saknar argument.");
        try
        {
            switch (cmd)
            {
                case "--extend":
                    Settings.ExtendIntent = true;
                    Smart.Extend(userInitiated: true);
                    break;
                case "--clone": Actions.CloneAll(); break;
                case "--only": Actions.Only(Actions.FindTarget(Arg(1))); break;
                case "--profile":
                    var name = Arg(1);
                    var p = ProfileStore.Load().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Profilen \"{name}\" finns inte.");
                    Actions.ApplyProfile(p);
                    break;
                case "--input":
                    var t = Actions.FindTarget(Arg(1));
                    Ddc.SetInput(t.GdiName, t.PhysIndex, ParseInput(Arg(2)));
                    break;
                case "--screenshot": // felsökning: --screenshot bild.png [system|light|dark] [javascript]
                    ApplicationConfiguration.Initialize();
                    Application.Run(new AppWindow(Arg(1), args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null));
                    return 0;
                case "--diag": // felsökning: testar layouter med SDC_VALIDATE, ändrar ingenting
                {
                    var log = DisplayConfig.ValidateLog = new List<string>();
                    var ts = DisplayConfig.GetTargets();
                    List<IReadOnlyList<DisplayTarget>> Singles(IEnumerable<DisplayTarget> xs) => xs.Select(x => (IReadOnlyList<DisplayTarget>)new[] { x }).ToList();
                    void Try(string name, Action a)
                    {
                        log.Add("== " + name);
                        try { a(); log.Add("  => OK"); } catch (Exception ex) { log.Add("  => FEL: " + ex.Message); }
                    }
                    log.Add("Skärmar: " + string.Join(", ", ts.Select(x => $"{x.Label}({(x.Active ? "på" : "av")}, t{x.TargetId})")));
                    Try("Utöka alla", () => DisplayConfig.ApplyGroups(Singles(ts)));
                    foreach (var skip in ts) Try("Alla utom " + skip.Label, () => DisplayConfig.ApplyGroups(Singles(ts.Where(x => x != skip))));
                    foreach (var only in ts) Try("Endast " + only.Label, () => DisplayConfig.ApplyGroups(Singles(new[] { only })));
                    Try("Duplicera alla", () => DisplayConfig.ApplyGroups(new[] { (IReadOnlyList<DisplayTarget>)ts }));
                    foreach (var prof in ProfileStore.Load())
                    {
                        var spec = prof.Spec ?? (prof.Layout != null ? DisplayConfig.SpecFromSnapshot(prof.Layout) : null);
                        if (spec == null) continue;
                        log.Add($"   ({prof.Name}: {spec.Describe()}, huvudskärm {spec.Monitors.FirstOrDefault(m => m.Dev == spec.Primary)?.Name})");
                        Try("Profil " + prof.Name, () =>
                        {
                            var errs = Actions.ApplySpec(spec, validateOnly: true);
                            if (errs.Count > 0) throw new InvalidOperationException(string.Join("; ", errs));
                        });
                    }
                    File.WriteAllLines(Arg(1), log);
                    return 0;
                }
                case "--status":
                    var act = DisplayConfig.GetTargets().Where(x => x.Active).ToList();
                    foreach (var d in act)
                    {
                        var caps = Ddc.Read(d.GdiName, d.PhysIndex);
                        if (caps.CapsFromMonitor) Smart.KnownCaps[d.DevicePath] = caps.Supported;
                    }
                    foreach (var (dev, pi) in Smart.Check(act))
                    {
                        var d = act.First(x => x.DevicePath == dev);
                        var home = Settings.HomeInput(dev);
                        Console.WriteLine($"{d.Label} | {pi.State} {pi.Reason} | datorns ingång: {(home is byte h ? Ddc.InputName(h) : "okänd")}");
                    }
                    return 0;
                case "--list":
                    int i = 1;
                    foreach (var d in DisplayConfig.GetTargets())
                    {
                        var info = Ddc.Read(d.GdiName, d.PhysIndex);
                        Console.WriteLine($"#{i++} {d.Label} | {d.Connector} | {(d.Active ? d.GdiName : "av")} | {d.Geometry} | " +
                            $"input nu: {(info.Current is uint c ? Ddc.InputName((byte)c) : "-")} | " +
                            $"ingångar: {(info.Supported.Count > 0 ? string.Join(", ", info.Supported.Select(Ddc.InputName)) : "-")}" +
                            (info.Error != null ? $" | {info.Error}" : ""));
                    }
                    return 0;
                default:
                    throw new ArgumentException("Okänt kommando. Använd --extend, --clone, --only <skärm>, --profile <namn>, --input <skärm> <ingång> eller --list.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (cmd == "--list") { Console.Error.WriteLine(ex.Message); return 1; }
            MessageBox.Show(ex.Message, "Skärmstyrning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    static byte ParseInput(string s)
    {
        var norm = s.Replace(" ", "").Replace("-", "");
        foreach (var (code, name) in Ddc.StandardInputs)
            if (name.Replace(" ", "").Replace("-", "").Equals(norm, StringComparison.OrdinalIgnoreCase)) return code;
        if (norm.Equals("DP1", StringComparison.OrdinalIgnoreCase)) return 0x0F;
        if (norm.Equals("DP2", StringComparison.OrdinalIgnoreCase)) return 0x10;
        var hex = norm.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? norm[2..] : norm;
        return byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? b : throw new ArgumentException($"Okänd ingång \"{s}\" (t.ex. HDMI1, HDMI2, DP1, USB-C eller 0x11).");
    }
}
