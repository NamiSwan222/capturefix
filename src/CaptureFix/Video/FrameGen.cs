using System.Reflection;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CaptureFix.Video;

[StructLayout(LayoutKind.Sequential)]
struct FrameGenParams
{
    public float TexelX, TexelY, Lod, Alpha;
    public int HasCoarse, Radius;
    public float HdrOut, Smooth, Cut, StaticBias, Refine, EdgeCost;
    public int Taps; public float TapStep, Select, SelMargin;
}

/// <summary>
/// Frame generation on the GPU: keeps the last two converted game frames and draws the picture
/// that belongs between them (see framegen.hlsl). A 30 fps game becomes 60 (or 90, 120) pictures
/// a second; the price is that each real frame is shown a fraction of a frame later, because the
/// next frame has to arrive before the in-between one can be made.
/// </summary>
public sealed class FrameGen : IDisposable
{
    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11VertexShader _vs;
    readonly ID3D11PixelShader _psLuma, _psMotion, _psMedian, _psInterp, _psBlit;
    readonly ID3D11SamplerState _sampler;
    readonly ID3D11Buffer _cb;

    sealed class Target : IDisposable
    {
        public ID3D11Texture2D Tex = null!;
        public ID3D11RenderTargetView Rtv = null!;
        public ID3D11ShaderResourceView Srv = null!;
        public int W, H;
        public void Dispose() { Srv.Dispose(); Rtv.Dispose(); Tex.Dispose(); }
    }

    Target[] _rgb = Array.Empty<Target>();      // [0] = previous frame, [1] = newest frame
    Target[] _luma = Array.Empty<Target>();     // half size, mip-mapped
    Target[] _flowRaw = Array.Empty<Target>(), _flow = Array.Empty<Target>();   // per luma mip level
    Target? _out;                               // off-screen result, for tests
    int _w, _h, _coarsest, _finest;
    Format _format;
    int _frames;

    /// <summary>Luma mip level of the finest motion field: 0 = half the frame size, 1 = a quarter.</summary>
    public int FinestLevel { get; set; } = 1;
    public float Smooth { get; set; } = 0.012f;
    public float StaticBias { get; set; } = 0.05f;
    public float EdgeCost { get; set; } = 0.03f;
    public int Taps { get; set; } = 2;
    public float TapStep { get; set; } = 1.5f;
    public float Select { get; set; } = 1.5f;
    public float SelMargin { get; set; } = 0.03f;
    public bool HdrOut { get; set; }

    public bool HasPair => _frames >= 2;
    public int Width => _w;
    public int Height => _h;

