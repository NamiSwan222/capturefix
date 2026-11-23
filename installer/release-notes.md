## CaptureFix 1.2.0

- **Frame generation tuned on real GTA VI gameplay.** It now remembers how things moved in the
  previous picture, which keeps fast objects with little detail (a dark van driving past) solid.
  On 70 real PS5 frames the generated picture beats plain blending in 67 of 68 cases (38.2 dB
  against 34.5 dB), in a test with twice the motion of normal use.
- **Sharp upscaling** (Settings > Picture, on by default): when the window is bigger than the game
  picture, for example 1080p on a 1440p screen, it is enlarged with a sharper filter. Measured
  1.3 dB closer to the true sharp picture on real game frames. Works with frame generation.
- **Screenshots (F12) no longer freeze the picture** for a moment: the file is saved in the
  background.
- F1 stats show the size the picture is scaled to and which scaler is used.
- Old update installers are deleted at start (they were piling up in the temp folder).

Not possible in a capture viewer: ray tracing, DLSS and the like. Those need the game's 3D scene,
which only exists inside the console. See the README for what was tried with RTX Video.
