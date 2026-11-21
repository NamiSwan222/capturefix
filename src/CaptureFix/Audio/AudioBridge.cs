using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CaptureFix.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Plays the capture card's game audio on your speakers or headset.
///
/// The card's clock and your headset's clock never run at exactly the same speed, so a plain
/// buffer slowly runs dry (gaps) or overflows (chunks thrown away). Both are heard as clicks and
/// static. Here the buffer is kept at the chosen delay by playing a hair faster or slower
/// (at most 0.5%, inaudible) with a smooth resampler, never by cutting audio. If the buffer ever
/// does run dry, the sound fades out and back in instead of clicking.
///
/// Because this app plays the audio itself, Discord Go Live (sharing this window) streams the
/// game sound and nothing else from your PC.
/// </summary>
public sealed class AudioBridge : IDisposable
{
    WasapiCapture? _capture;
    WasapiOut? _out;
    AdaptivePlayer? _player;
    float _gain = 1f;
    bool _muted;
    int _delayMs = 50;

    /// <summary>The HD60 X sends 10 ms packets that sometimes arrive late; below this, they cause gaps.</summary>
    public const int MinDelayMs = 40;

    public int DelayMs
    {
        get => _delayMs;
        set { _delayMs = Math.Max(value, MinDelayMs); if (_player != null) _player.TargetMs = _delayMs; }
    }
    public string? Error { get; private set; }
    public string? InputName { get; private set; }
    public string? OutputName { get; private set; }
    public string? FormatInfo { get; private set; }
    public double BufferedMs => _player?.FillMs ?? 0;
    public long Underruns => _player?.Underruns ?? 0;
    public int ExtraMs => _player?.ExtraMs ?? 0;
    public long TotalIn => _player?.TotalIn ?? 0;
    /// <summary>Share of the card's audio that never arrives (USB trouble). Null until measured.</summary>
    public double? LossPercent => _player is { RateKnown: true } p ? p.LossPercent : null;
    public long TotalOut => _player?.TotalOutFrames ?? 0;
    public int CaptureBufferMs { get; set; } = 20;
    public long Drops => _player?.Skips ?? 0;
    public double SpeedPercent => _player?.SpeedPercent ?? 0;

    /// <summary>Testing: copy everything sent to the speakers (before volume) into this WAV file.</summary>
    public string? TapPath { get; set; }

    public float Volume
    {
        get => _gain;
        set { _gain = value; Apply(); }
    }
    public bool Muted
    {
        get => _muted;
        set { _muted = value; Apply(); }
    }
    void Apply() { if (_player != null) _player.Gain = _muted ? 0 : _gain; }

    public static List<AudioDeviceInfo> ListOutputs()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName)).ToList();
    }

    public static List<AudioDeviceInfo> ListInputs()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName)).ToList();
    }

    /// <param name="videoDeviceName">The capture card's name, used to find its audio input.</param>
    /// <param name="outputId">null = Windows default playback device.</param>
    public void Start(string videoDeviceName, string? inputId, string? outputId)
    {
        Stop();
        try
        {
            using var en = new MMDeviceEnumerator();
            var inputs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            string key = videoDeviceName.Replace("Elgato ", "", StringComparison.OrdinalIgnoreCase);
            var input = inputs.FirstOrDefault(d => d.ID == inputId)
                        ?? inputs.FirstOrDefault(d => d.FriendlyName.Contains(videoDeviceName, StringComparison.OrdinalIgnoreCase))
                        ?? inputs.FirstOrDefault(d => d.FriendlyName.Contains(key, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Couldn't find the audio input for {videoDeviceName}.");
            var output = (outputId != null ? en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => d.ID == outputId) : null)
                         ?? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            InputName = input.FriendlyName;
            OutputName = output.FriendlyName;

            _capture = new WasapiCapture(input, true, CaptureBufferMs);
            var inFmt = _capture.WaveFormat;
            var outMix = output.AudioClient.MixFormat;
            _player = new AdaptivePlayer(inFmt, outMix.SampleRate, outMix.Channels) { TargetMs = _delayMs };
            if (TapPath != null) _player.Tap = new WaveFileWriter(TapPath, _player.WaveFormat);
            Apply();
            FormatInfo = $"{inFmt.SampleRate / 1000.0:0.#} kHz {inFmt.Channels}ch {inFmt.BitsPerSample}-bit {inFmt.Encoding} → {outMix.SampleRate / 1000.0:0.#} kHz {outMix.Channels}ch";

            _capture.DataAvailable += (_, e) => _player?.Write(e.Buffer, e.BytesRecorded);
            _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error = "Game audio stopped: " + e.Exception.Message; };

            _out = new WasapiOut(output, AudioClientShareMode.Shared, true, 30);
            _out.Init(_player);
            ConfigureSession(output);
            _capture.StartRecording();
            _out.Play();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Stop();
        }
    }

    SimpleAudioVolume? _sessionVolume;

    /// <summary>True when something outside CaptureFix (the Windows volume mixer, a mixer app or a
    /// Stream Deck key) has muted CaptureFix's sound. Windows remembers that per app.</summary>
    public bool SessionMuted { get { try { return _sessionVolume?.Mute == true; } catch { return false; } } }

    public void UnmuteSession() { try { if (_sessionVolume != null) _sessionVolume.Mute = false; } catch { } }

    /// <summary>
    /// Sets up CaptureFix's entry in the Windows volume mixer: clears a mute Windows remembered from
    /// before (CaptureFix has its own mute, M), and opts out of "ducking", where Windows turns other
    /// sounds down by 80% whenever it detects a call. Game audio shouldn't vanish because you joined a
    /// Discord call.
    /// </summary>
    void ConfigureSession(MMDevice output)
    {
        _sessionVolume = null;
        try
        {
            var mgr = output.AudioSessionManager;
            mgr.RefreshSessions();
            uint pid = (uint)Environment.ProcessId;
            var sessions = mgr.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var sc = sessions[i];
                if (sc.GetProcessID != pid) continue;
                var f = typeof(AudioSessionControl).GetField("audioSessionControlInterface2",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                (f?.GetValue(sc) as NAudio.CoreAudioApi.Interfaces.IAudioSessionControl2)?.SetDuckingPreference(true);
                if (!_muted && sc.SimpleAudioVolume.Mute) { sc.SimpleAudioVolume.Mute = false; UnmutedAtStart = true; }
                _sessionVolume = sc.SimpleAudioVolume;
            }
        }
        catch { }
    }

    /// <summary>Set when start-up found CaptureFix muted in the Windows mixer and turned it back on.</summary>
    public bool UnmutedAtStart { get; private set; }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _out?.Stop(); } catch { }
        _capture?.Dispose(); _capture = null;
        _out?.Dispose(); _out = null;
        _sessionVolume = null;
        _player?.Tap?.Dispose();
        _player = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Ring buffer of captured audio, read out at the output's rate with a very slowly adjusted
