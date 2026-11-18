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

1. YCbCr to RGB with the right matrix (BT.2020 for HDR, BT.709 for SDR) and range (limited / full).
2. PQ decoded to real brightness in nits.
3. BT.2020 mapped to normal-screen colours, keeping hue for colours that don't fit.
4. Tone mapped with the BT.2390 curve: everything below the knee stays as the game made it, and
   highlights roll off smoothly to the screen's white. Or, with an HDR monitor, the image is shown
   as real HDR (scRGB).

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

The real fix is the USB port: move the HD60 X to a port on another controller (a rear USB port
connected to the CPU), so 0% is lost.

Low delay: only the newest frame is kept, and each is drawn the moment it arrives (flip-model
swap chain, one frame queued at most).

## Use

Install it from the [latest release](https://github.com/NamiSwan222/capturefix/releases/latest) and
open CaptureFix from the Start menu. **Close Elgato Studio first**: only one app can use the card at
a time.

| Key | |
|---|---|
| Right-click | All options |
| F11 / double-click / Alt+Enter | Full screen (Esc to leave) |
| 1 / 2 | PS5 / Switch 2 profile |
| H | HDR signal on/off (if colours look washed out or too strong, press H) |
| S | Settings (picture, sound, video, consoles, window, keys) |
| A | Picture settings |
| M | Mute game audio |
| F1 | Stats: fps, delay, signal, audio queue |
| F12 | Screenshot to Pictures\CaptureFix |
| F5 | Reconnect |

## Settings (S)

- **Picture:** HDR on/off, HDR colour style (Natural / Vivid), HDR brightness, HDR peak, contrast,
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
`CaptureFix.exe --probe out.txt` lists devices and modes, captures from each main mode and writes the
frame rate and brightness statistics. `CaptureFix.exe --snapshot out.png` saves what the window shows
after 4 seconds and quits.

## Layout

- `Video/VideoCapture.cs`: finds the card, lists modes, reads frames (newest only).
- `Video/Renderer.cs`: Direct3D 11 upload, draw, HDR output, screenshots.
- `Shaders/video.hlsl`: colour conversion and tone mapping.
- `Audio/AudioBridge.cs`: game audio passthrough that follows the card's real rate.
- `Updater.cs`: GitHub release check, verified download, silent install.
- `MainForm.cs`: window, quick menu, hotkeys, profiles.
- `SettingsForm.cs`: the settings window.

## Not done yet

- Recording to disk (use OBS for now; close CaptureFix first).
- Detecting HDR on/off automatically. The card reports it only through Elgato's private USB
  commands, so it's a per-console switch (H) for now.
