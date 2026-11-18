using NAudio.CoreAudioApi;
using System.Text;

namespace CaptureFix;

/// <summary>"--gaps out.txt seconds": raw WASAPI capture loop logging device positions and glitch flags.</summary>
static class GapProbe
{
    public static void Run(string[] args)
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();
        var dev = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).First(d => d.FriendlyName.Contains("HD60"));
        var client = dev.AudioClient;
        var fmt = client.MixFormat;
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None, 200 * 10000L, 0, fmt, Guid.Empty);
        var cap = client.AudioCaptureClient;
        client.Start();
        long expected = -1, totalFrames = 0, gaps = 0, gapFrames = 0, flagged = 0, packets = 0;
        var end = Environment.TickCount64 + int.Parse(args[1]) * 1000L;
        while (Environment.TickCount64 < end)
        {
            Thread.Sleep(5);
            int next;
            while ((next = cap.GetNextPacketSize()) > 0)
            {
                IntPtr p = cap.GetBuffer(out int frames, out AudioClientBufferFlags flags, out long devPos, out long qpc);
                packets++;
                if (flags != AudioClientBufferFlags.None) { flagged++; if (flagged < 20) sb.AppendLine($"flags {flags} at {devPos}"); }
                if (expected >= 0 && devPos != expected)
                {
                    gaps++; gapFrames += devPos - expected;
                    if (gaps < 40) sb.AppendLine($"position jump {devPos - expected} frames at {devPos} (packet {frames} frames)");
                }
                expected = devPos + frames;
                totalFrames += frames;
                cap.ReleaseBuffer(frames);
            }
        }
        client.Stop();
        sb.AppendLine($"packets {packets}, frames {totalFrames}, position jumps {gaps} totalling {gapFrames} frames, flagged {flagged}");
        File.WriteAllText(args[0], sb.ToString());
    }
}
