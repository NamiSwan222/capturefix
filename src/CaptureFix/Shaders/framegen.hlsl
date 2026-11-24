// Frame generation: makes the in-between picture for two real game frames.
//
// 1. PSLuma     brightness of each converted frame at half size (then mip-mapped down to ~30x17).
// 2. PSMotion   motion search, coarsest level first. For every spot of the picture being made it
//               finds the vector v where the previous frame at (p - a*v) looks most like the next
//               frame at (p + (1-a)*v). Searching from the in-between picture's own point of view
//               leaves no holes to fill.
// 3. PSMedian   3x3 median of the vectors: removes single wrong vectors without blurring edges.
// 4. PSInterp   fetches both frames along the vector and mixes them. Where doing nothing matches
//               better than following the motion (HUD, map, subtitles: things that don't move),
//               the pixel is left alone, so text stays sharp over a moving background.

Texture2D<float4> T0 : register(t0);
Texture2D<float4> T1 : register(t1);
Texture2D<float4> T2 : register(t2);
Texture2D<float4> T3 : register(t3);
Texture2D<float4> T4 : register(t4);
SamplerState Linear : register(s0);

cbuffer FrameGen : register(b0)
{
    float2 Texel;       // one texel of the level being worked on, in UV
    float  Lod;         // luma mip level being matched
    float  Alpha;       // time of the generated picture: 0 = previous frame, 1 = next frame
    int    HasCoarse;   // a coarser motion field exists to start from
    int    Radius;      // search radius in texels around the starting vector
    float  HdrOut;      // 1 = frames are linear scRGB, 0 = gamma-encoded SDR
    float  Smooth;      // cost of moving away from the starting vector, per texel
    float  Cut;         // 1 = scene change: don't mix the two frames
    float  StaticBias;  // how readily a pixel is treated as not moving
    float  Refine;      // 1 = also search in half and quarter texel steps
    float  EdgeCost;    // cost of a tap that falls outside the picture
    int    Taps;        // patch is (2*Taps+1)^2 samples
    float  TapStep;     // spacing of the patch samples, in texels
    float  Select;      // final pass: pick among neighbouring vectors, this many texels apart (0 = off)
    float  SelMargin;   // how much better a neighbouring vector must be to replace the one under the pixel
    int    HasHistory;  // T3 holds the motion field of the previous generated picture
    float3 Pad;
};

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// Roughly perceptual, 0..1, for both SDR (gamma) and HDR (linear, 1.0 = 80 nits) frames.
float3 Perceptual(float3 c)
{
    if (HdrOut > 0.5) { c = max(c, 0) / 3.0; return sqrt(c / (1.0 + c)); }
    return saturate(c);
}
float Luma(float3 c) { return dot(Perceptual(c), float3(0.299, 0.587, 0.114)); }

// T0 = converted frame. Target: half-size luma (bilinear at the centre of 2x2 = their average).
float4 PSLuma(VSOut i) : SV_Target
{
    return Luma(T0.SampleLevel(Linear, i.uv, 0).rgb);
}

float4 PSBlit(VSOut i) : SV_Target
{
    return float4(T0.SampleLevel(Linear, i.uv, 0).rgb, 1);
}

// Sharp upscaling for a window bigger than the game picture: Catmull-Rom bicubic (16 texels, done
// in 9 bilinear fetches), then pulled back toward the range of the 4 nearest texels so edges get
// crisp without bright or dark halos. Texel = one source texel in UV; Alpha = halo limit (0..1).
float4 PSScale(VSOut i) : SV_Target
{
    float2 size = 1.0 / Texel;
    float2 pos = i.uv * size;
    float2 p1 = floor(pos - 0.5) + 0.5;
    float2 f = pos - p1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 t0 = (p1 - 1.0) * Texel, t3 = (p1 + 2.0) * Texel, t12 = (p1 + w2 / w12) * Texel;

    float3 c = 0;
    c += T0.SampleLevel(Linear, float2(t0.x, t0.y), 0).rgb * (w0.x * w0.y);
    c += T0.SampleLevel(Linear, float2(t12.x, t0.y), 0).rgb * (w12.x * w0.y);
    c += T0.SampleLevel(Linear, float2(t3.x, t0.y), 0).rgb * (w3.x * w0.y);
    c += T0.SampleLevel(Linear, float2(t0.x, t12.y), 0).rgb * (w0.x * w12.y);
    c += T0.SampleLevel(Linear, float2(t12.x, t12.y), 0).rgb * (w12.x * w12.y);
    c += T0.SampleLevel(Linear, float2(t3.x, t12.y), 0).rgb * (w3.x * w12.y);
    c += T0.SampleLevel(Linear, float2(t0.x, t3.y), 0).rgb * (w0.x * w3.y);
    c += T0.SampleLevel(Linear, float2(t12.x, t3.y), 0).rgb * (w12.x * w3.y);
    c += T0.SampleLevel(Linear, float2(t3.x, t3.y), 0).rgb * (w3.x * w3.y);

    float2 q = p1 * Texel;
    float3 a = T0.SampleLevel(Linear, q, 0).rgb, b = T0.SampleLevel(Linear, q + float2(Texel.x, 0), 0).rgb;
    float3 d = T0.SampleLevel(Linear, q + float2(0, Texel.y), 0).rgb, e = T0.SampleLevel(Linear, q + Texel, 0).rgb;
    float3 lo = min(min(a, b), min(d, e)), hi = max(max(a, b), max(d, e));
    return float4(lerp(c, clamp(c, lo, hi), Alpha), 1);
}

