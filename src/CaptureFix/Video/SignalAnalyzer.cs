using System.Diagnostics;

namespace CaptureFix.Video;

/// <summary>
/// Works out from the picture itself whether the capture card is delivering HDR10 or SDR, and
/// whether its brightness range is limited (black = 16) or full (black = 0). Wrong guesses are what
/// make HDR look dim, flat or too contrasty, so nobody should have to set them by hand.
///
/// What the evidence looks like (luma on a 10-bit scale):
///  - Range: limited-range video never goes below 64 and parks black at 64. Full range puts black
///    at 0-30. The HD60 X's range varies (seen: a PS5 HDR menu in full range, with black near 14 and
///    nothing at 64, and another in limited range). Reading full range as limited crushes every
///    shadow; reading limited as full lifts blacks to grey.
///  - SDR: white text, lights and highlights reach the top of the range (940, or 1023 in full range).
///    HDR10 keeps normal content at 200-600 and doesn't go near 900 (that would be over 5000 nits),
///    so seeing a handful of pixels there proves the signal is SDR. HDR is the default whenever that
///    proof doesn't show up for a long time, and SDR wins again the moment it does.
/// </summary>
public sealed class SignalAnalyzer
{
    const int SampleStep = 8;                 // look at 1 pixel in 64
    const long IntervalMs = 250;              // 4 looks per second
    const int RangeVotesFirst = 3;            // 0.75 s to decide the first time
    const int RangeVotesFlip = 8;             // 2 s of contrary evidence to change a decision
    const int SdrProofVotes = 2;              // two looks in a row with SDR-only brightness
    const int HdrQuietLooks = 120;            // 30 s of picture without any SDR proof to go back to HDR

    public bool Hdr { get; private set; }
    public bool FullRange { get; private set; }
    public bool HdrKnown { get; private set; }
    public bool RangeKnown { get; private set; }

    /// <summary>Raised on the render thread when a decision changes: (hdr, fullRange).</summary>
    public event Action<bool, bool>? Changed;

    long _last;
    int _fullVotes, _limitedVotes, _sdrProof, _quiet;

    // Last measurements, for the stats overlay and tests.
    public int BlackCode { get; private set; }
    public int WhiteCode { get; private set; }

    public SignalAnalyzer() { Reset(true, true); }

    /// <summary>Start from what was seen last time, until the picture says otherwise.</summary>
    public void Reset(bool hdrGuess, bool fullGuess)
    {
        Hdr = hdrGuess; FullRange = fullGuess;
        HdrKnown = false; RangeKnown = false;
        _fullVotes = _limitedVotes = _sdrProof = _quiet = 0;
        _last = 0;
    }

    public void Analyze(VideoFrame f, bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && _last != 0 && (now - _last) * 1000 < IntervalMs * Stopwatch.Frequency) return;
        _last = now;

        var hist = new int[1024];
        int n = 0;
        for (int y = SampleStep / 2; y < f.Height; y += SampleStep)
        {
            int row = y * f.Pitch;
            for (int x = SampleStep / 2; x < f.Width; x += SampleStep)
            {
                int code;
                switch (f.Format)
                {
                    case PixelFormat.P010: code = (f.Data[row + x * 2] | f.Data[row + x * 2 + 1] << 8) >> 6; break;
                    case PixelFormat.NV12: code = f.Data[row + x] << 2; break;
                    default: code = f.Data[row + x * 2] << 2; break;   // YUY2: Y0 Cb Y1 Cr
                }
                hist[code & 1023]++; n++;
            }
        }
        if (n == 0) return;

        int Percentile(double p)
        {
            long target = (long)(n * p), acc = 0;
            for (int i = 0; i < 1024; i++) { acc += hist[i]; if (acc > target) return i; }
            return 1023;
        }
        int p2 = Percentile(0.02), p99 = Percentile(0.99);
        int high = 0;
        for (int i = 900; i < 1024; i++) high += hist[i];

        BlackCode = p2; WhiteCode = Percentile(0.999);
        bool active = p99 >= 120;             // something is on screen (not a black frame / no signal)
        if (!active) return;

        // --- range ---
        // Black level is the best clue. Without black on screen, the white level decides: limited
        // range clips white at 940 (a pile-up there and nothing above 960), full range reaches 1023.
        int clip940 = 0, above960 = 0, above1000 = 0;
        for (int i = 936; i <= 944; i++) clip940 += hist[i];
        for (int i = 960; i < 1024; i++) above960 += hist[i];
        for (int i = 1000; i < 1024; i++) above1000 += hist[i];
        int few = Math.Max(4, n / 2000);   // ~0.05% of pixels
        bool blackFull = p2 < 44, blackLimited = p2 is >= 56 and <= 80;
        bool whiteLimited = clip940 >= few && above960 <= n / 20000;
        bool whiteFull = above1000 >= few;

        // The hard evidence: a limited-range signal cannot go below 64 or above 940 at all (a console
        // clips there exactly, leaving a pile-up on the limit and nothing beyond), while a full-range
        // picture with any shadows or highlights has plenty beyond. Measured on one PS5: gameplay at
        // 16-235 exactly, and later a pause menu with 5-9% below 64 and 1% above 940. The range
        // follows the console's output mode, so it has to be watched all the time.
        int below = 0, above = 0, floorPile = 0, ceilPile = 0;
        for (int i = 0; i < 60; i++) below += hist[i];
        for (int i = 945; i < 1024; i++) above += hist[i];
        for (int i = 64; i < 68; i++) floorPile += hist[i];
        for (int i = 936; i < 944; i++) ceilPile += hist[i];
        int trace = Math.Max(1, n / 20000), clear = Math.Max(8, n / 330), pile = Math.Max(4, n / 5000);
        bool strongFull = below >= clear || above >= clear;
        bool cleanEdges = below <= trace && above <= trace;
        bool strongLimited = cleanEdges && (floorPile >= pile || ceilPile >= pile);

        bool wantFull = strongFull || blackFull || (!blackLimited && whiteFull);
        bool wantLimited = !wantFull && (strongLimited || (cleanEdges && (blackLimited || whiteLimited)));
        if (wantFull) { _fullVotes++; _limitedVotes = 0; }
        else if (wantLimited) { _limitedVotes++; _fullVotes = 0; }
        bool oldFull = FullRange, oldHdr = Hdr;
        int need = RangeKnown ? RangeVotesFlip : RangeVotesFirst;
        if (_fullVotes >= need) { FullRange = true; RangeKnown = true; }
        else if (_limitedVotes >= need) { FullRange = false; RangeKnown = true; }

        // --- HDR or SDR ---
        bool sdrProof = high >= Math.Max(4, n / 5000);   // at least ~0.02% of pixels at the very top
        if (sdrProof)
        {
            _quiet = 0;
            if (++_sdrProof >= SdrProofVotes) { Hdr = false; HdrKnown = true; }
        }
        else
        {
            _sdrProof = 0;
            if (!Hdr && ++_quiet >= HdrQuietLooks) { Hdr = true; HdrKnown = true; }
            else if (Hdr && ++_quiet >= HdrQuietLooks) HdrKnown = true;
        }

        if (oldFull != FullRange || oldHdr != Hdr) Changed?.Invoke(Hdr, FullRange);
    }
}
