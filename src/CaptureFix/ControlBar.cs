using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CaptureFix;

/// <summary>Colours, fonts and the dark menu look shared by the on-screen controls.</summary>
static class Ui
{
    public static readonly Color Bar = Color.FromArgb(22, 22, 26);
    public static readonly Color Line = Color.FromArgb(48, 48, 56);
    public static readonly Color Hover = Color.FromArgb(44, 44, 52);
    public static readonly Color Down = Color.FromArgb(58, 58, 68);
    public static readonly Color Text1 = Color.FromArgb(236, 236, 240);
    public static readonly Color Text2 = Color.FromArgb(150, 150, 162);
    public static readonly Color Accent = Color.FromArgb(94, 164, 255);

    static readonly string IconFamily = PickIconFont();
    static string PickIconFont()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    static readonly Dictionary<(string, int, FontStyle), Font> Fonts = new();
    /// <summary>A cached font with its size given in pixels, so it follows the monitor's scaling.</summary>
    public static Font Font(float px, bool icon = false, bool bold = false)
    {
        var key = (icon ? IconFamily : bold ? "Segoe UI Semibold" : "Segoe UI", (int)Math.Round(px * 4), FontStyle.Regular);
        if (!Fonts.TryGetValue(key, out var f))
            Fonts[key] = f = new Font(key.Item1, Math.Max(1f, key.Item2 / 4f), FontStyle.Regular, GraphicsUnit.Pixel);
        return f;
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public const string GlyphGame = "", GlyphPicture = "", GlyphMotion = "", GlyphSignal = "",
        GlyphVolume = "", GlyphMute = "", GlyphCamera = "", GlyphFull = "", GlyphWindow = "",
        GlyphSettings = "", GlyphMore = "", GlyphDown = "", GlyphUp = "";

    /// <summary>Gives a menu the app's dark look.</summary>
    public static void Style(ToolStripDropDown menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.BackColor = Color.FromArgb(32, 32, 38);
        menu.ForeColor = Text1;
        menu.Font = new Font("Segoe UI", 10f);
    }
}

sealed class DarkMenuColors : ProfessionalColorTable
{
    static readonly Color Bg = Color.FromArgb(32, 32, 38), Sel = Color.FromArgb(52, 52, 62), Edge = Color.FromArgb(60, 60, 70);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color MenuBorder => Edge;
    public override Color MenuItemBorder => Sel;
    public override Color MenuItemSelected => Sel;
    public override Color MenuItemSelectedGradientBegin => Sel;
    public override Color MenuItemSelectedGradientEnd => Sel;
    public override Color MenuItemPressedGradientBegin => Sel;
    public override Color MenuItemPressedGradientMiddle => Sel;
    public override Color MenuItemPressedGradientEnd => Sel;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color SeparatorDark => Edge;
    public override Color SeparatorLight => Edge;
    public override Color CheckBackground => Bg;
    public override Color CheckSelectedBackground => Sel;
    public override Color CheckPressedBackground => Sel;
    public override Color ButtonSelectedBorder => Sel;
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Text1 : Ui.Text2;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Ui.Text2;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // A plain tick in the accent colour instead of the light-theme check box.
        var r = e.ImageRectangle;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Ui.Accent, Math.Max(1.6f, r.Height / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = r.X + r.Width * 0.22f, y = r.Y + r.Height * 0.52f;
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(x, y), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.72f), new PointF(r.X + r.Width * 0.8f, r.Y + r.Height * 0.3f),
        });
    }
}

/// <summary>A flat button for the control bar: an icon, and optionally a small caption over a value.</summary>
sealed class BarButton : Control
{
    public string Glyph = "";
    public string Caption = "";      // empty = icon only
    public bool Compact;             // too little room: show the icon and value only
    public bool Pill;                // drawn as a floating rounded tab (the minimized bar)
    string _value = "";
    bool _accent, _hover, _down;

