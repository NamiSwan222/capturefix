using CaptureFix.Video;

namespace CaptureFix;

/// <summary>"CaptureFix.exe --dump out.raw [mode-key]": saves one untouched frame from the card (for colour analysis).</summary>
static class Dump
{
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
