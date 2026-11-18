using CaptureFix.Video;

namespace CaptureFix;

/// <summary>"CaptureFix.exe --dump out.raw [mode-key]": saves one untouched frame from the card (for colour analysis).</summary>
static class Dump
{
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
