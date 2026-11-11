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
    public string HotkeyHome { get; set; } = "Ctrl+Shift+Alt+H";
    public string? LastProfile { get; set; }
    /// <summary>Dela mus och tangentbord med anslutna datorer.</summary>
    public bool ShareInput { get; set; } = true;
    /// <summary>Dela urklipp (text) med anslutna datorer.</summary>
    public bool ShareClipboard { get; set; } = true;
    /// <summary>Spela upp ljud från anslutna datorer i den här datorns högtalare.</summary>
    public bool ShareAudio { get; set; } = true;
    /// <summary>Volym (0–1) för ljudet från varje dator.</summary>
    public Dictionary<string, float> AudioVolumes { get; set; } = new();
    /// <summary>Skärm → "server" eller klient-id (saknas = automatiskt).</summary>
    public Dictionary<string, string> MonitorOwners { get; set; } = new();
    /// <summary>Skärm → ingångskod (hex) → klient-id: vilken dator som sitter på vilken ingång.</summary>
    public Dictionary<string, Dictionary<string, string>> InputOwners { get; set; } = new();
    /// <summary>Skärm → ingången appen senast bytte till.</summary>
    public Dictionary<string, byte> LastInputs { get; set; } = new();
    /// <summary>Skärmar som inte rapporterar rätt ingång via DDC/CI (svarar alltid med sin egen port).</summary>
    public List<string> UnreliableInput { get; set; } = new();
    /// <summary>Skärm → klient-id → vilken av klientens skärmar det är ("EDID|namn"), om man valt själv.</summary>
    public Dictionary<string, Dictionary<string, string>> ClientMonitors { get; set; } = new();
    public int Version { get; set; }
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
            current.LastInputs = new Dictionary<string, byte>(current.LastInputs, StringComparer.OrdinalIgnoreCase);
            if (current.Version < 2)
            {
                // Tidigare var "Visar: …" en permanent låsning som kunde visa fel dator. Nu är det en
                // tillfällig upplysning som släpps när ingången byts – rensa de gamla låsningarna.
                current.MonitorOwners.Clear();
                current.Version = 2;
                Save();
            }
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

    public static string HotkeyHome
    {
        get { lock (Gate) return S.HotkeyHome; }
        set { lock (Gate) { S.HotkeyHome = value; Save(); } }
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

    public static bool ShareClipboard
    {
        get { lock (Gate) return S.ShareClipboard; }
        set { lock (Gate) { S.ShareClipboard = value; Save(); } }
    }

    /// <summary>
    /// Appen har bytt skärmens ingång. Kom ihåg det, och släpp en manuell "Visar: …"-upplysning –
    /// nu vet appen själv vad skärmen visar.
    /// </summary>
    public static void SetLastInput(string dev, byte code)
    {
        lock (Gate)
        {
            S.LastInputs[dev] = code;
            foreach (var k in S.MonitorOwners.Keys.Where(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)).ToList())
                S.MonitorOwners.Remove(k);
            Save();
        }
    }

    public static string? ClientMonitorFor(string dev, string clientId)
    {
        lock (Gate)
        {
            var map = S.ClientMonitors.FirstOrDefault(kv => string.Equals(kv.Key, dev, StringComparison.OrdinalIgnoreCase)).Value;
            return map?.GetValueOrDefault(clientId);
        }
    }

    public static void SetClientMonitorFor(string dev, string clientId, string? ident)
    {
        lock (Gate)
        {
            var key = S.ClientMonitors.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)) ?? dev;
            if (!S.ClientMonitors.TryGetValue(key, out var map)) S.ClientMonitors[key] = map = new();
            if (ident == null) map.Remove(clientId); else map[clientId] = ident;
            Save();
        }
    }

    /// <summary>
    /// Kopplingstabellen: dator ("server" eller klient-id) sitter på ingången code på skärmen dev
    /// (null = ingen/okänd). En ingång kan bara höra till en dator, och en dator till en ingång per skärm.
    /// </summary>
    public static void SetConnection(string dev, string computer, byte? code)
    {
        lock (Gate)
        {
            var key = S.InputOwners.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)) ?? dev;
            if (!S.InputOwners.TryGetValue(key, out var map)) S.InputOwners[key] = map = new();
            var homeKey = S.HomeInputs.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase));

            // Släpp datorns tidigare ingång.
            if (computer == "server") { if (homeKey != null) S.HomeInputs.Remove(homeKey); }
            else foreach (var k in map.Where(kv => kv.Value == computer).Select(kv => kv.Key).ToList()) map.Remove(k);

            if (code is byte c)
            {
                // Ingången tas från den dator som hade den.
                map.Remove(c.ToString("X2"));
                homeKey = S.HomeInputs.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase));
                if (homeKey != null && S.HomeInputs[homeKey] == c && computer != "server") S.HomeInputs.Remove(homeKey);
                if (computer == "server") S.HomeInputs[dev] = c;
                else map[c.ToString("X2")] = computer;
            }
            Save();
        }
    }

    /// <summary>En avläsning som går att lita på: den blir sanningen om vad skärmen visar.</summary>
    public static void ObserveInput(string dev, byte code)
    {
        lock (Gate)
        {
            if (S.LastInputs.TryGetValue(dev, out var cur) && cur == code) return;
            S.LastInputs[dev] = code;
            foreach (var k in S.MonitorOwners.Keys.Where(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase)).ToList())
                S.MonitorOwners.Remove(k); // nu vet appen själv – släpp en manuell upplysning
            Save();
        }
    }

    /// <summary>
    /// Rensa kopplingar som inte kan stämma: ingångar skärmen inte har, eller den här datorns egen ingång.
    /// (Tidigare versioner gissade kopplingar i det tysta och kunde spara fel.)
    /// </summary>
    public static void PruneInputOwners(string dev, IReadOnlyCollection<byte> supported)
    {
        if (supported.Count == 0) return;
        lock (Gate)
        {
            var key = S.InputOwners.Keys.FirstOrDefault(k => string.Equals(k, dev, StringComparison.OrdinalIgnoreCase));
            if (key == null) return;
            var map = S.InputOwners[key];
            byte? home = S.HomeInputs.TryGetValue(dev, out var h) ? h : null;
            var bad = map.Keys.Where(k => !byte.TryParse(k, System.Globalization.NumberStyles.HexNumber, null, out var c)
                                          || !supported.Contains(c) || c == home).ToList();
            if (bad.Count == 0) return;
            foreach (var b in bad) map.Remove(b);
            Save();
        }
    }

    public static byte? LastInput(string dev)
    {
        lock (Gate) return S.LastInputs.TryGetValue(dev, out var c) ? c : null;
    }

    public static bool IsInputUnreliable(string dev)
    {
        lock (Gate) return S.UnreliableInput.Contains(dev, StringComparer.OrdinalIgnoreCase);
    }

    public static void SetInputUnreliable(string dev, bool unreliable)
    {
        lock (Gate)
        {
            bool has = S.UnreliableInput.Contains(dev, StringComparer.OrdinalIgnoreCase);
            if (has == unreliable) return;
            if (unreliable) S.UnreliableInput.Add(dev);
            else S.UnreliableInput.RemoveAll(d => string.Equals(d, dev, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    /// <summary>
    /// Ingången skärmen visar: det DDC/CI säger, utom för skärmar som inte rapporterar rätt –
    /// då gäller den ingång appen senast bytte till.
    /// </summary>
    public static byte? EffectiveInput(string dev, byte? readBack) =>
        IsInputUnreliable(dev) && LastInput(dev) is byte last ? last : readBack;

    public static bool ShareAudio
    {
        get { lock (Gate) return S.ShareAudio; }
        set { lock (Gate) { S.ShareAudio = value; Save(); } }
    }

    public static float AudioVolume(string clientId)
    {
        lock (Gate) return S.AudioVolumes.TryGetValue(clientId, out var v) ? v : 1f;
    }

    public static void SetAudioVolume(string clientId, float volume)
    {
        lock (Gate) { S.AudioVolumes[clientId] = Math.Clamp(volume, 0f, 1f); Save(); }
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