    public BarButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        TabStop = false;
        BackColor = Ui.Bar;
    }

    float F => DeviceDpi / 96f;

    public string Value => _value;
    /// <summary>Returns true when the text changed (the bar then lays itself out again).</summary>
    public bool Set(string value, bool accent = false, string? glyph = null)
    {
        bool textChanged = value != _value;
        if (!textChanged && accent == _accent && (glyph == null || glyph == Glyph)) return false;
        _value = value; _accent = accent;
        if (glyph != null) Glyph = glyph;
        Invalidate();
        return textChanged;
    }

    public int PreferredWidth()
    {
        float f = F;
        if (Caption.Length == 0 && _value.Length == 0) return (int)(42 * f);
        int text = TextRenderer.MeasureText(_value, Ui.Font(13.5f * f, bold: true)).Width;
        if (!Compact && Caption.Length > 0) text = Math.Max(text, TextRenderer.MeasureText(Caption, Ui.Font(11f * f)).Width);
        return (int)(40 * f) + text + (int)(8 * f);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float f = F;
        g.Clear(Pill ? Color.Black : Ui.Bar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1.5f * f, Pill ? 0.5f : 5 * f, Width - 3 * f, Height - (Pill ? 1 : 10 * f));
        if (Pill || _hover || _down)
        {
            using var path = Ui.Rounded(r, Pill ? r.Height / 2 - 0.5f : 7 * f);
            using var b = new SolidBrush(_down ? Ui.Down : _hover ? Ui.Hover : Color.FromArgb(30, 30, 36));
            g.FillPath(b, path);
            if (Pill) { using var pen = new Pen(Ui.Line); g.DrawPath(pen, path); }
        }

        bool labeled = Caption.Length > 0 || _value.Length > 0;
        var iconColor = _accent ? Ui.Accent : Ui.Text1;
        var iconFont = Ui.Font(17 * f, icon: true);
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (!labeled)
        {
            TextRenderer.DrawText(g, Glyph, iconFont, ClientRectangle, iconColor, Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        int ix = (int)(11 * f), tx = (int)(38 * f);
        TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(ix, 0, (int)(22 * f), Height), iconColor, Flags | TextFormatFlags.VerticalCenter);
        var valueFont = Ui.Font(13.5f * f, bold: true);
        var valueColor = _accent ? Ui.Accent : Ui.Text1;
        if (Compact || Caption.Length == 0)
            TextRenderer.DrawText(g, _value, valueFont, new Rectangle(tx, 0, Width - tx, Height), valueColor, Flags | TextFormatFlags.VerticalCenter);
        else
        {
            int mid = Height / 2;
            TextRenderer.DrawText(g, Caption, Ui.Font(11f * f), new Rectangle(tx, mid - (int)(17 * f), Width - tx, (int)(15 * f)), Ui.Text2, Flags);
            TextRenderer.DrawText(g, _value, valueFont, new Rectangle(tx, mid - (int)(2 * f), Width - tx, (int)(19 * f)), valueColor, Flags);
        }
    }
}

/// <summary>A thin volume slider: click or drag anywhere on it.</summary>
sealed class VolumeSlider : Control
{
    float _value = 1;
    public float Maximum = 1.5f;
    public event Action<float>? Changed;      // while dragging
    public event Action? Committed;           // mouse released

