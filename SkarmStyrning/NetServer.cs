using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkarmStyrning.Net;

namespace SkarmStyrning;

public sealed class PairedClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyProtected { get; set; } = "";
    public DateTime Added { get; set; }
}

/// <summary>En ansluten klient (t.ex. jobbdatorn).</summary>
public sealed class ClientSession
{
    public required string Id;
    public required string Name;
    public required string Address;
    public required SecureChannel Channel;
    public ClientInfo? Info;
    public DateTime Since = DateTime.Now;
    public readonly BlockingCollection<(MsgType type, byte[] payload)> Outbox = new(new ConcurrentQueue<(MsgType, byte[])>(), 4096);

    /// <summary>Köar ett meddelande – anropas från tangentbords-/muskroken, får aldrig blockera.</summary>
    public void Post(MsgType type, byte[]? payload = null) => Outbox.TryAdd((type, payload ?? Array.Empty<byte>()));
}

/// <summary>
/// Servern: tar emot klienter på TCP-port 47811, svarar på sökningar (UDP 47812) och sköter
/// parkopplingar. Allt efter handskakningen är krypterat (se SecureChannel).
/// </summary>
public sealed class NetServer : IDisposable
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkarmStyrning");
    static readonly string ClientsFile = Path.Combine(Dir, "datorer.json");
    static readonly string IdFile = Path.Combine(Dir, "server-id.txt");

    readonly object gate = new();
    readonly List<PairedClient> paired;
    readonly Dictionary<string, ClientSession> sessions = new();
    readonly CancellationTokenSource cts = new();
    TcpListener? listener;
    UdpClient? discovery;

    public string ServerId { get; }
    public string ServerName { get; } = Environment.MachineName;
    public string? ListenError { get; private set; }
    public string? PairCode { get; private set; }
    public DateTime PairUntil { get; private set; }
    int failedAttempts;

    /// <summary>Något ändrades (ny klient, frånkopplad, ny skärminfo). Anropas från bakgrundstrådar.</summary>
    public event Action? Changed;
    /// <summary>En dator blev nyss parkopplad.</summary>
    public event Action<string>? Paired;
    /// <summary>En session försvann – kroken måste släppa den.</summary>
    public event Action<string>? Disconnected;

    public NetServer()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(IdFile)) ServerId = File.ReadAllText(IdFile).Trim();
        else { ServerId = Guid.NewGuid().ToString("N"); File.WriteAllText(IdFile, ServerId); }
        try { paired = File.Exists(ClientsFile) ? JsonSerializer.Deserialize<List<PairedClient>>(File.ReadAllText(ClientsFile)) ?? new() : new(); }
        catch { paired = new(); }
    }

    public void Start()
    {
        try
        {
            listener = new TcpListener(IPAddress.Any, Proto.Port);
            listener.Start();
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex) { ListenError = $"Kan inte lyssna på port {Proto.Port}: {ex.Message}"; }
        try
        {
            discovery = new UdpClient(new IPEndPoint(IPAddress.Any, Proto.DiscoveryPort));
            _ = Task.Run(DiscoveryLoop);
        }
        catch { /* sökning är frivillig – man kan alltid skriva in adressen */ }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { listener?.Stop(); } catch { }
        try { discovery?.Dispose(); } catch { }
        lock (gate) foreach (var s in sessions.Values) { s.Outbox.CompleteAdding(); s.Channel.Dispose(); }
    }

    // ---------- Publikt läge ----------

    public List<PairedClient> PairedClients() { lock (gate) return paired.ToList(); }
    public List<ClientSession> Sessions() { lock (gate) return sessions.Values.ToList(); }
    public ClientSession? Session(string id) { lock (gate) return sessions.GetValueOrDefault(id); }

    public string StartPairing()
    {
        PairCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        PairUntil = DateTime.Now.AddMinutes(5);
        failedAttempts = 0;
        Changed?.Invoke();
        return PairCode;
    }

    public void StopPairing()
    {
        PairCode = null;
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        ClientSession? s;
        lock (gate)
        {
            paired.RemoveAll(p => p.Id == id);
            SaveClients();
            sessions.Remove(id, out s);
        }
        if (s != null) { s.Outbox.CompleteAdding(); s.Channel.Dispose(); Disconnected?.Invoke(id); }
        Changed?.Invoke();
    }

    public void Rename(string id, string name)
    {
        lock (gate)
        {
            var p = paired.FirstOrDefault(x => x.Id == id);
            if (p == null) return;
            p.Name = name;
            SaveClients();
            if (sessions.TryGetValue(id, out var s)) s.Name = name;
        }
        Changed?.Invoke();
    }

    /// <summary>IPv4-adresser som klienten kan ansluta till.</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<(int prio, string addr)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            bool virt = ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                        || ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                list.Add((virt ? 1 : 0, ua.Address.ToString()));
            }
        }
        return list.OrderBy(x => x.prio).Select(x => x.addr).Distinct().ToList();
    }

    void SaveClients()
    {
        try { File.WriteAllText(ClientsFile, JsonSerializer.Serialize(paired, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    static byte[]? Unprotect(string b64)
    {
        try { return ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    // ---------- Nätverk ----------

    async Task DiscoveryLoop()
    {
        var reply = Encoding.UTF8.GetBytes($"{Proto.DiscoveryReplyPrefix}{ServerName}|{Proto.Port}|{ServerId}");
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await discovery!.ReceiveAsync(cts.Token);
                if (Encoding.UTF8.GetString(r.Buffer) == Proto.DiscoveryQuery)
                    await discovery.SendAsync(reply, r.RemoteEndPoint, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); }
        }
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener!.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); continue; }
            tcp.NoDelay = true;
            _ = Task.Run(() => Handle(tcp));
        }
    }

    async Task Handle(TcpClient tcp)
    {
        string address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        ClientSession? session = null;
        bool pairing = false;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var (ch, hello) = await SecureChannel.AcceptAsServer(tcp.GetStream(), ServerId, ServerName, h =>
            {
                if (h.Mode == "pair")
                {
                    pairing = true;
                    if (PairCode == null || DateTime.Now > PairUntil) return null;
                    return Encoding.UTF8.GetBytes(PairCode);
                }
                PairedClient? p;
                lock (gate) p = paired.FirstOrDefault(x => x.Id == h.Id);
                return p == null ? null : Unprotect(p.KeyProtected);
            }, handshakeTimeout.Token);

            string name = hello.Name;
            if (pairing)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                lock (gate)
                {
                    paired.RemoveAll(p => p.Id == hello.Id);
                    paired.Add(new PairedClient
                    {
                        Id = hello.Id, Name = string.IsNullOrWhiteSpace(hello.Name) ? "Dator" : hello.Name,
                        KeyProtected = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)),
                        Added = DateTime.Now,
                    });
                    SaveClients();
                }
                PairCode = null; // en kod = en dator
                await ch.Send(MsgType.Accept, Proto.ToJson(new AcceptMsg { ServerId = ServerId, ServerName = ServerName, PairKey = Convert.ToBase64String(key) }));
                ch.Dispose();
                Paired?.Invoke(name);
                Changed?.Invoke();
                return; // klienten ansluter igen med den nya nyckeln
            }

            lock (gate) name = paired.FirstOrDefault(x => x.Id == hello.Id)?.Name ?? name;
            await ch.Send(MsgType.Accept, Proto.ToJson(new AcceptMsg { ServerId = ServerId, ServerName = ServerName }));

            session = new ClientSession { Id = hello.Id, Name = name, Address = address, Channel = ch };
            ClientSession? old;
            lock (gate) { sessions.Remove(hello.Id, out old); sessions[hello.Id] = session; }
            if (old != null) { old.Outbox.CompleteAdding(); old.Channel.Dispose(); }
            Changed?.Invoke();

            var writer = Task.Run(() => Writer(session));
            var pinger = Task.Run(() => Pinger(session));
            while (true)
            {
                var (type, p) = await ch.Receive(cts.Token);
                if (type == MsgType.Info)
                {
                    session.Info = Proto.FromJson<ClientInfo>(p);
                    Changed?.Invoke();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (pairing && PairCode != null && ++failedAttempts >= 5)
            {
                PairCode = null; // för många fel – kräv en ny kod
                Changed?.Invoke();
            }
        }
        catch { /* bortkopplad */ }
        finally
        {
            if (session != null)
            {
                bool removed;
                lock (gate) removed = sessions.TryGetValue(session.Id, out var cur) && cur == session && sessions.Remove(session.Id);
                session.Outbox.CompleteAdding();
                session.Channel.Dispose();
                if (removed) { Disconnected?.Invoke(session.Id); Changed?.Invoke(); }
            }
            tcp.Dispose();
        }
    }

    static void Writer(ClientSession s)
    {
        try
        {
            foreach (var (type, payload) in s.Outbox.GetConsumingEnumerable())
                s.Channel.Send(type, payload).GetAwaiter().GetResult();
        }
        catch { try { s.Channel.Dispose(); } catch { } }
    }

    static async Task Pinger(ClientSession s)
    {
        while (!s.Outbox.IsAddingCompleted)
        {
            await Task.Delay(5000);
            s.Post(MsgType.Ping, BitConverter.GetBytes(DateTime.UtcNow.Ticks));
        }
    }
}
