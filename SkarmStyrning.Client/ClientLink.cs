using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkarmStyrning.Net;

namespace SkarmStyrning.Client;

sealed class ClientConfig
{
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.MachineName;
    public string? ServerAddress { get; set; }
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public string? KeyProtected { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkarmStyrningKlient");
    static readonly string FilePath = Path.Combine(Dir, "klient.json");

    public static ClientConfig Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        var c = new ClientConfig();
        c.Save();
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Nyckeln sparas krypterad med Windows (DPAPI) – bara du på den här datorn kan läsa den.</summary>
    public byte[]? Key
    {
        get
        {
            if (KeyProtected == null) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), null, DataProtectionScope.CurrentUser); }
            catch { return null; }
        }
        set => KeyProtected = value == null ? null : Convert.ToBase64String(ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser));
    }

    public bool IsPaired => ServerAddress != null && KeyProtected != null;
}

/// <summary>Förbindelsen till servern: ansluter, håller kontakten och spelar upp mus/tangentbord.</summary>
sealed class ClientLink : IDisposable
{
    public ClientConfig Config { get; } = ClientConfig.Load();
    public event Action<string, bool, bool>? StatusChanged; // text, ansluten, styrs just nu

    CancellationTokenSource? cts;
    SecureChannel? channel;
    bool controlled;

