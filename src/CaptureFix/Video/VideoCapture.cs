using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace CaptureFix.Video;

public enum PixelFormat { NV12, P010, YUY2 }

/// <summary>A video mode the card offers, e.g. 1920x1080 @ 60 in P010.</summary>
public sealed record VideoMode(PixelFormat Format, int Width, int Height, int FpsNum, int FpsDen)
{
    public double Fps => FpsDen == 0 ? 0 : (double)FpsNum / FpsDen;
    public string Key => $"{Format}:{Width}x{Height}@{Math.Round(Fps)}";
    public override string ToString()
    {
        string kind = Format == PixelFormat.P010 ? "HDR 10-bit" : Format == PixelFormat.YUY2 ? "SDR 4:2:2" : "SDR";
        return $"{Width}x{Height} {Math.Round(Fps)} fps  ({kind})";
    }
}

public sealed record CaptureDeviceInfo(string Name, string SymbolicLink);

/// <summary>One frame as the card sent it: planes packed one after another, rows <see cref="Pitch"/> bytes apart.</summary>
public sealed class VideoFrame
{
    public byte[] Data = Array.Empty<byte>();
    public int Length;
    public int Pitch;
    public int Width, Height;
    public PixelFormat Format;
    public long ArrivedTicks;   // Stopwatch ticks when the frame left the card's driver

    // Cadence: a 30 fps game arrives as 60 frames a second with each picture sent twice.
    public long Index;          // counts every frame the card delivered
    public long ChangeIndex;    // Index of the newest frame whose picture differed from the one before
    public float DiffAny;       // share of sampled pixels that changed at all since the previous frame
    public float DiffBig;       // share that changed by more than 2/255
    public float DiffMean;      // mean absolute change, in 8-bit codes
    public float ChangeDiffMean; // DiffMean of the frame at ChangeIndex (large = scene cut)
}

/// <summary>
/// Reads frames from a UVC capture card through Media Foundation, with every converter switched
/// off: the card's own pixels come out exactly as sent, and the GPU shader does the colour work.
/// Only the newest frame is kept, so a slow moment never builds up delay.
/// </summary>
public sealed class VideoCapture : IDisposable
{
    // Media Foundation attribute GUIDs (mfidl.h / mfreadwrite.h).
    static readonly Guid DevSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    static readonly Guid DevSourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    static readonly Guid DevSourceSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    static readonly Guid DevSourceFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid ReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    static readonly Guid ReadWriteDisableConverters = new("98d5b065-1374-4847-8d5d-31520fee7156");

    static bool _mfStarted;

    readonly ConcurrentBag<VideoFrame> _free = new();
    VideoFrame? _latest;
    Thread? _thread;
    volatile bool _stop;
    IMFMediaSource? _source;
    IMFSourceReader? _reader;

    public VideoMode Mode { get; }
    public CaptureDeviceInfo Device { get; }
    public AutoResetEvent FrameReady { get; } = new(false);
    public string? Error { get; private set; }
    public long FramesCaptured;
    public long LastFrameTicks;
    public long StartedTicks;

    public event Action<string>? Failed;

    VideoCapture(CaptureDeviceInfo device, VideoMode mode) { Device = device; Mode = mode; }

    static void EnsureStarted()
    {
        if (_mfStarted) return;
        MediaFactory.MFStartup(true).CheckError();
        _mfStarted = true;
    }

    static IMFActivate[] Enumerate()
    {
        EnsureStarted();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(DevSourceType, DevSourceTypeVidcap);
        return MediaFactory.MFEnumDeviceSources(attrs).ToArray();
    }

    public static List<CaptureDeviceInfo> ListDevices()
    {
        var list = new List<CaptureDeviceInfo>();
        foreach (var a in Enumerate())
        {
            using (a)
                list.Add(new CaptureDeviceInfo(a.GetAllocatedString(DevSourceFriendlyName) ?? "", a.GetAllocatedString(DevSourceSymbolicLink) ?? ""));
        }
        return list;
    }

