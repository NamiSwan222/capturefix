using System.Diagnostics;
using CaptureFix.Audio;
using CaptureFix.Video;

namespace CaptureFix;

public sealed class MainForm : Form
{
    readonly AppSettings _s = AppSettings.Load();
    readonly Panel _video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Label _overlay = new()
    {
        AutoSize = true, BackColor = Color.FromArgb(20, 20, 24), ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 10f), Padding = new Padding(10, 6, 10, 6), Location = new Point(12, 12), Visible = false,
    };
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    readonly AudioBridge _audio = new();

    Renderer? _renderer;
    VideoCapture? _capture;
    CaptureDeviceInfo? _device;
    List<VideoMode> _modes = new();
    string? _status;          // problem to show on screen
    long _retryAt;
    bool _starting;
    long _toastUntil;
    string? _toast;
    SettingsForm? _settings;
    Point _lastMousePos;
    long _lastMouseMove;
    bool _cursorHidden;
    Rectangle _windowedBounds;

    readonly string? _autoSnapshot;
    readonly int _autoSnapshotDelayMs;

    /// <param name="autoSnapshot">Testing: save a screenshot here after a few seconds, then quit.</param>
    readonly bool _justUpdated;

    public MainForm(string? autoSnapshot = null, int autoSnapshotDelayMs = 4000, bool justUpdated = false)
    {
        _justUpdated = justUpdated;
        _autoSnapshot = autoSnapshot;
        _autoSnapshotDelayMs = autoSnapshotDelayMs;
        Text = "CaptureFix";
        BackColor = Color.Black;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(320, 200);
        KeyPreview = true;
        DarkTitleBar.Apply(this);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(_overlay);
        Controls.Add(_video);
        _overlay.BringToFront();

        _video.ContextMenuStrip = _menu;
        _overlay.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => BuildMenu();
        _video.DoubleClick += (_, _) => ToggleFullscreen();
        _video.Resize += (_, _) => _renderer?.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
        _tick.Tick += (_, _) => OnTick();
        _video.MouseMove += (_, e) =>
        {
            if (e.Location == _lastMousePos) return;
            _lastMousePos = e.Location;
            _lastMouseMove = Environment.TickCount64;
            if (_cursorHidden) { Cursor.Show(); _cursorHidden = false; }
        };

        if (_s.Bounds is { Length: 4 } b)
        {
            var r = new Rectangle(b[0], b[1], b[2], b[3]);
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
        }
        TopMost = _s.AlwaysOnTop;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _renderer = new Renderer(_video.Handle);
            _renderer.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
            ApplyColor();
            _renderer.SnapshotSaved += (path, err) => BeginInvoke(() =>
            {
                if (_autoSnapshot != null) { Close(); return; }
                Toast(err == null ? $"Screenshot saved: {path}" : $"Screenshot failed: {err}");
            });
            _renderer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't start Direct3D 11: " + ex.Message, "CaptureFix", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        if (_s.StartFullscreen) ToggleFullscreen();
        _tick.Start();
        _ = StartCaptureAsync();
        if (_autoSnapshot != null)
        {
            var t = new System.Windows.Forms.Timer { Interval = _autoSnapshotDelayMs };
            t.Tick += (_, _) =>
            {
                t.Stop();
                // Testing aid: also save each settings page next to the snapshot.
                if (Environment.GetEnvironmentVariable("CAPTUREFIX_UI_SHOTS") == "1")
                {
                    OpenSettings();
                    for (int i = 0; i < 7; i++)
                    {
                        _settings!.SelectTab(i);
                        Application.DoEvents();
                        using var bmp = new Bitmap(_settings.Width, _settings.Height);
                        _settings.DrawToBitmap(bmp, new Rectangle(Point.Empty, _settings.Size));
                        bmp.Save(Path.ChangeExtension(_autoSnapshot, null) + $"-settings{i}.png");
                    }
                }
                _renderer.RequestSnapshot(_autoSnapshot);
            };
            t.Start();
            return;
        }
        if (_justUpdated)
        {
            Toast($"CaptureFix updated to {Updater.Current}");
            Updater.CleanupDownloads();
        }
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = 6 * 60 * 60 * 1000; _ = CheckUpdatesAsync(false); };
        _updateTimer.Start();
        if (_justUpdated) return;
        if (_s.ShowTips) Toast("Right-click for options   ·   F11 full screen   ·   H HDR on/off");
    }

    // ---------- capture ----------

    async Task StartCaptureAsync()
    {
        if (_starting) return;
        _starting = true;
        StopCapture();
        _status = "Connecting to the capture card…";
        var profile = _s.Active;
        try
        {
            var (dev, modes) = await Task.Run(() =>
            {
                var d = VideoCapture.FindCaptureCard(_s.DeviceLink) ?? throw new InvalidOperationException("No capture card found. Plug in the HD60 X and press F5.");
                return (d, VideoCapture.ListModes(d));
            });
            _device = dev; _modes = modes;
            var mode = PickMode(profile);
            var cap = await Task.Run(() => VideoCapture.Start(dev, mode));
            cap.Failed += msg => BeginInvoke(() => { _status = msg; _retryAt = Environment.TickCount64 + 3000; });
            _capture = cap;
            _renderer!.Source = cap;
            _status = null;
            _retryAt = 0;
            Text = $"CaptureFix {Updater.Current}  ·  {profile.Name}  ·  {mode}";
            StartAudio();
        }
        catch (Exception ex)
        {
            _status = VideoCapture.Describe(ex);
            _retryAt = Environment.TickCount64 + 3000;
        }
        finally { _starting = false; }
    }

    VideoMode PickMode(Profile p)
    {
        var m = _modes.FirstOrDefault(x => x.Key == p.ModeKey);
        if (m != null) return m;
        bool Hd60(VideoMode x) => x.Width == 1920 && x.Height == 1080 && Math.Round(x.Fps) == 60;
        if (p.HdrSignal)
            m = _modes.FirstOrDefault(x => x.Format == PixelFormat.P010 && Hd60(x)) ?? _modes.FirstOrDefault(x => x.Format == PixelFormat.P010);
        m ??= _modes.FirstOrDefault(x => x.Format == PixelFormat.NV12 && Hd60(x))
              ?? _modes.FirstOrDefault(x => x.Format != PixelFormat.P010)
              ?? _modes.FirstOrDefault();
        return m ?? throw new InvalidOperationException("The capture card reports no usable video modes.");
    }

    void StopCapture()
    {
        if (_renderer != null) { _renderer.Source = null; _renderer.ClearPicture(); }
        var c = _capture; _capture = null;
        c?.Dispose();
    }

    public void StartAudio()
    {
        if (_device == null) return;
        var p = _s.Active;
        _audio.DelayMs = p.AudioDelayMs;
        _audio.Volume = p.Volume;
        _audio.Muted = _s.Muted;
        _audio.Start(_device.Name, _s.AudioInputId, _s.AudioOutputId);
    }

    // ---------- settings ----------

    public AppSettings Settings => _s;

    public void ApplyColor()
    {
        if (_renderer == null) return;
        var p = _s.Active;
        _renderer.Color = new ColorSettings
        {
            HdrSignal = p.HdrSignal, FullRange = p.FullRange, PeakNits = p.PeakNits, SdrPeakNits = p.SdrPeakNits,
            Saturation = p.Saturation, HdrSdrWhite = _s.HdrSdrWhite, AllowHdrOutput = _s.AllowHdrOutput,
            ToneMap = p.ToneMap, Contrast = p.Contrast, BlackLevel = p.BlackLevel, Gamma = p.Gamma,
            Warmth = p.Warmth, Sharpness = p.Sharpness,
        };
        _renderer.Invalidate();
    }

    public void ApplyAudioLevels()
    {
        var p = _s.Active;
        _audio.DelayMs = p.AudioDelayMs;
        _audio.Volume = p.Volume;
        _audio.Muted = _s.Muted;
    }

    public void SaveSoon() => _s.Save();

    public IReadOnlyList<VideoMode> Modes => _modes;
    public VideoMode? CurrentMode => _capture?.Mode;
    public CaptureDeviceInfo? CurrentDevice => _device;
    public string? AudioOutputName => _audio.OutputName;

    /// <summary>One line on how well game audio is arriving, for the settings window.</summary>
    public string AudioHealth
    {
        get
        {
            if (_audio.Error != null) return "Problem: " + _audio.Error;
            if (_audio.InputName == null) return "Not running";
            var loss = _audio.LossPercent;
            if (loss == null) return "Measuring…";
            return loss < 0.5
                ? $"Healthy: all of the card's audio is arriving. Buffer {_audio.BufferedMs:0} ms."
                : $"{loss:0.0}% of the game audio is lost before it reaches Windows. CaptureFix smooths over it, but sound may be slightly lower in pitch. " + UsbAdvice;
        }
    }

    public const string UsbAdvice =
        "Fix: plug the HD60 X into a different USB port on the back of the PC, away from your keyboard, mouse and wireless receivers. " +
        "Right now it shares a USB controller with about 10 of them, and HD video plus all that traffic overloads it.";

    bool _lossWarned;
    public void Reconnect() => _ = StartCaptureAsync();
    public void SelectDevice(string link) { _s.DeviceLink = link; _s.AudioInputId = null; _s.Save(); _ = StartCaptureAsync(); }
    public void SetAlwaysOnTop(bool on) { _s.AlwaysOnTop = on; TopMost = on; _s.Save(); }

    /// <summary>Raised when settings change outside the settings window (hotkeys, menu, reconnect).</summary>
    public event Action? SettingsChanged;
    void Changed() => SettingsChanged?.Invoke();

    public void SwitchProfile(string name, bool force = false)
    {
        if (_s.ActiveProfile == name && !force) return;
        _s.ActiveProfile = name;
        _s.Save();
        ApplyColor();
        Changed();
        Toast($"Profile: {name}");
        _ = StartCaptureAsync();
    }

    public void SetMode(VideoMode m)
    {
        var p = _s.Active;
        p.ModeKey = m.Key;
        // The HD60 X passes an HDR signal through unchanged in every mode (8-bit ones too), so the
        // mode says nothing about HDR. Only switch HDR on when picking a 10-bit mode.
        if (m.Format == PixelFormat.P010) p.HdrSignal = true;
        _s.Save();
        ApplyColor();
        Changed();
        _ = StartCaptureAsync();
    }

    void ToggleHdr()
    {
        var p = _s.Active;
        p.HdrSignal = !p.HdrSignal;
        _s.Save();
        ApplyColor();
        Toast(p.HdrSignal ? "Signal: HDR10 (decoding PQ / BT.2020)" : "Signal: SDR");
        Changed();
    }

    void ToggleMute()
    {
        _s.Muted = !_s.Muted;
        _s.Save();
        ApplyAudioLevels();
        Toast(_s.Muted ? "Game audio muted" : "Game audio on");
        Changed();
    }

    public void ToggleFullscreen()
    {
        _s.Fullscreen = FormBorderStyle != FormBorderStyle.None;
        if (_s.Fullscreen)
        {
            _windowedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            if (!_windowedBounds.IsEmpty) Bounds = _windowedBounds;
        }
        _s.Save();
    }

    public void Screenshot()
    {
        _renderer?.RequestSnapshot(Path.Combine(_s.ScreenshotFolder, $"CaptureFix {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png"));
    }

    public void OpenSettings(int tab = 0)
    {
        if (_settings == null || _settings.IsDisposed) _settings = new SettingsForm(this);
        _settings.LoadValues();
        _settings.SelectTab(tab);
        _settings.Show(this);
        _settings.Activate();
    }

    // ---------- updates ----------

    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 8000 };   // first check soon after start
    public Updater.Release? AvailableUpdate { get; private set; }
    string? _readyInstaller;
    bool _updateBusy;
    public string UpdateStatus { get; private set; } = "";
    public event Action? UpdateStatusChanged;
    void SetUpdateStatus(string text) { UpdateStatus = text; UpdateStatusChanged?.Invoke(); }

    /// <summary>Looks for a newer release. With automatic updates on, downloads it in the background;
    /// it installs when CaptureFix closes (never in the middle of a game).</summary>
    public async Task CheckUpdatesAsync(bool userAsked)
    {
        if (_updateBusy || _autoSnapshot != null) return;
        _updateBusy = true;
        try
        {
            if (userAsked) SetUpdateStatus("Checking…");
            var r = await Updater.CheckAsync();
            if (r == null)
            {
                SetUpdateStatus($"You're up to date (checked {DateTime.Now:HH:mm}).");
                return;
            }
            AvailableUpdate = r;
            if (!_s.AutoUpdate && !userAsked)
            {
                SetUpdateStatus($"Version {r.Version} is available.");
                Toast($"CaptureFix {r.Version} is available. Settings > About to install it.");
                return;
            }
            if (_readyInstaller == null)
            {
                SetUpdateStatus($"Downloading {r.Version}…");
                var progress = new Progress<double>(f => SetUpdateStatus($"Downloading {r.Version}… {f:P0}"));
                _readyInstaller = await Updater.DownloadAsync(r, progress);
            }
            SetUpdateStatus($"Version {r.Version} is ready. It installs when you close CaptureFix, or press Install now.");
            if (!userAsked) Toast($"CaptureFix {r.Version} downloaded. It installs when you close the app.");
        }
        catch (Exception ex)
        {
            SetUpdateStatus("Couldn't check for updates: " + ex.Message);
        }
        finally { _updateBusy = false; }
    }

    /// <summary>Installs the update now and restarts CaptureFix.</summary>
    public async Task InstallUpdateNowAsync()
    {
        if (AvailableUpdate == null) await CheckUpdatesAsync(true);
        if (AvailableUpdate == null) return;
        if (_readyInstaller == null) await CheckUpdatesAsync(true);
        if (_readyInstaller == null) return;
        SetUpdateStatus("Installing…");
        Updater.StartInstaller(_readyInstaller, relaunch: true);
        _installStarted = true;
        Close();
    }
    bool _installStarted;

    // ---------- profiles ----------

    public void AddProfile(string name)
    {
        var src = _s.Active;
        var json = System.Text.Json.JsonSerializer.Serialize(src);
        var p = System.Text.Json.JsonSerializer.Deserialize<Profile>(json)!;
        p.Name = name;
        _s.Profiles.Add(p);
        SwitchProfile(name);
    }

    public void RenameProfile(string newName)
    {
        _s.Active.Name = newName;
        _s.ActiveProfile = newName;
        _s.Save();
        Changed();
    }

    public void DeleteProfile()
    {
        if (_s.Profiles.Count <= 1) return;
        _s.Profiles.Remove(_s.Active);
        SwitchProfile(_s.Profiles[0].Name, true);
    }

    // ---------- menu ----------

    void BuildMenu()
    {
        _menu.Items.Clear();
        var p = _s.Active;

        var prof = new ToolStripMenuItem("Console");
        int i = 1;
        foreach (var pr in _s.Profiles)
        {
            string name = pr.Name;
            prof.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SwitchProfile(name))
            { Checked = pr.Name == p.Name, ShortcutKeyDisplayString = i <= 9 ? (i++).ToString() : null });
        }
        _menu.Items.Add(prof);

        var modes = new ToolStripMenuItem("Video mode");
        foreach (var m in _modes)
        {
            var mm = m;
            modes.DropDownItems.Add(new ToolStripMenuItem(m.ToString(), null, (_, _) => SetMode(mm)) { Checked = _capture?.Mode.Key == m.Key });
        }
        if (_modes.Count == 0) modes.DropDownItems.Add(new ToolStripMenuItem("(no capture card)") { Enabled = false });
        _menu.Items.Add(modes);

        _menu.Items.Add(new ToolStripMenuItem("HDR signal", null, (_, _) => ToggleHdr()) { Checked = p.HdrSignal, ShortcutKeyDisplayString = "H" });
        _menu.Items.Add(new ToolStripMenuItem("Full range (0-255)", null, (_, _) => { p.FullRange = !p.FullRange; _s.Save(); ApplyColor(); }) { Checked = p.FullRange });
        _menu.Items.Add(new ToolStripMenuItem("Show real HDR on an HDR monitor", null, (_, _) =>
        {
            _s.AllowHdrOutput = !_s.AllowHdrOutput; _s.Save(); ApplyColor();
            if (_s.AllowHdrOutput && _renderer?.MonitorHdr != true) Toast("Turn on HDR in Windows display settings to see it");
        }) { Checked = _s.AllowHdrOutput, ToolTipText = "Leave off while sharing on Discord: it streams SDR" });
        _menu.Items.Add(new ToolStripMenuItem("Natural HDR colors (like a TV)", null, (_, _) => { p.ToneMap = p.ToneMap == 0 ? 1 : 0; _s.Save(); ApplyColor(); Changed(); }) { Checked = p.ToneMap == 0, ToolTipText = "Off = Vivid: stronger colors, but bright gold can look orange" });
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()) { ShortcutKeyDisplayString = "S", Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());

        var outs = new ToolStripMenuItem("Play game audio on");
        outs.DropDownItems.Add(new ToolStripMenuItem("Windows default", null, (_, _) => { _s.AudioOutputId = null; _s.Save(); StartAudio(); }) { Checked = _s.AudioOutputId == null });
        try
        {
            foreach (var o in AudioBridge.ListOutputs())
            {
                var oo = o;
                outs.DropDownItems.Add(new ToolStripMenuItem(o.Name, null, (_, _) => { _s.AudioOutputId = oo.Id; _s.Save(); StartAudio(); }) { Checked = _s.AudioOutputId == o.Id });
            }
        }
        catch { }
        _menu.Items.Add(outs);
        _menu.Items.Add(new ToolStripMenuItem("Mute game audio", null, (_, _) => ToggleMute()) { Checked = _s.Muted, ShortcutKeyDisplayString = "M" });
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(new ToolStripMenuItem("Full screen", null, (_, _) => ToggleFullscreen()) { Checked = FormBorderStyle == FormBorderStyle.None, ShortcutKeyDisplayString = "F11" });
        _menu.Items.Add(new ToolStripMenuItem("Screenshot", null, (_, _) => Screenshot()) { ShortcutKeyDisplayString = "F12" });
        _menu.Items.Add(new ToolStripMenuItem("Always on top", null, (_, _) => { _s.AlwaysOnTop = !_s.AlwaysOnTop; TopMost = _s.AlwaysOnTop; _s.Save(); }) { Checked = _s.AlwaysOnTop });
        _menu.Items.Add(new ToolStripMenuItem("Show stats", null, (_, _) => { _s.ShowStats = !_s.ShowStats; _s.Save(); Changed(); }) { Checked = _s.ShowStats, ShortcutKeyDisplayString = "F1" });

        var devs = new ToolStripMenuItem("Capture device");
        try
        {
            foreach (var d in VideoCapture.ListDevices())
            {
                var dd = d;
                devs.DropDownItems.Add(new ToolStripMenuItem(d.Name, null, (_, _) => { _s.DeviceLink = dd.SymbolicLink; _s.AudioInputId = null; _s.Save(); _ = StartCaptureAsync(); })
                { Checked = _device?.SymbolicLink == d.SymbolicLink });
            }
        }
        catch { }
        _menu.Items.Add(devs);
        _menu.Items.Add(new ToolStripMenuItem("Reconnect", null, (_, _) => _ = StartCaptureAsync()) { ShortcutKeyDisplayString = "F5" });
        if (AvailableUpdate != null)
        {
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem($"Update to {AvailableUpdate.Version} and restart", null, (_, _) => _ = InstallUpdateNowAsync())
            { Font = new Font(_menu.Font, FontStyle.Bold) });
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F11:
            case Keys.Alt | Keys.Enter: ToggleFullscreen(); return true;
            case Keys.Escape when FormBorderStyle == FormBorderStyle.None: ToggleFullscreen(); return true;
            case Keys.H: ToggleHdr(); return true;
            case Keys.M: ToggleMute(); return true;
            case Keys.A: OpenSettings(0); return true;
            case Keys.S: OpenSettings(); return true;
            case Keys.F1: _s.ShowStats = !_s.ShowStats; _s.Save(); Changed(); return true;
            case Keys.F5: _ = StartCaptureAsync(); return true;
            case Keys.F12: Screenshot(); return true;
        }
        if (keyData is >= Keys.D1 and <= Keys.D9)
        {
            int idx = keyData - Keys.D1;
            if (idx < _s.Profiles.Count) SwitchProfile(_s.Profiles[idx].Name);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- overlay ----------

    void Toast(string text) { _toast = text; _toastUntil = Environment.TickCount64 + 3500; }

    void OnTick()
    {
        long now = Environment.TickCount64;
        if (_retryAt != 0 && now >= _retryAt && !_starting) { _retryAt = 0; _ = StartCaptureAsync(); }

        // Hide the mouse pointer over the picture in full screen once it stops moving.
        bool full = FormBorderStyle == FormBorderStyle.None;
        if (full && _s.HideCursor && !_cursorHidden && now - _lastMouseMove > 2500 && ContainsFocus && !_menu.Visible
            && _video.ClientRectangle.Contains(_video.PointToClient(Cursor.Position)))
        { Cursor.Hide(); _cursorHidden = true; }
        else if (_cursorHidden && (!full || !ContainsFocus || _menu.Visible))
        { Cursor.Show(); _cursorHidden = false; }

        string? problem = _status;
        var cap = _capture;
        if (problem == null && cap != null)
        {
            long last = Math.Max(cap.LastFrameTicks, cap.StartedTicks);
            double since = (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency;
            if (since > 2)
                problem = "No signal. Check the console is on and the HDMI cable goes into the HD60 X's IN port.\n" +
                          "PS5: Settings > System > HDMI > turn OFF \"Enable HDCP\".";
        }
        if (problem == null && _renderer?.Error is { } re) problem = "Display: " + re;

        if (!_lossWarned && _audio.LossPercent is double loss && loss >= 1.0)
        {
            _lossWarned = true;
            _toast = $"Game audio: {loss:0}% is being lost on this USB port. Settings > Sound shows how to fix it.";
            _toastUntil = now + 12000;
        }

        var lines = new List<string>();
        if (problem != null) lines.Add(problem);
        if (_audio.Error != null) lines.Add(_audio.Error);
        if (_toast != null && now < _toastUntil) lines.Add(_toast);
        if (_s.ShowStats && cap != null && _renderer != null)
        {
            var p = _s.Active;
            lines.Add($"{p.Name}  ·  {cap.Mode}");
            lines.Add($"{_renderer.PresentFps:0.0} fps  ·  card to screen {_renderer.LatencyMs:0.0} ms (+ display)");
            lines.Add($"Signal {(p.HdrSignal ? "HDR10" : "SDR")} {(p.FullRange ? "full" : "limited")} range  ·  output {(_renderer.OutputIsHdr ? "HDR (scRGB)" : "SDR")}{(_renderer.MonitorHdr ? ", monitor in HDR" : "")}");
            lines.Add($"Audio {_audio.InputName ?? "-"} → {_audio.OutputName ?? "-"}  ·  buffer {_audio.BufferedMs:0}/{_audio.DelayMs} ms  ·  speed {_audio.SpeedPercent:+0.00;-0.00}%  ·  gaps {_audio.Underruns} (cushion +{_audio.ExtraMs} ms)");
            if (_audio.FormatInfo != null) lines.Add("Audio " + _audio.FormatInfo + (_audio.LossPercent is double l ? $"  ·  lost on USB {l:0.0}%" : ""));
        }

        string text = string.Join("\n", lines);
        _overlay.Visible = text.Length > 0;
        if (_overlay.Text != text) _overlay.Text = text;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (FormBorderStyle != FormBorderStyle.None && WindowState == FormWindowState.Normal)
            _s.Bounds = new[] { Left, Top, Width, Height };
        else if (!_windowedBounds.IsEmpty)
            _s.Bounds = new[] { _windowedBounds.Left, _windowedBounds.Top, _windowedBounds.Width, _windowedBounds.Height };
        _s.Save();
        _tick.Stop();
        _updateTimer.Stop();
        // A downloaded update installs quietly as the app closes.
        if (!_installStarted && _readyInstaller != null && File.Exists(_readyInstaller))
        {
            try { Updater.StartInstaller(_readyInstaller, relaunch: false); } catch { }
        }
        _audio.Dispose();
        StopCapture();
        _renderer?.Dispose();
        base.OnFormClosing(e);
    }
}