/// speed so the amount buffered stays at the target.
/// </summary>
sealed class AdaptivePlayer : ISampleProvider
{
    readonly WaveFormat _in;
    readonly int _inCh, _outCh;
    readonly float[] _ring;          // interleaved input-channel frames
    readonly int _ringFrames;
    readonly object _lock = new();
    long _writePos, _readPos;        // in frames, monotonic
    double _frac;                    // fractional read position between _readPos and _readPos+1
    double _baseStep;                // input frames per output frame at nominal speed
    double _fillAvg = -1;
    double _speed = 1.0;
    bool _playing;
    float _fade;                     // 0..1 ramp used for start/stop without clicks
    float _gainNow;
    float[] _conv = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }
    public int TargetMs { get; set; } = 50;
    /// <summary>Extra cushion learned from gaps on this device pair (grows on a gap, shrinks slowly when stable).</summary>
    public int ExtraMs;
    long _stableSince;
    public float Gain { get; set; } = 1f;
    public long Underruns, Skips;
    public WaveFileWriter? Tap;

    public long TotalIn, TotalOutFrames;

    // How fast audio really arrives. On an overloaded USB controller the card loses packets and
    // delivers fewer than 48,000 samples a second; playing at that real rate (instead of 48k)
    // keeps playback continuous rather than running dry over and over.
    readonly Queue<(long ticks, long frames)> _rateHist = new();
    double _measuredRate;
    public double LossPercent { get; private set; }   // 0 when healthy; valid after a few seconds
    public bool RateKnown { get; private set; }
    public double FillMs { get { lock (_lock) return (_writePos - _readPos) * 1000.0 / _in.SampleRate; } }
    public double SpeedPercent => (_speed - 1.0) * 100.0;

    public AdaptivePlayer(WaveFormat input, int outRate, int outChannels)
    {
        _in = input;
        _inCh = input.Channels;
        _outCh = outChannels;
        _ringFrames = input.SampleRate * 2;  // 2 seconds
        _ring = new float[_ringFrames * _inCh];
        _baseStep = (double)input.SampleRate / outRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels);
    }

    /// <summary>Called by the capture thread with raw bytes in the card's format.</summary>
    public void Write(byte[] data, int bytes)
    {
        int samples = ToFloat(data, bytes);
        int frames = samples / _inCh;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                int dst = (int)((_writePos + f) % _ringFrames) * _inCh;
                Array.Copy(_conv, f * _inCh, _ring, dst, _inCh);
            }
            _writePos += frames;
            TotalIn += frames;
            MeasureRate();
            // Something stalled for a long time: keep only the newest audio.
            long max = _ringFrames - _in.SampleRate / 10;
            if (_writePos - _readPos > max) { _readPos = _writePos - TargetFrames(); _frac = 0; Skips++; }
        }
    }

    int ToFloat(byte[] data, int bytes)
    {
        int bits = _in.BitsPerSample;
        bool isFloat = _in.Encoding == WaveFormatEncoding.IeeeFloat
            || (_in is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        int n = bytes / (bits / 8);
        if (_conv.Length < n) _conv = new float[n];
        if (isFloat && bits == 32) Buffer.BlockCopy(data, 0, _conv, 0, n * 4);
        else if (bits == 16) for (int i = 0; i < n; i++) _conv[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        else if (bits == 24) for (int i = 0; i < n; i++) _conv[i] = ((data[i * 3] << 8 | data[i * 3 + 1] << 16 | data[i * 3 + 2] << 24) >> 8) / 8388608f;
        else if (bits == 32) for (int i = 0; i < n; i++) _conv[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
        else n = 0;
        return n;
    }

    void MeasureRate()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        _rateHist.Enqueue((now, TotalIn));
        while (_rateHist.Count > 2 && now - _rateHist.Peek().ticks > 5 * freq) _rateHist.Dequeue();
        var (t0, f0) = _rateHist.Peek();
        double secs = (now - t0) / (double)freq;
        if (secs < 1.0) return;
        double rate = (TotalIn - f0) / secs;
        double nominal = _in.SampleRate;
        rate = Math.Clamp(rate, nominal * 0.85, nominal * 1.02);
        _measuredRate = _measuredRate == 0 ? rate : _measuredRate * 0.9 + rate * 0.1;
        _baseStep = _measuredRate / WaveFormat.SampleRate;
        if (secs >= 3.0)
        {
            RateKnown = true;
            LossPercent = Math.Max(0, (1 - _measuredRate / nominal) * 100);
        }
    }

    long TargetFrames() => (long)(TargetMs + ExtraMs) * _in.SampleRate / 1000;

    float Sample(long frame, int ch) => _ring[(int)(frame % _ringFrames) * _inCh + ch];

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outCh;
        lock (_lock)
        {
            long target = TargetFrames();
            long fill = _writePos - _readPos;

            if (!_playing)
            {
                // Wait until the target delay is buffered, then fade in.
                if (fill >= target) { _playing = true; _fade = 0; _fillAvg = fill; }
            }

            if (_playing)
            {
                // Slow, smooth speed correction toward the target fill (max ±0.5%).
                // After 2 minutes without a gap, give back 5 ms of the learned cushion.
                long now = Environment.TickCount64;
                if (_stableSince == 0) _stableSince = now;
                if (ExtraMs > 0 && now - _stableSince > 120_000) { ExtraMs -= 5; _stableSince = now; }
                _fillAvg = _fillAvg < 0 ? fill : _fillAvg * 0.95 + fill * 0.05;
                double err = (_fillAvg - target) / Math.Max(target, 1);
                _speed = 1.0 + Math.Clamp(err * 0.01, -0.005, 0.005);
            }

            double step = _baseStep * _speed;
            float fadeStep = 1f / (WaveFormat.SampleRate * 0.005f);   // 5 ms ramps
            for (int f = 0; f < frames; f++)
            {
                int o = offset + f * _outCh;
                // Need frames _readPos and _readPos+1 available.
                bool have = _playing && _writePos - _readPos >= 2;
                if (_playing && !have)
                {
                    // Ran dry: fade to silence from the last sample, then wait for the buffer to refill.
                    _playing = false;
                    Underruns++;
                    ExtraMs = Math.Min(ExtraMs + 10, 100);
                    _stableSince = Environment.TickCount64;
                }
                _fade = have ? Math.Min(1f, _fade + fadeStep) : Math.Max(0f, _fade - fadeStep);

                long p = Math.Min(_readPos, _writePos - 2);
                for (int c = 0; c < _outCh; c++)
                {
                    float v = 0;
                    if (_fade > 0 && p >= 0 && _writePos >= 2)
                    {
                        int ic = _inCh == 1 ? 0 : c < _inCh ? c : -1;
                        if (ic >= 0)
                        {
                            float a = Sample(p, ic), b = Sample(p + 1, ic);
                            v = a + (b - a) * (float)_frac;
                        }
                        if (_outCh == 1 && _inCh >= 2)
                        {
                            float a = 0.5f * (Sample(p, 0) + Sample(p, 1)), b = 0.5f * (Sample(p + 1, 0) + Sample(p + 1, 1));
                            v = a + (b - a) * (float)_frac;
                        }
                    }
                    buffer[o + c] = v * _fade;
                }

                if (have)
                {
                    _frac += step;
                    long adv = (long)_frac;
                    _readPos += adv;
                    _frac -= adv;
                }
            }
        }

        TotalOutFrames += frames;
        Tap?.WriteSamples(buffer, offset, frames * _outCh);
        // Volume changes glide over 20 ms so moving the slider never clicks.
        float gainStep = 1f / (WaveFormat.SampleRate * 0.02f);
        for (int f = 0; f < frames; f++)
        {
            _gainNow += Math.Clamp(Gain - _gainNow, -gainStep, gainStep);
            for (int c = 0; c < _outCh; c++) buffer[offset + f * _outCh + c] *= _gainNow;
        }
        return frames * _outCh;
    }
}
