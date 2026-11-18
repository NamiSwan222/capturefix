namespace CaptureFix;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
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
        Application.Run(new MainForm(justUpdated: args.Contains("--updated")));
    }
}
