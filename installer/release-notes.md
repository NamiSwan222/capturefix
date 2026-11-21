## CaptureFix 1.0.4

- Brightness range detection also uses the white level now: limited range clips white at 940, full
  range reaches 1023. Bright scenes with no black on screen (like the PS5 home and login screens)
  no longer depend on a guess.

Also in 1.0.3: game audio is no longer left muted by the Windows volume mixer, and Discord calls
don't turn the game audio down.
