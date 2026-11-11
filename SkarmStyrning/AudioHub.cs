using System.Collections.Concurrent;
using System.Security.Cryptography;
using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SkarmStyrning.Net;

namespace SkarmStyrning;

/// <summary>
/// Utjämningsbuffert: tar emot avkodat ljud i ojämn takt och lämnar ut det i jämn takt till
/// högtalarna. Väntar in ~30 ms innan uppspelning börjar, och kastar äldsta ljudet om bufferten
/// växer (datorernas ljudklockor går aldrig exakt lika fort).
/// </summary>
public sealed class JitterBuffer : ISampleProvider
{
    const int Target = AudioFormat.FrameFloats * 3;   // 30 ms
    const int Max = AudioFormat.FrameFloats * 8;      // 80 ms
    readonly float[] ring = new float[AudioFormat.SampleRate * AudioFormat.Channels]; // 1 s
    int read, count;
    bool playing;
    readonly object gate = new();

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioFormat.SampleRate, AudioFormat.Channels);
    public float Volume { get; set; } = 1f;
    public int BufferedMs { get { lock (gate) return count * 1000 / (AudioFormat.SampleRate * AudioFormat.Channels); } }

    public void Write(ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            foreach (var s in samples)
            {
                ring[(read + count) % ring.Length] = s;
                if (count < ring.Length) count++; else read = (read + 1) % ring.Length;
            }
            if (count > Max)
            {
                // Ligger efter – hoppa fram till målnivån (ett kort hopp hellre än växande fördröjning).
                int drop = count - Target;
                drop -= drop % AudioFormat.Channels;
                read = (read + drop) % ring.Length;
                count -= drop;
            }
        }
    }

    public int Read(Span<float> buffer)
    {
        lock (gate)
        {
            if (!playing && count >= Target) playing = true;
            int n = playing ? Math.Min(buffer.Length, count) : 0;
            float v = Volume;
            for (int i = 0; i < n; i++)
            {
                buffer[i] = ring[read] * v;
                read = (read + 1) % ring.Length;
            }
            count -= n;
            if (n < buffer.Length)
            {
                buffer[n..].Clear();                          // tystnad tills mer ljud kommer
                if (playing && count == 0) playing = false;   // fyll på igen innan nästa ljud spelas
            }
        }
        return buffer.Length; // högtalarna ska aldrig vänta
    }
}

/// <summary>Ljudet från en ansluten dator: avkodning och uppspelning.</summary>
public sealed class AudioStream : IDisposable
{
    public required string ClientId;
    public required byte[] Key;
    public required ulong Token;
    public required AudioCrypto Crypto;
    public readonly JitterBuffer Buffer = new();
    readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(AudioFormat.SampleRate, AudioFormat.Channels, null);
    readonly float[] pcm = new float[AudioFormat.FrameFloats * 6];
    WasapiOut? output;
    long lastSeq = -1;
    readonly object gate = new();

    public bool UdpSeen;
    public DateTime LastPacket = DateTime.MinValue;
    public AudioStatusMsg Status = new();

    public void Feed(long seq, ReadOnlySpan<byte> opus)
    {
        lock (gate)
        {
            if (seq <= lastSeq) return; // sent eller dubblett
            EnsureOutput();
            long gap = lastSeq < 0 ? 0 : seq - lastSeq - 1;
            if (gap is > 0 and <= 5)
                for (long i = 0; i < gap; i++)
                {
                    // Tappat paket: låt Opus fylla i (låter mycket bättre än ett klick).
                    try
                    {
                        int n = decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, AudioFormat.FrameSamples, false);
                        if (n > 0) Buffer.Write(pcm.AsSpan(0, n * AudioFormat.Channels));
                    }
                    catch { break; }
                }
            lastSeq = seq;
            int got = decoder.Decode(opus, pcm, AudioFormat.FrameSamples * 6, false);
            if (got > 0) Buffer.Write(pcm.AsSpan(0, got * AudioFormat.Channels));
            LastPacket = DateTime.UtcNow;
        }
    }

    void EnsureOutput()
    {
        if (output != null) return;
        // Delat läge, 20 ms – Windows mixar ihop det med datorns eget ljud.
        var o = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        ISampleProvider src = Buffer;
        try
        {
            using var en = new MMDeviceEnumerator();
            int rate = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioClient.MixFormat.SampleRate;
            if (rate != AudioFormat.SampleRate) src = new WdlResamplingSampleProvider(Buffer, rate);
        }
        catch { }
        o.Init(new SampleToWaveProvider(src));
        o.Play();
        output = o;
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { output?.Stop(); output?.Dispose(); } catch { }
            output = null;
            Crypto.Dispose();
        }
    }
}

