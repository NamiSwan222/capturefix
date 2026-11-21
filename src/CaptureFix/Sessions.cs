using NAudio.CoreAudioApi;
using System.Diagnostics;
using System.Text;

namespace CaptureFix;

/// <summary>"--sessions out.txt": lists every app playing sound on each output, with volume, mute and level.</summary>
static class Sessions
{
    public static void Run(string[] args)
    {
        var sb = new StringBuilder();
        using var en = new MMDeviceEnumerator();
        if (args.Length > 2 && args[1] == "mute")
        {
            // Testing: mute one process's sessions, the way the volume mixer does.
            uint target = uint.Parse(args[2]);
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var ss = d.AudioSessionManager.Sessions;
                for (int i = 0; i < ss.Count; i++) if (ss[i].GetProcessID == target) { ss[i].SimpleAudioVolume.Mute = true; sb.AppendLine($"muted pid {target} on {d.FriendlyName}"); }
            }
            File.WriteAllText(args[0], sb.ToString());
            return;
        }
        var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var com = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        sb.AppendLine($"Default output: {def.FriendlyName} | communications: {com.FriendlyName}");
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            sb.AppendLine($"== {d.FriendlyName}  device volume {d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}{(d.AudioEndpointVolume.Mute ? " MUTED" : "")}  level {d.AudioMeterInformation.MasterPeakValue:0.000}");
            var s = d.AudioSessionManager.Sessions;
            for (int i = 0; i < s.Count; i++)
            {
                var x = s[i];
                string name;
                try { name = x.GetProcessID == 0 ? "system sounds" : Process.GetProcessById((int)x.GetProcessID).ProcessName; } catch { name = "pid " + x.GetProcessID; }
                if (x.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                float peak = 0;
                for (int k = 0; k < 10; k++) { peak = Math.Max(peak, x.AudioMeterInformation.MasterPeakValue); Thread.Sleep(50); }
                sb.AppendLine($"   {name,-24} {x.State.ToString().Replace("AudioSessionState", ""),-9} app volume {x.SimpleAudioVolume.Volume:P0}{(x.SimpleAudioVolume.Mute ? " MUTED" : "")}  level {peak:0.000}");
            }
        }
        File.WriteAllText(args[0], sb.ToString());
    }
}
