## CaptureFix 1.0.2

**Fixes bad lighting with PS5 HDR.**

- The HD60 X can send a PS5's HDR in *full* range (seen in one game menu; another was limited), but CaptureFix always assumed *limited* range. That crushed
  every shadow to black and stretched the rest, so scenes looked too contrasty and the lighting was
  off. CaptureFix now reads the signal and sets the range itself.
- It also detects HDR vs SDR from the picture, so a wrong setting (or pressing H by mistake) can no
  longer leave HDR looking dim and grey. Your old HDR on/off switch is replaced by
  **Signal: auto-detect** (recommended), always HDR, or always SDR; same for the brightness range.
  Press H to cycle the signal choices.
- The stats overlay (F1) shows what was detected and the measured black and white levels.

Tested against a real PS5 HDR frame plus HDR and SDR versions in both ranges: all four are detected
correctly and render within about 1/255 of the intended picture.
