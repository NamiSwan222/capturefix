// Capture card frame -> screen.
//
// Inputs come straight from the card, untouched:
//   NV12  8-bit 4:2:0   t0 = Y (R8),  t1 = CbCr (R8G8)
//   P010 10-bit 4:2:0   t0 = Y (R16), t1 = CbCr (R16G16), 10 bits in the top of each 16
//   YUY2  8-bit 4:2:2   t0 = packed Y0 Cb Y1 Cr (R8G8B8A8), t1 unused
//
// HDR games send BT.2020 colors with the PQ (SMPTE 2084) curve. Showing those numbers as if they
// were normal SDR video is what makes HDR look grey and washed out; guessing the brightness wrong
// is what makes it too dark. Here they are decoded to real light levels (nits) and then either
// tone mapped to SDR with the BT.2390 curve, or handed to an HDR monitor as scRGB.

Texture2D<float4> PlaneY  : register(t0);
Texture2D<float4> PlaneUV : register(t1);
SamplerState Linear : register(s0);

cbuffer Params : register(b0)
{
    int   Format;       // 0 = planar (NV12/P010), 1 = YUY2
    int   HdrIn;        // 1 = signal is HDR10 (PQ + BT.2020)
    int   HdrOut;       // 1 = swap chain is scRGB (FP16, 1.0 = 80 nits)
    int   FullRange;    // 1 = full range YCbCr (0-255), 0 = limited/TV range (16-235)
    float PeakIn;       // brightest level the game sends, in nits (PS5 HDR calibration)
    float SdrPeak;      // SDR output: nits shown as full white (lower = brighter picture)
    float HdrSdrWhite;  // HDR output: nits for SDR white
    int   TenBit;       // 1 = P010
    float2 TexSize;     // luma size in pixels
    float Saturation;
    int   ToneMap;      // 0 = Natural (per channel, like a TV), 1 = Vivid (keeps saturation)
    float Contrast;     // 1 = unchanged
    float BlackLevel;   // -0.1..0.1, added in the display (gamma) domain
    float Gamma;        // output encoding exponent, 2.2 = Windows/sRGB-like
    float Warmth;       // -1 (cool) .. 1 (warm)
    float Sharpness;    // 0..1
    float GamutOn;      // 1 = convert to the monitor's own primaries (wide-colour screens)
    float2 Pad;
    float4 Gamut0;      // rows of the BT.709 -> monitor matrix
    float4 Gamut1;
    float4 Gamut2;
};

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    // One triangle that covers the viewport.
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

static const float PQ_M1 = 0.1593017578125;
static const float PQ_M2 = 78.84375;
static const float PQ_C1 = 0.8359375;
static const float PQ_C2 = 18.8515625;
static const float PQ_C3 = 18.6875;

float3 PqToNits(float3 e)
{
    float3 p = pow(saturate(e), 1.0 / PQ_M2);
    return 10000.0 * pow(max(p - PQ_C1, 0) / (PQ_C2 - PQ_C3 * p), 1.0 / PQ_M1);
}

float PqFromNits(float nits)
{
    float y = pow(saturate(nits / 10000.0), PQ_M1);
    return pow((PQ_C1 + PQ_C2 * y) / (1.0 + PQ_C3 * y), PQ_M2);
}

float PqToNits1(float e) { return PqToNits(float3(e, e, e)).x; }

// BT.2390 EETF: leaves everything below the knee alone, rolls the rest off smoothly so the
// game's peak lands exactly on the target peak. Works in PQ space, as the spec does.
float Eetf(float nits, float srcPeak, float dstPeak)
{
    if (srcPeak <= dstPeak) return min(nits, dstPeak);
    float srcPq = PqFromNits(srcPeak);
    float e = PqFromNits(nits) / srcPq;           // 0..1 of the source range
    float maxLum = PqFromNits(dstPeak) / srcPq;
    float ks = 1.5 * maxLum - 0.5;
    if (e > ks)
    {
        float t = (e - ks) / (1.0 - ks);
        float t2 = t * t, t3 = t2 * t;
        e = (2 * t3 - 3 * t2 + 1) * ks + (t3 - 2 * t2 + t) * (1 - ks) + (-2 * t3 + 3 * t2) * maxLum;
    }
    return PqToNits1(min(e, 1.0) * srcPq);
}

float3 Bt2020ToBt709(float3 c)
{
    return float3(
        dot(c, float3( 1.6605, -0.5876, -0.0728)),
        dot(c, float3(-0.1246,  1.1329, -0.0083)),
        dot(c, float3(-0.0182, -0.1006,  1.1187)));
}

