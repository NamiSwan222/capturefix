using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CaptureFix.Video;

/// <summary>
/// NVIDIA RTX Video Super Resolution: the driver's AI upscaler, reached through the Direct3D 11 video
/// processor (the same route browsers and video players use). It takes the card's NV12 picture and
/// returns RGB at the size shown on screen. It only does anything when "Super Resolution" is switched
/// on under RTX Video Enhancement in the NVIDIA app; otherwise the driver scales plainly.
/// </summary>
public sealed class AiUpscaler : IDisposable
{
    static readonly Guid NvidiaExtension = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");

    [StructLayout(LayoutKind.Sequential)]
    struct NvExt { public uint Version, Method, Enable; }

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11VideoDevice _vdev;
    readonly ID3D11VideoContext _vctx;
    ID3D11VideoProcessorEnumerator? _enum;
    ID3D11VideoProcessor? _vp;
    ID3D11Texture2D? _in, _out;
    ID3D11VideoProcessorInputView? _inView;
    ID3D11VideoProcessorOutputView? _outView;
    ID3D11ShaderResourceView? _outSrv;
    int _inW, _inH, _outW, _outH;
    uint _frame;
    bool _tenBitOut = Environment.GetEnvironmentVariable("CF_AI_8BIT") == null;

    public int OutWidth => _outW;
    public int OutHeight => _outH;
    public bool TenBit => _tenBitOut;
    /// <summary>Testing: ask the driver for plain scaling instead of the AI.</summary>
    public bool Plain;

    public AiUpscaler(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        _device = device; _ctx = ctx;
        _vdev = device.QueryInterface<ID3D11VideoDevice>();
        _vctx = ctx.QueryInterface<ID3D11VideoContext>();
    }

    /// <summary>True when the device runs on an NVIDIA card.</summary>
    public static bool IsNvidia(ID3D11Device device)
    {
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            return adapter.Description.VendorId == 0x10DE;
        }
        catch { return false; }
    }

    /// <summary>The "Super Resolution" switch of the NVIDIA app, as the driver stores it: true = on,
    /// false = off, null = this driver doesn't say.</summary>
    public static bool? DriverSwitchOn()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return null;
            bool? result = null;
            foreach (var name in cls.GetSubKeyNames())
            {
                try
                {
                    using var k = cls.OpenSubKey(name);
                    if (k?.GetValue("_User_Global_VAL_SuperResolution") is int v)
                    {
                        if (v != 0) return true;
                        result = false;
                    }
                }
                catch { }
            }
            return result;
        }
        catch { return null; }
    }

    void Release()
    {
        _outSrv?.Dispose(); _outView?.Dispose(); _inView?.Dispose(); _out?.Dispose(); _in?.Dispose(); _vp?.Dispose(); _enum?.Dispose();
        _outSrv = null; _outView = null; _inView = null; _out = null; _in = null; _vp = null; _enum = null;
        _inW = 0;
    }

    void Ensure(int w, int h, int ow, int oh)
    {
        if (_vp != null && w == _inW && h == _inH && ow == _outW && oh == _outH) return;
        Release();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)w, InputHeight = (uint)h, OutputWidth = (uint)ow, OutputHeight = (uint)oh,
            InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };
        _enum = _vdev.CreateVideoProcessorEnumerator(content);
        _vp = _vdev.CreateVideoProcessor(_enum, 0);
        _in = _device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _inView = _vdev.CreateVideoProcessorInputView(_in, _enum, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        // Ten bits out keeps the full precision of the picture; eight if the driver refuses.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _out = _device.CreateTexture2D(new Texture2DDescription(_tenBitOut ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm, (uint)ow, (uint)oh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
                _outView = _vdev.CreateVideoProcessorOutputView(_out, _enum, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
                break;
            }
            catch when (_tenBitOut && attempt == 0) { _out?.Dispose(); _out = null; _tenBitOut = false; }
        }
        _outSrv = _device.CreateShaderResourceView(_out);
        _inW = w; _inH = h; _outW = ow; _outH = oh;

        var ext = new NvExt { Version = 1, Method = 2, Enable = Plain ? 0u : 1u };
        unsafe { _vctx.VideoProcessorSetStreamExtension(_vp, 0, NvidiaExtension, (uint)sizeof(NvExt), (nint)(&ext)); }
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(0, 0, w, h));
        _vctx.VideoProcessorSetStreamDestRect(_vp, 0, true, new Vortice.RawRect(0, 0, ow, oh));
        _vctx.VideoProcessorSetOutputTargetRect(_vp, true, new Vortice.RawRect(0, 0, ow, oh));
    }

    /// <summary>Upscales one NV12 frame to ow x oh. The result is plain BT.709 RGB (full range) of the
    /// picture, with no colour settings applied yet.</summary>
    public unsafe ID3D11ShaderResourceView Process(VideoFrame f, int ow, int oh, bool fullRange)
    {
        Ensure(f.Width, f.Height, ow, oh);
        fixed (byte* p = f.Data) _ctx.UpdateSubresource(_in!, 0, null, (nint)p, (uint)f.Pitch, 0);
        _vctx.VideoProcessorSetStreamColorSpace(_vp!, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1, Nominal_Range = fullRange ? 2u : 1u });
        _vctx.VideoProcessorSetOutputColorSpace(_vp!, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1 });
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inView!, InputFrameOrField = _frame, OutputIndex = 0 };
        _vctx.VideoProcessorBlt(_vp!, _outView!, _frame++, 1, new[] { stream });
        return _outSrv!;
    }

    public void Dispose() { Release(); _vctx.Dispose(); _vdev.Dispose(); }
}