// T0 = previous luma, T1 = next luma, T2 = coarser motion field, T3 = last picture's motion field.
float MatchCost(float2 uv, float2 v)
{
    float2 pa = uv - Alpha * v, pb = uv + (1.0 - Alpha) * v;
    float c = 0;
    [loop] for (int y = -Taps; y <= Taps; y++)
    [loop] for (int x = -Taps; x <= Taps; x++)
    {
        float2 o = float2(x, y) * Texel * TapStep;
        float2 qa = pa + o, qb = pb + o;
        // Near the picture edge the true vector points outside one frame, where there is nothing
        // to compare. Such a tap counts as a so-so match: not proof, but not ruled out either, so
        // the vector handed down from the coarser level (which saw the interior) can win.
        bool inside = all(qa >= 0) && all(qa <= 1) && all(qb >= 0) && all(qb <= 1);
        c += inside ? abs(T0.SampleLevel(Linear, qa, Lod).r - T1.SampleLevel(Linear, qb, Lod).r) : EdgeCost;
    }
    float n = 2 * Taps + 1;
    return c * 9.0 / (n * n);   // same scale as a 3x3 patch
}

float4 PSMotion(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float2 start = HasCoarse ? T2.SampleLevel(Linear, uv, 0).xy : float2(0, 0);

    float2 best = start;
    float bestCost = 1e9;
    [loop] for (int y = -Radius; y <= Radius; y++)
    [loop] for (int x = -Radius; x <= Radius; x++)
    {
        float2 d = float2(x, y);
        float c = MatchCost(uv, start + d * Texel) + Smooth * length(d);
        if (c < bestCost) { bestCost = c; best = start + d * Texel; }
    }

    // Other ideas worth one try each: not moving at all, and what the neighbours are doing
    // (the right answer near the edge of a moving object is often next door).
    float c0 = MatchCost(uv, float2(0, 0)) + Smooth * 0.5;
    if (c0 < bestCost) { bestCost = c0; best = float2(0, 0); }
    if (HasCoarse)
    {
        static const float2 N[4] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };
        [unroll] for (int k = 0; k < 4; k++)
        {
            float2 v = T2.SampleLevel(Linear, uv + N[k] * Texel * 3.0, 0).xy;
            float c = MatchCost(uv, v) + Smooth * 0.5;
            if (c < bestCost) { bestCost = c; best = v; }
        }
    }
    // Things keep moving the way they did a moment ago: try the vector this spot had in the
    // previous generated picture. This is what keeps hold of a fast object with little detail
    // (a dark van, a plain wall) once it has been found.
    if (HasHistory)
    {
        static const float2 H[5] = { float2(0, 0), float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1) };
        [unroll] for (int k = 0; k < 5; k++)
        {
            float2 v = T3.SampleLevel(Linear, uv + H[k] * Texel * 2.0, 0).xy;
            float c = MatchCost(uv, v) + Smooth * 0.5;
            if (c < bestCost) { bestCost = c; best = v; }
        }
    }

    // Sub-texel steps: without them vectors snap to whole texels of this level, and a slow pan
    // comes out a pixel or two off, which blurs fine detail.
    if (Refine > 0.5)
    {
        static const float2 D[8] = { float2(1, 0), float2(-1, 0), float2(0, 1), float2(0, -1),
                                     float2(1, 1), float2(-1, 1), float2(1, -1), float2(-1, -1) };
        float stepSize = 0.5;
        [unroll] for (int r = 0; r < 2; r++)
        {
            float2 centre = best;
            [unroll] for (int k = 0; k < 8; k++)
            {
                float2 v = centre + D[k] * Texel * stepSize;
                float c = MatchCost(uv, v) + Smooth * length(v - start) / max(Texel.x, 1e-6) * 0.25;
                if (c < bestCost - 1e-5) { bestCost = c; best = v; }
            }
            stepSize *= 0.5;
        }
    }
    return float4(best, bestCost, 1);
}

