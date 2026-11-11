using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SkarmStyrning.Net;

/// <summary>Ljudet som strömmas från klienten: Opus, 48 kHz stereo, 10 ms per paket.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 480;                    // 10 ms per kanal
    public const int FrameFloats = FrameSamples * Channels; // 960 värden per paket
    public const int Bitrate = 160_000;                     // nära genomskinligt för musik
    public const byte UdpMagic = 0xA5;                      // skiljer ljudpaket från sökningar på samma port
    public const int HeaderBytes = 1 + 8 + 8;               // magi + token + löpnummer
}

public sealed class AudioConfigMsg
{
    public bool Enabled { get; set; }
    public string Key { get; set; } = "";    // 32 byte, base64 – för AES-GCM på UDP-paketen
    public string Token { get; set; } = "";  // 8 byte, base64 – talar om vilken session paketet hör till
    public int UdpPort { get; set; }
}

public sealed class AudioStatusMsg
{
    public bool Sending { get; set; }
    public string? Device { get; set; }
    public string? Transport { get; set; }   // "udp" eller "tcp"
}

/// <summary>
/// Kryptering av ljudpaket som går via UDP. Nyckeln skickas över den redan krypterade TCP-förbindelsen.
/// Paket: [0xA5][token 8][löpnummer 8][chiffertext][tagg 16]. Huvudet är autentiserat (AAD).
/// </summary>
public sealed class AudioCrypto : IDisposable
{
    readonly AesGcm aes;
    readonly byte[] token;

    public AudioCrypto(byte[] key, byte[] token)
    {
        aes = new AesGcm(key, 16);
        this.token = token;
    }

    public void Dispose() => aes.Dispose();

    static void Nonce(Span<byte> nonce, long seq)
    {
        nonce.Clear();
        nonce[0] = 0xAD; // eget nonce-utrymme för ljud
        BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], seq);
    }

    public byte[] Seal(long seq, ReadOnlySpan<byte> opus)
    {
        var pkt = new byte[AudioFormat.HeaderBytes + opus.Length + 16];
        pkt[0] = AudioFormat.UdpMagic;
        token.CopyTo(pkt.AsSpan(1));
        BinaryPrimitives.WriteInt64LittleEndian(pkt.AsSpan(9), seq);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, seq);
        aes.Encrypt(nonce, opus, pkt.AsSpan(AudioFormat.HeaderBytes, opus.Length), pkt.AsSpan(AudioFormat.HeaderBytes + opus.Length), pkt.AsSpan(0, AudioFormat.HeaderBytes));
        return pkt;
    }

    public static bool TryHeader(ReadOnlySpan<byte> pkt, out ulong token, out long seq)
    {
        token = 0; seq = 0;
        if (pkt.Length < AudioFormat.HeaderBytes + 16 || pkt[0] != AudioFormat.UdpMagic) return false;
        token = BinaryPrimitives.ReadUInt64LittleEndian(pkt[1..]);
        seq = BinaryPrimitives.ReadInt64LittleEndian(pkt[9..]);
        return true;
    }

    public bool TryOpen(ReadOnlySpan<byte> pkt, out byte[] opus)
    {
        opus = Array.Empty<byte>();
        if (!TryHeader(pkt, out _, out long seq)) return false;
        int len = pkt.Length - AudioFormat.HeaderBytes - 16;
        var plain = new byte[len];
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, seq);
        try
        {
            aes.Decrypt(nonce, pkt.Slice(AudioFormat.HeaderBytes, len), pkt.Slice(AudioFormat.HeaderBytes + len, 16), plain, pkt[..AudioFormat.HeaderBytes]);
        }
        catch (CryptographicException) { return false; }
        opus = plain;
        return true;
    }

    /// <summary>TCP-reserv: [löpnummer 8][opus] – förbindelsen är redan krypterad.</summary>
    public static byte[] TcpPayload(long seq, ReadOnlySpan<byte> opus)
    {
        var b = new byte[8 + opus.Length];
        BinaryPrimitives.WriteInt64LittleEndian(b, seq);
        opus.CopyTo(b.AsSpan(8));
        return b;
    }
}
