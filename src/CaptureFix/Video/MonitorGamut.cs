using System.Runtime.InteropServices;

namespace CaptureFix.Video;

/// <summary>
/// Wide-colour monitors (QD-OLED, many gaming panels) have deeper red, green and blue than the
/// standard a console picture is made for. Windows normally sends the picture's numbers straight
/// to the panel, so everything comes out oversaturated: skin goes red, bronze looks red, neon
/// glows too hard. This works out the matrix that converts standard (BT.709 / sRGB) colours to
/// the numbers the panel needs to show those same colours.
/// </summary>
public static class MonitorGamut
{
    // BT.709 / sRGB primaries.
    const double Rx = 0.640, Ry = 0.330, Gx = 0.300, Gy = 0.600, Bx = 0.150, By = 0.060;

    /// <summary>How much bigger the monitor's colour triangle is than the standard one (1.0 = same).</summary>
    public static double AreaRatio(double rx, double ry, double gx, double gy, double bx, double by)
    {
        static double Area(double ax, double ay, double bx2, double by2, double cx, double cy) => Math.Abs(ax * (by2 - cy) + bx2 * (cy - ay) + cx * (ay - by2)) / 2;
        return Area(rx, ry, gx, gy, bx, by) / Area(Rx, Ry, Gx, Gy, Bx, By);
    }

    /// <summary>Linear RGB to XYZ for a set of primaries and a white point.</summary>
    static double[,] RgbToXyz(double rx, double ry, double gx, double gy, double bx, double by, double wx, double wy)
    {
        double[,] p =
        {
            { rx / ry, gx / gy, bx / by },
            { 1, 1, 1 },
            { (1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by },
        };
        var inv = Invert(p);
        double[] w = { wx / wy, 1, (1 - wx - wy) / wy };
        var s = new double[3];
        for (int i = 0; i < 3; i++) s[i] = inv[i, 0] * w[0] + inv[i, 1] * w[1] + inv[i, 2] * w[2];
        var m = new double[3, 3];
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) m[r, c] = p[r, c] * s[c];
        return m;
    }

    static double[,] Invert(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new[,]
        {
            { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
            { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
            { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
        };
    }

    /// <summary>
    /// Row-major 3x3: linear BT.709 RGB to the monitor's linear RGB. Both sides use the monitor's
    /// own white point, so white and greys are left exactly as they are; only colourfulness changes.
    /// </summary>
    public static float[] Matrix709ToMonitor(double rx, double ry, double gx, double gy, double bx, double by, double wx, double wy)
    {
        var src = RgbToXyz(Rx, Ry, Gx, Gy, Bx, By, wx, wy);
        var dstInv = Invert(RgbToXyz(rx, ry, gx, gy, bx, by, wx, wy));
        var m = new float[9];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                m[r * 3 + c] = (float)(dstInv[r, 0] * src[0, c] + dstInv[r, 1] * src[1, c] + dstInv[r, 2] * src[2, c]);
        return m;
    }

    public static bool PlausiblePrimaries(double rx, double ry, double gx, double gy, double bx, double by, double wx, double wy)
        => rx is > 0.55 and < 0.75 && ry is > 0.25 and < 0.40 && gx is > 0.15 and < 0.40 && gy is > 0.50 and < 0.85
           && bx is > 0.10 and < 0.20 && by is > 0.02 and < 0.12 && wx is > 0.28 and < 0.35 && wy is > 0.29 and < 0.37;

    // ---------- is Windows already managing colour on this monitor? ----------

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo   // DISPLAYCONFIG_PATH_INFO, 72 bytes
    {
        public Luid SourceAdapter; public uint SourceId, SourceModeIdx, SourceFlags;
        public Luid TargetAdapter; public uint TargetId, TargetModeIdx, OutputTechnology, Rotation, Scaling, RefreshNum, RefreshDen, ScanLine, TargetAvailable, TargetFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)] struct ModeInfo { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName   // DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AdvancedColorInfo   // DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public uint Type, Size; public Luid Adapter; public uint Id;
        public uint Value, ColorEncoding, BitsPerChannel;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo info);

    /// <summary>
    /// True when Windows runs this monitor in "advanced colour" (HDR, or Auto Color Management):
    /// then Windows converts colours itself and the app must not do it a second time.
    /// </summary>
    public static bool WindowsManagesColor(string gdiDeviceName)
    {
        try
        {
            const uint OnlyActivePaths = 2;
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint np, out uint nm) != 0) return false;
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(OnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < np; i++)
            {
                var name = new SourceName { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].SourceAdapter, Id = paths[i].SourceId };
                if (DisplayConfigGetDeviceInfo(ref name) != 0 || !string.Equals(name.Device, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var ac = new AdvancedColorInfo { Type = 9, Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(), Adapter = paths[i].TargetAdapter, Id = paths[i].TargetId };
                if (DisplayConfigGetDeviceInfo(ref ac) != 0) return false;
                return (ac.Value & 2) != 0;   // advancedColorEnabled
            }
        }
        catch { }
        return false;
    }
}
