using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkarmStyrning.Net;

/// <summary>Gemensamt för servern (datorn med skärmarna) och klienten (t.ex. jobbdatorn).</summary>
public static class Proto
{
    public const int Port = 47811;
    public const int DiscoveryPort = 47812;
    public const int Version = 1;
    public const string DiscoveryQuery = "SKARMSTYRNING?1";
    public const string DiscoveryReplyPrefix = "SKARMSTYRNING!1|";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static byte[] ToJson<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static T FromJson<T>(ReadOnlySpan<byte> data) => JsonSerializer.Deserialize<T>(data, Json)!;
}

public enum MsgType : byte
{
    Proof = 1,        // klient → server: första krypterade meddelandet (bevisar rätt kod/nyckel)
    Accept = 2,       // server → klient: välkommen (+ ny parkopplingsnyckel)
    Info = 10,        // klient → server: namn och skärmar (JSON)
    Enter = 20,       // server → klient: pekaren kommer in (x, y)
    Leave = 21,       // server → klient: pekaren lämnar
    MouseMove = 22,   // x, y (klientens skrivbordskoordinater)
    MouseButton = 23, // knapp, ned/upp
    Wheel = 24,       // delta, horisontell
    Key = 25,         // vk, scan, flaggor
    ReleaseAll = 26,  // släpp alla nedtryckta tangenter/knappar
    SetInput = 27,    // server → klient: byt ingång på en skärm (JSON SetInputMsg)
    InputResult = 28, // klient → server: svar (JSON InputResultMsg)
    Clipboard = 40,   // båda hållen: urklippets text (UTF-8)
    ClipboardPolicy = 41, // server → klient: 1 = dela urklipp, 0 = dela inte
    Ping = 30,
    Pong = 31,
}

public sealed class HelloMsg
{
    public int Version { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "";   // "pair" eller "auth" (bara klienten)
    public string Pub { get; set; } = "";    // ECDH P-256 publik nyckel (SPKI, base64)
}

public sealed class AcceptMsg
{
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string? PairKey { get; set; }     // bara vid parkoppling
}

public sealed class ClientInfo
{
    public string Name { get; set; } = "";
    public List<ClientMonitor> Monitors { get; set; } = new();
}

public sealed class ClientMonitor
{
    /// <summary>Tillverkare + produktkod ur EDID, t.ex. "09D1-7F31". Samma skärm ger samma nyckel på båda datorerna.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Primary { get; set; }
    /// <summary>Windows namn på skärmen hos den datorn (\\.\DISPLAYn) – används lokalt för DDC/CI.</summary>
    public string Gdi { get; set; } = "";
    /// <summary>Index bland skärmar som delar samma bild (duplicering).</summary>
    public int PhysIndex { get; set; }
    /// <summary>Ingången skärmen visar enligt DDC/CI, sett från den datorn (null = svarar inte).</summary>
    public int? Input { get; set; }
}

/// <summary>Be klienten byta ingång – den når skärmen när den här datorn inte gör det.</summary>
public sealed class SetInputMsg
{
    public int Id { get; set; }
    public string Key { get; set; } = "";   // EDID-nyckel
    public int Code { get; set; }           // VCP 0x60-värde
}

public sealed class InputResultMsg
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Krypterad förbindelse: ECDH (P-256) + HKDF + AES-GCM. Nyckeln blandas med en hemlighet
/// (parkopplingskoden första gången, därefter en slumpad nyckel som båda sparat), så en
/// förbindelse fungerar bara om båda sidor känner till samma hemlighet.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    readonly Stream stream;
    readonly AesGcm send, recv;
    long sendCounter, recvCounter;
    readonly SemaphoreSlim writeLock = new(1, 1);
    const int MaxFrame = 1 << 20;

    SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey)
    {
        this.stream = stream;
        send = new AesGcm(sendKey, 16);
        recv = new AesGcm(recvKey, 16);
    }

    public void Dispose()
    {
        send.Dispose();
        recv.Dispose();
        try { stream.Dispose(); } catch { }
    }

    // ---------- Ramar ----------

    static async Task WriteFrame(Stream s, byte[] data, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(data, ct);
        await s.FlushAsync(ct);
    }

    static async Task<byte[]> ReadFrame(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n <= 0 || n > MaxFrame) throw new InvalidDataException("Ogiltig ram.");
        var data = new byte[n];
        await s.ReadExactlyAsync(data, ct);
        return data;
    }

    // ---------- Handskakning ----------

    static (byte[] c2s, byte[] s2c) Derive(ECDiffieHellman me, string peerPub, byte[] secret, byte[] transcript)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPub), out _);
        byte[] shared = me.DeriveRawSecretAgreement(peer.PublicKey);
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 64, SHA256.HashData(secret), transcript);
        return (okm[..32], okm[32..]);
    }

    static byte[] Transcript(byte[] a, byte[] b) => SHA256.HashData(a.Concat(b).ToArray());

    /// <summary>Klientsidan. secret = parkopplingskoden (mode "pair") eller sparad nyckel (mode "auth").</summary>
    public static async Task<(SecureChannel channel, HelloMsg server, AcceptMsg accept)> ConnectAsClient(
        Stream s, string clientId, string clientName, string mode, byte[] secret, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new HelloMsg
        {
            Version = Proto.Version, Id = clientId, Name = clientName, Mode = mode,
            Pub = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo()),
        };
        byte[] helloBytes = Proto.ToJson(hello);
        await WriteFrame(s, helloBytes, ct);
        byte[] serverBytes = await ReadFrame(s, ct);
        var server = Proto.FromJson<HelloMsg>(serverBytes);
        if (server.Version != Proto.Version) throw new InvalidOperationException("Servern kör en annan version av Skärmstyrning.");

        var (c2s, s2c) = Derive(ecdh, server.Pub, secret, Transcript(helloBytes, serverBytes));
        var ch = new SecureChannel(s, c2s, s2c);
        try
        {
            await ch.Send(MsgType.Proof, Encoding.UTF8.GetBytes("hej"), ct);
            var (type, payload) = await ch.Receive(ct);
            if (type != MsgType.Accept) throw new InvalidOperationException("Servern svarade oväntat.");
            return (ch, server, Proto.FromJson<AcceptMsg>(payload));
        }
        catch (Exception ex) when (ex is CryptographicException or EndOfStreamException or IOException)
        {
            ch.Dispose();
            throw new UnauthorizedAccessException(mode == "pair"
                ? "Fel kod, eller så är parkopplingen inte öppen på servern."
                : "Servern känner inte igen den här datorn längre – parkoppla igen.");
        }
    }

    /// <summary>Serversidan. secretFor väljer hemlighet utifrån klientens hälsning (null = avvisa).</summary>
    public static async Task<(SecureChannel channel, HelloMsg client)> AcceptAsServer(
        Stream s, string serverId, string serverName, Func<HelloMsg, byte[]?> secretFor, CancellationToken ct)
    {
        byte[] clientBytes = await ReadFrame(s, ct);
        var client = Proto.FromJson<HelloMsg>(clientBytes);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new HelloMsg
        {
            Version = Proto.Version, Id = serverId, Name = serverName,
            Pub = Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo()),
        };
        byte[] helloBytes = Proto.ToJson(hello);
        await WriteFrame(s, helloBytes, ct);

        // Okänd klient eller ingen öppen parkoppling: använd en slumpad hemlighet så att beviset misslyckas.
        byte[] secret = secretFor(client) ?? RandomNumberGenerator.GetBytes(32);
        var (c2s, s2c) = Derive(ecdh, client.Pub, secret, Transcript(clientBytes, helloBytes));
        var ch = new SecureChannel(s, s2c, c2s);
        try
        {
            var (type, _) = await ch.Receive(ct);
            if (type != MsgType.Proof) throw new CryptographicException();
            return (ch, client);
        }
        catch
        {
            ch.Dispose();
            throw new UnauthorizedAccessException("Fel kod eller okänd dator.");
        }
    }

    // ---------- Meddelanden ----------

    public async Task Send(MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var plain = new byte[1 + payload.Length];
        plain[0] = (byte)type;
        payload.Span.CopyTo(plain.AsSpan(1));
        var frame = new byte[plain.Length + 16];
        await writeLock.WaitAsync(ct);
        try
        {
            var nonce = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), sendCounter++);
            send.Encrypt(nonce, plain, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length));
            await WriteFrame(stream, frame, ct);
        }
        finally { writeLock.Release(); }
    }

    public async Task<(MsgType type, byte[] payload)> Receive(CancellationToken ct = default)
    {
        byte[] frame = await ReadFrame(stream, ct);
        if (frame.Length < 17) throw new InvalidDataException("För kort ram.");
        var plain = new byte[frame.Length - 16];
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4), recvCounter++);
        recv.Decrypt(nonce, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length), plain);
        return ((MsgType)plain[0], plain[1..]);
    }
}

/// <summary>Små binära meddelanden för mus och tangentbord.</summary>
public static class Wire
{
    public static byte[] Point(int x, int y)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, x);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), y);
        return b;
    }
    public static (int x, int y) ReadPoint(byte[] b) =>
        (BinaryPrimitives.ReadInt32LittleEndian(b), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)));

    public static byte[] Button(byte button, bool down) => new[] { button, (byte)(down ? 1 : 0) };

    public static byte[] Wheel(short delta, bool horizontal)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(b, delta);
        b[2] = (byte)(horizontal ? 1 : 0);
        return b;
    }
    public static (short delta, bool horizontal) ReadWheel(byte[] b) => (BinaryPrimitives.ReadInt16LittleEndian(b), b[2] != 0);

    public const byte KeyUp = 1, KeyExtended = 2;
    public static byte[] Key(ushort vk, ushort scan, byte flags)
    {
        var b = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(b, vk);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), scan);
        b[4] = flags;
        return b;
    }
    public static (ushort vk, ushort scan, byte flags) ReadKey(byte[] b) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(b), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2)), b[4]);
}
