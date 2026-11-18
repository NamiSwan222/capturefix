## CaptureFix 1.0.0

First release: a capture card viewer for PS5 and Switch 2 through an Elgato HD60 X.

- **Correct HDR colors.** Decodes the PS5's HDR10 signal (PQ, BT.2020) on the GPU and tone maps it
  like a TV, so it isn't washed out, too dark, or oversaturated.
- **Smooth game audio.** Plays at the rate the card really delivers, so no static or dropouts even
  when the USB port is losing packets. Settings > Sound shows how healthy the connection is.
- **Low delay.** Newest frame only, drawn the moment it arrives.
- **Discord friendly.** Share the CaptureFix window in Go Live: friends get the picture and the game
  sound.
- **Settings** for picture (HDR style, brightness, contrast, color, warmth, sharpness, gamma),
  sound, video mode, console profiles and window behaviour.
- **Automatic updates** from GitHub releases, checked against a SHA-256 fingerprint, installed when
  you close the app.

Close Elgato Studio before starting CaptureFix: only one app can use the capture card at a time.