    /// <summary>Picks the capture card: anything named like a capture card wins over webcams.</summary>
    public static CaptureDeviceInfo? FindCaptureCard(string? preferredLink)
    {
        var all = ListDevices();
        return all.FirstOrDefault(d => d.SymbolicLink == preferredLink)
            ?? all.FirstOrDefault(d => d.Name.Contains("HD60", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.Name.Contains("Elgato", StringComparison.OrdinalIgnoreCase)
                                    || d.Name.Contains("Capture", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault();
    }

    static IMFMediaSource OpenSource(CaptureDeviceInfo dev)
    {
        IMFActivate[] acts = Enumerate();
        try
        {
            var act = acts.FirstOrDefault(a => a.GetAllocatedString(DevSourceSymbolicLink) == dev.SymbolicLink)
                      ?? throw new InvalidOperationException($"{dev.Name} isn't connected.");
            return act.ActivateObject<IMFMediaSource>();
        }
        finally { foreach (var a in acts) a.Dispose(); }
    }

    static PixelFormat? ToPixelFormat(Guid subtype)
    {
        if (subtype == VideoFormatGuids.NV12) return PixelFormat.NV12;
        if (subtype == VideoFormatGuids.P010) return PixelFormat.P010;
        if (subtype == VideoFormatGuids.YUY2) return PixelFormat.YUY2;
        return null;
    }

    static Guid ToSubtype(PixelFormat f) => f switch
    {
        PixelFormat.NV12 => VideoFormatGuids.NV12,
        PixelFormat.P010 => VideoFormatGuids.P010,
        _ => VideoFormatGuids.YUY2,
    };

    static (int, int) Split(ulong v) => ((int)(v >> 32), (int)(v & 0xffffffff));

    /// <summary>Every mode the card offers, best first.</summary>
    public static List<VideoMode> ListModes(CaptureDeviceInfo dev)
    {
        var modes = new List<VideoMode>();
        using var source = OpenSource(dev);
        using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, null!);
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i); }
            catch (SharpGen.Runtime.SharpGenException) { break; }
            using (t)
            {
                var fmt = ToPixelFormat(t.GetGUID(MediaTypeAttributeKeys.Subtype));
                if (fmt is null) continue;
                var (w, h) = Split(t.GetUInt64(MediaTypeAttributeKeys.FrameSize));
                var (n, d) = Split(t.GetUInt64(MediaTypeAttributeKeys.FrameRate));
                var m = new VideoMode(fmt.Value, w, h, n, d);
                if (!modes.Any(x => x.Key == m.Key)) modes.Add(m);
            }
        }
        source.Shutdown();
        return modes
            .OrderByDescending(m => m.Format == PixelFormat.P010)
            .ThenByDescending(m => m.Width * m.Height)
            .ThenByDescending(m => m.Fps)
            .ThenBy(m => m.Format == PixelFormat.YUY2)
            .ToList();
    }

    public static VideoCapture Start(CaptureDeviceInfo dev, VideoMode mode)
    {
        var cap = new VideoCapture(dev, mode);
        cap.Open();
        return cap;
    }

    void Open()
    {
        _source = OpenSource(Device);
        using var attrs = MediaFactory.MFCreateAttributes(3);
        attrs.Set(LowLatency, true);
        attrs.Set(ReaderEnableVideoProcessing, false);
        attrs.Set(ReadWriteDisableConverters, true);
        _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, attrs);

        // Find the exact native type so the card does no conversion at all.
        IMFMediaType? chosen = null;
        for (int i = 0; chosen is null; i++)
        {
            IMFMediaType t;
            try { t = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i); }
            catch (SharpGen.Runtime.SharpGenException) { break; }
            var (w, h) = Split(t.GetUInt64(MediaTypeAttributeKeys.FrameSize));
            var (n, d) = Split(t.GetUInt64(MediaTypeAttributeKeys.FrameRate));
            if (ToPixelFormat(t.GetGUID(MediaTypeAttributeKeys.Subtype)) == Mode.Format && w == Mode.Width && h == Mode.Height
                && Math.Abs((double)n / Math.Max(d, 1) - Mode.Fps) < 0.5)
                chosen = t;
            else
                t.Dispose();
        }
        if (chosen is null)
        {
            // Range-based types: ask for the size and rate directly.
            chosen = MediaFactory.MFCreateMediaType();
            chosen.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            chosen.Set(MediaTypeAttributeKeys.Subtype, ToSubtype(Mode.Format));
            chosen.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)Mode.Width << 32) | (uint)Mode.Height);
            chosen.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Mode.FpsNum << 32) | (uint)Mode.FpsDen);
        }
        using (chosen)
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, chosen);
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

        StartedTicks = Stopwatch.GetTimestamp();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Video capture", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    void Loop()
    {
        try
        {
            while (!_stop)
            {
                using var sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out var flags, out _);
                if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("The capture card stopped sending video.");
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (sample is null) continue;
                long now = Stopwatch.GetTimestamp();

                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr ptr, out _, out int len);
                try { Publish(ptr, len, now); }
                finally { buffer.Unlock(); }
            }
        }
        catch (Exception ex) when (!_stop)
        {
            Error = Describe(ex);
            Failed?.Invoke(Error);
        }
        catch { }
        finally
        {
            // The reading thread owns the reader: releasing it from another thread while a
            // read is in flight can deadlock inside Media Foundation.
            try { _reader?.Dispose(); } catch { }
            try { _source?.Shutdown(); } catch { }
            try { _source?.Dispose(); } catch { }
            _reader = null; _source = null;
        }
    }

    void Publish(IntPtr ptr, int len, long now)
    {
        int h = Mode.Height;
        // Rows may be padded; work out the real pitch from the buffer size.
        int pitch = Mode.Format == PixelFormat.YUY2 ? len / h : (int)(len / (h * 1.5));
        int minPitch = Mode.Format == PixelFormat.NV12 ? Mode.Width : Mode.Width * 2;
        if (pitch < minPitch) return; // truncated frame

        if (!_free.TryTake(out var f)) f = new VideoFrame();
        if (f.Data.Length < len) f.Data = new byte[len];
        Marshal.Copy(ptr, f.Data, 0, len);
        f.Length = len; f.Pitch = pitch; f.Width = Mode.Width; f.Height = h; f.Format = Mode.Format;
        f.ArrivedTicks = now;
        Compare(f);

        var old = Interlocked.Exchange(ref _latest, f);
        if (old != null) _free.Add(old);  // renderer never saw it: drop, don't queue
        Interlocked.Increment(ref FramesCaptured);
        LastFrameTicks = now;
        FrameReady.Set();
    }

    // Luma of every 4th pixel on every 4th row of the previous frame.
    byte[] _sig = Array.Empty<byte>(), _sigPrev = Array.Empty<byte>();
    long _index, _changeIndex;
    float _changeDiffMean;

    /// <summary>A repeat is a frame where under 0.05% of sampled pixels moved by more than 2/255.</summary>
    public const float RepeatThreshold = 0.0005f;

    unsafe void Compare(VideoFrame f)
    {
        int sw = f.Width / 4, sh = f.Height / 4, n = sw * sh;
        if (_sig.Length != n) { _sig = new byte[n]; _sigPrev = new byte[n]; _index = 0; _changeIndex = 0; }
        int stride = f.Format == PixelFormat.NV12 ? 4 : 8;      // bytes between sampled pixels
        int first = f.Format == PixelFormat.P010 ? 1 : 0;       // P010: take the high byte
        int any = 0, big = 0; long sum = 0;
        fixed (byte* data = f.Data, sig = _sig, prev = _sigPrev)
        {
            for (int y = 0; y < sh; y++)
            {
                byte* src = data + (long)(y * 4 + 2) * f.Pitch + first;
                byte* d = sig + y * sw, pv = prev + y * sw;
                for (int x = 0; x < sw; x++)
                {
                    byte v = src[x * stride];
                    int diff = v - pv[x]; if (diff < 0) diff = -diff;
                    if (diff != 0) { any++; sum += diff; if (diff > 2) big++; }
                    d[x] = v;
                }
            }
        }
        (_sig, _sigPrev) = (_sigPrev, _sig);
        f.Index = ++_index;
        f.DiffAny = (float)any / n; f.DiffBig = (float)big / n; f.DiffMean = (float)sum / n;
        if (f.DiffBig > RepeatThreshold || _index == 1) { _changeIndex = _index; _changeDiffMean = f.DiffMean; }
        f.ChangeIndex = _changeIndex;
        f.ChangeDiffMean = _changeDiffMean;
    }

    /// <summary>Testing: a capture that plays saved frames like a console would send them, each one
    /// <paramref name="repeat"/> times (2 = a 30 fps game in a 60 Hz signal), back and forth.</summary>
    public static VideoCapture StartSynthetic(VideoMode mode, IReadOnlyList<byte[]> frames, int repeat)
    {
        var cap = new VideoCapture(new CaptureDeviceInfo("Synthetic", ""), mode) { StartedTicks = Stopwatch.GetTimestamp() };
        cap._thread = new Thread(() => cap.SyntheticLoop(frames, repeat)) { IsBackground = true, Name = "Synthetic capture" };
        cap._thread.Start();
        return cap;
    }

    unsafe void SyntheticLoop(IReadOnlyList<byte[]> frames, int repeat)
    {
        long period = (long)(Stopwatch.Frequency / Mode.Fps), next = Stopwatch.GetTimestamp();
        int i = 0, dir = 1, shown = 0;
        while (!_stop)
        {
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > Stopwatch.Frequency / 500) Thread.Sleep((int)(wait * 1000 / Stopwatch.Frequency) - 1);
            while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(20);
            fixed (byte* p = frames[i]) Publish((IntPtr)p, frames[i].Length, Stopwatch.GetTimestamp());
            if (++shown >= repeat && frames.Count > 1)
            {
                shown = 0; i += dir;
                if (i == frames.Count - 1 || i == 0) dir = -dir;
            }
            next += period;
        }
    }

    /// <summary>Takes the newest frame (or null). Hand it back with <see cref="Return"/>.</summary>
    public VideoFrame? TakeLatest() => Interlocked.Exchange(ref _latest, null);
    public void Return(VideoFrame f) => _free.Add(f);

    public static string Describe(Exception ex)
    {
        uint hr = (uint)ex.HResult;
        return hr switch
        {
            // MF_E_VIDEO_RECORDING_DEVICE_LOCKED / _PREEMPTED, ERROR_BUSY, sharing violation
            0xC00D3704 or 0xC00D3EA3 or 0x800700AA or 0x80070020 =>
                "The capture card is busy. Close Elgato Studio, OBS, or anything else using it, then press F5.",
            0xC00D3E85 => "Windows is blocking camera access. Turn on Settings > Privacy & security > Camera > Let desktop apps access your camera.",
            _ => ex.Message,
        };
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread == null) { _reader?.Dispose(); _source?.Shutdown(); _source?.Dispose(); return; }
        // With a signal the current read finishes within a frame. Without one it would wait
        // forever, so shut the source down to wake it.
        if (!_thread.Join(500))
        {
            try { _source?.Shutdown(); } catch { }
            _thread.Join(2000);
        }
    }
}
