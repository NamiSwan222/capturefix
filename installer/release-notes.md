## CaptureFix 1.2.2

**Fixes "everything is too red" on wide-colour monitors.**

- QD-OLED and many gaming monitors have deeper colours than a console picture is made for. Without
  colour management the game looks oversaturated on them, reds and skin tones most of all, in SDR
  and HDR alike. CaptureFix now reads the colours of the monitor its window is on and converts the
  picture for it. White and greys stay exactly as they are.
- It is automatic: on when the monitor is clearly wide-colour and Windows is not already managing
  its colour, off otherwise, and it follows the window if you move it to another monitor.
- Settings > Picture > Monitor colors shows what was detected and lets you force it on or off.
  Turn it off if your monitor is set to an sRGB mode in its own menu.
- F1 stats show the monitor's colour range and whether the conversion is active.
