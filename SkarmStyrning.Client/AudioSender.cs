using System.Net;
using System.Net.Sockets;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SkarmStyrning.Net;

namespace SkarmStyrning.Client;

/// <summary>Loopback-inspelning med 20 ms buffert (NAudio:s standard är 100 ms).</summary>
sealed class LowLatencyLoopback : WasapiCapture
{
    public LowLatencyLoopback(MMDevice device) : base(device, false, 20) { }
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

/// <summary>
/// Fångar allt ljud som spelas upp på den här datorn (standardutgången), kodar det med Opus och
/// skickar det krypterat till servern – via UDP, eller via TCP om UDP inte går fram.
/// </summary>
sealed class AudioSender : IDisposable
{
    readonly Func<MsgType, byte[], Task> sendTcp;
    readonly object gate = new();
    AudioCrypto? crypto;
    UdpClient? udp;
    IPEndPoint? server;
    volatile bool useTcp;
    long seq;

    WasapiCapture? capture;
    string? deviceId;
    System.Threading.Timer? deviceWatch;
    IResampler? resampler;
    int sourceRate, sourceChannels;
    bool sourceFloat;
    readonly List<float> pending = new(AudioFormat.FrameFloats * 8);
    IOpusEncoder? encoder;
    readonly byte[] opusBuffer = new byte[1500];
    DateTime lastSound = DateTime.MinValue;
    bool sending;

    public string? DeviceName { get; private set; }
    public bool Sending => sending;
    public string Transport => useTcp ? "tcp" : "udp";
    public event Action? StatusChanged;

    // Tysta högtalarna här medan ljudet spelas på servern.
    readonly ClientConfig config;
    MMDevice? device;
    bool mutedByUs;
    DateTime mutedAt, lastSoundBeforeMute = DateTime.MinValue;
    bool sawSoundAfterMute;
    /// <summary>"muted" = högtalarna här är avstängda, "unsupported" = går inte på den här utgången, null = av.</summary>
    public string? LocalMute { get; private set; }

    public AudioSender(Func<MsgType, byte[], Task> sendTcp, ClientConfig config)
    {
        this.sendTcp = sendTcp;
        this.config = config;
    }

    /// <summary>Har klienten kraschat medan högtalarna var avstängda? Slå på dem igen.</summary>
    public static void RestoreAfterCrash(ClientConfig config)
    {
        if (config.MutedDeviceId == null) return;
        try
        {
            using var en = new MMDeviceEnumerator();
            var d = en.GetDevice(config.MutedDeviceId);
            if (d.AudioEndpointVolume.Mute) d.AudioEndpointVolume.Mute = false;
        }
        catch { }
        config.MutedDeviceId = null;
        config.Save();
    }

    /// <summary>Slå på/av funktionen medan klienten kör.</summary>
    public void SetMuteLocal(bool on)
    {
        config.MuteLocal = on;
        config.Save();
        lock (gate)
        {
            if (on && capture != null) MuteHere();
            else if (!on) UnmuteHere();
        }
        StatusChanged?.Invoke();
    }

    void MuteHere()
    {
        if (device == null || !config.MuteLocal) return;
        if (config.NoMuteDevices.Contains(device.ID)) { LocalMute = "unsupported"; return; }
        try
        {
            var vol = device.AudioEndpointVolume;
            if (vol.Mute) { LocalMute = mutedByUs ? "muted" : null; return; } // redan avstängt av användaren – rör inte
            vol.Mute = true;
            mutedByUs = true;
            mutedAt = DateTime.UtcNow;
            sawSoundAfterMute = false;
            config.MutedDeviceId = device.ID;
            config.Save();
            LocalMute = "muted";
        }
        catch { LocalMute = null; }
    }

    void UnmuteHere()
    {
        if (!mutedByUs || device == null) { LocalMute = null; return; }
        try { if (device.AudioEndpointVolume.Mute) device.AudioEndpointVolume.Mute = false; } catch { }
        mutedByUs = false;
        config.MutedDeviceId = null;
        config.Save();
        LocalMute = null;
    }

    DateTime lastMeterCheck;
    int silentWhilePlaying;

    /// <summary>
    /// På vissa ljudkort tystar avstängt ljud även fångsten. Windows ljudmätare visar ljud även när
    /// högtalarna är avstängda – spelas något upp men fångsten får bara digital tystnad, slås
    /// högtalarna på igen och utgången kommer ihåg att det inte går där.
    /// </summary>
    void CheckMuteSideEffect(bool frameSilent)
    {
        if (!mutedByUs || device == null || sawSoundAfterMute) return;
        if (!frameSilent) { sawSoundAfterMute = true; return; } // fångsten fungerar trots avstängt ljud
        var now = DateTime.UtcNow;
        if (now - lastMeterCheck < TimeSpan.FromMilliseconds(200)) return;
        lastMeterCheck = now;
        float peak;
        try { peak = device.AudioMeterInformation.MasterPeakValue; } catch { return; }
        if (peak <= 0.01f) { silentWhilePlaying = 0; return; }
        if (++silentWhilePlaying < 4) return; // ~0,8 s med ljud i Windows men tystnad i fångsten
        UnmuteHere();
        if (!config.NoMuteDevices.Contains(device.ID)) config.NoMuteDevices.Add(device.ID);
        config.Save();
        LocalMute = "unsupported";
        StatusChanged?.Invoke();
    }

