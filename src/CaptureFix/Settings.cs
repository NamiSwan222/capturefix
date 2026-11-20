using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaptureFix;

/// <summary>Everything that differs between consoles. One click switches all of it.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public string? ModeKey { get; set; }          // e.g. "P010:1920x1080@60"; null = pick automatically
    public int HdrMode { get; set; }               // 0 auto-detect, 1 HDR10, 2 SDR
    public int RangeMode { get; set; }             // 0 auto-detect, 1 limited, 2 full
    public bool LastHdr { get; set; } = true;      // what auto-detect saw last time (the starting guess)
    public bool LastFullRange { get; set; } = true;
    public float PeakNits { get; set; } = 1000;
    public float SdrPeakNits { get; set; } = 200;
    public float Saturation { get; set; } = 1f;
    public int ToneMap { get; set; }               // 0 Natural, 1 Vivid
    public float Contrast { get; set; } = 1f;
    public float BlackLevel { get; set; }
    public float Gamma { get; set; } = 2.2f;
    public float Warmth { get; set; }
    public float Sharpness { get; set; }
    public int AudioDelayMs { get; set; } = 50;
    public float Volume { get; set; } = 1f;
}

public sealed class AppSettings
{
    public List<Profile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = "PS5";
    public string? DeviceLink { get; set; }
    public string? AudioInputId { get; set; }
    public string? AudioOutputId { get; set; }
    public bool AllowHdrOutput { get; set; }
    public float HdrSdrWhite { get; set; } = 240;
    public bool ShowStats { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool Fullscreen { get; set; }
    public bool Muted { get; set; }
    public bool HideCursor { get; set; } = true;
    public bool StartFullscreen { get; set; }
    public bool ShowTips { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public string? ScreenshotDir { get; set; }

    [JsonIgnore]
    public string ScreenshotFolder => string.IsNullOrWhiteSpace(ScreenshotDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CaptureFix")
        : ScreenshotDir!;
    public int[]? Bounds { get; set; }

    [JsonIgnore]
    public Profile Active => Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles[0];

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureFix");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s;
        try { s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        if (s.Profiles.Count == 0)
        {
            // PS5 with HDR on sends HDR10 all the time, menus included.
            s.Profiles.Add(new Profile { Name = "PS5", PeakNits = 1000 });
            // Switch 2 in SDR by default; press H if HDR is on in its TV settings.
            s.Profiles.Add(new Profile { Name = "Switch 2", LastHdr = false, LastFullRange = false });
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