#define S2(a, b) { float2 t = min(a, b); b = max(a, b); a = t; }
#define MN3(a, b, c) S2(a, b); S2(a, c);
#define MX3(a, b, c) S2(b, c); S2(a, c);
#define MNMX3(a, b, c) MX3(a, b, c); S2(a, b);
#define MNMX4(a, b, c, d) S2(a, b); S2(c, d); S2(a, c); S2(b, d);
#define MNMX5(a, b, c, d, e) S2(a, b); S2(c, d); MN3(a, c, e); MX3(b, d, e);
#define MNMX6(a, b, c, d, e, f) S2(a, d); S2(b, e); S2(c, f); MN3(a, b, c); MX3(d, e, f);

// T0 = raw motion field of this level.
float4 PSMedian(VSOut i) : SV_Target
{
    float2 v[9];
    int n = 0;
    [unroll] for (int y = -1; y <= 1; y++)
    [unroll] for (int x = -1; x <= 1; x++)
        v[n++] = T0.SampleLevel(Linear, i.uv + float2(x, y) * Texel, 0).xy;
    MNMX6(v[0], v[1], v[2], v[3], v[4], v[5]);
    MNMX5(v[1], v[2], v[3], v[4], v[6]);
    MNMX4(v[2], v[3], v[4], v[7]);
    MNMX3(v[3], v[4], v[8]);
    return float4(v[4], 0, 1);
}

// T0 = previous frame, T1 = next frame, T2 = motion field, T3 = previous luma, T4 = next luma.
float4 PSInterp(VSOut i) : SV_Target
{
    float2 uv = i.uv;
    float3 sp = T0.SampleLevel(Linear, uv, 0).rgb;
    float3 sc = T1.SampleLevel(Linear, uv, 0).rgb;
    if (Cut > 0.5) return float4(sp, 1);

    // The motion field is coarser than the picture, so right at the edge of a moving object or
    // a HUD element the vector under this pixel may belong to the other side. Try the vectors
    // of the surrounding texels too and keep the one that makes the two frames agree best here.
    float2 v = T2.SampleLevel(Linear, uv, 0).xy;
    float3 changed = abs(Perceptual(sp) - Perceptual(sc));
    // Not for pixels that are the same in both frames (HUD text): there the vector under the
    // pixel must stay "wrong", so the mismatch it causes keeps the pixel protected below.
    if (Select > 0.5 && max(changed.r, max(changed.g, changed.b)) > 0.008)
    {
        float bestErr = 1e9;
        float2 bestV = v;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 cv = T2.SampleLevel(Linear, uv + float2(x, y) * Texel * Select, 0).xy;
            float2 ca = uv - Alpha * cv, cb = uv + (1.0 - Alpha) * cv;
            float err = Luma(abs(T0.SampleLevel(Linear, ca, 0).rgb - T1.SampleLevel(Linear, cb, 0).rgb))
                      + abs(T3.SampleLevel(Linear, ca, 0).r - T4.SampleLevel(Linear, cb, 0).r)
                      + ((x != 0 || y != 0) ? SelMargin : 0.0);   // a neighbour has to be clearly better
            if (err < bestErr) { bestErr = err; bestV = cv; }
        }
        v = bestV;
    }
    float2 ua = uv - Alpha * v, ub = uv + (1.0 - Alpha) * v;
    float3 a = T0.SampleLevel(Linear, ua, 0).rgb;
    float3 b = T1.SampleLevel(Linear, ub, 0).rgb;

    // Came from outside the picture on one side: only the other frame has it.
    bool inA = all(ua >= 0) && all(ua <= 1), inB = all(ub >= 0) && all(ub <= 1);
    float3 moved = lerp(a, b, Alpha);
    if (!inA && inB) moved = b; else if (inA && !inB) moved = a;

    // How well does each explanation fit: "it moved along v" or "it didn't move"?
    float dm = Luma(abs(a - b)) + abs(T3.SampleLevel(Linear, ua, 0).r - T4.SampleLevel(Linear, ub, 0).r);
    float ds = Luma(abs(sp - sc)) + abs(T3.SampleLevel(Linear, uv, 0).r - T4.SampleLevel(Linear, uv, 0).r);
    float still = smoothstep(0.0, StaticBias, dm - ds);
    // A pixel that is exactly the same in both frames stays as it is. Opaque HUD pixels are
    // bit-identical between frames; a moving picture almost never is.
    return float4(lerp(moved, lerp(sp, sc, Alpha), still), 1);
}

