using System.Diagnostics;
using CaptureFix.Audio;
using CaptureFix.Video;

namespace CaptureFix;

/// <summary>
/// All settings in one window: pages on the left, the console profile on top. Every change applies
/// live and is saved straight away; picture and sound settings belong to the selected console.
/// </summary>
public sealed class SettingsForm : Form
{
    static readonly Color Bg = Color.FromArgb(24, 24, 28);
    static readonly Color PanelBg = Color.FromArgb(32, 32, 38);
    static readonly Color Field = Color.FromArgb(44, 44, 52);
    static readonly Color Text1 = Color.FromArgb(236, 236, 240);
    static readonly Color Text2 = Color.FromArgb(150, 150, 162);
    static readonly Color Accent = Color.FromArgb(94, 164, 255);

    readonly MainForm _main;
    readonly AppSettings _s;
    readonly List<Action> _loaders = new();
    readonly Dictionary<string, Control> _pages = new();
    readonly List<Button> _navButtons = new();
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(24, 16, 24, 16) };
    readonly ComboBox _profileBox = MakeCombo(220);
    bool _loading;

    Profile P => _s.Active;

    public SettingsForm(MainForm main)
    {
        _main = main;
        _s = main.Settings;
        Text = "CaptureFix settings";
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(760, 640);
        MinimumSize = new Size(640, 480);
        var area = Screen.FromControl(main).WorkingArea;
        Location = new Point(Math.Max(area.Left, Math.Min(main.Right - Width - 20, area.Right - Width)), Math.Max(area.Top, main.Top + 40));
        KeyPreview = true;
        DarkTitleBar.Apply(this);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

        // Header: which console these settings are for.
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = PanelBg, Padding = new Padding(20, 0, 20, 0) };
        var consoleLabel = new Label { Text = "Console", AutoSize = true, ForeColor = Text2, Location = new Point(20, 19) };
        _profileBox.Location = new Point(90, 15);
        _profileBox.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _profileBox.SelectedItem is not string name) return;
            _main.SwitchProfile(name);
            LoadValues();
        };
        var hint = new Label { Text = "Picture and sound settings are saved per console.", AutoSize = true, ForeColor = Text2, Location = new Point(325, 19) };
        header.Controls.AddRange(new Control[] { consoleLabel, _profileBox, hint });

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 150, BackColor = PanelBg, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8, 12, 8, 8),
        };

        Controls.Add(_content);
        Controls.Add(nav);
        Controls.Add(header);

        AddPage(nav, "Picture", BuildPicture());
        AddPage(nav, "Sound", BuildSound());
        AddPage(nav, "Video", BuildVideo());
        AddPage(nav, "Consoles", BuildConsoles());
        AddPage(nav, "Window", BuildWindow());
        AddPage(nav, "Keys", BuildKeys());
        AddPage(nav, "About", BuildAbout());
        SelectTab(0);

        _main.SettingsChanged += OnMainChanged;
        FormClosed += (_, _) => _main.SettingsChanged -= OnMainChanged;
    }

    void OnMainChanged() { if (Visible) LoadValues(); }

    // ---------- pages ----------

    FlowLayoutPanel BuildPicture()
    {
        var page = NewPage();
        Section(page, "Smooth motion");
        Choice(page, "Frame generation", new[] { "Off", "2x  (30 fps game shown at 60)", "3x  (30 fps game shown at 90)", "4x  (30 fps game shown at 120)" },
            "Draws in-between pictures so a 30 fps game moves like a 60 fps one. Costs a little delay (about 17 ms at 2x for a 30 fps game) and can smear fast edges. Never goes above your monitor's refresh rate. Hotkey: G.",
            () => P.FrameGen switch { < 2 => 0, 2 => 1, 3 => 2, _ => 3 }, v => _main.SetFrameGen(v == 0 ? 0 : v + 1));
        Choice(page, "Motion quality", new[] { "Fast", "High (recommended with a gaming GPU)" }, null,
            () => Math.Clamp(P.FrameGenQuality, 0, 1), v => { P.FrameGenQuality = v; ApplyPicture(); });
        var fgStatus = new Label { AutoSize = true, ForeColor = Accent, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 4) };
        page.Controls.Add(fgStatus);
        var fgTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        fgTimer.Tick += (_, _) => { if (page.Visible) fgStatus.Text = _main.FrameGenStatus; };
        fgTimer.Start();
        Disposed += (_, _) => fgTimer.Dispose();
        _loaders.Add(() => fgStatus.Text = _main.FrameGenStatus);

        Section(page, "HDR");
        Choice(page, "Signal", new[] { "Detect automatically (recommended)", "Always HDR10", "Always SDR" },
            "CaptureFix reads the picture to tell HDR from SDR. Only force it if detection gets it wrong. Hotkey: H cycles the choices.",
            () => P.HdrMode, v => { P.HdrMode = v; ApplyPicture(); });
        Choice(page, "Brightness range", new[] { "Detect automatically (recommended)", "Limited (16-235)", "Full (0-255)" },
            "Black can sit at 0 (full) or 16 (limited). Reading it wrong crushes shadows or turns blacks grey.",
            () => P.RangeMode, v => { P.RangeMode = v; ApplyPicture(); });
        Choice(page, "HDR color style", new[] { "Natural (like a TV)", "Vivid (stronger colors)" },
            "Natural keeps gold, bronze and skin tones true. Vivid keeps more saturation, but very bright yellows drift toward orange.",
            () => P.ToneMap, v => { P.ToneMap = v; ApplyPicture(); });
        Slider(page, "HDR brightness", 100, 400, () => 500 - (int)P.SdrPeakNits, v => { P.SdrPeakNits = 500 - v; ApplyPicture(); },
            v => $"{v * 100 / 300}%", "How bright HDR looks once converted. Raise it if the picture is dark.");
        Slider(page, "HDR peak (nits)", 400, 4000, () => (int)P.PeakNits, v => { P.PeakNits = v; ApplyPicture(); }, v => $"{v}",
            "Match the max brightness from the console's HDR calibration (PS5: Settings > Screen and Video > Adjust HDR). Lower = punchier highlights.");

        Section(page, "Picture");
        Slider(page, "Contrast", 50, 150, () => (int)Math.Round(P.Contrast * 100), v => { P.Contrast = v / 100f; ApplyPicture(); }, v => $"{v}%", null);
        Slider(page, "Black level", -10, 10, () => (int)Math.Round(P.BlackLevel * 100), v => { P.BlackLevel = v / 100f; ApplyPicture(); },
            v => v > 0 ? $"+{v}" : $"{v}", "Raise to see into dark areas; lower for deeper blacks.");
        Slider(page, "Color", 0, 200, () => (int)Math.Round(P.Saturation * 100), v => { P.Saturation = v / 100f; ApplyPicture(); }, v => $"{v}%", "100% = what the console sends.");
        Slider(page, "Warmth", -100, 100, () => (int)Math.Round(P.Warmth * 100), v => { P.Warmth = v / 100f; ApplyPicture(); },
            v => v == 0 ? "0" : v > 0 ? $"warm {v}" : $"cool {-v}", null);
        Slider(page, "Sharpness", 0, 100, () => (int)Math.Round(P.Sharpness * 100), v => { P.Sharpness = v / 100f; ApplyPicture(); }, v => $"{v}%", "Crisper text and edges. Useful when the window is bigger than the video.");
        Choice(page, "Gamma", new[] { "2.2  (normal)", "2.4  (dark room)", "2.0  (bright room)" }, null,
            () => P.Gamma >= 2.3f ? 1 : P.Gamma <= 2.1f ? 2 : 0, v => { P.Gamma = v == 1 ? 2.4f : v == 2 ? 2.0f : 2.2f; ApplyPicture(); });

        Section(page, "HDR monitor");
        Check(page, "Show real HDR on an HDR monitor", "Needs HDR turned on in Windows display settings. Leave off while sharing on Discord: Discord streams SDR.",
            () => _s.AllowHdrOutput, v => { _s.AllowHdrOutput = v; ApplyPicture(); });
        Slider(page, "SDR white on HDR screen", 80, 480, () => (int)_s.HdrSdrWhite, v => { _s.HdrSdrWhite = v; ApplyPicture(); }, v => $"{v} nits", null);

        Buttons(page, ("Reset picture for this console", () =>
        {
            var d = new Profile();
            P.SdrPeakNits = d.SdrPeakNits; P.PeakNits = d.PeakNits; P.Saturation = d.Saturation; P.ToneMap = d.ToneMap;
            P.Contrast = d.Contrast; P.BlackLevel = d.BlackLevel; P.Gamma = d.Gamma; P.Warmth = d.Warmth; P.Sharpness = d.Sharpness;
            ApplyPicture(); LoadValues();
        }));
        return page;
    }

    FlowLayoutPanel BuildSound()
    {
        var page = NewPage();
        Section(page, "Game audio");
        var outputs = new List<AudioDeviceInfo?>();
        Choice(page, "Play on", Array.Empty<string>(), null, () =>
        {
            outputs.Clear(); outputs.Add(null);
            try { outputs.AddRange(AudioBridge.ListOutputs()); } catch { }
            int i = outputs.FindIndex(o => o?.Id == _s.AudioOutputId);
            return Math.Max(i, 0);
        }, v => { _s.AudioOutputId = outputs.ElementAtOrDefault(v)?.Id; Save(); _main.StartAudio(); },
        fill: () => outputs.Select(o => o == null ? "Windows default" : o.Name).ToArray());
        Slider(page, "Volume", 0, 150, () => (int)Math.Round(P.Volume * 100), v => { P.Volume = v / 100f; Audio(); }, v => $"{v}%", null);
        Check(page, "Mute", "Hotkey: M", () => _s.Muted, v => { _s.Muted = v; Audio(); });
        Slider(page, "Audio delay", AudioBridge.MinDelayMs, 200, () => P.AudioDelayMs, v => { P.AudioDelayMs = v; Audio(); }, v => $"{v} ms",
            "Raise it if you hear things before you see them; lower it if sound comes late.");
        Buttons(page, ("Restart audio", () => _main.StartAudio()));

        Section(page, "Connection");
        var health = new Label { AutoSize = true, ForeColor = Text1, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 4) };
        page.Controls.Add(health);
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => { if (page.Visible) health.Text = _main.AudioHealth; };
        timer.Start();
        Disposed += (_, _) => timer.Dispose();
        _loaders.Add(() => health.Text = _main.AudioHealth);
        Note(page, "Sharing on Discord: Go Live and pick the CaptureFix window. The game sound comes from this window, so friends hear the game without your other PC sounds.");
        return page;
    }

    FlowLayoutPanel BuildVideo()
    {
        var page = NewPage();
        Section(page, "Capture card");
        var devices = new List<CaptureDeviceInfo>();
        Choice(page, "Device", Array.Empty<string>(), null, () =>
        {
            devices.Clear();
            try { devices.AddRange(VideoCapture.ListDevices()); } catch { }
            return Math.Max(0, devices.FindIndex(d => d.SymbolicLink == _main.CurrentDevice?.SymbolicLink));
        }, v => { if (v < devices.Count) _main.SelectDevice(devices[v].SymbolicLink); },
        fill: () => devices.Select(d => d.Name).ToArray());

        var modes = new List<VideoMode>();
        Choice(page, "Video mode", Array.Empty<string>(),
            "1080p 60 HDR 10-bit gives the cleanest HDR. 1080p 120 and 1440p 60 are 8-bit: smoother or sharper, with a little banding in HDR.",
            () =>
            {
                modes.Clear(); modes.AddRange(_main.Modes.Where(m => m.Width >= 1280));
                return Math.Max(0, modes.FindIndex(m => m.Key == _main.CurrentMode?.Key));
            }, v => { if (v < modes.Count && modes[v].Key != _main.CurrentMode?.Key) _main.SetMode(modes[v]); },
            fill: () => modes.Select(m => m.ToString()).ToArray());
        Buttons(page, ("Reconnect", () => _main.Reconnect()));
        Note(page, "Only one app can use the capture card at a time. Close Elgato Studio and OBS's capture source while CaptureFix runs.\n\nBlack screen from a PS5: Settings > System > HDMI > turn off Enable HDCP.");
        return page;
    }

    FlowLayoutPanel BuildConsoles()
    {
        var page = NewPage();
        Section(page, "Console profiles");
        Note(page, "Each console remembers its video mode, HDR, picture and sound. Switch with 1, 2, 3… or the box at the top.");
        var list = new ListBox { Width = 300, Height = 150, BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.None, Margin = new Padding(0, 6, 0, 8) };
        list.SelectedIndexChanged += (_, _) => { if (!_loading && list.SelectedItem is string n) { _main.SwitchProfile(n); LoadValues(); } };
        page.Controls.Add(list);
        _loaders.Add(() => { list.Items.Clear(); foreach (var p in _s.Profiles) list.Items.Add(p.Name); list.SelectedItem = P.Name; });

        var name = new TextBox { Width = 300, BackColor = Field, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Name (e.g. Xbox, PC, Switch 2 HDR)" };
        page.Controls.Add(name);
        Buttons(page,
            ("Add (copy of current)", () =>
            {
                string n = name.Text.Trim();
                if (n.Length == 0 || _s.Profiles.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Flash(name); return; }
                _main.AddProfile(n); name.Clear(); LoadValues();
            }),
            ("Rename", () =>
            {
                string n = name.Text.Trim();
                if (n.Length == 0 || _s.Profiles.Any(p => p != P && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) { Flash(name); return; }
                _main.RenameProfile(n); name.Clear(); LoadValues();
            }),
            ("Delete", () =>
            {
                if (_s.Profiles.Count <= 1) return;
                if (MessageBox.Show(this, $"Delete the \"{P.Name}\" profile?", "CaptureFix", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                _main.DeleteProfile(); LoadValues();
            }));
        return page;
    }

    FlowLayoutPanel BuildWindow()
    {
        var page = NewPage();
        Section(page, "Window");
        Check(page, "Always on top", null, () => _s.AlwaysOnTop, v => _main.SetAlwaysOnTop(v));
        Check(page, "Start in full screen", null, () => _s.StartFullscreen, v => { _s.StartFullscreen = v; Save(); });
        Check(page, "Hide the mouse pointer in full screen", "It comes back when you move the mouse.", () => _s.HideCursor, v => { _s.HideCursor = v; Save(); });
        Check(page, "Show stats (fps, delay, signal)", "Hotkey: F1", () => _s.ShowStats, v => { _s.ShowStats = v; Save(); });
        Check(page, "Show tips when starting", null, () => _s.ShowTips, v => { _s.ShowTips = v; Save(); });

        Section(page, "Screenshots (F12)");
        var folder = new Label { AutoSize = true, ForeColor = Text2, MaximumSize = new Size(500, 0), Margin = new Padding(0, 4, 0, 6) };
        page.Controls.Add(folder);
        _loaders.Add(() => folder.Text = _s.ScreenshotFolder);
        Buttons(page,
            ("Change folder…", () =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = _s.ScreenshotFolder, UseDescriptionForTitle = true, Description = "Save screenshots to" };
                if (dlg.ShowDialog(this) == DialogResult.OK) { _s.ScreenshotDir = dlg.SelectedPath; Save(); LoadValues(); }
            }),
            ("Open folder", () =>
            {
                Directory.CreateDirectory(_s.ScreenshotFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_s.ScreenshotFolder}\"") { UseShellExecute = true });
            }),
            ("Take one now", () => _main.Screenshot()));
        return page;
    }

    FlowLayoutPanel BuildKeys()
    {
        var page = NewPage();
        Section(page, "Keyboard shortcuts");
        var keys = new (string, string)[]
        {
            ("F11, Alt+Enter, double-click", "Full screen (Esc to leave)"),
            ("1, 2, 3…", "Switch console"),
            ("G", "Frame generation: off, 2x, 3x, 4x"),
            ("H", "Signal: auto-detect, always HDR, always SDR"),
            ("S", "Settings"),
            ("A", "Picture settings"),
            ("M", "Mute game audio"),
            ("F1", "Stats"),
            ("F12", "Screenshot"),
            ("F5", "Reconnect the capture card"),
            ("Right-click", "Quick menu"),
        };
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        foreach (var (k, d) in keys)
        {
            grid.Controls.Add(new Label { Text = k, AutoSize = true, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, 4, 24, 4) });
            grid.Controls.Add(new Label { Text = d, AutoSize = true, ForeColor = Text1, Margin = new Padding(0, 4, 0, 4) });
        }
        page.Controls.Add(grid);
        return page;
    }

    FlowLayoutPanel BuildAbout()
    {
        var page = NewPage();
        Section(page, "CaptureFix");
        page.Controls.Add(new Label { Text = $"Version {Updater.Current}", AutoSize = true, ForeColor = Text1, Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0, 0, 0, 4) });
        Note(page, "Capture card viewer with correct HDR colors and smooth game audio.");

        Section(page, "Updates");
        Check(page, "Update automatically", "New versions download in the background and install when you close CaptureFix, so a game is never interrupted.",
            () => _s.AutoUpdate, v => { _s.AutoUpdate = v; Save(); });
        var status = new Label { AutoSize = true, ForeColor = Text1, MaximumSize = new Size(520, 0), Margin = new Padding(0, 6, 0, 0) };
        page.Controls.Add(status);
        void Refresh() { if (!IsDisposed) BeginInvoke(() => status.Text = _main.UpdateStatus); }
        _main.UpdateStatusChanged += Refresh;
        FormClosed += (_, _) => _main.UpdateStatusChanged -= Refresh;
        _loaders.Add(() => status.Text = _main.UpdateStatus);
        Buttons(page,
            ("Check for updates", () => _ = _main.CheckUpdatesAsync(true)),
            ("Install now", () => _ = _main.InstallUpdateNowAsync()),
            ("Open on GitHub", () => Process.Start(new ProcessStartInfo($"https://github.com/{Updater.Repo}") { UseShellExecute = true })));
        Note(page, "Updates come only from this project's GitHub releases, and each download is checked against the release's SHA-256 fingerprint before it installs.");
        return page;
    }

    // ---------- helpers ----------

    void ApplyPicture() { Save(); _main.ApplyColor(); }
    void Audio() { Save(); _main.ApplyAudioLevels(); }
    void Save() => _s.Save();

    static FlowLayoutPanel NewPage() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
        BackColor = Bg, Visible = false,
    };

    void AddPage(FlowLayoutPanel nav, string name, Control page)
    {
        _pages[name] = page;
        _content.Controls.Add(page);
        var b = new Button
        {
            Text = name, Width = 132, Height = 36, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Text1, BackColor = PanelBg, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(8, 0, 0, 0), Tag = name,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Field;
        b.Click += (_, _) => ShowPage(name);
        nav.Controls.Add(b);
        _navButtons.Add(b);
    }

    void ShowPage(string name)
    {
        foreach (var (n, c) in _pages) c.Visible = n == name;
        foreach (var b in _navButtons)
        {
            bool on = (string)b.Tag! == name;
            b.BackColor = on ? Field : PanelBg;
            b.ForeColor = on ? Accent : Text1;
        }
    }

    public void SelectTab(int index) => ShowPage(_pages.Keys.ElementAt(Math.Clamp(index, 0, _pages.Count - 1)));

    static void Section(FlowLayoutPanel page, string title)
    {
        page.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(), AutoSize = true, ForeColor = Text2, Font = new Font("Segoe UI Semibold", 8.5f),
            Margin = new Padding(0, page.Controls.Count == 0 ? 0 : 18, 0, 6),
        });
    }

    static void Note(FlowLayoutPanel page, string text)
    {
        page.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Text2, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 4) });
    }

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Text2, MaximumSize = new Size(520, 0), Margin = new Padding(22, 0, 0, 6), Font = new Font("Segoe UI", 8.75f) };

    void Check(FlowLayoutPanel page, string text, string? hint, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = text, AutoSize = true, ForeColor = Text1, Margin = new Padding(0, 4, 0, 2) };
        c.CheckedChanged += (_, _) => { if (!_loading) set(c.Checked); };
        page.Controls.Add(c);
        if (hint != null) page.Controls.Add(Hint(hint));
        _loaders.Add(() => c.Checked = get());
    }

    void Slider(FlowLayoutPanel page, string text, int min, int max, Func<int> get, Action<int> set, Func<int, string> fmt, string? hint)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var label = new Label { Text = text, AutoSize = true, ForeColor = Text1, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
        var bar = new TrackBar
        {
            Minimum = min, Maximum = max, TickStyle = TickStyle.None, Width = 250, BackColor = Bg, AutoSize = false, Height = 30,
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10),
        };
        var value = new Label { AutoSize = true, ForeColor = Accent, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0) };
        bar.ValueChanged += (_, _) => { value.Text = fmt(bar.Value); if (!_loading) set(bar.Value); };
        row.Controls.Add(label, 0, 0); row.Controls.Add(bar, 1, 0); row.Controls.Add(value, 2, 0);
        page.Controls.Add(row);
        if (hint != null) { var h = Hint(hint); h.Margin = new Padding(0, 0, 0, 6); page.Controls.Add(h); }
        _loaders.Add(() => { bar.Value = Math.Clamp(get(), min, max); value.Text = fmt(bar.Value); });
    }

    static ComboBox MakeCombo(int width) => new()
    {
        Width = width, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1,
    };

    void Choice(FlowLayoutPanel page, string text, string[] items, string? hint, Func<int> get, Action<int> set, Func<string[]>? fill = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        var label = new Label { Text = text, AutoSize = true, ForeColor = Text1, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) };
        var box = MakeCombo(320);
        box.Items.AddRange(items);
        box.SelectedIndexChanged += (_, _) => { if (!_loading && box.SelectedIndex >= 0) set(box.SelectedIndex); };
        row.Controls.Add(label, 0, 0); row.Controls.Add(box, 1, 0);
        page.Controls.Add(row);
        if (hint != null) { var h = Hint(hint); h.Margin = new Padding(0, 0, 0, 6); page.Controls.Add(h); }
        _loaders.Add(() =>
        {
            int idx = get();   // may rebuild the list behind fill()
            if (fill != null) { box.Items.Clear(); box.Items.AddRange(fill()); }
            box.SelectedIndex = idx < box.Items.Count ? idx : -1;
        });
    }

    static void Buttons(FlowLayoutPanel page, params (string text, Action click)[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 4), WrapContents = false };
        foreach (var (t, a) in buttons)
        {
            var b = new Button { Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Text1, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 8, 0) };
            b.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(70, 70, 80);
            b.Click += (_, _) => a();
            row.Controls.Add(b);
        }
        page.Controls.Add(row);
    }

    static async void Flash(Control c)
    {
        var old = c.BackColor;
        c.BackColor = System.Drawing.Color.FromArgb(120, 40, 40);
        await Task.Delay(400);
        c.BackColor = old;
    }

    public void LoadValues()
    {
        _loading = true;
        try
        {
            _profileBox.Items.Clear();
            foreach (var p in _s.Profiles) _profileBox.Items.Add(p.Name);
            _profileBox.SelectedItem = P.Name;
            foreach (var l in _loaders) l();
        }
        finally { _loading = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