/// <summary>Tar emot ljud från anslutna datorer och spelar upp det i den här datorns högtalare.</summary>
public sealed class AudioHub : IDisposable
{
    readonly ConcurrentDictionary<string, AudioStream> byClient = new();
    readonly ConcurrentDictionary<ulong, AudioStream> byToken = new();

    public event Action? Changed;

    /// <summary>Ge en nyanslutna dator nyckel och port så att den kan börja skicka.</summary>
    public AudioConfigMsg Configure(string clientId, bool enabled, float volume)
    {
        Remove(clientId);
        if (!enabled) return new AudioConfigMsg { Enabled = false };
        var key = RandomNumberGenerator.GetBytes(32);
        var tokenBytes = RandomNumberGenerator.GetBytes(8);
        ulong token = BitConverter.ToUInt64(tokenBytes);
        var s = new AudioStream { ClientId = clientId, Key = key, Token = token, Crypto = new AudioCrypto(key, tokenBytes) };
        s.Buffer.Volume = volume;
        byClient[clientId] = s;
        byToken[token] = s;
        return new AudioConfigMsg
        {
            Enabled = true, Key = Convert.ToBase64String(key), Token = Convert.ToBase64String(tokenBytes), UdpPort = Proto.DiscoveryPort,
        };
    }

    public void Remove(string clientId)
    {
        if (byClient.TryRemove(clientId, out var s))
        {
            byToken.TryRemove(s.Token, out _);
            s.Dispose();
            Changed?.Invoke();
        }
    }

    public AudioStream? Stream(string clientId) => byClient.GetValueOrDefault(clientId);

    public void SetVolume(string clientId, float volume)
    {
        if (byClient.TryGetValue(clientId, out var s)) s.Buffer.Volume = volume;
    }

    /// <summary>Ett UDP-paket på sökporten som börjar med ljudmarkören.</summary>
    public void OnUdp(byte[] packet)
    {
        if (!AudioCrypto.TryHeader(packet, out ulong token, out long seq)) return;
        if (!byToken.TryGetValue(token, out var s)) return;
        if (!s.Crypto.TryOpen(packet, out var opus)) return; // fel nyckel eller manipulerat – släng
        bool first = !s.UdpSeen;
        s.UdpSeen = true;
        try { s.Feed(seq, opus); } catch { }
        if (first) Changed?.Invoke();
    }

    /// <summary>Ljudpaket via TCP (reserv när UDP inte går fram).</summary>
    public void OnTcp(string clientId, byte[] payload)
    {
        if (payload.Length < 9 || !byClient.TryGetValue(clientId, out var s)) return;
        long seq = BitConverter.ToInt64(payload, 0);
        try { s.Feed(seq, payload.AsSpan(8)); } catch { }
    }

    public void OnStatus(string clientId, AudioStatusMsg status)
    {
        if (byClient.TryGetValue(clientId, out var s)) { s.Status = status; Changed?.Invoke(); }
    }

    public void Dispose()
    {
        foreach (var id in byClient.Keys.ToList()) Remove(id);
    }
}