    public void Start()
    {
        Stop();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() => Loop(token));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    public void Stop()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        cts?.Cancel();
        channel?.Dispose();
        channel = null;
    }

    public void Dispose() => Stop();

    void Status(string text, bool connected) => StatusChanged?.Invoke(text, connected, controlled);

    void OnDisplayChanged(object? s, EventArgs e) => _ = SendInfo();

    async Task SendInfo()
    {
        var ch = channel;
        if (ch == null) return;
        try
        {
            var info = new ClientInfo { Name = Config.Name, Monitors = LocalMonitors.Read() };
            await ch.Send(MsgType.Info, Proto.ToJson(info));
        }
        catch { }
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Config.IsPaired)
            {
                Status("Inte parkopplad – ange serverns adress och koden som visas i Skärmstyrning.", false);
                return;
            }
            try
            {
                Status($"Ansluter till {Config.ServerName ?? Config.ServerAddress} …", false);
                using var tcp = await Connect(Config.ServerAddress!, ct);
                var (ch, server, _) = await SecureChannel.ConnectAsClient(tcp.GetStream(), Config.ClientId, Config.Name, "auth", Config.Key!, ct);
                if (Config.ServerId != null && server.Id != Config.ServerId)
                    throw new UnauthorizedAccessException("En annan server svarade på adressen.");
                channel = ch;
                Status($"Ansluten till {server.Name}", true);
                await SendInfo();
                await Receive(ch, ct);
            }
            catch (UnauthorizedAccessException ex) { Status(ex.Message, false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Status("Ingen kontakt med servern: " + Short(ex) + " – försöker igen …", false); }
            finally
            {
                channel?.Dispose();
                channel = null;
                if (controlled) { controlled = false; Injector.ReleaseAll(); }
            }
            try { await Task.Delay(5000, ct); } catch { return; }
        }
    }

    async Task Receive(SecureChannel ch, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (type, p) = await ch.Receive(ct);
            switch (type)
            {
                case MsgType.Enter:
                    controlled = true;
                    var (ex, ey) = Wire.ReadPoint(p);
                    Injector.MoveTo(ex, ey);
                    Status($"Styrs från {Config.ServerName} – Ctrl+Shift+Alt + valfri tangent tar tillbaka musen", true);
                    break;
                case MsgType.Leave:
                    controlled = false;
                    Injector.ReleaseAll();
                    Status($"Ansluten till {Config.ServerName}", true);
                    break;
                case MsgType.MouseMove:
                    var (x, y) = Wire.ReadPoint(p);
                    Injector.MoveTo(x, y);
                    break;
                case MsgType.MouseButton:
                    Injector.Button(p[0], p[1] != 0);
                    break;
                case MsgType.Wheel:
                    var (delta, h) = Wire.ReadWheel(p);
                    Injector.Wheel(delta, h);
                    break;
                case MsgType.Key:
                    var (vk, scan, flags) = Wire.ReadKey(p);
                    Injector.Key(vk, scan, flags);
                    break;
                case MsgType.ReleaseAll:
                    Injector.ReleaseAll();
                    break;
                case MsgType.Ping:
                    await ch.Send(MsgType.Pong, p, ct);
                    break;
            }
        }
    }

    static string Short(Exception ex) => ex switch
    {
        SocketException se when se.SocketErrorCode == SocketError.ConnectionRefused => "servern svarar inte (är Skärmstyrning igång?)",
        SocketException se when se.SocketErrorCode is SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
            => "nätverket når inte servern (VPN eller brandvägg?)",
        OperationCanceledException => "tog för lång tid (VPN eller brandvägg?)",
        EndOfStreamException or IOException => "förbindelsen bröts",
        _ => ex.Message,
    };

    static async Task<TcpClient> Connect(string address, CancellationToken ct)
    {
        string host = address;
        int port = Proto.Port;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out int p)) { host = address[..colon]; port = p; }
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try { await tcp.ConnectAsync(host, port, timeout.Token); }
        catch { tcp.Dispose(); throw; }
        return tcp;
    }

    /// <summary>Parkoppla med koden som visas på servern.</summary>
    public async Task Pair(string address, string code)
    {
        Stop();
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tcp = await Connect(address, cts2.Token);
        var (ch, server, accept) = await SecureChannel.ConnectAsClient(tcp.GetStream(), Config.ClientId, Config.Name, "pair",
            Encoding.UTF8.GetBytes(code.Trim()), cts2.Token);
        ch.Dispose();
        if (accept.PairKey == null) throw new InvalidOperationException("Servern skickade ingen nyckel.");
        Config.ServerAddress = address;
        Config.ServerId = server.Id;
        Config.ServerName = server.Name;
        Config.Key = Convert.FromBase64String(accept.PairKey);
        Config.Save();
        Start();
    }

    public void Forget()
    {
        Stop();
        Config.ServerAddress = null;
        Config.ServerId = null;
        Config.ServerName = null;
        Config.Key = null;
        Config.Save();
        Status("Inte parkopplad.", false);
    }

    /// <summary>Kollar bara att en TCP-anslutning går igenom (för felsökning av VPN/brandvägg).</summary>
    public static async Task<string> Test(string address)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tcp = await Connect(address, CancellationToken.None);
            return $"✓ Servern svarar ({sw.ElapsedMilliseconds} ms). Nätverket fungerar.";
        }
        catch (Exception ex) { return "✗ " + Short(ex); }
    }

    /// <summary>Letar efter servrar på det lokala nätet (fungerar inte alltid via VPN).</summary>
    public static async Task<List<(string name, string address)>> Discover()
    {
        var found = new List<(string, string)>();
        using var udp = new UdpClient { EnableBroadcast = true };
        var query = Encoding.UTF8.GetBytes(Proto.DiscoveryQuery);
        try { await udp.SendAsync(query, new IPEndPoint(IPAddress.Broadcast, Proto.DiscoveryPort)); }
        catch { return found; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (!text.StartsWith(Proto.DiscoveryReplyPrefix)) continue;
                var parts = text[Proto.DiscoveryReplyPrefix.Length..].Split('|');
                string addr = r.RemoteEndPoint.Address.ToString();
                if (parts.Length > 1 && int.TryParse(parts[1], out int port) && port != Proto.Port) addr += ":" + port;
                if (!found.Any(f => f.Item2 == addr)) found.Add((parts[0], addr));
            }
        }
        catch (OperationCanceledException) { }
        return found;
    }
}