float Luma709(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float3 SrgbToLinear(float3 c) { return pow(saturate(c), 2.2); }
float3 LinearToSrgb(float3 c) { return pow(saturate(c), 1.0 / 2.2); }

float3 SampleYCbCr(float2 uv)
{
    float y, cb, cr;
    if (Format == 1)
    {
        // YUY2: each texel is two pixels. Nearest pixel for luma, shared chroma.
        int2 p = int2(uv * TexSize);
        p = clamp(p, int2(0, 0), int2(TexSize) - 1);
        float4 t = PlaneY.Load(int3(p.x >> 1, p.y, 0));
        y = (p.x & 1) ? t.b : t.r;
        cb = t.g; cr = t.a;
    }
    else
    {
        y = PlaneY.Sample(Linear, uv).r;
        if (Sharpness > 0)
        {
            // Unsharp mask on luma only: crisper text and edges without colour fringes.
            float2 t = 1.0 / TexSize;
            float n = PlaneY.Sample(Linear, uv + float2(t.x, 0)).r + PlaneY.Sample(Linear, uv - float2(t.x, 0)).r
                    + PlaneY.Sample(Linear, uv + float2(0, t.y)).r + PlaneY.Sample(Linear, uv - float2(0, t.y)).r;
            y = y + Sharpness * 1.5 * (y - n * 0.25);
        }
        float2 c = PlaneUV.Sample(Linear, uv).rg;
        cb = c.x; cr = c.y;
    }

    float3 ycc = float3(y, cb, cr);
    if (TenBit)
    {
        // R16_UNORM holds code << 6. Back to 10-bit code values.
        float3 code = ycc * (65535.0 / 64.0);
        if (FullRange) return float3(code.x / 1023.0, (code.yz - 512.0) / 1023.0);
        return float3((code.x - 64.0) / 876.0, (code.yz - 512.0) / 896.0);
    }
    float3 code8 = ycc * 255.0;
    if (FullRange) return float3(code8.x / 255.0, (code8.yz - 128.0) / 255.0);
    return float3((code8.x - 16.0) / 219.0, (code8.yz - 128.0) / 224.0);
}

float3 YCbCrToRgb(float3 ycc, bool bt2020)
{
    float y = ycc.x, cb = ycc.y, cr = ycc.z;
    if (bt2020)
        return float3(y + 1.4746 * cr, y - 0.16455 * cb - 0.57135 * cr, y + 1.8814 * cb);
    return float3(y + 1.5748 * cr, y - 0.1873 * cb - 0.4681 * cr, y + 1.8556 * cb);
}

float3 ApplySaturation(float3 lin)
{
    float l = Luma709(lin);
    return max(l + (lin - l) * Saturation, 0);
}

float3 WhiteBalance(float3 lin)
{
    // Simple warm/cool tilt, normalised so white stays as bright.
    float3 k = float3(1 + 0.12 * Warmth, 1 + 0.02 * Warmth, 1 - 0.18 * Warmth);
    return lin * k / Luma709(k);
}

// Display-referred (0..1, gamma encoded) adjustments shared by every path.
float3 Finish(float3 lin)
{
    lin = WhiteBalance(ApplySaturation(max(lin, 0)));
    // A wide-colour monitor shows standard numbers too saturated (reds most of all). Give it the
    // numbers that produce the intended colours on its own primaries.
    if (GamutOn > 0.5)
        lin = max(float3(dot(Gamut0.xyz, lin), dot(Gamut1.xyz, lin), dot(Gamut2.xyz, lin)), 0);
    float3 e = pow(saturate(lin), 1.0 / Gamma);
    e = (e - 0.5) * Contrast + 0.5 + BlackLevel;
    return saturate(e);
}

float4 PSMain(VSOut i) : SV_Target
{
    float3 ycc = SampleYCbCr(i.uv);

    if (HdrIn)
    {
        float3 nits = Bt2020ToBt709(PqToNits(YCbCrToRgb(ycc, true)));

        // Colors outside BT.709: pull toward grey instead of clipping channels (keeps hue).
        float l = max(Luma709(nits), 0);
        float mn = min(nits.r, min(nits.g, nits.b));
        if (mn < 0) nits = l + (nits - l) * (l / max(l - mn, 1e-4));
        nits = max(nits, 0);

        if (HdrOut)
            return float4(WhiteBalance(ApplySaturation(nits)) / 80.0, 1);

        float3 lin;
        if (ToneMap == 1)
        {
            // Vivid: scale by the brightest channel. Keeps full saturation, but very bright
            // yellows and golds drift toward orange.
            float m = max(nits.r, max(nits.g, nits.b));
            lin = m > 0 ? nits * (Eetf(m, PeakIn, SdrPeak) / m) : 0;
        }
        else
        {
            // Natural: each channel through the curve, as TVs and the games' own SDR modes do.
            // Bright colours ease toward white, so gold stays gold.
            lin = float3(Eetf(nits.r, PeakIn, SdrPeak), Eetf(nits.g, PeakIn, SdrPeak), Eetf(nits.b, PeakIn, SdrPeak));
        }
        return float4(Finish(lin / SdrPeak), 1);
    }

    float3 rgb = saturate(YCbCrToRgb(ycc, false));
    if (HdrOut)
        return float4(WhiteBalance(ApplySaturation(SrgbToLinear(rgb))) * (HdrSdrWhite / 80.0), 1);
    return float4(Finish(pow(rgb, Gamma)), 1);
}
