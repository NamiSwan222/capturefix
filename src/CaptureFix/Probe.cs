using System.Diagnostics;
using System.Text;
using CaptureFix.Audio;
using CaptureFix.Video;

namespace CaptureFix;

/// <summary>
/// "CaptureFix.exe --probe [out.txt] [seconds]": checks the capture card without the UI. Lists
/// devices and modes, captures from the main modes, and reports frame rate and pixel statistics
/// (which show whether the console is sending HDR). Written to a text file, since this is a GUI exe.
/// </summary>
static class Probe
{
    public static void Run(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "probe.txt";
        double seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 3;
        var sb = new StringBuilder();
        void W(string line) { sb.AppendLine(line); File.WriteAllText(outPath, sb.ToString()); }

        try
        {
            W("Video devices:");
            foreach (var d in VideoCapture.ListDevices()) W($"  {d.Name}  [{d.SymbolicLink}]");
            var dev = VideoCapture.FindCaptureCard(null);
            if (dev == null) { W("No capture device."); return; }
            W($"Using: {dev.Name}");
            var modes = VideoCapture.ListModes(dev);
            W("Modes:");
            foreach (var m in modes) W($"  {m.Key,-26} {m}");

            W("Audio inputs:");
            foreach (var a in AudioBridge.ListInputs()) W($"  {a.Name}");

            var tests = modes.Where(m =>
                (m.Format == PixelFormat.P010 && m.Width == 1920) ||
                (m.Format == PixelFormat.NV12 && m.Width == 1920 && Math.Round(m.Fps) is 60 or 120) ||
                (m.Format == PixelFormat.NV12 && m.Width == 2560)).ToList();
            foreach (var m in tests) TestMode(dev, m, seconds, W);

            W("Audio test (2 s, default output, muted):");
            using var audio = new AudioBridge { Volume = 0 };
            audio.Start(dev.Name, null, null);
            Thread.Sleep(2000);
            W(audio.Error != null ? "  error: " + audio.Error : $"  {audio.InputName} -> {audio.OutputName}, queue {audio.BufferedMs:0} ms, resyncs {audio.Drops}");
        }
        catch (Exception ex) { W("FAILED: " + VideoCapture.Describe(ex) + "\n" + ex); }
    }

    /// <summary>"--audiotest out.wav seconds [outputId|-]": plays the game audio silently, records what would be played, logs gaps.</summary>
    public static void AudioTest(string[] args)
    {
        string wav = args.Length > 0 ? args[0] : "audiotest.wav";
        int secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;
        var log = new StringBuilder();
        int capMs = args.Length > 3 && int.TryParse(args[3], out var cm) ? cm : 20;
        using var a = new AudioBridge { TapPath = wav, Volume = 0, DelayMs = 50, CaptureBufferMs = capMs };
        a.Start("Elgato HD60 X", null, args.Length > 2 && args[2] != "-" ? args[2] : null);
        log.AppendLine($"{a.InputName} -> {a.OutputName}  {a.FormatInfo}  {a.Error}");
        for (int i = 0; i < secs; i++)
        {
            Thread.Sleep(1000);
            log.AppendLine($"t={i + 1}s buffer {a.BufferedMs:0.0} ms speed {a.SpeedPercent:+0.000;-0.000}% gaps {a.Underruns} cushion +{a.ExtraMs} ms skips {a.Drops} in {a.TotalIn} out {a.TotalOut} loss {a.LossPercent:0.0}%");
        }
        a.Stop();
        File.WriteAllText(Path.ChangeExtension(wav, ".txt"), log.ToString());
    }

    static void TestMode(CaptureDeviceInfo dev, VideoMode m, double seconds, Action<string> W)
    {
        W($"Capture {m.Key}:");
        try
        {
            using var cap = VideoCapture.Start(dev, m);
            var sw = Stopwatch.StartNew();
            int frames = 0;
            VideoFrame? last = null;
            long firstFrameMs = -1;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                if (!cap.FrameReady.WaitOne(500)) continue;
                var f = cap.TakeLatest();
                if (f == null) continue;
                if (firstFrameMs < 0) firstFrameMs = sw.ElapsedMilliseconds;
                frames++;
                if (last != null) cap.Return(last);
                last = f;
            }
            double fps = frames / Math.Max(0.001, seconds - Math.Max(0, firstFrameMs) / 1000.0);
            W($"  frames {cap.FramesCaptured} (rendered-path {frames}), ~{fps:0.0} fps, first frame after {firstFrameMs} ms{(cap.Error != null ? ", error: " + cap.Error : "")}");
            if (last != null) W("  " + Stats(last));
        }
        catch (Exception ex) { W("  FAILED: " + VideoCapture.Describe(ex)); }
    }

    /// <summary>Luma statistics as code values. PQ HDR menus sit far lower than SDR white.</summary>
    static string Stats(VideoFrame f)
    {
        int w = f.Width, h = f.Height;
        long sum = 0; int n = 0, min = int.MaxValue, max = 0;
        var hist = new int[1024];
        for (int y = 0; y < h; y += 4)
        for (int x = 0; x < w; x += 4)
        {
            int v;
            if (f.Format == PixelFormat.P010) v = BitConverter.ToUInt16(f.Data, y * f.Pitch + x * 2) >> 6;
            else if (f.Format == PixelFormat.NV12) v = f.Data[y * f.Pitch + x] << 2;
            else v = f.Data[y * f.Pitch + x * 2] << 2;
            sum += v; n++; min = Math.Min(min, v); max = Math.Max(max, v);
            hist[v]++;
        }
        int acc = 0, p99 = 0;
        for (int i = 0; i < 1024; i++) { acc += hist[i]; if (acc >= n * 0.99) { p99 = i; break; } }
        return $"pitch {f.Pitch}, bytes {f.Length}, luma (10-bit scale) min {min} avg {sum / Math.Max(n, 1)} p99 {p99} max {max}";
    }
}
