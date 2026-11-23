using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CaptureFix;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--monitors") { Dump.Monitors(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--scaletest") { Dump.ScaleTest(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--fgseq") { Dump.FrameGenSequence(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--fgplay") { Dump.FrameGenPlay(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--fgtest") { Dump.FrameGenTest(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--cadence") { Dump.Cadence(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--sessions") { Sessions.Run(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--rendertest") { Dump.RenderTest(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--analyze") { Dump.Analyze(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--gaps") { GapProbe.Run(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--hold") { Dump.Hold(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--dump") { Dump.Run(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--audiotest") { Probe.AudioTest(args.Skip(1).ToArray()); return; }
        if (args.Length > 0 && args[0] == "--probe")
        {
            Probe.Run(args.Skip(1).ToArray());
            return;
        }
        ApplicationConfiguration.Initialize();
        if (args.Length > 0 && args[0] == "--snapshot")
        {
            int delay = args.Length > 2 && int.TryParse(args[2], out var ms) ? ms : 4000;
            Application.Run(new MainForm(args.Length > 1 ? args[1] : "snapshot.png", delay));
            return;
        }
        // One window at a time: two copies would fight over the capture card.
        bool updated = args.Contains("--updated");
        using var mutex = new Mutex(false, @"Local\CaptureFix.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(updated ? 15000 : 0); }   // after an update the old copy may still be closing
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { ShowExisting(); return; }
        try { Application.Run(new MainForm(justUpdated: updated)); }
        finally { mutex.ReleaseMutex(); }
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

    static void ShowExisting()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
            SetForegroundWindow(p.MainWindowHandle);
            return;
        }
    }
}
