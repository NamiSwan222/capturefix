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
        Signal.Changed += (h, f) => SignalDetected?.Invoke(h, f);
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

    void Loop()
    {
        long fpsStart = Stopwatch.GetTimestamp(); int fpsCount = 0;
        double latencyAcc = 0; int latencyCount = 0;
        while (!_stop)
        {
            var src = Source;
            if (src != null) WaitHandle.WaitAny(new WaitHandle[] { src.FrameReady, _wake }, 100);
            else _wake.WaitOne(100);
            if (_stop) break;

            try
            {
                if (_clear) { _clear = false; _haveFrame = false; _dirty = true; }
                UpdateOutputMode();
                if (_resize) { ResizeSwapChain(); _resize = false; _dirty = true; }

                var frame = src?.TakeLatest();
                if (frame != null)
                {
                    Upload(frame);
                    Signal.Analyze(frame);
                    long arrived = frame.ArrivedTicks;
                    src!.Return(frame);
                    Draw();
                    latencyAcc += (Stopwatch.GetTimestamp() - arrived) * 1000.0 / Stopwatch.Frequency;
                    latencyCount++; fpsCount++;
                }
                else if (_dirty) Draw();
                _dirty = false;

                long now = Stopwatch.GetTimestamp();
                double secs = (now - fpsStart) / (double)Stopwatch.Frequency;
                if (secs >= 1)
                {
                    PresentFps = fpsCount / secs;
                    LatencyMs = latencyCount > 0 ? latencyAcc / latencyCount : 0;
                    fpsStart = now; fpsCount = 0; latencyAcc = 0; latencyCount = 0;
                }
                Error = null;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Thread.Sleep(250);
                _resize = true;
            }
        }
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

    void Draw()
    {
        if (_swap == null || _rtv == null) return;
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.ClearRenderTargetView(_rtv, new Color4(0, 0, 0, 1));

        if (_haveFrame && _srvY != null)
        {
            // Letterbox: keep the console's aspect ratio.
            float scale = Math.Min((float)_width / _texW, (float)_height / _texH);
            float w = _texW * scale, h = _texH * scale;
            _ctx.RSSetViewport(new Viewport((_width - w) / 2, (_height - h) / 2, w, h));

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
            _ctx.PSSetShaderResources(0, new[] { _srvY, _srvUV ?? _srvY });
            _ctx.PSSetSampler(0, _sampler);
            _ctx.PSSetConstantBuffer(0, _cbuffer);
            _ctx.Draw(3, 0);
        }
        var snap = Interlocked.Exchange(ref _snapshotPath, null);
        if (snap != null)
        {
            try { SaveBackBuffer(snap); SnapshotSaved?.Invoke(snap, null); }
            catch (Exception ex) { SnapshotSaved?.Invoke(snap, ex.Message); }
        }
        _swap.Present(0, PresentFlags.None);
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
        _srvY?.Dispose(); _srvUV?.Dispose(); _texY?.Dispose(); _texUV?.Dispose();
        _rtv?.Dispose(); _swap?.Dispose();
        _cbuffer.Dispose(); _sampler.Dispose(); _ps.Dispose(); _vs.Dispose();
        _ctx.Dispose(); _device.Dispose(); _factory.Dispose();
    }
}
