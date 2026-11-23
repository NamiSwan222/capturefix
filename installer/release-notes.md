## CaptureFix 1.2.1

**Fixes wrong colours when the console changes its brightness range.**

- A PS5 can switch between limited (16-235) and full (0-255) range depending on its output mode.
  The automatic detection now uses hard evidence (pixels beyond the limited range, or a pile-up
  exactly on its limits) and gets cases right that it used to leave undecided.
- If the range is forced in Settings and the console sends the other one, CaptureFix now says so
  on screen. Press **R** to switch back to automatic.

If colours look crushed or washed out, check Settings > Picture > Brightness range: "Detect
automatically" is the right choice for almost everyone.
