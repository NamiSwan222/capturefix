## CaptureFix 1.1.0

**Frame generation: 30 fps games shown at 60 fps (or 90 / 120).**

- CaptureFix detects the game's real frame rate from the capture and draws in-between pictures on
  the GPU, so a 30 fps game such as GTA VI moves like a 60 fps one. Choose 2x, 3x or 4x in
  Settings > Picture, the right-click menu, or with **G**. It is on at 2x by default.
- 60 fps games can go to 120 or 240 on a fast monitor. It never exceeds the monitor's refresh rate.
- HUD, map and subtitles are kept sharp; scene cuts are not blended.
- Cost: a little delay (about 17 ms at 2x for a 30 fps game) and a thin halo around fast-moving
  objects. Press G to turn it off for games where reaction time matters most.
- F1 stats show the game's frame rate, the shown frame rate and the added delay.

Tested on synthetic motion clips against the true in-between frame, and with a simulated 30 fps
capture for pacing. Not yet tuned on long real gameplay: feedback welcome.