    public VolumeSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        TabStop = false;
        BackColor = Ui.Bar;
    }

    public float Value
    {
        get => _value;
        set { value = Math.Clamp(value, 0, Maximum); if (Math.Abs(value - _value) < 0.001f) return; _value = value; Invalidate(); }
    }
    public bool Dimmed;

    float F => DeviceDpi / 96f;
    void FromMouse(int x)
    {
        float pad = 8 * F;
        Value = MathF.Round(Math.Clamp((x - pad) / Math.Max(1, Width - 2 * pad), 0, 1) * Maximum * 20) / 20;   // 5% steps
        Changed?.Invoke(_value);
    }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Capture = true; FromMouse(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) FromMouse(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { if (Capture) { Capture = false; Committed?.Invoke(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float f = F, pad = 8 * f, y = Height / 2f, w = Width - 2 * pad;
        g.Clear(Ui.Bar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = pad + w * (_value / Maximum);
        using var track = new Pen(Ui.Line, 4 * f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new Pen(Dimmed ? Ui.Text2 : Ui.Accent, 4 * f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, pad, y, pad + w, y);
        if (x > pad + 0.5f) g.DrawLine(fill, pad, y, x, y);
        using var knob = new SolidBrush(Ui.Text1);
        g.FillEllipse(knob, x - 6 * f, y - 6 * f, 12 * f, 12 * f);
    }
}

/// <summary>
/// The strip of controls under the picture: console, picture mode, smooth motion, signal, volume,
/// screenshot, full screen, settings. It can be minimized to a small tab that only appears while
/// the mouse moves, so nothing of it shows in a shared Discord window.
/// </summary>
sealed class ControlBar : Control
{
    readonly MainForm _m;
    readonly BarButton _console = new() { Glyph = Ui.GlyphGame, Caption = "Console" };
    readonly BarButton _picture = new() { Glyph = Ui.GlyphPicture, Caption = "Picture" };
    readonly BarButton _motion = new() { Glyph = Ui.GlyphMotion, Caption = "Smooth motion" };
    readonly BarButton _signal = new() { Glyph = Ui.GlyphSignal, Caption = "Signal" };
    readonly BarButton _mute = new() { Glyph = Ui.GlyphVolume };
    readonly VolumeSlider _volume = new();
    readonly BarButton _shot = new() { Glyph = Ui.GlyphCamera };
    readonly BarButton _full = new() { Glyph = Ui.GlyphFull };
    readonly BarButton _settings = new() { Glyph = Ui.GlyphSettings };
    readonly BarButton _more = new() { Glyph = Ui.GlyphMore };
    readonly BarButton _min = new() { Glyph = Ui.GlyphDown };
    readonly ContextMenuStrip _drop = new();
    readonly ToolTip _tips = new() { InitialDelay = 500, ReshowDelay = 100 };
    string _status = "";

    public ControlBar(MainForm main)
    {
        _m = main;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Ui.Bar;
        Ui.Style(_drop);
        Controls.AddRange(new Control[] { _console, _picture, _motion, _signal, _mute, _volume, _shot, _full, _settings, _more, _min });

        _console.Click += (_, _) => Drop(_console, m =>
        {
            int i = 1;
            foreach (var p in _m.Settings.Profiles)
            {
                string name = p.Name;
                m.Add(new ToolStripMenuItem(name, null, (_, _) => _m.SwitchProfile(name)) { Checked = name == _m.Settings.Active.Name, ShortcutKeyDisplayString = i <= 9 ? (i++).ToString() : null });
            }
            m.Add(new ToolStripSeparator());
            m.Add(new ToolStripMenuItem("Add or rename consoles…", null, (_, _) => _m.OpenSettings(3)));
        });
        _picture.Click += (_, _) => Drop(_picture, m =>
        {
            int cur = _m.CurrentPreset;
            for (int i = 0; i < MainForm.PresetNames.Length; i++)
            {
                int ii = i;
                m.Add(new ToolStripMenuItem(MainForm.PresetNames[i] + "   " + MainForm.PresetHints[i], null, (_, _) => _m.SetPreset(ii)) { Checked = cur == i });
            }
            if (cur < 0) m.Add(new ToolStripMenuItem("Custom (your own slider values)") { Checked = true, Enabled = false });
            m.Add(new ToolStripSeparator());
            m.Add(new ToolStripMenuItem("All picture settings…", null, (_, _) => _m.OpenSettings(0)) { ShortcutKeyDisplayString = "A" });
        });
        _motion.Click += (_, _) => Drop(_motion, m =>
        {
            int fg = _m.Settings.Active.FrameGen;
            foreach (var (name, factor) in new[] { ("Off", 0), ("2x   a 30 fps game shown at 60", 2), ("3x   a 30 fps game shown at 90", 3), ("4x   a 30 fps game shown at 120", 4) })
            {
                int ff = factor;
                m.Add(new ToolStripMenuItem(name, null, (_, _) => _m.SetFrameGen(ff)) { Checked = factor == 0 ? fg < 2 : fg == factor });
            }
            m.Add(new ToolStripSeparator());
            m.Add(new ToolStripMenuItem(_m.FrameGenStatus) { Enabled = false });
        });
        _signal.Click += (_, _) => Drop(_signal, m =>
        {
            string[] names = { "Detect automatically", "Always HDR10", "Always SDR" };
            for (int i = 0; i < 3; i++)
            {
                int ii = i;
                m.Add(new ToolStripMenuItem(names[i], null, (_, _) => _m.SetSignal(ii)) { Checked = _m.Settings.Active.HdrMode == i });
            }
        });
        _mute.Click += (_, _) => _m.ToggleMute();
        _volume.Changed += v => _m.SetVolume(v, save: false);
        _volume.Committed += () => _m.SetVolume(_volume.Value, save: true);
        _shot.Click += (_, _) => _m.Screenshot();
        _full.Click += (_, _) => _m.ToggleFullscreen();
        _settings.Click += (_, _) => _m.OpenSettings();
        _more.Click += (_, _) => _m.ShowFullMenu(_more);
        _min.Click += (_, _) => _m.SetBarMinimized(true);

        _tips.SetToolTip(_mute, "Mute game audio (M)");
        _tips.SetToolTip(_volume, "Game volume");
        _tips.SetToolTip(_shot, "Screenshot (F12)");
        _tips.SetToolTip(_full, "Full screen (F11)");
        _tips.SetToolTip(_settings, "Settings (S)");
        _tips.SetToolTip(_more, "More: video mode, audio device, capture card…");
        _tips.SetToolTip(_min, "Hide this bar (Tab). Move the mouse to bring it back.");
        _tips.SetToolTip(_console, "Which console is plugged in (1, 2, 3…)");
        _tips.SetToolTip(_picture, "How much CaptureFix improves the picture");
        _tips.SetToolTip(_motion, "Frame generation: in-between pictures for smoother movement (G)");
        _tips.SetToolTip(_signal, "HDR or SDR signal from the console (H)");
    }

    public int BarHeight => (int)(54 * DeviceDpi / 96f);
    /// <summary>One of the bar's drop-down menus is open (keep the bar on screen).</summary>
    public bool MenuOpen => _drop.Visible;

    void Drop(Control anchor, Action<ToolStripItemCollection> build)
    {
        _drop.Items.Clear();
        build(_drop.Items);
        _drop.Show(anchor, new Point(0, 2), ToolStripDropDownDirection.AboveRight);
    }

    /// <summary>Brings every label in line with the current state. Cheap; called a few times a second.</summary>
    public void Sync()
    {
        var s = _m.Settings; var p = s.Active;
        bool relayout = false;
        relayout |= _console.Set(p.Name);
        int preset = _m.CurrentPreset;
        relayout |= _picture.Set(preset < 0 ? "Custom" : MainForm.PresetNames[preset], accent: preset == 2);
        relayout |= _motion.Set(_m.MotionLabel, accent: _m.MotionActive);
        relayout |= _signal.Set(_m.SignalLabel);
        bool muted = _m.EffectivelyMuted;
        _mute.Set("", accent: false, glyph: muted ? Ui.GlyphMute : Ui.GlyphVolume);
        _volume.Dimmed = muted;
        if (!_volume.Capture) _volume.Value = p.Volume;
        _volume.Invalidate();
        _full.Set("", glyph: _m.IsFullscreen ? Ui.GlyphWindow : Ui.GlyphFull);
        string status = _m.BarStatus;
        if (status != _status) { _status = status; Invalidate(); }
        if (relayout) PerformLayout();
    }

    Rectangle _gap;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float f = DeviceDpi / 96f;
        int h = Height, pad = (int)(6 * f);
        var left = new[] { _console, _picture, _motion, _signal };
        var icons = new[] { _min, _more, _settings, _full, _shot };
        int iconW = (int)(42 * f), volW = (int)(110 * f);

        // Right side, from the edge inward.
        int x = Width - pad;
        foreach (var b in icons) { x -= iconW; b.SetBounds(x, 1, iconW, h - 1); }
        x -= (int)(10 * f);
        int rightNoVol = x;

        // Captions are dropped, then the volume slider, when the window is too narrow.
        foreach (var b in left) b.Compact = false;
        int leftW = pad + left.Sum(b => b.PreferredWidth());
        bool showVol = true;
        if (leftW > rightNoVol - volW - iconW - (int)(10 * f))
        {
            foreach (var b in left) b.Compact = true;
            leftW = pad + left.Sum(b => b.PreferredWidth());
            if (leftW > rightNoVol - volW - iconW - (int)(10 * f)) showVol = false;
        }
        _volume.Visible = showVol;
        if (showVol) { x -= volW; _volume.SetBounds(x, 1, volW, h - 1); }
        x -= iconW; _mute.SetBounds(x, 1, iconW, h - 1);
        int rightStart = x;

        int lx = pad;
        foreach (var b in left)
        {
            int w = b.PreferredWidth();
            // (A child's Visible reads false while the bar itself is hidden, so keep our own flag.)
            bool fits = lx + w <= rightStart;
            b.Visible = fits;
            b.SetBounds(lx, 1, w, h - 1);
            if (fits) lx += w;
        }
        _gap = Rectangle.FromLTRB(lx + (int)(12 * f), 0, rightStart - (int)(12 * f), h);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Bar);
        using var pen = new Pen(Ui.Line);
        g.DrawLine(pen, 0, 0, Width, 0);
        if (_status.Length > 0 && _gap.Width > 60)
            TextRenderer.DrawText(g, _status, Ui.Font(12.5f * DeviceDpi / 96f), _gap, Ui.Text2,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _drop.Dispose(); _tips.Dispose(); }
        base.Dispose(disposing);
    }
}