    /// <summary>Börja skicka till servern med inställningarna den gav oss.</summary>
    public void Start(AudioConfigMsg config, IPAddress serverAddress)
    {
        Stop();
        if (!config.Enabled) return;
        lock (gate)
        {
            crypto = new AudioCrypto(Convert.FromBase64String(config.Key), Convert.FromBase64String(config.Token));
            udp = new UdpClient(serverAddress.AddressFamily);
            server = new IPEndPoint(serverAddress, config.UdpPort);
            useTcp = false;
            seq = 0;
            encoder = OpusCodecFactory.CreateEncoder(AudioFormat.SampleRate, AudioFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO, null);
            encoder.Bitrate = AudioFormat.Bitrate;
            encoder.Complexity = 8;
        }
        StartCapture();
        // Följ med om standardutgången byts (t.ex. hörlurar kopplas in).
        deviceWatch = new System.Threading.Timer(_ => CheckDevice(), null, 3000, 3000);
    }

    public void UseTcp()
    {
        useTcp = true;
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        deviceWatch?.Dispose();
        deviceWatch = null;
        StopCapture();
        lock (gate)
        {
            crypto?.Dispose(); crypto = null;
            udp?.Dispose(); udp = null;
            encoder = null;
        }
        if (sending) { sending = false; StatusChanged?.Invoke(); }
    }

    public void Dispose() => Stop();

    // ---------- Inspelning ----------

    void StartCapture()
    {
        // Skapas på en egen tråd: annars skickar NAudio ljudhändelserna via UI-tråden.
        var t = new Thread(() =>
        {
            try
            {
                var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                lock (gate)
                {
                    device = dev;
                    deviceId = dev.ID;
                    DeviceName = dev.FriendlyName;
                    capture = new LowLatencyLoopback(dev);
                    var f = capture.WaveFormat;
                    sourceRate = f.SampleRate;
                    sourceChannels = f.Channels;
                    sourceFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                        || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
                    resampler = sourceRate == AudioFormat.SampleRate ? null
                        : ResamplerFactory.CreateResampler(AudioFormat.Channels, sourceRate, AudioFormat.SampleRate, 5, null);
                    pending.Clear();
                    capture.DataAvailable += OnData;
                    capture.StartRecording();
                    MuteHere(); // ljudet ska höras på servern – inte här också
                }
                StatusChanged?.Invoke();
            }
            catch { DeviceName = null; }
        }) { IsBackground = true, Name = "Ljud: start" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join(3000);
    }

    void StopCapture()
    {
        WasapiCapture? c;
        lock (gate)
        {
            c = capture; capture = null;
            UnmuteHere(); // högtalarna här tillbaka
        }
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); c.Dispose(); } catch { }
    }

    void CheckDevice()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var id = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            if (id != deviceId) { StopCapture(); StartCapture(); }
        }
        catch { }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        lock (gate)
        {
            if (encoder == null) return;
            // 1) Till flyttal, stereo, i enhetens takt.
            int bytesPerSample = sourceFloat ? 4 : 2;
            int frames = e.BytesRecorded / (bytesPerSample * sourceChannels);
            var stereo = new float[frames * 2];
            var src = e.Buffer;
            for (int i = 0; i < frames; i++)
            {
                int o = i * sourceChannels * bytesPerSample;
                float l = sourceFloat ? BitConverter.ToSingle(src, o) : BitConverter.ToInt16(src, o) / 32768f;
                float r = l;
                if (sourceChannels > 1)
                    r = sourceFloat ? BitConverter.ToSingle(src, o + 4) : BitConverter.ToInt16(src, o + 2) / 32768f;
                stereo[i * 2] = l;
                stereo[i * 2 + 1] = r;
            }

            // 2) Till 48 kHz om enheten går i en annan takt (t.ex. 44,1 kHz).
            if (resampler != null)
            {
                int inLen = frames;
                int outLen = (int)((long)frames * AudioFormat.SampleRate / sourceRate) + 16;
                var outBuf = new float[outLen * 2];
                resampler.ProcessInterleaved(stereo, ref inLen, outBuf, ref outLen);
                pending.AddRange(outBuf.AsSpan(0, outLen * 2).ToArray());
            }
            else pending.AddRange(stereo);

            // 3) Koda och skicka 10 ms i taget.
            while (pending.Count >= AudioFormat.FrameFloats)
            {
                var frame = pending.GetRange(0, AudioFormat.FrameFloats).ToArray();
                pending.RemoveRange(0, AudioFormat.FrameFloats);
                SendFrame(frame);
            }
        }
    }

    void SendFrame(float[] frame)
    {
        bool silent = true;
        foreach (var v in frame) if (Math.Abs(v) > 1e-5f) { silent = false; break; }
        var now = DateTime.UtcNow;
        if (!silent) { lastSound = now; if (!mutedByUs) lastSoundBeforeMute = now; }
        CheckMuteSideEffect(silent);
        // Efter en stunds tystnad skickas ingenting (sparar nätverk); ljud skickas direkt igen.
        bool shouldSend = now - lastSound < TimeSpan.FromMilliseconds(300);
        if (!shouldSend)
        {
            if (sending && now - lastSound > TimeSpan.FromSeconds(2)) { sending = false; StatusChanged?.Invoke(); }
            return;
        }
        if (!sending) { sending = true; StatusChanged?.Invoke(); }

        int len = encoder!.Encode(frame, AudioFormat.FrameSamples, opusBuffer, opusBuffer.Length);
        if (len <= 0) return;
        long s = seq++;
        var opus = opusBuffer.AsSpan(0, len);
        if (useTcp)
            _ = sendTcp(MsgType.Audio, AudioCrypto.TcpPayload(s, opus));
        else if (udp != null && server != null && crypto != null)
        {
            var pkt = crypto.Seal(s, opus);
            try { udp.Send(pkt, pkt.Length, server); } catch { }
        }
    }
}
