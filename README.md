# CaptureFix: a capture card viewer that gets PS5 HDR right

A replacement for Elgato Studio's preview, for playing PS5 / Switch 2 on the PC screen through an
Elgato HD60 X and sharing it on Discord.

**[Download the installer](https://github.com/NamiSwan222/capturefix/releases/latest)**
(`CaptureFix-Setup-x.y.z.exe`). It installs for your Windows user only, with no admin prompt and
no .NET install needed, and CaptureFix keeps itself up to date.

## The problem

With HDR on, the PS5 sends HDR10: BT.2020 colours encoded with the PQ (SMPTE 2084) brightness curve.
The HD60 X passes that signal through **unchanged in every mode**, the 8-bit ones (1440p60, 1080p120)
included. It does not convert HDR to normal video. Measured on this card: the same PQ levels arrive
in P010 and NV12.

If an app shows those numbers as normal SDR video, whites turn grey, blacks lift and colours go dull
(the "washed out" look). If it guesses the brightness wrong, the picture looks too dark instead.

## The fix

CaptureFix takes the card's raw pixels (Media Foundation with every converter off) and does the colour
work itself in a GPU shader ([video.hlsl](src/CaptureFix/Shaders/video.hlsl)):

1. YCbCr to RGB with the right matrix (BT.2020 for HDR, BT.709 for SDR) and range (limited / full),
   both **detected from the picture itself** (see below).
2. PQ decoded to real brightness in nits.
3. BT.2020 mapped to normal-screen colours, keeping hue for colours that don't fit.
4. Tone mapped with the BT.2390 curve: everything below the knee stays as the game made it, and
   highlights roll off smoothly to the screen's white. Or, with an HDR monitor, the image is shown
   as real HDR (scRGB).

### Automatic signal detection

Two things must be right or the lighting is wrong, and they differ between consoles, modes and
settings, so CaptureFix reads them from the picture ([SignalAnalyzer.cs](src/CaptureFix/Video/SignalAnalyzer.cs)):

- **Brightness range.** Limited-range video never goes below 64 (10-bit) or above 940; full range
  uses everything. It is **not fixed**: on the same PS5 and HD60 X, gameplay arrived at 16-235
  exactly (8-bit) and, later, a pause menu had 5-9% of its pixels below the limited black level and
  1% above its white level. Reading full as limited crushes shadows and clips whites; reading
  limited as full turns blacks grey. The detector uses hard evidence: more than 0.3% of pixels
  beyond the limited range means full; nothing beyond it plus a pile-up exactly on a limit (a
  console clips there) means limited. A forced setting that contradicts the evidence shows an
  on-screen warning, and R switches back to automatic.
- **HDR or SDR.** SDR white text, lights and highlights reach the top of the range (940 / 1023). HDR10
  keeps normal content at 200-600 and never gets near 900 (over 5000 nits), so a few pixels up there
  prove SDR. Without that proof for 30 seconds, it's HDR.

Decisions need a second or two of agreeing evidence, start from what was last seen for that console,
and can be forced in Settings > Picture or with H (auto, always HDR, always SDR).

Two tone mapping styles:

- **Natural (default):** each colour channel goes through the curve, the way TVs and games' own SDR
  modes do it. Bright colours ease toward white, so gold, bronze and skin stay true.
- **Vivid:** scales all channels by the brightest one, which keeps full saturation. The catch: very
  bright yellows and golds drift toward orange, and a bronze trophy looks red. Measured on a PS5
  frame: the game's yellow progress bar came out orange in Vivid and yellow in Natural.

Game audio is played by CaptureFix itself, from the HD60 X's audio input to your headset or speakers,
with a delay you can tune. Because the audio comes from this window, **Discord Go Live (share the
CaptureFix window) streams the game sound**.

### Static / crackling game audio

On a busy USB controller the HD60 X can lose audio packets before they reach Windows while it
streams HD video. Measured on an AMD chipset controller shared with Bluetooth, keyboard, mouse,
lighting and several wireless receivers: 2-7% lost (1080p 10-bit HDR worst, ~2% at 1080p60 8-bit),
0% with no video running. Any app that assumes a steady 48 kHz then runs dry over and over, which
you hear as static.

What CaptureFix does about it:

- Measures how fast audio really arrives and plays at that rate, with a slow ±0.5% correction to
  hold the buffer at the chosen delay. No gaps, no thrown-away chunks (tested: 0 dropouts in 20 s
  under 1080p60 HDR load, where a fixed 48 kHz buffer had one every 1.3 s).
- Fades out and back in if the buffer ever runs dry, and adds a little cushion each time.
- Shows the loss in Settings > Sound and the F1 stats, and warns once when it's over 1%.

CaptureFix also clears a mute Windows remembered for it in the volume mixer when it starts (M is
its own mute), warns if something mutes it while running, and opts out of Windows call "ducking",
so a Discord call doesn't turn the game down by 80%.

The real fix is the USB port: move the HD60 X to a port on another controller (a rear USB port
connected to the CPU), so 0% is lost.

Low delay: only the newest frame is kept, and each is drawn the moment it arrives (flip-model
swap chain, one frame queued at most).

## Frame generation (G)

A 30 fps game reaches the capture card as 60 frames a second with every picture sent twice.
CaptureFix spots the repeats (they arrive bit-identical), so it knows the game's real frame rate,
and draws in-between pictures on the GPU: a 30 fps game is shown at 60 (2x), 90 (3x) or 120 (4x)
pictures a second, a 60 fps game at 120 or 240. It never goes above the monitor's refresh rate.

How ([framegen.hlsl](src/CaptureFix/Shaders/framegen.hlsl), [FrameGen.cs](src/CaptureFix/Video/FrameGen.cs)):

1. Each converted frame gets a half-size brightness map with mip levels down to about 30x17.
2. Motion search, coarsest level first, from the in-between picture's point of view: for every spot
   it finds the vector where the previous frame and the next frame agree best (5x5 patch, with half
   and quarter texel steps). No holes to fill, because every output pixel gets a vector.
3. A 3x3 median removes stray vectors; near edges each pixel may take a neighbour's vector when
   that fits clearly better.
4. Both frames are fetched along the vector and mixed. Where "nothing moved" fits better than the
   motion (HUD, map, subtitles), the pixel is left alone so text stays sharp.
5. Pictures are spread evenly over the game frame with 1 ms timers: generated ones first, the real
   frame last. Scene cuts and uneven frame rates are shown as they are.

The cost is delay: the next frame must arrive before the in-between one can be drawn. At 2x a
30 fps game gets 16.7 ms extra (25 ms at 4x); a 60 fps game 8.3 ms.

Measured (RTX 5080, 1080p): generating a picture takes 0.8 ms (Fast) or 1.9 ms (High). Against the
true middle frame of 13 test clips (pans up to 200 px a frame, zoom, a moving object, a static HUD),
generated pictures score 29.6 to 52.8 dB PSNR where simply blending the two frames scores 15.6 to
29.3 dB. Picture spacing in a live 30 to 60 fps run: 16.67 ms on average, 0.6 ms deviation.

On real gameplay (70 consecutive GTA VI frames from a PS5, camera moving around the character, a
van driving past): making the picture between frames k and k+2 and comparing it with the real
frame k+1 (twice the motion of normal use) scores 38.2 dB on average against 34.5 dB for blending,
and wins in 67 of 68 cases. Repeated frames from a real console are not bit-identical: about 25% of
pixels flicker by one or two levels (dithering), so a repeat is "under 0.05% of pixels changed by
more than 2/255".

Each spot also tries the vector it had in the previous generated picture, which keeps hold of fast
objects with little detail once they have been found.

Known limits: a thin halo around objects that move against the background (visible at the back
edge of a passing car), and slight fringing around HUD text over a fast-moving scene.

`CaptureFix.exe --fgtest out.txt A.raw B.raw C.raw NV12 1920 1080 1920 [pngPrefix]` scores a generated
picture against a real middle frame; `--fgplay` runs saved frames through the live renderer and
reports pacing; `--cadence out.txt 6 MODE dir 70` logs the game's real frame rate from the card and
saves 70 new frames; `--fgseq out.txt dir` scores frame generation over such a saved sequence.

## Sharp upscaling

A 1080p capture shown on a 1440p or 4K screen has to be enlarged. With "Sharp upscaling" on
(Settings > Picture, on by default) CaptureFix uses a Catmull-Rom bicubic filter with a halo limit
instead of plain bilinear, for real and generated frames alike. Measured against the true sharper
picture at the same 1.33x factor: +1.3 dB on real GTA VI frames, +1.0 dB on a detailed photo, at
0.04 ms a frame. `--scaletest` produces both versions of a saved frame.

## Control bar and picture modes

A bar under the picture holds the everyday controls: console, picture mode, smooth motion, signal,
volume and mute, screenshot, full screen, settings, and "more" (the full menu). The arrow at its
right end (or Tab) minimizes it: then nothing of it is on screen, and a small "Controls" tab shows
up at the bottom only while the mouse moves, so a shared Discord window stays clean. In full screen
the bar lies over the bottom of the picture and goes away when the mouse rests.

Picture modes set four enhancers at once ([framegen.hlsl](src/CaptureFix/Shaders/framegen.hlsl),
`PSEnhance`, run last at the size shown on screen):

- **Sharpening**: contrast adaptive, the same idea as AMD CAS (the sharpening stage of FSR). Flat
  areas and edges that are already strong are left alone, so there are no halos.
- **Smooth gradients**: debanding. Nearly flat areas are averaged with their surroundings and get a
  touch of noise, which removes the steps 8-bit video leaves in skies and fog. Detail is untouched.
- **Clarity**: local contrast in the midtones, against a heavily blurred copy of the picture.
- **Vibrance**: lifts weak colours only. Off in every mode.

| Mode | Sharpening | Smooth gradients | Clarity | Also |
|---|---|---|---|---|
| Original | off | off | off | |
| Balanced (default) | 35% | 60% | 12% | |
| Ultra | 60% | 100% | 25% | sharp upscaling, frame generation |

All of it costs 0.4 ms per picture at 3440x1935 on an RTX 5080, and the average colour of the
picture does not move (checked on a real game frame).

## Wide-colour monitors (why everything looked too red)

A console picture is made for standard colours (BT.709 / sRGB). QD-OLED and many gaming monitors
have deeper primaries, and unless Windows manages colour for them (HDR mode or Auto Color
Management), the picture's numbers go straight to the panel: every colour gets stronger, reds and
skin tones most of all. On the test PC the main monitor (a QD-OLED, red primary at 0.676, 0.316
against the standard 0.640, 0.330) covers 1.35 times the standard colour range, and the game looked
"too red" in SDR and HDR alike while the app's own output measured perfectly neutral.

CaptureFix reads the primaries of the monitor its window is on (from DXGI, which gets them from
the monitor's EDID), builds the BT.709-to-monitor matrix with the monitor's own white point (so
white and greys do not move), and applies it in linear light at the end of the shader
([MonitorGamut.cs](src/CaptureFix/Video/MonitorGamut.cs)). For that monitor, standard red is sent
as 79% red plus 3% green. "Automatic" converts only when the monitor is clearly wide (over 1.12x)
and Windows is not already managing its colour; it follows the window from monitor to monitor.
Checked on the GPU against the same maths in Python: within 1/255, greys unchanged.

If the monitor is switched to an sRGB mode in its own menu, set "Wide-color monitor" to Off, or the
picture will come out too pale. `--monitors out.txt` lists every monitor's primaries and matrix.

## What a capture card viewer can and can't do with the GPU

CaptureFix receives finished pictures from the console, not the game's 3D scene. So features that
need the scene (ray tracing, path tracing, DLSS, higher render resolution, better textures) are
not possible here: they can only happen inside the game on the console. What the GPU can do to a
finished picture is what CaptureFix does: frame generation, sharp upscaling, HDR tone mapping,
sharpening and colour controls.

NVIDIA's RTX Video Super Resolution (AI upscaling) was tried through the Direct3D video processor
extension. On the test PC the driver accepted the request but returned exactly the same picture
with it on and off, so it is not used. It probably needs "RTX Video Enhancement" switched on in the
NVIDIA app; untested with that on.

## Use

Install it from the [latest release](https://github.com/NamiSwan222/capturefix/releases/latest) and
open CaptureFix from the Start menu. **Close Elgato Studio first**: only one app can use the card at
a time.

| Key | |
|---|---|
| Tab | Hide or show the control bar |
| Right-click | All options |
| F11 / double-click / Alt+Enter | Full screen (Esc to leave) |
| 1 / 2 | PS5 / Switch 2 profile |
| G | Frame generation: off, 2x, 3x, 4x |
| H | Signal: auto-detect, always HDR, always SDR |
| R | Brightness range back to automatic |
| S | Settings (picture, sound, video, consoles, window, keys) |
| A | Picture settings |
| M | Mute game audio |
| F1 | Stats: fps, delay, signal, audio queue |
| F12 | Screenshot to Pictures\CaptureFix |
| F5 | Reconnect |

## Settings (S)

- **Picture:** frame generation (off / 2x / 3x / 4x) and its quality, sharp upscaling, picture mode with sharpening / smooth gradients / clarity / vibrance, wide-colour monitor conversion, signal (auto / HDR / SDR), brightness range (auto / limited / full), HDR colour style (Natural / Vivid), HDR brightness, HDR peak, contrast,
  black level, colour, warmth, sharpness, gamma (2.2 / 2.4 dark room / 2.0 bright room), full range,
  real HDR on an HDR monitor.
- **Sound:** output device, volume, mute, audio delay, restart audio.
- **Video:** capture device, video mode, reconnect.
- **Consoles:** add (copy of current), rename and delete console profiles.
- **Window:** always on top, start in full screen, hide the mouse pointer in full screen, stats,
  tips, screenshot folder.
- **About:** version, automatic updates on/off, check now, install now.

## Updates

CaptureFix checks this repo's GitHub releases a few seconds after it starts and every 6 hours. A new
version downloads in the background and installs silently when you close the app, so a game is
never interrupted ("Install now" in Settings > About or the right-click menu does it right away and
restarts). A download is only installed if it comes from GitHub and matches the SHA-256 file
published with the release ([Updater.cs](src/CaptureFix/Updater.cs)).

Each console profile remembers its video mode, HDR, picture settings, audio delay and volume.
Settings: `%AppData%\CaptureFix\settings.json`.

### Getting the best picture

- PS5: Settings > Screen and Video > Video Output: **HDR: On When Supported**, then run
  **Adjust HDR** and set CaptureFix's **HDR peak** slider to the brightness you end on (default 1000).
- If the picture is dark, raise **HDR brightness** (A).
- **Leave "Show real HDR on an HDR monitor" off while sharing on Discord.** Discord streams SDR,
  and the tone-mapped picture is what your friends should see.
- Black screen with a PS5: Settings > System > HDMI > turn off **Enable HDCP**.
- 1080p120 and 1440p60 are 8-bit: HDR still works there, with a little more banding than 1080p60 10-bit.

## Video modes on the HD60 X

| Mode | Format |
|---|---|
| 1080p 60/50/30/25 | P010 10-bit (best for HDR) |
| 1080p 120, 1440p 60, 4K 30 | NV12 8-bit |
| 1080p 60 and lower | NV12 / YUY2 8-bit |

## Build and release

```powershell
dotnet build src\CaptureFix
```

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)): bump `<Version>`
in the .csproj, update [installer/release-notes.md](installer/release-notes.md), then push a tag:

```powershell
git tag v1.0.1; git push origin v1.0.1
```

The workflow publishes a self-contained single-file exe, builds the Inno Setup installer
([CaptureFix.iss](installer/CaptureFix.iss)), writes its `.sha256`, and attaches both to the release.
The icon is drawn by [tools/make-icon.ps1](tools/make-icon.ps1).

`CaptureFix.exe --dump frame.raw` saves one untouched frame from the card for colour analysis.
`CaptureFix.exe --analyze out.txt frame.raw P010 1920 1080 3840 true true` runs the signal detector on a
saved frame, and `CaptureFix.exe --rendertest out.png frame.raw P010 1920 1080 3840` runs it through the
real GPU renderer in an invisible window, so colour fixes can be checked without a console or a screen.
`CaptureFix.exe --probe out.txt` lists devices and modes, captures from each main mode and writes the
frame rate and brightness statistics. `CaptureFix.exe --snapshot out.png` saves what the window shows
after 4 seconds and quits.

## Layout

- `Video/VideoCapture.cs`: finds the card, lists modes, reads frames (newest only).
- `Video/Renderer.cs`: Direct3D 11 upload, draw, HDR output, screenshots, frame pacing.
- `Video/FrameGen.cs`, `Shaders/framegen.hlsl`: frame generation.
- `Video/SignalAnalyzer.cs`: HDR / range detection.
- `Video/MonitorGamut.cs`: colour conversion for wide-colour monitors.
- `Shaders/video.hlsl`: colour conversion and tone mapping.
- `Audio/AudioBridge.cs`: game audio passthrough that follows the card's real rate.
- `Updater.cs`: GitHub release check, verified download, silent install.
- `MainForm.cs`: window, quick menu, hotkeys, profiles.
- `SettingsForm.cs`: the settings window.

## Not done yet

- Recording to disk (use OBS for now; close CaptureFix first).
- Detecting HDR on/off automatically. The card reports it only through Elgato's private USB
  commands, so it's a per-console switch (H) for now.