    public FrameGen(ID3D11Device device, ID3D11DeviceContext ctx, ID3D11SamplerState sampler)
    {
        // Tuning overrides for the test tools.
        static float? Env(string n) => float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        if (Env("CF_EDGE") is float e) EdgeCost = e;
        if (Env("CF_TAPS") is float t) Taps = (int)t;
        if (Env("CF_TAPSTEP") is float ts) TapStep = ts;
        if (Env("CF_SELECT") is float se) Select = se;
        if (Env("CF_SELMARGIN") is float sm) SelMargin = sm;
        _device = device; _ctx = ctx; _sampler = sampler;
        string src;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CaptureFix.framegen.hlsl")!)
        using (var r = new StreamReader(s)) src = r.ReadToEnd();
        ReadOnlyMemory<byte> C(string entry, string profile) => Compiler.Compile(src, entry, "framegen.hlsl", profile, ShaderFlags.OptimizationLevel3);
        _vs = device.CreateVertexShader(C("VSMain", "vs_5_0").Span);
        _psLuma = device.CreatePixelShader(C("PSLuma", "ps_5_0").Span);
        _psMotion = device.CreatePixelShader(C("PSMotion", "ps_5_0").Span);
        _psMedian = device.CreatePixelShader(C("PSMedian", "ps_5_0").Span);
        _psInterp = device.CreatePixelShader(C("PSInterp", "ps_5_0").Span);
        _psBlit = device.CreatePixelShader(C("PSBlit", "ps_5_0").Span);
        _cb = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<FrameGenParams>(), BindFlags.ConstantBuffer));
    }

    Target Make(int w, int h, Format fmt, bool mips = false)
    {
        var t = new Target { W = w, H = h };
        t.Tex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = mips ? 0u : 1u, ArraySize = 1, Format = fmt,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = mips ? ResourceOptionFlags.GenerateMips : ResourceOptionFlags.None,
        });
        t.Rtv = _device.CreateRenderTargetView(t.Tex);
        t.Srv = _device.CreateShaderResourceView(t.Tex);
        return t;
    }

    void Free()
    {
        foreach (var t in _rgb.Concat(_luma).Concat(_flowRaw).Concat(_flow)) t?.Dispose();
        _out?.Dispose(); _out = null;
        _rgb = _luma = _flowRaw = _flow = Array.Empty<Target>();
    }

    /// <summary>Forget the stored frames (mode change, scene restart).</summary>
    public void Reset() => _frames = 0;

    void Ensure(int w, int h, Format fmt)
    {
        if (w == _w && h == _h && fmt == _format && _rgb.Length == 2) return;
        Free();
        _w = w; _h = h; _format = fmt; _frames = 0;
        _rgb = new[] { Make(w, h, fmt), Make(w, h, fmt) };
        int lw = Math.Max(1, w / 2), lh = Math.Max(1, h / 2);
        _luma = new[] { Make(lw, lh, Format.R16_UNorm, true), Make(lw, lh, Format.R16_UNorm, true) };

        // Motion fields from the level about 32 texels wide up to the finest one.
        int levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(lw, lh)));
        _coarsest = 0;
        while (_coarsest < levels - 1 && (lw >> _coarsest) > 32) _coarsest++;
        _flowRaw = new Target[_coarsest + 1]; _flow = new Target[_coarsest + 1];
        for (int l = 0; l <= _coarsest; l++)
        {
            int fw = Math.Max(1, lw >> l), fh = Math.Max(1, lh >> l);
            _flowRaw[l] = Make(fw, fh, Format.R16G16B16A16_Float);
            _flow[l] = Make(fw, fh, Format.R16G16B16A16_Float);
        }
    }

    void Pass(Target target, ID3D11PixelShader ps, in FrameGenParams p, params ID3D11ShaderResourceView[] srvs)
        => Pass(target.Rtv, new Viewport(0, 0, target.W, target.H), ps, p, srvs);

    void Pass(ID3D11RenderTargetView rtv, Viewport vp, ID3D11PixelShader ps, in FrameGenParams p, params ID3D11ShaderResourceView[] srvs)
    {
        _ctx.PSUnsetShaderResources(0, 5);
        _ctx.OMSetRenderTargets(rtv);
        _ctx.RSSetViewport(vp);
        _ctx.UpdateSubresource(in p, _cb);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(ps);
        _ctx.PSSetShaderResources(0, srvs);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetConstantBuffer(0, _cb);
        _ctx.Draw(3, 0);
        _ctx.PSUnsetShaderResources(0, 5);
    }

    /// <summary>Makes room for a new frame and returns where to draw it (converted, at source size).</summary>
    public ID3D11RenderTargetView BeginFrame(int w, int h, Format fmt)
    {
        Ensure(w, h, fmt);
        (_rgb[0], _rgb[1]) = (_rgb[1], _rgb[0]);
        (_luma[0], _luma[1]) = (_luma[1], _luma[0]);
        _ctx.PSUnsetShaderResources(0, 5);
        return _rgb[1].Rtv;
    }

    /// <summary>Call after the new frame has been drawn into the target from <see cref="BeginFrame"/>.</summary>
    public void EndFrame()
    {
        var p = new FrameGenParams { HdrOut = HdrOut ? 1 : 0 };
        Pass(_luma[1], _psLuma, p, _rgb[1].Srv);
        _ctx.GenerateMips(_luma[1].Srv);
        _frames = Math.Min(_frames + 1, 2);
    }

    /// <summary>Works out the motion field for a picture at time <paramref name="alpha"/> (0 = previous frame, 1 = newest).</summary>
    void Estimate(float alpha)
    {
        _finest = Math.Clamp(FinestLevel, 0, _coarsest);
        for (int l = _coarsest; l >= _finest; l--)
        {
            var p = new FrameGenParams
            {
                TexelX = 1f / _flow[l].W, TexelY = 1f / _flow[l].H, Lod = l, Alpha = alpha,
                HasCoarse = l < _coarsest ? 1 : 0, Radius = l == _coarsest ? 3 : l >= _coarsest - 1 ? 2 : 1,
                HdrOut = HdrOut ? 1 : 0, Smooth = Smooth, Refine = 1, EdgeCost = EdgeCost, Taps = Taps, TapStep = TapStep,
            };
            if (l < _coarsest) Pass(_flowRaw[l], _psMotion, p, _luma[0].Srv, _luma[1].Srv, _flow[l + 1].Srv);
            else Pass(_flowRaw[l], _psMotion, p, _luma[0].Srv, _luma[1].Srv);
            Pass(_flow[l], _psMedian, p, _flowRaw[l].Srv);
        }
    }

    /// <summary>Draws the in-between picture at <paramref name="alpha"/> into the given target.</summary>
    public void DrawInterpolated(ID3D11RenderTargetView rtv, Viewport vp, float alpha, bool cut = false)
    {
        if (!HasPair) { DrawNewest(rtv, vp); return; }
        if (!cut) Estimate(alpha);
        var p = new FrameGenParams
        {
            Alpha = alpha, HdrOut = HdrOut ? 1 : 0, Cut = cut ? 1 : 0, StaticBias = StaticBias, Select = Select, SelMargin = SelMargin,
            TexelX = 1f / _flow[_finest].W, TexelY = 1f / _flow[_finest].H,
        };
        Pass(rtv, vp, _psInterp, p, _rgb[0].Srv, _rgb[1].Srv, _flow[_finest].Srv, _luma[0].Srv, _luma[1].Srv);
    }

    /// <summary>Draws the newest real frame.</summary>
    public void DrawNewest(ID3D11RenderTargetView rtv, Viewport vp)
        => Pass(rtv, vp, _psBlit, new FrameGenParams(), _rgb[1].Srv);

    // ---------- test support ----------

    Target Out => _out ??= Make(_w, _h, _format);

    /// <summary>Testing: the newest real frame as 8-bit BGRA.</summary>
    public byte[] ReadNewest() => Read(_rgb[1].Tex);

    /// <summary>Testing: the generated picture at <paramref name="alpha"/> as 8-bit BGRA.</summary>
    public byte[] ReadInterpolated(float alpha)
    {
        DrawInterpolated(Out.Rtv, new Viewport(0, 0, _w, _h), alpha);
        return Read(Out.Tex);
    }

    /// <summary>Testing: milliseconds of GPU time to generate one picture.</summary>
    public double Benchmark(int runs = 60)
    {
        using var q = _device.CreateQuery(QueryType.Event, QueryFlags.None);
        void Wait() { _ctx.End(q); _ctx.Flush(); while (!_ctx.GetData(q, out int done) || done == 0) Thread.SpinWait(50); }
        var vp = new Viewport(0, 0, _w, _h);
        DrawInterpolated(Out.Rtv, vp, 0.5f); Wait();
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < runs; i++) DrawInterpolated(Out.Rtv, vp, 0.5f);
        Wait();
        return (System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / runs;
    }

    unsafe byte[] Read(ID3D11Texture2D tex)
    {
        var d = tex.Description;
        if (d.Format != Format.B8G8R8A8_UNorm) throw new NotSupportedException("Readback is for SDR frames.");
        d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(d);
        _ctx.CopyResource(staging, tex);
        var map = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new byte[_w * _h * 4];
            fixed (byte* dst = bytes)
                for (int y = 0; y < _h; y++)
                    Buffer.MemoryCopy((byte*)map.DataPointer + (long)y * map.RowPitch, dst + (long)y * _w * 4, _w * 4, _w * 4);
            return bytes;
        }
        finally { _ctx.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        Free();
        _cb.Dispose(); _psBlit.Dispose(); _psInterp.Dispose(); _psMedian.Dispose(); _psMotion.Dispose(); _psLuma.Dispose(); _vs.Dispose();
    }
}
