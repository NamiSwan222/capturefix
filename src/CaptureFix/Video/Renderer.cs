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
}

[StructLayout(LayoutKind.Sequential)]
struct ShaderParams
{
    public int Format, HdrIn, HdrOut, FullRange;
    public float PeakIn, SdrPeak, HdrSdrWhite;
    public int TenBit;
    public float TexW, TexH, Saturation;
    public int ToneMap;
    public float Contrast, BlackLevel, Gamma, Warmth, Sharpness, Pad0, Pad1, Pad2;
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
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out _device, out _ctx).CheckError();
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
                    if (want < 2)
                    {
                        if (_fgShowing) ResetFrameGen();
                        Upload(frame);
                        Draw();
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
                if (_dirty && !drew && _pending.Count == 0) Draw();
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
        }
        if (_swap == null || WantHdrSwap() != _hdrSwap) { _resize = true; }
    }

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
    }

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
            Format = _texFormat == PixelFormat.YUY2 ? 1 : 0,
            HdrIn = EffectiveHdr ? 1 : 0,
            HdrOut = _hdrSwap ? 1 : 0,
            FullRange = EffectiveFullRange ? 1 : 0,
            PeakIn = c.PeakNits, SdrPeak = c.SdrPeakNits, HdrSdrWhite = c.HdrSdrWhite,
            TenBit = _texFormat == PixelFormat.P010 ? 1 : 0,
            TexW = _texW, TexH = _texH, Saturation = c.Saturation,
            ToneMap = c.ToneMap, Contrast = c.Contrast, BlackLevel = c.BlackLevel, Gamma = c.Gamma,
            Warmth = c.Warmth, Sharpness = _texFormat == PixelFormat.YUY2 ? 0 : c.Sharpness,
        };
        _ctx.UpdateSubresource(in p, _cbuffer);

        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResources(0, new[] { _srvY!, _srvUV ?? _srvY! });
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetConstantBuffer(0, _cbuffer);
        _ctx.Draw(3, 0);
    }

    Format SwapFormat => _hdrSwap ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;

    /// <summary>Converts the uploaded frame and stores it as the newest frame for frame generation.</summary>
    void PushToFrameGen()
    {
        _fg.HdrOut = _hdrSwap;
        var rtv = _fg.BeginFrame(_texW, _texH, SwapFormat);
        DrawSource(rtv, new Viewport(0, 0, _texW, _texH));
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
            try { SaveBackBuffer(snap); SnapshotSaved?.Invoke(snap, null); }
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
        for (int i = 0; i < 150; i++) Signal.Analyze(b, force: true);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        _fg.FinestLevel = finestLevel; _fg.Smooth = smooth; _fg.StaticBias = staticBias;
        _fg.Reset();
        Upload(b); PushToFrameGen(); byte[] truth = _fg.ReadNewest();
        _fg.Reset();
        Upload(a); PushToFrameGen(); byte[] fa = _fg.ReadNewest();
        Upload(c); PushToFrameGen(); byte[] fc = _fg.ReadNewest();
        byte[] gen = _fg.ReadInterpolated(0.5f);
        double ms = _fg.Benchmark();

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
        return $"generated {Psnr(gen, truth):0.00} dB | plain blend {Psnr(blend, truth):0.00} dB | repeat frame {Psnr(fa, truth):0.00} dB | {ms:0.00} ms per generated frame";
    }

    void SavePng(string path, byte[] bgra)
    {
        using var bmp = new System.Drawing.Bitmap(_texW, _texH, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, _texW, _texH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (int y = 0; y < _texH; y++) Marshal.Copy(bgra, y * _texW * 4, bd.Scan0 + y * bd.Stride, _texW * 4);
        bmp.UnlockBits(bd);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Testing: draws one frame without the render thread and saves exactly what would be shown.</summary>
    public void RenderOnce(VideoFrame f, string pngPath)
    {
        for (int i = 0; i < 150; i++) Signal.Analyze(f, force: true);
        UpdateOutputMode();
        ResizeSwapChain(); _resize = false;
        Upload(f);
        _snapshotPath = pngPath;   // Draw saves it just before presenting
        Draw();
    }

    string? _snapshotPath;
    /// <summary>Raised on the render thread: (path, error or null).</summary>
    public event Action<string, string?>? SnapshotSaved;

    /// <summary>Saves the next drawn picture, exactly as shown, to a PNG.</summary>
    public void RequestSnapshot(string path) { _snapshotPath = path; Invalidate(); }

    unsafe void SaveBackBuffer(string path)
    {
        using var back = _swap!.GetBuffer<ID3D11Texture2D>(0);
        var d = back.Description;
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(d);
        _ctx.CopyResource(staging, back);
        var map = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int w = (int)d.Width, h = (int)d.Height;
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            float white = Color.HdrSdrWhite / 80f;
            for (int y = 0; y < h; y++)
            {
                byte* src = (byte*)map.DataPointer + (long)y * map.RowPitch;
                byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                if (d.Format == Format.B8G8R8A8_UNorm)
                    Buffer.MemoryCopy(src, dst, bd.Stride, w * 4);
                else
                {
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
        finally { _ctx.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(1000);
        _fg.Dispose();
        _srvY?.Dispose(); _srvUV?.Dispose(); _texY?.Dispose(); _texUV?.Dispose();
        _rtv?.Dispose(); _swap?.Dispose();
        _cbuffer.Dispose(); _sampler.Dispose(); _ps.Dispose(); _vs.Dispose();
        _ctx.Dispose(); _device.Dispose(); _factory.Dispose();
    }
}
