using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CaptureFix.Video;

/// <summary>What the shader needs to know about the picture. Changed from the UI thread at any time.</summary>
public sealed class ColorSettings
{
    public int HdrMode;             // 0 detect from the picture, 1 HDR10, 2 SDR
    public int RangeMode;           // 0 detect from the picture, 1 limited (16-235), 2 full (0-255)
    public float PeakNits = 1000;   // matches the console's HDR calibration
    public float SdrPeakNits = 200; // SDR output: brightness (lower = brighter)
    public float HdrSdrWhite = 240; // HDR output: SDR white level
    public float Saturation = 1f;
    public int ToneMap;             // 0 natural, 1 vivid
    public float Contrast = 1f;
    public float BlackLevel;
    public float Gamma = 2.2f;
    public float Warmth;
    public float Sharpness;
    public bool AllowHdrOutput;     // show real HDR when the monitor is in HDR mode
    public int FrameGen;            // 0/1 = off, 2-4 = pictures shown per game frame
    public int FrameGenQuality = 1; // 0 = fast (quarter-size motion field), 1 = high (half-size)
    public bool SharpUpscale;       // bicubic instead of bilinear when the window is bigger than the picture
    public int MonitorGamut;        // wide-colour monitor correction: 0 = automatic, 1 = on, 2 = off
    public float Deband;            // 0..1: smooths colour steps in skies and fog
    public float Clarity;           // 0..1: local contrast
    public float Vibrance;          // 0..1: lifts weak colours, leaves strong ones alone
    public bool AiUpscale;          // NVIDIA RTX Video Super Resolution when the picture is shown larger than captured
}

[StructLayout(LayoutKind.Sequential)]
struct ShaderParams
{
    public int Format, HdrIn, HdrOut, FullRange;
    public float PeakIn, SdrPeak, HdrSdrWhite;
    public int TenBit;
    public float TexW, TexH, Saturation;
    public int ToneMap;
    public float Contrast, BlackLevel, Gamma, Warmth, Sharpness, GamutOn, Pad1, Pad2;
    public float G00, G01, G02, G03, G10, G11, G12, G13, G20, G21, G22, G23;
}

/// <summary>
/// Draws capture frames with Direct3D 11 on its own thread. Each new frame is uploaded and presented
/// the moment it arrives (flip model, one frame queued at most), so the picture is never more than
/// one refresh behind the card.
/// </summary>
public sealed class Renderer : IDisposable
{
    readonly IntPtr _hwnd;
    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGIFactory2 _factory;
    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _rtv;
    readonly ID3D11VertexShader _vs;
    readonly ID3D11PixelShader _ps;
    readonly ID3D11SamplerState _sampler;
    readonly ID3D11Buffer _cbuffer;
    readonly FrameGen _fg;
    AiUpscaler? _ai;
    bool _aiFailed;
    ID3D11ShaderResourceView? _aiSrv;   // the newest frame, AI upscaled; null = not in use
    int _cvW, _cvH;                     // size of the converted frame handed to frame generation
    /// <summary>The AI upscaler made the picture now on screen.</summary>
    public bool AiActive => _aiSrv != null;
    /// <summary>Why AI upscaling is not running although it is switched on (null = no problem).</summary>
    public string? AiProblem { get; private set; }
    /// <summary>Testing: the driver's plain scaler instead of the AI.</summary>
    public bool AiPlain;

    ID3D11Texture2D? _texY, _texUV;
    ID3D11ShaderResourceView? _srvY, _srvUV;
    PixelFormat _texFormat;
    int _texW, _texH;
    bool _haveFrame;

    Thread? _thread;
    volatile bool _stop;
    volatile bool _resize = true, _dirty = true;
    int _width = 1, _height = 1;
    bool _hdrSwap;
    long _lastHdrCheck;
    bool _monitorHdr;

    public ColorSettings Color { get; set; } = new();

    /// <summary>Detects HDR vs SDR and limited vs full range from the incoming picture.</summary>
    public readonly SignalAnalyzer Signal = new();
    public bool EffectiveHdr => Color.HdrMode == 0 ? Signal.Hdr : Color.HdrMode == 1;
    public bool EffectiveFullRange => Color.RangeMode == 0 ? Signal.FullRange : Color.RangeMode == 2;
    /// <summary>Raised on the render thread when auto-detection changes its mind: (hdr, fullRange).</summary>
    public event Action<bool, bool>? SignalDetected;
    public volatile VideoCapture? Source;

    // Stats for the overlay.
    public double PresentFps { get; private set; }
    public double LatencyMs { get; private set; }
    public bool OutputIsHdr => _hdrSwap;
    public bool MonitorHdr => _monitorHdr;
    public string? Error { get; private set; }

