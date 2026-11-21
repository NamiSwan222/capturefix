## CaptureFix 1.0.3

**Fixes "no game audio".**

- Windows had muted CaptureFix in the volume mixer (a mute there is remembered per app, so it
  survived restarts and updates). CaptureFix now turns its own mixer entry back on when it starts;
  it has its own mute (M) for when you want silence.
- If something mutes it while it runs (the volume mixer, a mixer app or a Stream Deck key),
  CaptureFix says so on screen and in Settings > Sound, and M turns the sound back on.
- Opts out of Windows "ducking": joining a Discord call no longer turns the game audio down by 80%.
