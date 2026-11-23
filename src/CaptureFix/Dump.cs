using CaptureFix.Video;

namespace CaptureFix;

/// <summary>"CaptureFix.exe --dump out.raw [mode-key]": saves one untouched frame from the card (for colour analysis).</summary>
static class Dump
{
    /// <summary>"--scaletest out.txt file.raw FORMAT W H PITCH outW outH pngPrefix": scales a frame with both scalers.</summary>
    public static void ScaleTest(string[] a)
    {
        var f = new VideoFrame { Data = File.ReadAllBytes(a[1]), Format = Enum.Parse<PixelFormat>(a[2]), Width = int.Parse(a[3]), Height = int.Parse(a[4]), Pitch = int.Parse(a[5]) };
        f.Length = f.Data.Length;
        ApplicationConfiguration.Initialize();
        using var form = new Form { ClientSize = new Size(960, 540), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        _ = form.Handle;
        using var r = new Renderer(form.Handle);
        r.Resize(960, 540);
        r.Color = new ColorSettings { HdrMode = 2, RangeMode = 1 };
        string result;
        try { result = r.ScaleTest(f, int.Parse(a[6]), int.Parse(a[7]), a[8]); } catch (Exception ex) { result = "FAILED: " + ex; }
        File.WriteAllText(a[0], result);
    }

    /// <summary>"--fgseq out.txt dir [finestLevel] [pngWorst]": scores frame generation on consecutive real game frames
    /// saved by --cadence: for every k, makes the picture between g(k) and g(k+2) and compares it with g(k+1).</summary>
    public static void FrameGenSequence(string[] a)
    {
        var index = File.ReadAllLines(Path.Combine(a[1], "index.txt")).Where(l => l.Length > 0).Select(l => l.Split(' ')).ToList();
        VideoFrame Load(string[] e)
        {
            var f = new VideoFrame { Data = File.ReadAllBytes(Path.Combine(a[1], e[0])), Format = Enum.Parse<PixelFormat>(e[1]), Width = int.Parse(e[2]), Height = int.Parse(e[3]), Pitch = int.Parse(e[4]) };
            f.Length = f.Data.Length; return f;
        }
        int level = a.Length > 2 ? int.Parse(a[2]) : 0;
        ApplicationConfiguration.Initialize();
        using var form = new Form { ClientSize = new Size(960, 540), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        _ = form.Handle;
        using var r = new Renderer(form.Handle);
        r.Resize(960, 540);
        r.Color = new ColorSettings { HdrMode = 2, RangeMode = 1 };
        var sb = new System.Text.StringBuilder();
        var frames = index.Select(Load).ToList();
        var rows = new List<(int k, double gen, double blend, double rep)>();
        for (int k = 0; k + 2 < frames.Count; k++)
        {
            // Only where the three pictures really are consecutive game frames (2 capture frames apart).
            if (long.Parse(index[k + 1][5]) - long.Parse(index[k][5]) != 2 || long.Parse(index[k + 2][5]) - long.Parse(index[k + 1][5]) != 2) continue;
            var (gen, blend, rep, _) = r.FrameGenScore(frames[k], frames[k + 1], frames[k + 2], "", level, 0.012f, 0.05f, false);
            rows.Add((k, gen, blend, rep));
        }
        sb.AppendLine($"{rows.Count} triplets | generated {rows.Average(x => x.gen):0.00} dB (min {rows.Min(x => x.gen):0.00}) | plain blend {rows.Average(x => x.blend):0.00} dB | repeat {rows.Average(x => x.rep):0.00} dB | generated beats blend in {rows.Count(x => x.gen > x.blend)} of {rows.Count}");
        foreach (var x in rows) sb.AppendLine($"  k={x.k,3}  generated {x.gen:0.00}  blend {x.blend:0.00}  repeat {x.rep:0.00}");
        if (a.Length > 3 && rows.Count > 0)
        {
            var worst = rows.OrderBy(x => x.gen - x.blend).First();
            r.FrameGenScore(frames[worst.k], frames[worst.k + 1], frames[worst.k + 2], a[3], level, 0.012f, 0.05f, false);
            sb.AppendLine($"saved pictures for k={worst.k}");
        }
        File.WriteAllText(a[0], sb.ToString());
    }

    /// <summary>"--fgplay out.txt framesDir prefix count seconds factor repeat [snapshot.png]": plays saved NV12 frames
    /// through the live renderer the way a console sends them and reports how evenly pictures were shown.</summary>
    public static void FrameGenPlay(string[] a)
    {
        var frames = Enumerable.Range(0, int.Parse(a[3])).Select(i => File.ReadAllBytes(Path.Combine(a[1], $"{a[2]}{i}.raw"))).ToList();
        double seconds = double.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture);
        int factor = int.Parse(a[5]), repeat = int.Parse(a[6]);
        ApplicationConfiguration.Initialize();
        int pw = int.TryParse(Environment.GetEnvironmentVariable("CF_PLAYW"), out var ew) ? ew : 1280, ph = pw * 9 / 16;
        bool sharp = Environment.GetEnvironmentVariable("CF_SHARP") == "1";
        using var form = new Form { ClientSize = new Size(pw, ph), FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(100, 100), Text = "CaptureFix frame generation test" };
        var sb = new System.Text.StringBuilder();
        form.Shown += async (_, _) =>
        {
            using var r = new Renderer(form.Handle);
            r.Resize(form.ClientSize.Width, form.ClientSize.Height);
            r.Color = new ColorSettings { FrameGen = factor, HdrMode = 2, RangeMode = 1, SharpUpscale = sharp };
            using var cap = VideoCapture.StartSynthetic(new VideoMode(PixelFormat.NV12, 1920, 1080, 60, 1), frames, repeat);
            r.Source = cap;
            r.Start();
            await Task.Delay(1500);
            r.PresentLog = new();
            if (a.Length > 7) { await Task.Delay(700); r.RequestSnapshot(a[7]); }
            await Task.Delay((int)(seconds * 1000));
            var log = r.PresentLog.ToArray(); r.PresentLog = null;
            double f = System.Diagnostics.Stopwatch.Frequency;
            var gaps = log.Zip(log.Skip(1), (x, y) => (y.ticks - x.ticks) * 1000.0 / f).DefaultIfEmpty(0).ToArray();
            double mean = gaps.Average(), sd = Math.Sqrt(gaps.Select(g => (g - mean) * (g - mean)).Average());
            sb.AppendLine($"monitor {r.RefreshHz:0} Hz | game {r.GameFps:0.0} fps -> shown {r.PresentFps:0.0} fps | factor {r.FrameGenFactor}x | added delay {r.FrameGenDelayMs:0.0} ms | arrival-to-screen of real frames {r.LatencyMs:0.0} ms");
            sb.AppendLine($"pictures {log.Length}: generated {log.Count(x => x.alpha is > 0 and < 1)}, real {log.Count(x => x.alpha >= 1)}, direct {log.Count(x => x.alpha < 0)}");
            sb.AppendLine($"time between pictures: mean {mean:0.00} ms, std {sd:0.00} ms, min {gaps.Min():0.00}, max {gaps.Max():0.00}; over 1.5x mean: {gaps.Count(g => g > mean * 1.5)}");
            sb.AppendLine("first pictures (alpha): " + string.Join(" ", log.Take(16).Select(x => x.alpha.ToString("0.##"))));
            sb.AppendLine($"shown at {r.ShownWidth}x{r.ShownHeight}, sharp scaling {(r.SharpScalingActive ? "on" : "off")}");
            if (r.Error != null) sb.AppendLine("renderer error: " + r.Error);
            r.Source = null;
            File.WriteAllText(a[0], sb.ToString());
            form.Close();
        };
        Application.Run(form);
    }

    /// <summary>"--fgtest out.txt A.raw B.raw C.raw FORMAT W H PITCH [pngPrefix] [finestLevel] [smooth] [staticBias]":
    /// generates the frame between A and C on the GPU and compares it with the real middle frame B.</summary>
    public static void FrameGenTest(string[] a)
    {
        VideoFrame Load(string path)
        {
            var f = new VideoFrame { Data = File.ReadAllBytes(path), Format = Enum.Parse<PixelFormat>(a[4]), Width = int.Parse(a[5]), Height = int.Parse(a[6]), Pitch = int.Parse(a[7]) };
            f.Length = f.Data.Length; return f;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        ApplicationConfiguration.Initialize();
        using var form = new Form { ClientSize = new Size(960, 540), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        _ = form.Handle;
        using var r = new Renderer(form.Handle);
        r.Resize(960, 540);
        r.Color = new ColorSettings();
        string result;
        try
        {
            result = r.FrameGenTest(Load(a[1]), Load(a[2]), Load(a[3]), a.Length > 8 ? a[8] : "",
                a.Length > 9 ? int.Parse(a[9]) : 1, a.Length > 10 ? float.Parse(a[10], inv) : 0.012f, a.Length > 11 ? float.Parse(a[11], inv) : 0.05f);
        }
        catch (Exception ex) { result = "FAILED: " + ex; }
        File.WriteAllText(a[0], result);
    }

    /// <summary>"--cadence out.txt seconds [mode-key] [saveDir saveCount]": logs every frame's arrival time and how much it
    /// changed, to see the game's real frame rate. Optionally saves the first frames that follow a change.</summary>
    public static void Cadence(string[] a)
    {
        var dev = VideoCapture.FindCaptureCard(null) ?? throw new InvalidOperationException("no device");
        var modes = VideoCapture.ListModes(dev);
        var mode = modes.FirstOrDefault(m => a.Length > 2 && m.Key == a[2]) ?? modes.First(m => m.Format == PixelFormat.P010 && m.Width == 1920 && Math.Round(m.Fps) == 60);
        string? saveDir = a.Length > 3 ? a[3] : null; int saveCount = a.Length > 4 ? int.Parse(a[4]) : 0, saved = 0;
        if (saveDir != null) Directory.CreateDirectory(saveDir);
        var sb = new System.Text.StringBuilder();
        var keep = new List<(byte[] data, string info)>();
        using var cap = VideoCapture.Start(dev, mode);
        long end = Environment.TickCount64 + (long)(double.Parse(a[1]) * 1000), last = 0, lastIndex = 0; int n = 0, changes = 0, missed = 0;
        while (Environment.TickCount64 < end)
        {
            if (!cap.FrameReady.WaitOne(500)) continue;
            var f = cap.TakeLatest(); if (f == null) continue;
            double dt = last == 0 ? 0 : (f.ArrivedTicks - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; last = f.ArrivedTicks;
            if (lastIndex != 0 && f.Index != lastIndex + 1) missed += (int)(f.Index - lastIndex - 1);
            lastIndex = f.Index;
            bool change = f.ChangeIndex == f.Index; n++; if (change) changes++;
            if (n <= 240) sb.AppendLine($"{f.Index,5} +{dt,5:0.0} ms  {(change ? "NEW" : "rep")}  any {f.DiffAny:P2} big {f.DiffBig:P3} mean {f.DiffMean:0.000}");
            // Keep only new pictures, in memory (writing 6 MB files in this loop would drop frames).
            if (saveDir != null && saved < saveCount && n > 30 && change)
            {
                keep.Add((f.Data.AsSpan(0, f.Length).ToArray(), $"{f.Format} {f.Width} {f.Height} {f.Pitch} {f.Index} {f.DiffMean:0.0}"));
                saved++;
            }
            cap.Return(f);
        }
        sb.Insert(0, $"{mode.Key}: {n} frames, {changes} new pictures, {missed} missed by this loop -> game runs at about {changes / double.Parse(a[1]):0.0} fps\n");
        File.WriteAllText(a[0], sb.ToString());
        for (int k = 0; k < keep.Count; k++)
        {
            File.WriteAllBytes(Path.Combine(saveDir!, $"g{k:000}.raw"), keep[k].data);
            File.AppendAllText(Path.Combine(saveDir!, "index.txt"), $"g{k:000}.raw {keep[k].info}" + Environment.NewLine);
        }
    }

    /// <summary>"--rendertest out.png file.raw FORMAT W H PITCH": runs a saved frame through the real GPU renderer in an invisible window.</summary>
    public static void RenderTest(string[] a)
    {
        var f = new VideoFrame
        {
            Data = File.ReadAllBytes(a[1]), Format = Enum.Parse<PixelFormat>(a[2]), Width = int.Parse(a[3]), Height = int.Parse(a[4]), Pitch = int.Parse(a[5]),
        };
        f.Length = f.Data.Length;
        ApplicationConfiguration.Initialize();
        using var form = new Form { ClientSize = new Size(960, 540), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
        _ = form.Handle;   // create the window without showing it
        using var r = new Renderer(form.Handle);
        r.Resize(960, 540);
        r.Color = new ColorSettings();
        r.RenderOnce(f, a[0]);
    }

    /// <summary>"--analyze out.txt file.raw FORMAT W H PITCH hdrGuess fullGuess": runs the signal detector on a saved frame.</summary>
    public static void Analyze(string[] a)
    {
        var fmt = Enum.Parse<PixelFormat>(a[2]);
        var f = new VideoFrame
        {
            Data = File.ReadAllBytes(a[1]), Format = fmt, Width = int.Parse(a[3]), Height = int.Parse(a[4]), Pitch = int.Parse(a[5]),
        };
        f.Length = f.Data.Length;
        var an = new SignalAnalyzer();
        an.Reset(bool.Parse(a[6]), bool.Parse(a[7]));
        for (int i = 0; i < 150; i++) an.Analyze(f, force: true);
        File.WriteAllText(a[0], $"{(an.Hdr ? "HDR" : "SDR")} {(an.FullRange ? "full" : "limited")} (black {an.BlackCode}, white {an.WhiteCode})");
    }

    /// <summary>"--hold mode-key seconds": streams video (discarding frames) to test load on the card.</summary>
    public static void Hold(string[] args)
    {
        var dev = VideoCapture.FindCaptureCard(null)!;
        var mode = VideoCapture.ListModes(dev).First(m => m.Key == args[0]);
        using var cap = VideoCapture.Start(dev, mode);
        var end = Environment.TickCount64 + int.Parse(args[1]) * 1000L;
        while (Environment.TickCount64 < end) { if (cap.FrameReady.WaitOne(200)) { var f = cap.TakeLatest(); if (f != null) cap.Return(f); } }
    }

    public static void Run(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "frame.raw";
        var dev = VideoCapture.FindCaptureCard(null) ?? throw new InvalidOperationException("no device");
        var modes = VideoCapture.ListModes(dev);
        var mode = modes.FirstOrDefault(m => args.Length > 1 && m.Key == args[1])
                   ?? modes.First(m => m.Format == PixelFormat.P010 && m.Width == 1920 && Math.Round(m.Fps) == 60);
        using var cap = VideoCapture.Start(dev, mode);
        VideoFrame? f = null;
        int n = 0;
        while (n < 30 && cap.FrameReady.WaitOne(3000))
        {
            var g = cap.TakeLatest();
            if (g == null) continue;
            if (f != null) cap.Return(f);
            f = g; n++;
        }
        if (f == null) throw new InvalidOperationException("no frames");
        File.WriteAllBytes(path, f.Data.AsSpan(0, f.Length).ToArray());
        File.WriteAllText(path + ".txt", $"{f.Format} {f.Width} {f.Height} {f.Pitch}");
    }
}
