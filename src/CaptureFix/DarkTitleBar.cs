using System.Runtime.InteropServices;

namespace CaptureFix;

/// <summary>Dark window title bar on Windows 10 2004+ / 11, to match the dark video window.</summary>
static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Form f)
    {
        f.HandleCreated += (_, _) =>
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
        };
    }
}
