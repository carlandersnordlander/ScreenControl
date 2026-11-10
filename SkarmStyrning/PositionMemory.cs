using System.Runtime.InteropServices;
using System.Text.Json;

namespace SkarmStyrning;

/// <summary>En skärms plats i huvudlayouten, plus upplösning och Hz som den ska få tillbaka.</summary>
public sealed class SavedDisplay
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string SourceMode { get; set; } = "";   // MODE_INFO (source) som base64
    public string? TargetMode { get; set; }        // MODE_INFO (target) som base64
    public string TargetInfo { get; set; } = "";   // PATH_TARGET_INFO som base64
}

public sealed record LayoutItem(string Dev, string Name, int X, int Y, int Width, int Height);

internal readonly record struct Placement(MODE_INFO Source, MODE_INFO? Target, PATH_TARGET_INFO Info);

/// <summary>
/// Huvudlayouten: var varje skärm man någonsin haft sitter, i ett gemensamt koordinatsystem
/// (som i Windows inställningar, huvudskärmen på 0,0). Skärmar som är av behåller sin plats.
/// När bara några skärmar är på används deras platser härifrån och glapp skjuts ihop.
/// Ändras av användaren (dra i appen eller i Windows inställningar) – aldrig av att en skärm
/// försvinner eller kommer tillbaka.
/// </summary>
public static class PositionMemory
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkarmStyrning", "skarmlayout.json");
    static readonly object Gate = new();
    static Dictionary<string, SavedDisplay>? data;

    public static event Action? Changed;

    static Dictionary<string, SavedDisplay> Data
    {
        get
        {
            if (data != null) return data;
            try
            {
                data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, SavedDisplay>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch { data = new(); }
            data = new Dictionary<string, SavedDisplay>(data, StringComparer.OrdinalIgnoreCase);
            return data;
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* minnet är en bekvämlighet – fel här ska inte stoppa något */ }
        Changed?.Invoke();
    }

    static string ToB64<T>(T value) where T : unmanaged => Convert.ToBase64String(MemoryMarshal.AsBytes(new[] { value }.AsSpan()));
    static T FromB64<T>(string s) where T : unmanaged => MemoryMarshal.Cast<byte, T>(Convert.FromBase64String(s))[0];

    public static bool IsEmpty { get { lock (Gate) return Data.Count == 0; } }

    /// <summary>
    /// Tar in platser från Windows. Windows-koordinaterna kan vara förskjutna jämfört med
    /// huvudlayouten (t.ex. om huvudskärmen är avstängd), så förskjutningen räknas ut från de
    /// skärmar som redan finns och som inte flyttats.
    /// onlyNew = lägg bara till skärmar som inte redan finns.
    /// </summary>
    internal static void Learn(IReadOnlyList<(string dev, string name, MODE_INFO source, MODE_INFO? target, PATH_TARGET_INFO info)> list, bool onlyNew)
    {
        if (list.Count == 0) return;
        lock (Gate)
        {
            var known = list.Where(d => Data.ContainsKey(d.dev)).ToList();
            if (onlyNew && known.Count == list.Count) return;

            (int dx, int dy) shift = (0, 0);
            if (known.Count > 0)
                shift = known
                    .GroupBy(d => (d.source.sourceMode.position.x - Data[d.dev].X, d.source.sourceMode.position.y - Data[d.dev].Y))
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => Math.Abs((long)g.Key.Item1) + Math.Abs((long)g.Key.Item2))
                    .First().Key;

            foreach (var d in list)
            {
                if (onlyNew && Data.ContainsKey(d.dev)) continue;
                var s = d.source.sourceMode;
                Data[d.dev] = new SavedDisplay
                {
                    Name = d.name,
                    X = s.position.x - shift.dx,
                    Y = s.position.y - shift.dy,
                    Width = (int)s.width,
                    Height = (int)s.height,
                    SourceMode = ToB64(d.source),
                    TargetMode = d.target is MODE_INFO t ? ToB64(t) : null,
                    TargetInfo = ToB64(d.info),
                };
            }
            Save();
        }
    }

    static Placement? Decode(SavedDisplay d)
    {
        try
        {
            var src = FromB64<MODE_INFO>(d.SourceMode);
            src.sourceMode.position.x = d.X;
            src.sourceMode.position.y = d.Y;
            return new Placement(src,
                d.TargetMode != null ? FromB64<MODE_INFO>(d.TargetMode) : null,
                FromB64<PATH_TARGET_INFO>(d.TargetInfo));
        }
        catch { return null; }
    }

    internal static Dictionary<string, Placement> Resolve(IReadOnlyCollection<string> devs)
    {
        var result = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
            foreach (var dev in devs)
                if (Data.TryGetValue(dev, out var sd) && Decode(sd) is Placement p)
                    result[dev] = p;
        return result;
    }

    internal static Placement? Latest(string dev)
    {
        if (string.IsNullOrEmpty(dev)) return null;
        lock (Gate) return Data.TryGetValue(dev, out var sd) ? Decode(sd) : null;
    }

    public static List<LayoutItem> Items()
    {
        lock (Gate)
            return Data.Select(kv => new LayoutItem(kv.Key, kv.Value.Name, kv.Value.X, kv.Value.Y, kv.Value.Width, kv.Value.Height)).ToList();
    }

    /// <summary>Sparar platser från placeringsvyn. Flyttar allt så att vald huvudskärm hamnar på 0,0.</summary>
    public static void SetPositions(IEnumerable<(string dev, int x, int y)> positions, string? primaryDev = null)
    {
        lock (Gate)
        {
            var list = positions.Where(p => Data.ContainsKey(p.dev)).ToList();
            if (list.Count == 0) return;
            var origin = list.FirstOrDefault(p => string.Equals(p.dev, primaryDev, StringComparison.OrdinalIgnoreCase));
            if (origin.dev == null) origin = list.FirstOrDefault(p => p.x == 0 && p.y == 0);
            if (origin.dev == null) origin = list[0];
            foreach (var p in list)
            {
                Data[p.dev].X = p.x - origin.x;
                Data[p.dev].Y = p.y - origin.y;
            }
            Save();
        }
    }

    public static void Forget(string dev)
    {
        lock (Gate) { if (Data.Remove(dev)) Save(); }
    }
}
