using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkarmStyrning;

#region Win32-strukturer (CCD API: QueryDisplayConfig / SetDisplayConfig)

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
    public readonly long Value => ((long)HighPart << 32) | LowPart;
}

[StructLayout(LayoutKind.Sequential)]
public struct PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct RATIONAL { public uint Numerator, Denominator; }

[StructLayout(LayoutKind.Sequential)]
public struct PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public RATIONAL refreshRate;
    public uint scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct PATH_INFO
{
    public PATH_SOURCE_INFO sourceInfo;
    public PATH_TARGET_INFO targetInfo;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct POINTL { public int x, y; }

[StructLayout(LayoutKind.Sequential)]
public struct SOURCE_MODE
{
    public uint width, height, pixelFormat;
    public POINTL position;
}

/// <summary>DISPLAYCONFIG_MODE_INFO – 64 byte, unionen börjar på offset 16.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct MODE_INFO
{
    [FieldOffset(0)] public uint infoType; // 1 = source, 2 = target, 3 = desktop image
    [FieldOffset(4)] public uint id;
    [FieldOffset(8)] public LUID adapterId;
    [FieldOffset(16)] public SOURCE_MODE sourceMode;
}

[StructLayout(LayoutKind.Sequential)]
struct DEVICE_INFO_HEADER
{
    public uint type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct TARGET_DEVICE_NAME
{
    public DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct SOURCE_DEVICE_NAME
{
    public DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
}

#endregion

/// <summary>En fysisk skärm som Windows ser (ansluten, aktiv eller inte).</summary>
public sealed class DisplayTarget
{
    public LUID Adapter;
    public uint TargetId;
    public string Name = "";
    public string Label = "";
    public string DevicePath = "";
    public uint OutputTech;
    public bool Active;
    public string? GdiName;      // \\.\DISPLAYn när aktiv
    public uint SourceId;
    public int PhysIndex;        // index bland fysiska skärmar på samma HMONITOR (vid duplicering)
    public string Geometry = ""; // t.ex. "1920×1080 @ 144 Hz · pos 1920, 0"
    public int X, Y, Width, Height, Hz; // aktuell (om på) eller sparad placering
    public string EdidKey = "";         // tillverkare + produktkod, samma på alla datorer
    public bool HasGeometry;

    /// <summary>Stabil nyckel – device path om den finns.</summary>
    public string Id => DevicePath != "" ? DevicePath : $"{Adapter.Value}:{TargetId}";

    public bool SameAs(DisplayTarget? o) => o != null && Adapter.Value == o.Adapter.Value && TargetId == o.TargetId;

    public int? DisplayNumber =>
        GdiName != null && int.TryParse(GdiName.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : null;

    public string Connector => OutputTech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DisplayPort",
        11 => "eDP",
        15 => "Miracast",
        16 or 17 => "Indirekt",
        0x80000000 => "Inbyggd",
        _ => "Annan"
    };

    public override string ToString() => Label;
}

public sealed class LayoutSnapshot
{
    public string Paths { get; set; } = "";
    public string Modes { get; set; } = "";
    public List<string> PathDevices { get; set; } = new();
    public string Description { get; set; } = "";
}

public static class DisplayConfig
{
    const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const uint SDC_TOPOLOGY_CLONE = 0x2, SDC_TOPOLOGY_EXTEND = 0x4;
    const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const uint PATH_ACTIVE = 0x1;
    const uint INVALID = 0xFFFFFFFF;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH_INFO[] paths, ref uint numModes, [Out] MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, PATH_INFO[]? paths, uint numModes, MODE_INFO[]? modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME req);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME req);

    static (PATH_INFO[] paths, MODE_INFO[] modes) Query(uint flags)
    {
        while (true)
        {
            int err = GetDisplayConfigBufferSizes(flags, out uint np, out uint nm);
            if (err != 0) throw Fail("GetDisplayConfigBufferSizes", err);
            var p = new PATH_INFO[np];
            var m = new MODE_INFO[nm];
            err = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (err == ERROR_INSUFFICIENT_BUFFER) continue;
            if (err != 0) throw Fail("QueryDisplayConfig", err);
            Array.Resize(ref p, (int)np);
            Array.Resize(ref m, (int)nm);
            return (p, m);
        }
    }

    static Exception Fail(string what, int code) =>
        new InvalidOperationException($"{what} misslyckades: {new Win32Exception(code).Message} (kod {code})");

    static TARGET_DEVICE_NAME? GetTargetName(LUID adapter, uint id)
    {
        var r = new TARGET_DEVICE_NAME();
        r.header.type = 2; // GET_TARGET_NAME
        r.header.size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>();
        r.header.adapterId = adapter;
        r.header.id = id;
        return DisplayConfigGetDeviceInfo(ref r) == 0 ? r : null;
    }

    static string? GetSourceName(LUID adapter, uint id)
    {
        var r = new SOURCE_DEVICE_NAME();
        r.header.type = 1; // GET_SOURCE_NAME
        r.header.size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>();
        r.header.adapterId = adapter;
        r.header.id = id;
        return DisplayConfigGetDeviceInfo(ref r) == 0 ? r.viewGdiDeviceName : null;
    }

    static bool Match(PATH_TARGET_INFO ti, DisplayTarget t) => ti.adapterId.Value == t.Adapter.Value && ti.id == t.TargetId;

    static void SetGeometry(DisplayTarget t, MODE_INFO src, PATH_TARGET_INFO ti)
    {
        var s = src.sourceMode;
        t.X = s.position.x; t.Y = s.position.y; t.Width = (int)s.width; t.Height = (int)s.height;
        t.Hz = ti.refreshRate.Denominator != 0 ? (int)Math.Round((double)ti.refreshRate.Numerator / ti.refreshRate.Denominator) : 0;
        t.HasGeometry = true;
    }

    static string DescribeGeometry(MODE_INFO src, PATH_TARGET_INFO ti, string prefix = "")
    {
        var s = src.sourceMode;
        double hz = ti.refreshRate.Denominator != 0 ? (double)ti.refreshRate.Numerator / ti.refreshRate.Denominator : 0;
        return $"{prefix}{s.width}×{s.height}" + (hz > 0 ? $" @ {Math.Round(hz)} Hz" : "") + $" · pos {s.position.x}, {s.position.y}";
    }

    /// <summary>
    /// Sparar positionerna för alla utökade skärmar i den aktuella layouten. En ensam skärm
    /// (eller allt duplicerat) har ingen meningsfull position och skriver inte över minnet.
    /// </summary>
    static readonly object RememberGate = new();
    static string? observedSet;
    static string? baseline;
    static DateTime quietUntil;

    /// <summary>Läser av skärmlayouten (snabbt, ingen DDC) så att ändringar gjorda i Windows fångas.</summary>
    public static void Observe()
    {
        var (act, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        Remember(act, modes);
    }

    /// <summary>Ta platserna för skärmarna som är på just nu från Windows till huvudlayouten.</summary>
    public static void LearnCurrent()
    {
        var (act, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        PositionMemory.Learn(Extended(act, modes), onlyNew: false);
    }

    /// <summary>Efter egna ändringar: lär inte in det Windows gör de närmaste sekunderna.</summary>
    static void Quiet()
    {
        lock (RememberGate) { observedSet = null; quietUntil = DateTime.Now.AddSeconds(10); }
    }

    /// <summary>
    /// Huvudlayouten ändras bara när användaren flyttar skärmar i Windows inställningar:
    /// samma skärmar är på, men placeringen har ändrats. När en skärm försvinner eller kommer
    /// tillbaka flyttar Windows om på egen hand – det lärs inte in (bara nya skärmar läggs till).
    /// </summary>
    static void Remember(PATH_INFO[] act, MODE_INFO[] modes)
    {
        var list = Extended(act, modes);
        string set = string.Join("|", list.Select(d => d.dev.ToLowerInvariant()).OrderBy(x => x));
        string arrangement = string.Join(";", list.OrderBy(d => d.dev)
            .Select(d => $"{d.src.sourceMode.position.x},{d.src.sourceMode.position.y},{d.src.sourceMode.width},{d.src.sourceMode.height}"));

        bool userMoved = false;
        lock (RememberGate)
        {
            if (set != observedSet || DateTime.Now < quietUntil)
            {
                observedSet = set;
                baseline = arrangement;
                if (DateTime.Now < quietUntil) return;
            }
            else if (arrangement != baseline)
            {
                baseline = arrangement;
                userMoved = true;
            }
        }

        bool empty = PositionMemory.IsEmpty;
        if (list.Count < 2 && !empty) return; // en ensam skärm har ingen plats i förhållande till de andra
        PositionMemory.Learn(list, onlyNew: !(userMoved || empty));
    }

    static List<(string dev, string name, MODE_INFO src, MODE_INFO? tgt, PATH_TARGET_INFO info)> Extended(PATH_INFO[] act, MODE_INFO[] modes)
    {
        var bySource = act.GroupBy(p => (p.sourceInfo.adapterId.Value, p.sourceInfo.id)).ToList();
        var list = new List<(string, string, MODE_INFO, MODE_INFO?, PATH_TARGET_INFO)>();
        foreach (var g in bySource)
        {
            if (g.Count() != 1) continue; // duplicerade skärmar delar position – hoppa över
            var p = g.First();
            uint si = p.sourceInfo.modeInfoIdx, ti = p.targetInfo.modeInfoIdx;
            if (si >= modes.Length || modes[si].infoType != 1) continue;
            var tn = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            if (string.IsNullOrEmpty(tn?.monitorDevicePath)) continue;
            MODE_INFO? tm = ti < modes.Length && modes[ti].infoType == 2 ? modes[ti] : null;
            list.Add((tn!.Value.monitorDevicePath, tn.Value.monitorFriendlyDeviceName, modes[si], tm, p.targetInfo));
        }
        return list;
    }

    struct Rect
    {
        public int L, T, R, B;
        public static Rect Of(SOURCE_MODE m) => new() { L = m.position.x, T = m.position.y, R = m.position.x + (int)m.width, B = m.position.y + (int)m.height };
        public readonly bool Overlaps(Rect o) => L < o.R && o.L < R && T < o.B && o.T < B;
        public readonly bool Touches(Rect o) =>
            ((R == o.L || L == o.R) && T < o.B && o.T < B) || ((B == o.T || T == o.B) && L < o.R && o.L < R);
    }

    /// <summary>
    /// Ser till att skärmarna inte överlappar, sitter ihop (inga glapp när en skärm i mitten stängs av)
    /// och att någon skärm ligger på (0,0). Tidigare skärmar i listan har förtur.
    /// </summary>
    static void FixLayout(List<MODE_INFO> modes)
    {
        var idx = Enumerable.Range(0, modes.Count).Where(i => modes[i].infoType == 1).ToList();
        if (idx.Count == 0) return;

        void Move(int i, int x, int y)
        {
            var m = modes[i];
            m.sourceMode.position.x = x;
            m.sourceMode.position.y = y;
            modes[i] = m;
        }
        Rect R(int i) => Rect.Of(modes[i].sourceMode);

        // 1) Överlapp: flytta senare skärmar till höger om allt annat.
        var placed = new List<int>();
        foreach (var i in idx)
        {
            if (placed.Any(p => R(p).Overlaps(R(i))))
                Move(i, placed.Max(p => R(p).R), placed.Min(p => R(p).T));
            placed.Add(i);
        }

        // 2) Glapp: skjut lösa skärmar horisontellt (eller vertikalt) tills de nuddar resten.
        int anchor = idx.FirstOrDefault(i => modes[i].sourceMode.position.x == 0 && modes[i].sourceMode.position.y == 0, idx[0]);
        var group = new List<int> { anchor };
        var rest = idx.Where(i => i != anchor).ToList();
        while (rest.Count > 0)
        {
            int next = rest.FirstOrDefault(i => group.Any(g => R(g).Touches(R(i))), -1);
            if (next < 0)
            {
                next = rest.OrderBy(i => group.Min(g => Distance(R(g), R(i)))).First();
                var r = R(next);
                int gl = group.Min(g => R(g).L), gr = group.Max(g => R(g).R), gt = group.Min(g => R(g).T), gb = group.Max(g => R(g).B);
                bool vOverlap = group.Any(g => R(g).T < r.B && r.T < R(g).B);
                bool hOverlap = group.Any(g => R(g).L < r.R && r.L < R(g).R);
                if (r.L >= gr && vOverlap) Move(next, gr, r.T);
                else if (r.R <= gl && vOverlap) Move(next, gl - (r.R - r.L), r.T);
                else if (r.T >= gb && hOverlap) Move(next, r.L, gb);
                else if (r.B <= gt && hOverlap) Move(next, r.L, gt - (r.B - r.T));
                else if (r.L >= gr) Move(next, gr, gt);
                else if (r.R <= gl) Move(next, gl - (r.R - r.L), gt);
                else Move(next, gr, gt);
                // Säkerhet: om flytten ändå gav överlapp, lägg den längst till höger.
                if (group.Any(g => R(g).Overlaps(R(next)))) Move(next, gr, gt);
            }
            group.Add(next);
            rest.Remove(next);
        }

        // 3) Någon skärm måste ligga på (0,0) = huvudskärmen.
        var o = modes[anchor].sourceMode.position;
        if (o.x != 0 || o.y != 0)
            foreach (var i in idx) Move(i, modes[i].sourceMode.position.x - o.x, modes[i].sourceMode.position.y - o.y);
    }

    static long Distance(Rect a, Rect b)
    {
        long dx = Math.Max(0, Math.Max(a.L - b.R, b.L - a.R));
        long dy = Math.Max(0, Math.Max(a.T - b.B, b.T - a.B));
        return dx * dx + dy * dy;
    }

    /// <summary>Alla anslutna skärmar, aktiva och inaktiva.</summary>
    public static List<DisplayTarget> GetTargets()
    {
        var (all, _) = Query(QDC_ALL_PATHS);
        var (act, actModes) = Query(QDC_ONLY_ACTIVE_PATHS);
        Remember(act, actModes);
        var list = new List<DisplayTarget>();

        foreach (var p in all)
        {
            if (p.targetInfo.targetAvailable == 0) continue;
            if (list.Any(t => Match(p.targetInfo, t))) continue;

            var tn = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            var t = new DisplayTarget
            {
                Adapter = p.targetInfo.adapterId,
                TargetId = p.targetInfo.id,
                Name = tn?.monitorFriendlyDeviceName ?? "",
                DevicePath = tn?.monitorDevicePath ?? "",
                OutputTech = tn?.outputTechnology ?? p.targetInfo.outputTechnology,
                EdidKey = tn is TARGET_DEVICE_NAME n ? Net.LocalMonitors.EdidKey(n.edidManufactureId, n.edidProductCodeId) : "",
            };
            if (string.IsNullOrWhiteSpace(t.Name))
                t.Name = t.OutputTech == 0x80000000 ? "Inbyggd skärm" : "Okänd skärm";

            int ai = Array.FindIndex(act, a => Match(a.targetInfo, t));
            if (ai >= 0)
            {
                var src = act[ai].sourceInfo;
                t.Active = true;
                t.SourceId = src.id;
                t.GdiName = GetSourceName(src.adapterId, src.id);
                t.PhysIndex = act.Take(ai).Count(a => a.sourceInfo.adapterId.Value == src.adapterId.Value && a.sourceInfo.id == src.id);
                if (src.modeInfoIdx < actModes.Length)
                {
                    t.Geometry = DescribeGeometry(actModes[src.modeInfoIdx], act[ai].targetInfo);
                    SetGeometry(t, actModes[src.modeInfoIdx], act[ai].targetInfo);
                }
            }
            else if (PositionMemory.Latest(t.DevicePath) is Placement saved)
            {
                t.Geometry = DescribeGeometry(saved.Source, saved.Info, "Sparad: ");
                SetGeometry(t, saved.Source, saved.Info);
            }
            list.Add(t);
        }

        foreach (var t in list)
        {
            bool dup = list.Count(o => o.Name == t.Name) > 1;
            t.Label = dup ? $"{t.Name} ({t.Connector})" : t.Name;
        }
        return list.OrderBy(t => t.Active ? 0 : 1).ThenBy(t => t.DisplayNumber ?? 99).ThenBy(t => t.Name).ToList();
    }

    /// <summary>
    /// Aktiverar exakt de skärmar som finns i grupperna. Varje grupp får en egen bild:
    /// en grupp med en skärm = utökad, en grupp med flera = de duplicerar varandra.
    /// Skärmar som inte nämns stängs av.
    /// </summary>
    public static void ApplyGroups(IReadOnlyList<IReadOnlyList<DisplayTarget>> groups)
    {
        if (groups.Count == 0 || groups.All(g => g.Count == 0))
            throw new InvalidOperationException("Minst en skärm måste vara på.");

        var (all, _) = Query(QDC_ALL_PATHS);
        var (act, actModes) = Query(QDC_ONLY_ACTIVE_PATHS);
        Remember(act, actModes); // fånga ändringar som gjorts i Windows inställningar sedan sist

        var (pa, modes) = Build(groups, all, act, actModes);
        Apply(pa, modes);
    }

    /// <summary>
    /// Sitter skärmarna redan som de skulle göra om grupperna lades på nu?
    /// Jämför relativa positioner, så det spelar ingen roll vilken som är huvudskärm.
    /// </summary>
    public static bool LayoutMatches(IReadOnlyList<IReadOnlyList<DisplayTarget>> groups)
    {
        var (all, _) = Query(QDC_ALL_PATHS);
        var (act, actModes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var (paths, modes) = Build(groups, all, act, actModes);
        int? dx = null, dy = null;
        foreach (var p in paths)
        {
            uint si = p.sourceInfo.modeInfoIdx;
            if (si == INVALID || si >= modes.Count) continue;
            int ai = Array.FindIndex(act, a => a.targetInfo.adapterId.Value == p.targetInfo.adapterId.Value && a.targetInfo.id == p.targetInfo.id);
            if (ai < 0) return false;
            uint asi = act[ai].sourceInfo.modeInfoIdx;
            if (asi >= actModes.Length) continue;
            var want = modes[(int)si].sourceMode.position;
            var cur = actModes[asi].sourceMode.position;
            int ddx = want.x - cur.x, ddy = want.y - cur.y;
            if (dx == null) { dx = ddx; dy = ddy; }
            else if (dx != ddx || dy != ddy) return false;
        }
        return true;
    }

    static (PATH_INFO[] paths, List<MODE_INFO> modes) Build(
        IReadOnlyList<IReadOnlyList<DisplayTarget>> groups, PATH_INFO[] all, PATH_INFO[] act, MODE_INFO[] actModes)
    {
        var mem = PositionMemory.Resolve(groups.Where(g => g.Count == 1).Select(g => g[0].DevicePath).Where(d => d != "").ToList());

        var paths = new List<PATH_INFO>();
        var modes = new List<MODE_INFO>();
        var modeMap = new Dictionary<uint, uint>();
        var usedSources = new HashSet<(long, uint)>();

        uint CopyMode(uint idx)
        {
            if (idx == INVALID || idx >= actModes.Length) return INVALID;
            if (modeMap.TryGetValue(idx, out var n)) return n;
            modes.Add(actModes[idx]);
            n = (uint)(modes.Count - 1);
            modeMap[idx] = n;
            return n;
        }

        // 1) Utökade skärmar som redan är på: behåll upplösning och placering.
        var pending = new List<IReadOnlyList<DisplayTarget>>();
        foreach (var g in groups.Where(g => g.Count > 0))
        {
            if (g.Count == 1)
            {
                int ai = Array.FindIndex(act, a => Match(a.targetInfo, g[0]));
                if (ai >= 0 && usedSources.Add((act[ai].sourceInfo.adapterId.Value, act[ai].sourceInfo.id)))
                {
                    var p = act[ai];
                    p.sourceInfo.modeInfoIdx = CopyMode(p.sourceInfo.modeInfoIdx);
                    p.targetInfo.modeInfoIdx = CopyMode(p.targetInfo.modeInfoIdx);
                    // Lägg skärmen på sin sparade plats (den kan ha flyttats till (0,0) när den var ensam).
                    if (p.sourceInfo.modeInfoIdx != INVALID && mem.TryGetValue(g[0].DevicePath, out var saved))
                    {
                        var m = modes[(int)p.sourceInfo.modeInfoIdx];
                        m.sourceMode.position = saved.Source.sourceMode.position;
                        modes[(int)p.sourceInfo.modeInfoIdx] = m;
                    }
                    paths.Add(p);
                    continue;
                }
            }
            pending.Add(g);
        }

        // 2) Nya skärmar och dupliceringsgrupper: välj en ledig source på grafikkortet, låt Windows välja läge.
        foreach (var g in pending)
        {
            long adapter = g[0].Adapter.Value;
            if (g.Any(t => t.Adapter.Value != adapter))
                throw new InvalidOperationException(
                    "Skärmarna " + string.Join(", ", g.Select(t => t.Label)) +
                    " sitter på olika grafikkort och kan inte dupliceras med varandra.");

            uint? chosen = null;
            foreach (var s in all.Where(p => p.sourceInfo.adapterId.Value == adapter).Select(p => p.sourceInfo.id).Distinct().OrderBy(x => x))
            {
                if (usedSources.Contains((adapter, s))) continue;
                if (g.All(t => all.Any(p => p.sourceInfo.adapterId.Value == adapter && p.sourceInfo.id == s && Match(p.targetInfo, t))))
                {
                    chosen = s;
                    break;
                }
            }
            if (chosen == null)
                throw new InvalidOperationException("Grafikkortet har ingen ledig utgång för: " + string.Join(", ", g.Select(t => t.Label)));

            usedSources.Add((adapter, chosen.Value));
            foreach (var t in g)
            {
                var p = all.First(p => p.sourceInfo.adapterId.Value == adapter && p.sourceInfo.id == chosen.Value && Match(p.targetInfo, t));
                p.flags = PATH_ACTIVE;
                p.sourceInfo.modeInfoIdx = INVALID;
                p.targetInfo.modeInfoIdx = INVALID;

                // En skärm som slås på igen: återställ sparad plats, upplösning och Hz.
                if (g.Count == 1 && mem.TryGetValue(t.DevicePath, out var saved))
                {
                    var (src, tgt, info) = (saved.Source, saved.Target, saved.Info);
                    src.adapterId = p.sourceInfo.adapterId;
                    src.id = p.sourceInfo.id;
                    modes.Add(src);
                    p.sourceInfo.modeInfoIdx = (uint)(modes.Count - 1);
                    if (tgt is MODE_INFO tm)
                    {
                        tm.adapterId = p.targetInfo.adapterId;
                        tm.id = p.targetInfo.id;
                        modes.Add(tm);
                        p.targetInfo.modeInfoIdx = (uint)(modes.Count - 1);
                        p.targetInfo.refreshRate = info.refreshRate;
                        p.targetInfo.scanLineOrdering = info.scanLineOrdering;
                    }
                    p.targetInfo.rotation = info.rotation;
                    p.targetInfo.scaling = info.scaling;
                }
                paths.Add(p);
            }
        }

        // 3) Inga överlapp, inga glapp, huvudskärm på (0,0).
        FixLayout(modes);
        return (paths.ToArray(), modes);
    }

    static void Apply(PATH_INFO[] pa, List<MODE_INFO> modes)
    {
        Quiet();
        const uint flags = SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;
        int r = SetDisplayConfig((uint)pa.Length, pa, (uint)modes.Count, modes.Count > 0 ? modes.ToArray() : null, flags);
        if (r == 0) return;

        // Andra försök: behåll placeringen men låt Windows välja upplösning/Hz för skärmarna.
        var srcOnly = new List<MODE_INFO>();
        var pa2 = (PATH_INFO[])pa.Clone();
        var remap = new Dictionary<uint, uint>();
        for (int i = 0; i < pa2.Length; i++)
        {
            uint si = pa2[i].sourceInfo.modeInfoIdx;
            if (si != INVALID && si < modes.Count)
            {
                if (!remap.TryGetValue(si, out var n)) { srcOnly.Add(modes[(int)si]); n = (uint)(srcOnly.Count - 1); remap[si] = n; }
                pa2[i].sourceInfo.modeInfoIdx = n;
            }
            pa2[i].targetInfo.modeInfoIdx = INVALID;
        }
        if (SetDisplayConfig((uint)pa2.Length, pa2, (uint)srcOnly.Count, srcOnly.Count > 0 ? srcOnly.ToArray() : null, flags) == 0) return;

        // Tredje försök: låt Windows välja allt.
        for (int i = 0; i < pa.Length; i++)
        {
            pa[i].sourceInfo.modeInfoIdx = INVALID;
            pa[i].targetInfo.modeInfoIdx = INVALID;
        }
        int r3 = SetDisplayConfig((uint)pa.Length, pa, 0, null, flags);
        if (r3 != 0) throw Fail("SetDisplayConfig", r);
    }

    /// <summary>Windows inbyggda lägen (samma som Win+P).</summary>
    public static void ApplyTopology(uint topology)
    {
        Quiet();
        int r = SetDisplayConfig(0, null, 0, null, SDC_APPLY | topology);
        if (r != 0) throw Fail("SetDisplayConfig", r);
    }

    public static LayoutSnapshot TakeSnapshot()
    {
        var (act, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var snap = new LayoutSnapshot
        {
            Paths = Convert.ToBase64String(MemoryMarshal.AsBytes(act.AsSpan())),
            Modes = Convert.ToBase64String(MemoryMarshal.AsBytes(modes.AsSpan())),
        };
        var names = new List<string>();
        foreach (var p in act)
        {
            var tn = GetTargetName(p.targetInfo.adapterId, p.targetInfo.id);
            snap.PathDevices.Add(tn?.monitorDevicePath ?? "");
            names.Add(string.IsNullOrWhiteSpace(tn?.monitorFriendlyDeviceName) ? "?" : tn!.Value.monitorFriendlyDeviceName);
        }
        int sources = act.Select(p => (p.sourceInfo.adapterId.Value, p.sourceInfo.id)).Distinct().Count();
        snap.Description = (sources < act.Length ? "dupl. " : "") + string.Join(" + ", names);
        return snap;
    }

    public static void RestoreSnapshot(LayoutSnapshot snap)
    {
        var paths = MemoryMarshal.Cast<byte, PATH_INFO>(Convert.FromBase64String(snap.Paths)).ToArray();
        var modes = MemoryMarshal.Cast<byte, MODE_INFO>(Convert.FromBase64String(snap.Modes)).ToArray();
        Quiet();

        // Grafikkortens LUID ändras vid omstart – mappa om via skärmarnas stabila device path.
        var (all, _) = Query(QDC_ALL_PATHS);
        var current = new Dictionary<string, (LUID adapter, uint target)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in all.Select(p => (p.targetInfo.adapterId, p.targetInfo.id)).DistinctBy(k => (k.adapterId.Value, k.id)))
        {
            var tn = GetTargetName(key.adapterId, key.id);
            if (!string.IsNullOrEmpty(tn?.monitorDevicePath)) current.TryAdd(tn!.Value.monitorDevicePath, (key.adapterId, key.id));
        }

        var luidMap = new Dictionary<long, LUID>();
        for (int i = 0; i < paths.Length; i++)
        {
            string dev = i < snap.PathDevices.Count ? snap.PathDevices[i] : "";
            if (!current.TryGetValue(dev, out var now))
                throw new InvalidOperationException("En skärm i profilen är inte ansluten just nu.");
            luidMap[paths[i].targetInfo.adapterId.Value] = now.adapter;
            paths[i].targetInfo.id = now.target;
        }
        for (int i = 0; i < paths.Length; i++)
        {
            if (luidMap.TryGetValue(paths[i].targetInfo.adapterId.Value, out var l)) paths[i].targetInfo.adapterId = l;
            if (luidMap.TryGetValue(paths[i].sourceInfo.adapterId.Value, out l)) paths[i].sourceInfo.adapterId = l;
        }
        for (int i = 0; i < modes.Length; i++)
            if (luidMap.TryGetValue(modes[i].adapterId.Value, out var l)) modes[i].adapterId = l;

        int r = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
        if (r != 0) throw Fail("SetDisplayConfig", r);
    }
}