// ---------- picture enhancers (last pass, at the size shown on screen) ----------
//
// T0 = the picture already scaled to its on-screen size (with mips when Clarity is on).
// The cbuffer is reused: Texel = one output pixel in UV, Alpha = sharpening (0..1),
// Smooth = debanding (0..1), StaticBias = clarity (0..1), EdgeCost = vibrance (0..1),
// Refine = output height / 1080 (so radii cover the same part of the picture at any size),
// Lod = mip level used as the blurred picture for clarity.
//
// Sharpening is contrast adaptive (the same idea as AMD CAS, the sharpening stage of FSR):
// flat and already-contrasty areas are left alone, so it does not add halos or noise.
// Debanding smooths the visible steps in skies and fog that 8-bit video leaves behind, and only
// touches areas that are nearly flat already. Clarity lifts local contrast in the midtones.

float3 Enc(float3 c) { return HdrOut > 0.5 ? pow(max(c, 0) / 12.5, 1.0 / 2.2) : c; }
float3 Dec(float3 c) { return HdrOut > 0.5 ? pow(max(c, 0), 2.2) * 12.5 : saturate(c); }
float3 Tap(float2 uv) { return Enc(T0.SampleLevel(Linear, uv, 0).rgb); }
float Hash(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}
float Max3(float3 v) { return max(v.r, max(v.g, v.b)); }

float4 PSEnhance(VSOut i) : SV_Target
{
    float sharp = Alpha, deband = Smooth, clarity = StaticBias, vib = EdgeCost;
    static const float3 LW = float3(0.2126, 0.7152, 0.0722);
    float3 c = Tap(i.uv);
    float3 o = c;

    if (deband > 0)
    {
        float2 px = floor(i.pos.xy);
        bool changed = false;
        for (int k = 1; k <= 2; k++)
        {
            float ang = Hash(px + k * 17.0) * 6.2831853;
            float dist = (0.35 + 0.65 * Hash(px.yx + k * 31.0)) * 10.0 * k * Refine;
            float2 dir = float2(cos(ang), sin(ang)) * dist;
            float2 d = dir * Texel, e = float2(-dir.y, dir.x) * Texel;
            float3 s0 = Tap(i.uv + d), s1 = Tap(i.uv - d), s2 = Tap(i.uv + e), s3 = Tap(i.uv - e);
            float3 avg = (s0 + s1 + s2 + s3) * 0.25;
            float spread = Max3(max(max(abs(s0 - o), abs(s1 - o)), max(abs(s2 - o), abs(s3 - o))));
            float thr = lerp(0.004, 0.011, deband) / k;
            if (spread < thr * 2.0 && Max3(abs(avg - o)) < thr) { o = avg; changed = true; }
        }
        // A little noise where a gradient was smoothed, so the 8-bit output does not band again.
        if (changed) o += (Hash(px + 91.7) + Hash(px + 13.3) - 1.0) * (0.7 / 255.0);
    }

    if (sharp > 0)
    {
        float3 a = Tap(i.uv - float2(0, Texel.y)), b = Tap(i.uv - float2(Texel.x, 0));
        float3 d = Tap(i.uv + float2(Texel.x, 0)), e = Tap(i.uv + float2(0, Texel.y));
        float3 mn = min(c, min(min(a, b), min(d, e))), mx = max(c, max(max(a, b), max(d, e)));
        float3 amp = sqrt(saturate(min(mn, 1.0 - mx) / max(mx, 1e-4)));
        float3 w = amp * -lerp(0.0, 0.2, sharp);
        o += (c + (a + b + d + e) * w) / (1.0 + 4.0 * w) - c;
    }

    if (clarity > 0)
    {
        float2 t = Texel * exp2(Lod) * 0.75;
        float3 bl = Enc(T0.SampleLevel(Linear, i.uv + float2(t.x, t.y), Lod).rgb) + Enc(T0.SampleLevel(Linear, i.uv + float2(-t.x, t.y), Lod).rgb)
                  + Enc(T0.SampleLevel(Linear, i.uv + float2(t.x, -t.y), Lod).rgb) + Enc(T0.SampleLevel(Linear, i.uv - float2(t.x, t.y), Lod).rgb);
        float L = dot(o, LW), B = dot(bl, LW) * 0.25;
        float m = 2.0 * saturate(L) - 1.0;
        float dl = clamp(L - B, -0.12, 0.12) * clarity * (1.0 - m * m);
        o *= (max(L, 0.02) + dl) / max(L, 0.02);
    }

    if (vib > 0)
    {
        float L = dot(o, LW);
        float sat = Max3(o) - min(o.r, min(o.g, o.b));
        o = L + (o - L) * (1.0 + vib * 0.6 * (1.0 - saturate(sat * 1.5)));
    }
    return float4(Dec(o), 1);
}