    public Renderer(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Signal.Changed += (h, f) => { _dirty = true; SignalDetected?.Invoke(h, f); };
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        // Video support is for the AI upscaler; not every GPU driver has it.
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
            out _device!, out _ctx!).Failure)
        {
            _aiFailed = true;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
                out _device, out _ctx).CheckError();
        }
        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice1>())
            dxgiDevice.MaximumFrameLatency = 1;
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        string src;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptureFix.video.hlsl")!)
        using (var r = new StreamReader(s)) src = r.ReadToEnd();
        var vsCode = Compiler.Compile(src, "VSMain", "video.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3);
        var psCode = Compiler.Compile(src, "PSMain", "video.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3);
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);

        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _cbuffer = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer));
        _fg = new FrameGen(_device, _ctx, _sampler);
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Renderer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Resize(int w, int h)
    {
        _width = Math.Max(1, w); _height = Math.Max(1, h);
        _resize = true;
        Source?.FrameReady.Set();
        _wake.Set();
    }

    /// <summary>Redraw with new settings even if no new frame arrives (e.g. no signal).</summary>
    public void Invalidate() { _dirty = true; _wake.Set(); }

    /// <summary>Drop the last picture (e.g. when switching modes) so the screen goes black.</summary>
    public void ClearPicture() { _clear = true; _wake.Set(); }
    volatile bool _clear;

    readonly AutoResetEvent _wake = new(false);

    // ---------- frame generation pacing ----------

    long _fgChangeIndex = -1;                                    // picture currently stored as "newest"
    readonly List<(long due, float alpha)> _pending = new();     // pictures still to show for this game frame
    bool _fgShowing;                                             // the screen currently shows frame-generation output
    double _refreshHz = 60;

    /// <summary>How many pictures are shown per game frame right now (1 = frame generation idle).</summary>
    public int FrameGenFactor { get; private set; } = 1;
    /// <summary>The game's own frame rate, measured from repeated frames.</summary>
    public double GameFps { get; private set; }
    /// <summary>Extra delay frame generation adds to real frames, in ms.</summary>
    public double FrameGenDelayMs { get; private set; }
    public double RefreshHz => _refreshHz;

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    void Loop()
    {
        timeBeginPeriod(1);   // 1 ms waits; the default 15.6 ms tick is too coarse to pace 120 pictures a second
        try { LoopCore(); }
        finally { timeEndPeriod(1); }
    }

    void LoopCore()
    {
        long fpsStart = Stopwatch.GetTimestamp(); int fpsCount = 0;
        double latencyAcc = 0; int latencyCount = 0;
        long gameFrames = 0;
        var handles = new WaitHandle[2];
        while (!_stop)
        {
            var src = Source;
            // Sleep until a frame arrives, something changes, or the next picture is due.
            int timeout = 100;
            if (_pending.Count > 0)
            {
                double ms = (_pending[0].due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                timeout = ms <= 1.5 ? 0 : (int)(ms - 1.5);
            }
            if (timeout > 0)
            {
                if (src != null) { handles[0] = src.FrameReady; handles[1] = _wake; WaitHandle.WaitAny(handles, timeout); }
                else _wake.WaitOne(timeout);
            }
            if (_stop) break;

            try
            {
                if (_clear) { _clear = false; _haveFrame = false; _dirty = true; ResetFrameGen(); }
                UpdateOutputMode();
                if (_resize) { ResizeSwapChain(); _resize = false; _dirty = true; }

                var frame = src?.TakeLatest();
                bool drew = false;
                if (frame != null)
                {
                    Signal.Analyze(frame);
                    int want = Color.FrameGen;
                    SyncEnhancers();
                    if (want < 2)
                    {
                        if (_fgShowing) ResetFrameGen();
                        Upload(frame);
                        if (UpscalingNow() || _fg.Enhancing || _aiSrv != null) { PushToFrameGen(); PresentGenerated(1f); }   // convert at game size, then scale and enhance
                        else Draw();
                        drew = true;
                        latencyAcc += (Stopwatch.GetTimestamp() - frame.ArrivedTicks) * 1000.0 / Stopwatch.Frequency;
                        latencyCount++; fpsCount++;
                        if (frame.ChangeIndex != _fgChangeIndex) { _fgChangeIndex = frame.ChangeIndex; gameFrames++; }
                    }
                    else if (frame.ChangeIndex != _fgChangeIndex || !_haveFrame)
                    {
                        // A new game frame (repeats are skipped: the screen already shows them).
                        long gap = frame.ChangeIndex - _fgChangeIndex;
                        _fgChangeIndex = frame.ChangeIndex;
                        gameFrames++;
                        Upload(frame);
                        _fg.FinestLevel = Color.FrameGenQuality >= 1 ? 0 : 1;
                        PushToFrameGen();
                        _fgShowing = true;
                        _fgArrived = frame.ArrivedTicks;

                        double captureFps = Math.Max(1, src!.Mode.Fps);
                        int m = Math.Min(want, 4);
                        // Never more pictures than the monitor can show.
                        while (m > 1 && gap > 0 && captureFps / gap * m > _refreshHz * 1.03) m--;
                        bool cut = frame.ChangeDiffMean > 45;   // most of the picture changed: a scene cut
                        _pending.Clear();
                        long now = Stopwatch.GetTimestamp();
                        if (m >= 2 && gap >= 1 && gap <= 4 && _fg.HasPair && !cut)
                        {
                            // The game frame lasts `period`; spread m pictures evenly across it,
                            // generated ones first, the real frame last.
                            double period = gap / captureFps * Stopwatch.Frequency;
                            for (int i = 1; i <= m; i++)
                                _pending.Add((now + (long)(period * (i - 1) / m), i == m ? 1f : (float)i / m));
                            FrameGenFactor = m;
                            FrameGenDelayMs = gap / captureFps * 1000.0 * (m - 1) / m;
                        }
                        else
                        {
                            _pending.Add((now, 1f));
                            FrameGenFactor = 1; FrameGenDelayMs = 0;
                        }
                    }
                    src!.Return(frame);
                }
                // Settings changed (or the signal was re-detected) while the picture stands still:
                // redraw the newest frame with them. Moving pictures pick them up on the next frame.
                if (_dirty && !drew && _pending.Count == 0) Redraw();
                _dirty = false;

                // Show whatever is due. If we fell behind, skip straight to the latest due picture.
                long t = Stopwatch.GetTimestamp();
                while (_pending.Count > 0 && _pending[0].due - t <= Stopwatch.Frequency / 2000)
                {
                    var (_, alpha) = _pending[0];
                    _pending.RemoveAt(0);
                    if (_pending.Count > 0 && _pending[0].due <= t) continue;
                    PresentGenerated(alpha);
                    fpsCount++;
                    if (alpha >= 1f) { latencyAcc += (Stopwatch.GetTimestamp() - _fgArrived) * 1000.0 / Stopwatch.Frequency; latencyCount++; }
                    t = Stopwatch.GetTimestamp();
                }

                long nowTicks = Stopwatch.GetTimestamp();
                double secs = (nowTicks - fpsStart) / (double)Stopwatch.Frequency;
                if (secs >= 1)
                {
                    PresentFps = fpsCount / secs;
                    GameFps = gameFrames / secs;
                    LatencyMs = latencyCount > 0 ? latencyAcc / latencyCount : 0;
                    fpsStart = nowTicks; fpsCount = 0; latencyAcc = 0; latencyCount = 0; gameFrames = 0;
                    if (Color.FrameGen < 2) { FrameGenFactor = 1; FrameGenDelayMs = 0; }
                }
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _pending.Clear();
                Thread.Sleep(250);
                _resize = true;
            }
        }
    }

    long _fgArrived;

    public int ShownWidth => _texW > 0 ? (int)Letterbox().Width : 0;
    public int ShownHeight => _texW > 0 ? (int)Letterbox().Height : 0;
    public bool SharpScalingActive => UpscalingNow();

    /// <summary>The picture is being shown larger than captured and sharp upscaling is on.</summary>
    bool UpscalingNow() => Color.SharpUpscale && _haveFrame && _texW > 0 && Letterbox().Width > _texW * 1.02f;

    /// <summary>Shows the newest frame again with the current settings (picture standing still).</summary>
    void SyncEnhancers()
    {
        var c = Color;
        _fg.SharpUpscale = c.SharpUpscale;
        // The AI upscaler sharpens on its own; the full amount on top of it looks gritty.
        _fg.Sharp = Math.Clamp(c.Sharpness, 0, 1) * (_aiSrv != null ? 0.5f : 1f); _fg.Deband = Math.Clamp(c.Deband, 0, 1);
        _fg.Clarity = Math.Clamp(c.Clarity, 0, 1); _fg.Vibrance = Math.Clamp(c.Vibrance, 0, 1);
    }

    /// <summary>Any of the picture enhancers is on.</summary>
    public bool EnhancersActive => _fg.Enhancing;

    void Redraw()
    {
        SyncEnhancers();
        if (_haveFrame && _srvY != null && (_fg.Enhancing || _aiSrv != null) && !(_fg.HasFrame && _fg.Width == _cvW && _fg.Height == _cvH))
            PushToFrameGen();
        bool viaFrameGen = _haveFrame && _srvY != null && _fg.HasFrame && _fg.Width == _cvW && _fg.Height == _cvH
                           && (_fgShowing || UpscalingNow() || _fg.Enhancing || _aiSrv != null);
        if (!viaFrameGen) { Draw(); return; }
        _fg.HdrOut = _hdrSwap;
        DrawSource(_fg.NewestRtv, new Viewport(0, 0, _cvW, _cvH));
        _fg.RefreshNewest();
        PresentGenerated(1f);
    }

    /// <summary>Testing: when set, every picture shown is logged as (time, alpha); alpha 1 = real frame, -1 = direct.</summary>
    public List<(long ticks, float alpha)>? PresentLog;

    void ResetFrameGen()
    {
        _pending.Clear();
        _fg.Reset();
        _fgShowing = false;
        _fgChangeIndex = -1;
        FrameGenFactor = 1; FrameGenDelayMs = 0;
    }

    /// <summary>Shows the newest real frame (alpha 1) or the generated picture at alpha.</summary>
    void PresentGenerated(float alpha)
    {
        if (_swap == null || _rtv == null) return;
        BeginPresent();
        var vp = Letterbox();
        if (alpha >= 1f) _fg.DrawNewest(_rtv, vp);
        else _fg.DrawInterpolated(_rtv, vp, alpha);
        _presentAlpha = alpha;
        EndPresent();
        _presentAlpha = -1;
    }

    bool WantHdrSwap() => Color.AllowHdrOutput && _monitorHdr;

    void UpdateOutputMode()
    {
        long now = Environment.TickCount64;
        if (_swap != null && now - _lastHdrCheck > 1000)
        {
            _lastHdrCheck = now;
            bool hdr = false;
            try
            {
                using var output = _swap.GetContainingOutput();
                using var o6 = output.QueryInterface<IDXGIOutput6>();
                hdr = o6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
            }
            catch { }
            _monitorHdr = hdr;
            _refreshHz = Displays.RefreshHz(_hwnd);
            UpdateMonitorGamut();
        }
        if (_swap == null || WantHdrSwap() != _hdrSwap) { _resize = true; }
    }

    // ---------- wide-colour monitor ----------

    float[] _gamut = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    bool _gamutKnown, _windowsManaged;
    string _gamutKey = "";

    /// <summary>How much bigger the monitor's colour range is than the standard one (1.0 = standard).</summary>
    public double MonitorGamutRatio { get; private set; } = 1;
    /// <summary>Windows itself converts colours for this monitor (HDR or Auto Color Management).</summary>
    public bool WindowsManagesColor => _windowsManaged;
    public bool MonitorIsWideColor => _gamutKnown && MonitorGamutRatio > 1.12;
    /// <summary>The picture is being converted to the monitor's own primaries right now.</summary>
    public bool GamutCorrectionActive => _gamutKnown && !_hdrSwap && Color.MonitorGamut switch
    {
        1 => true,
        2 => false,
        _ => MonitorIsWideColor && !_windowsManaged,
    };

    void UpdateMonitorGamut()
    {
        try
        {
            double rx, ry, gx, gy, bx, by, wx, wy; string device;
            var forced = Environment.GetEnvironmentVariable("CF_GAMUT_PRIMARIES");   // tests: "rx,ry,gx,gy,bx,by,wx,wy"
            if (forced != null)
            {
                var v = forced.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                (rx, ry, gx, gy, bx, by, wx, wy) = (v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]); device = "forced";
            }
            else
            {
                if (_swap == null) return;
                using var output = _swap.GetContainingOutput();
                using var o6 = output.QueryInterface<IDXGIOutput6>();
                var d = o6.Description1;
                (rx, ry, gx, gy, bx, by, wx, wy) = (d.RedPrimary[0], d.RedPrimary[1], d.GreenPrimary[0], d.GreenPrimary[1], d.BluePrimary[0], d.BluePrimary[1], d.WhitePoint[0], d.WhitePoint[1]);
                device = d.DeviceName;
            }
            string key = FormattableString.Invariant($"{device}|{rx:0.0000},{ry:0.0000},{gx:0.0000},{gy:0.0000},{bx:0.0000},{by:0.0000},{wx:0.0000},{wy:0.0000}");
            bool managed = forced == null && MonitorGamut.WindowsManagesColor(device);
            if (key == _gamutKey && managed == _windowsManaged) return;
            _gamutKey = key; _windowsManaged = managed;
            _gamutKnown = MonitorGamut.PlausiblePrimaries(rx, ry, gx, gy, bx, by, wx, wy);
            if (_gamutKnown)
            {
                _gamut = MonitorGamut.Matrix709ToMonitor(rx, ry, gx, gy, bx, by, wx, wy);
                MonitorGamutRatio = MonitorGamut.AreaRatio(rx, ry, gx, gy, bx, by);
            }
            else MonitorGamutRatio = 1;
            MonitorPrimaries = FormattableString.Invariant($"red {rx:0.000},{ry:0.000}  green {gx:0.000},{gy:0.000}  blue {bx:0.000},{by:0.000}");
            _dirty = true;
        }
        catch { }
    }

    public string MonitorPrimaries { get; private set; } = "";

    void ResizeSwapChain()
    {
        bool hdr = WantHdrSwap();
        var format = hdr ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _rtv?.Dispose(); _rtv = null;

        if (_swap == null || hdr != _hdrSwap)
        {
            _swap?.Dispose();
            _ctx.Flush();
            var desc = new SwapChainDescription1
            {
                Width = (uint)_width, Height = (uint)_height, Format = format,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipDiscard, AlphaMode = AlphaMode.Ignore,
            };
            _swap = _factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            _factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }
        else
        {
            _swap.ResizeBuffers(2, (uint)_width, (uint)_height, format, SwapChainFlags.None).CheckError();
        }
        using (var sc3 = _swap.QueryInterface<IDXGISwapChain3>())
            sc3.SetColorSpace1(hdr ? ColorSpaceType.RgbFullG10NoneP709 : ColorSpaceType.RgbFullG22NoneP709);
        _hdrSwap = hdr;
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(back);
    }

    void EnsureTextures(VideoFrame f)
    {
        if (_texY != null && f.Format == _texFormat && f.Width == _texW && f.Height == _texH) return;
        _srvY?.Dispose(); _srvUV?.Dispose(); _texY?.Dispose(); _texUV?.Dispose();
        _srvUV = null; _texUV = null;

        Texture2DDescription Desc(Format fmt, int w, int h) => new()
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = fmt,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        };

        switch (f.Format)
        {
            case PixelFormat.NV12:
                _texY = _device.CreateTexture2D(Desc(Format.R8_UNorm, f.Width, f.Height));
                _texUV = _device.CreateTexture2D(Desc(Format.R8G8_UNorm, f.Width / 2, f.Height / 2));
                break;
            case PixelFormat.P010:
                _texY = _device.CreateTexture2D(Desc(Format.R16_UNorm, f.Width, f.Height));
                _texUV = _device.CreateTexture2D(Desc(Format.R16G16_UNorm, f.Width / 2, f.Height / 2));
                break;
            default:
                _texY = _device.CreateTexture2D(Desc(Format.R8G8B8A8_UNorm, f.Width / 2, f.Height));
                break;
        }
        _srvY = _device.CreateShaderResourceView(_texY);
        if (_texUV != null) _srvUV = _device.CreateShaderResourceView(_texUV);
        _texFormat = f.Format; _texW = f.Width; _texH = f.Height;
    }

    unsafe void CopyPlane(ID3D11Texture2D tex, byte[] data, int offset, int srcPitch, int rowBytes, int rows)
    {
        var map = _ctx.Map(tex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* src = data)
            {
                byte* dst = (byte*)map.DataPointer;
                int n = Math.Min(rowBytes, (int)map.RowPitch);
                for (int y = 0; y < rows; y++)
                {
                    long so = offset + (long)y * srcPitch;
                    if (so + n > data.Length) break;
                    Buffer.MemoryCopy(src + so, dst + (long)y * map.RowPitch, map.RowPitch, n);
                }
            }
        }
        finally { _ctx.Unmap(tex, 0); }
    }

    void Upload(VideoFrame f)
    {
        EnsureTextures(f);
        int bpp = f.Format == PixelFormat.P010 ? 2 : 1;
        switch (f.Format)
        {
            case PixelFormat.NV12:
            case PixelFormat.P010:
                CopyPlane(_texY!, f.Data, 0, f.Pitch, f.Width * bpp, f.Height);
                CopyPlane(_texUV!, f.Data, f.Pitch * f.Height, f.Pitch, f.Width * bpp, f.Height / 2);
                break;
            default:
                CopyPlane(_texY!, f.Data, 0, f.Pitch, f.Width * 2, f.Height);
                break;
        }
        _haveFrame = true;
        _cvW = f.Width; _cvH = f.Height;
        _aiSrv = null;
        if (Color.AiUpscale) TryAiUpscale(f);
        else AiProblem = null;
    }

    /// <summary>Runs the frame through the AI upscaler when everything it needs is there.</summary>
    void TryAiUpscale(VideoFrame f)
    {
        if (_aiFailed) { AiProblem ??= "not available on this graphics card"; return; }
        if (f.Format != PixelFormat.NV12 || EffectiveHdr) { AiProblem = "works with SDR pictures only"; return; }
        var lb = Letterbox();
        int ow = Math.Min((int)MathF.Round(lb.Width), 3840), oh = Math.Min((int)MathF.Round(lb.Height), 2160);
        ow &= ~1; oh &= ~1;
        if (ow < f.Width * 1.02f || f.Height > 1440) { AiProblem = "the picture is not shown larger than the console sends it"; return; }
        // With the switch off the driver would only scale plainly, worse than our own scaler.
        if (Environment.TickCount64 - _aiSwitchChecked > 3000)
        {
            _aiSwitchChecked = Environment.TickCount64;
            _aiSwitchOff = !AiPlain && AiUpscaler.DriverSwitchOn() == false;
        }
        if (_aiSwitchOff) { AiProblem = "turn on Super Resolution in the NVIDIA app (System > Video > RTX Video Enhancement)"; return; }
        try
        {
            if (_ai == null)
            {
                if (!AiUpscaler.IsNvidia(_device)) { _aiFailed = true; AiProblem = "needs an NVIDIA RTX graphics card"; return; }
                _ai = new AiUpscaler(_device, _ctx) { Plain = AiPlain };
            }
            _aiSrv = _ai.Process(f, ow, oh, EffectiveFullRange);
            _cvW = ow; _cvH = oh;
            AiProblem = null;
        }
        catch (Exception ex)
        {
            _aiFailed = true; _aiSrv = null; _cvW = f.Width; _cvH = f.Height;
            AiProblem = "the graphics driver refused it (" + ex.Message + ")";
            _ai?.Dispose(); _ai = null;
        }
    }
    long _aiSwitchChecked = -100000;
    bool _aiSwitchOff;

    Viewport Letterbox()
    {
        // Keep the console's aspect ratio.
        float scale = Math.Min((float)_width / _texW, (float)_height / _texH);
        float w = _texW * scale, h = _texH * scale;
        return new Viewport((_width - w) / 2, (_height - h) / 2, w, h);
    }

    /// <summary>Converts the uploaded frame (colour, HDR, picture settings) into the given target.</summary>
    void DrawSource(ID3D11RenderTargetView rtv, Viewport vp)
    {
        _ctx.OMSetRenderTargets(rtv);
        _ctx.RSSetViewport(vp);
        var c = Color;
        var p = new ShaderParams
        {
            Format = _aiSrv != null ? 2 : _texFormat == PixelFormat.YUY2 ? 1 : 0,
            HdrIn = EffectiveHdr ? 1 : 0,
            HdrOut = _hdrSwap ? 1 : 0,
            FullRange = EffectiveFullRange ? 1 : 0,
            PeakIn = c.PeakNits, SdrPeak = c.SdrPeakNits, HdrSdrWhite = c.HdrSdrWhite,
            TenBit = _texFormat == PixelFormat.P010 ? 1 : 0,
            TexW = _aiSrv != null ? _cvW : _texW, TexH = _aiSrv != null ? _cvH : _texH, Saturation = c.Saturation,
            ToneMap = c.ToneMap, Contrast = c.Contrast, BlackLevel = c.BlackLevel, Gamma = c.Gamma,
            Warmth = c.Warmth, Sharpness = 0,   // sharpening now happens at the on-screen size (PSEnhance)
            GamutOn = GamutCorrectionActive ? 1 : 0,
            G00 = _gamut[0], G01 = _gamut[1], G02 = _gamut[2],
            G10 = _gamut[3], G11 = _gamut[4], G12 = _gamut[5],
            G20 = _gamut[6], G21 = _gamut[7], G22 = _gamut[8],
        };
        _ctx.UpdateSubresource(in p, _cbuffer);

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResources(0, _aiSrv != null ? new[] { _aiSrv, _aiSrv } : new[] { _srvY!, _srvUV ?? _srvY! });
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetConstantBuffer(0, _cbuffer);
        _ctx.Draw(3, 0);
    }

    Format SwapFormat => _hdrSwap ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;

    /// <summary>Converts the uploaded frame and stores it as the newest frame for frame generation.</summary>
    void PushToFrameGen()
    {
        _fg.HdrOut = _hdrSwap;
        var rtv = _fg.BeginFrame(_cvW, _cvH, SwapFormat);
        DrawSource(rtv, new Viewport(0, 0, _cvW, _cvH));
        _fg.EndFrame();
    }

    void BeginPresent()
    {
        _ctx.OMSetRenderTargets(_rtv!);
        _ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
    }

    void EndPresent()
    {
        var snap = Interlocked.Exchange(ref _snapshotPath, null);
        if (snap != null)
        {
            try { SaveBackBuffer(snap); }
            catch (Exception ex) { SnapshotSaved?.Invoke(snap, ex.Message); }
        }
        _swap!.Present(0, PresentFlags.None);
        PresentLog?.Add((Stopwatch.GetTimestamp(), _presentAlpha));
    }
    float _presentAlpha = -1;

    /// <summary>Shows the uploaded frame directly (no frame generation).</summary>
    void Draw()
    {
        if (_swap == null || _rtv == null) return;
        BeginPresent();
        if (_haveFrame && _srvY != null) DrawSource(_rtv, Letterbox());
        EndPresent();
    }

    /// <summary>Testing: makes the picture between frames a and c and compares it with the real middle frame b.</summary>
    public string FrameGenTest(VideoFrame a, VideoFrame b, VideoFrame c, string outPrefix, int finestLevel, float smooth, float staticBias)
    {
        var (gen, blend, repeat, ms) = FrameGenScore(a, b, c, outPrefix, finestLevel, smooth, staticBias, true);
        return $"generated {gen:0.00} dB | plain blend {blend:0.00} dB | repeat frame {repeat:0.00} dB | {ms:0.00} ms per generated frame";
    }

    /// <summary>Testing: PSNR against the real middle frame of (generated, plain blend, repeated frame), and GPU ms.</summary>
    public (double gen, double blend, double repeat, double ms) FrameGenScore(VideoFrame a, VideoFrame b, VideoFrame c, string outPrefix, int finestLevel, float smooth, float staticBias, bool benchmark)
    {
        for (int i = 0; i < 150; i++) Signal.Analyze(b, force: true);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        _fg.FinestLevel = finestLevel;
        if (Environment.GetEnvironmentVariable("CF_SMOOTH") == null) _fg.Smooth = smooth;
        if (Environment.GetEnvironmentVariable("CF_STATIC") == null) _fg.StaticBias = staticBias;
        _fg.Reset(keepHistory: true);
        Upload(b); PushToFrameGen(); byte[] truth = _fg.ReadNewest();
        _fg.Reset(keepHistory: true);
        Upload(a); PushToFrameGen(); byte[] fa = _fg.ReadNewest();
        Upload(c); PushToFrameGen(); byte[] fc = _fg.ReadNewest();
        byte[] gen = _fg.ReadInterpolated(0.5f);
        double ms = benchmark ? _fg.Benchmark() : 0;

        static double Psnr(byte[] x, byte[] y)
        {
            double se = 0; long n = 0;
            for (int i = 0; i < x.Length; i++) { if ((i & 3) == 3) continue; double d = x[i] - y[i]; se += d * d; n++; }
            return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
        }
        var blend = new byte[fa.Length];
        for (int i = 0; i < blend.Length; i++) blend[i] = (byte)((fa[i] + fc[i] + 1) >> 1);

        if (outPrefix.Length > 0)
        {
            SavePng(outPrefix + "_generated.png", gen); SavePng(outPrefix + "_truth.png", truth); SavePng(outPrefix + "_blend.png", blend);
            var err = new byte[gen.Length];
            for (int i = 0; i < err.Length; i++) err[i] = (i & 3) == 3 ? (byte)255 : (byte)Math.Min(255, Math.Abs(gen[i] - truth[i]) * 4);
            SavePng(outPrefix + "_error_x4.png", err);
        }
        return (Psnr(gen, truth), Psnr(blend, truth), Psnr(fa, truth), ms);
    }

    void SavePng(string path, byte[] bgra) => SavePng(path, bgra, _texW, _texH);

    static void SavePng(string path, byte[] bgra, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Testing: scales a frame to w x h with the plain and the sharp scaler and saves both.</summary>
    public string ScaleTest(VideoFrame f, int w, int h, string outPrefix)
    {
        for (int i = 0; i < 150; i++) Signal.Analyze(f, force: true);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        _fg.Reset();
        Upload(f); PushToFrameGen();
        SavePng(outPrefix + "_bilinear.png", _fg.ReadScaled(w, h, false), w, h);
        SavePng(outPrefix + "_sharp.png", _fg.ReadScaled(w, h, true), w, h);
        string res = $"bilinear {_fg.BenchmarkScale(w, h, false):0.000} ms, sharp {_fg.BenchmarkScale(w, h, true):0.000} ms";
        SyncEnhancers();
        if (_fg.Enhancing)
        {
            SavePng(outPrefix + "_enhanced.png", _fg.ReadScaled(w, h, true), w, h);
            res += $", sharp + enhancers {_fg.BenchmarkScale(w, h, true):0.000} ms";
        }
        return res;
    }

    /// <summary>Testing: the same frame at w x h through our own scaler, the driver's plain scaler and the AI
    /// upscaler. Saves all three, compares their average colours and times the AI and frame generation.</summary>
    public string AiTest(VideoFrame f, VideoFrame f2, int w, int h, string outPrefix)
    {
        for (int i = 0; i < 150; i++) Signal.Analyze(f, force: true);
        Resize(w, h);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        var sb = new System.Text.StringBuilder();
        static string Mean(byte[] b)
        {
            double r = 0, g = 0, bl = 0; int n = b.Length / 4;
            for (int i = 0; i < b.Length; i += 4) { bl += b[i]; g += b[i + 1]; r += b[i + 2]; }
            return $"mean RGB {r / n:0.00} {g / n:0.00} {bl / n:0.00}";
        }
        var c = Color;
        c.AiUpscale = false; SyncEnhancers();
        _fg.Reset(); Upload(f); PushToFrameGen();
        byte[] own = _fg.ReadScaled(w, h, true);
        SavePng(outPrefix + "_own.png", own, w, h);
        sb.AppendLine($"own scaler      {Mean(own)}");
        _fg.Reset(); Upload(f); PushToFrameGen(); Upload(f2); PushToFrameGen();
        sb.AppendLine($"frame generation at {_fg.Width}x{_fg.Height}: {_fg.Benchmark():0.00} ms per generated picture");

        c.AiUpscale = true; AiPlain = true;
        _fg.Reset(); Upload(f); PushToFrameGen();
        if (!AiActive) return sb + "AI path not active: " + AiProblem;
        byte[] plain = _fg.ReadNewest();
        SavePng(outPrefix + "_plain.png", plain, _fg.Width, _fg.Height);
        sb.AppendLine($"driver, AI off  {Mean(plain)}   ({_fg.Width}x{_fg.Height}, {(_ai!.TenBit ? "10" : "8")} bit)");

        _ai.Dispose(); _ai = null; AiPlain = false;
        _fg.Reset(); Upload(f); PushToFrameGen();
        byte[] ai = _fg.ReadNewest();
        SavePng(outPrefix + "_ai.png", ai, _fg.Width, _fg.Height);
        long diff = 0; for (int i = 0; i < ai.Length; i++) diff += Math.Abs(ai[i] - plain[i]);
        sb.AppendLine($"driver, AI on   {Mean(ai)}   differs from AI off by {diff / (double)ai.Length:0.000} per value");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 60; i++) { Upload((i & 1) == 0 ? f : f2); PushToFrameGen(); }
        _fg.ReadNewest();
        sb.AppendLine($"AI upscale + convert: {sw.Elapsed.TotalMilliseconds / 60:0.00} ms per game frame");
        sb.AppendLine($"frame generation at {_fg.Width}x{_fg.Height}: {_fg.Benchmark():0.00} ms per generated picture");
        SyncEnhancers();
        byte[] shown = _fg.ReadScaled(w, h, true);
        SavePng(outPrefix + "_ai_enhanced.png", shown, w, h);
        sb.AppendLine($"AI + enhancers  {Mean(shown)}");
        sb.AppendLine($"problem: {AiProblem ?? "none"}; driver switch: {AiUpscaler.DriverSwitchOn()}");
        return sb.ToString();
    }

    /// <summary>Testing: draws one frame without the render thread and saves exactly what would be shown.</summary>
    public void RenderOnce(VideoFrame f, string pngPath)
    {
        for (int i = 0; i < 150; i++) Signal.Analyze(f, force: true);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        UpdateMonitorGamut();
        Upload(f);
        _snapshotSync = true;
        _snapshotPath = pngPath;   // Draw saves it just before presenting
        Draw();
    }

    string? _snapshotPath;
    /// <summary>Raised on the render thread: (path, error or null).</summary>
    public event Action<string, string?>? SnapshotSaved;

    /// <summary>Saves the next drawn picture, exactly as shown, to a PNG.</summary>
    public void RequestSnapshot(string path) { _snapshotPath = path; Invalidate(); }

    /// <summary>Copies the picture about to be shown, then encodes the PNG on another thread so a
    /// screenshot doesn't freeze the game picture (encoding takes 100+ ms at 1440p).</summary>
    unsafe void SaveBackBuffer(string path)
    {
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        var d = back.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(d);
        _ctx.CopyResource(staging, back);
        int w = (int)d.Width, h = (int)d.Height;
        bool hdr = d.Format != Format.B8G8R8A8_UNorm;
        int rowBytes = w * (hdr ? 8 : 4);
        var pixels = new byte[rowBytes * h];
        var map = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            fixed (byte* dst = pixels)
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
        }
        finally { _ctx.Unmap(staging, 0); }
        float white = Color.HdrSdrWhite / 80f;

        void Encode()
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            fixed (byte* srcAll = pixels)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* src = srcAll + (long)y * rowBytes;
                    byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                    if (!hdr) { Buffer.MemoryCopy(src, dst, bd.Stride, w * 4); continue; }
                    // scRGB -> SDR preview: SDR white = 1.0, clipped above.
                    Half* px = (Half*)src;
                    for (int x = 0; x < w; x++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float v = Math.Clamp((float)px[x * 4 + c] / white, 0f, 1f);
                            dst[x * 4 + (2 - c)] = (byte)(MathF.Pow(v, 1f / 2.2f) * 255f + 0.5f);
                        }
                        dst[x * 4 + 3] = 255;
                    }
                }
            }
            bmp.UnlockBits(bd);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        if (_snapshotSync) { Encode(); SnapshotSaved?.Invoke(path, null); return; }
        Task.Run(() =>
        {
            try { Encode(); SnapshotSaved?.Invoke(path, null); }
            catch (Exception ex) { SnapshotSaved?.Invoke(path, ex.Message); }
        });
    }

    bool _snapshotSync;   // tests: finish writing before returning

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(1000);
        _fg.Dispose();
        _ai?.Dispose();
        _srvY?.Dispose(); _srvUV?.Dispose(); _texY?.Dispose(); _texUV?.Dispose();
        _rtv?.Dispose(); _swap?.Dispose();
        _cbuffer.Dispose(); _sampler.Dispose(); _ps.Dispose(); _vs.Dispose();
        _ctx.Dispose(); _device.Dispose(); _factory.Dispose();
    }
}
