using System.Text.Json;

namespace SkarmStyrning;

public sealed class AppSettings
{
    /// <summary>Anpassa automatiskt när skärmar stängs av, byter källa eller kommer tillbaka.</summary>
    public bool AutoAdapt { get; set; } = true;
    /// <summary>Senaste valet var "Utöka alla" – bara då anpassar appen automatiskt.</summary>
    public bool ExtendIntent { get; set; }
    /// <summary>Skärmens device path → den ingång som den här datorn sitter på.</summary>
    public Dictionary<string, byte> HomeInputs { get; set; } = new();
    /// <summary>Skärmar som har svarat på DDC/CI någon gång (tystnad betyder då något).</summary>
    public List<string> DdcCapable { get; set; } = new();
    /// <summary>"system", "light" eller "dark".</summary>
    public string Theme { get; set; } = "system";
    public string HotkeyNextProfile { get; set; } = "Ctrl+Shift+Alt+P";
    public string HotkeyExtend { get; set; } = "Ctrl+Shift+Alt+E";
    public string? LastProfile { get; set; }
    /// <summary>Dela mus och tangentbord med anslutna datorer.</summary>
    public bool ShareInput { get; set; } = true;
    /// <summary>Skärm → "server" eller klient-id (saknas = automatiskt).</summary>
    public Dictionary<string, string> MonitorOwners { get; set; } = new();
    /// <summary>Skärm → ingångskod (hex) → klient-id: vilken dator som sitter på vilken ingång.</summary>
    public Dictionary<string, Dictionary<string, string>> InputOwners { get; set; } = new();
}

public static class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkarmStyrning", "installningar.json");
    static readonly object Gate = new();
    static AppSettings? current;

    static AppSettings S
    {
        get
        {
            if (current != null) return current;
            try { current = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) : null; }
            catch { current = null; }
            current ??= new AppSettings();
            current.HomeInputs = new Dictionary<string, byte>(current.HomeInputs, StringComparer.OrdinalIgnoreCase);
            return current;
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(S, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool AutoAdapt
    {
        get { lock (Gate) return S.AutoAdapt; }
        set { lock (Gate) { S.AutoAdapt = value; Save(); } }
    }

    public static string HotkeyNextProfile
    {
        get { lock (Gate) return S.HotkeyNextProfile; }
        set { lock (Gate) { S.HotkeyNextProfile = value; Save(); } }
    }

    public static string HotkeyExtend
    {
        get { lock (Gate) return S.HotkeyExtend; }
        set { lock (Gate) { S.HotkeyExtend = value; Save(); } }
    }

    public static string? LastProfile
    {
        get { lock (Gate) return S.LastProfile; }
        set { lock (Gate) { S.LastProfile = value; Save(); } }
    }

    public static bool ShareInput
    {
        get { lock (Gate) return S.ShareInput; }
        set { lock (Gate) { S.ShareInput = value; Save(); } }
    }

    public static string? MonitorOwner(string dev)
    {
        lock (Gate) return S.MonitorOwners.FirstOrDefault(kv => string.Equals(kv.Key, dev, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static void SetMonitorOwner(string dev, string? owner)
    {
        lock (Gate)
        {
            foreach (var k in S.MonitorOwners.Keys.Where(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)).ToList()) S.MonitorOwners.Remove(k);
            if (owner != null) S.MonitorOwners[dev] = owner;
            Save();
        }
    }

    public static string? InputOwner(string dev, byte code)
    {
        lock (Gate)
        {
            var map = S.InputOwners.FirstOrDefault(kv => string.Equals(kv.Key, dev, StringComparison.OrdinalIgnoreCase)).Value;
            return map?.GetValueOrDefault(code.ToString("X2"));
        }
    }

    public static void SetInputOwner(string dev, byte code, string? owner)
    {
        lock (Gate)
        {
            var key = S.InputOwners.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)) ?? dev;
            if (!S.InputOwners.TryGetValue(key, out var map)) S.InputOwners[key] = map = new();
            if (owner == null) map.Remove(code.ToString("X2")); else map[code.ToString("X2")] = owner;
            Save();
        }
    }

    public static string Theme
    {
        get { lock (Gate) return S.Theme; }
        set { lock (Gate) { S.Theme = value; Save(); } }
    }

    public static bool ExtendIntent
    {
        get { lock (Gate) return S.ExtendIntent; }
        set { lock (Gate) { if (S.ExtendIntent == value) return; S.ExtendIntent = value; Save(); } }
    }

    /// <summary>Vilka ingångar som hör ihop med kabeltypen datorn använder.</summary>
    static byte[] Family(uint outputTech) => outputTech switch
    {
        0 => new byte[] { 0x01, 0x02 },             // VGA
        4 => new byte[] { 0x03, 0x04 },             // DVI
        5 => new byte[] { 0x11, 0x12 },             // HDMI
        10 => new byte[] { 0x0F, 0x10, 0x1B },      // DisplayPort (även USB-C)
        _ => Array.Empty<byte>(),
    };

    public static byte? HomeInput(string dev)
    {
        lock (Gate) return S.HomeInputs.TryGetValue(dev, out var h) ? h : null;
    }

    public static void SetHomeInput(string dev, byte code)
    {
        lock (Gate) { S.HomeInputs[dev] = code; Save(); }
    }

    /// <summary>true = visar den här datorn, false = visar en annan källa, null = vet inte.</summary>
    public static bool? IsHome(DisplayTarget t, byte input)
    {
        lock (Gate)
        {
            if (S.HomeInputs.TryGetValue(t.DevicePath, out var h)) return input == h;
            if (!Ddc.StandardInputs.ContainsKey(input)) return null; // tillverkarspecifik kod – gissa inte
            var fam = Family(t.OutputTech);
            return fam.Length == 0 ? null : fam.Contains(input);
        }
    }

    /// <summary>Lär sig datorns ingång när det är entydigt (t.ex. skärmen har bara en DVI-ingång).</summary>
    public static void LearnHome(DisplayTarget t, byte input, IReadOnlyCollection<byte>? supported)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(t.DevicePath) || S.HomeInputs.ContainsKey(t.DevicePath)) return;
            var fam = Family(t.OutputTech);
            if (!fam.Contains(input) || supported == null || supported.Count == 0) return;
            if (supported.Count(fam.Contains) != 1) return;
            S.HomeInputs[t.DevicePath] = input;
            Save();
        }
    }

    public static bool IsCapable(string dev)
    {
        lock (Gate) return S.DdcCapable.Contains(dev, StringComparer.OrdinalIgnoreCase);
    }

    public static void MarkCapable(string dev)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(dev) || S.DdcCapable.Contains(dev, StringComparer.OrdinalIgnoreCase)) return;
            S.DdcCapable.Add(dev);
            Save();
        }
    }
}
