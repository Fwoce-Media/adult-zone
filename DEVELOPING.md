# Adult Zone — developer notes

For the public description, see [README.md](README.md).

A native WPF window with VLC doing the playing. Adult Zone 3.0 replaces the
browser-based 1.x and 2.x builds and keeps their look: the ink-black shell,
the ember accent, the top tabs, the looping previews.

---

## Build and run

1. Install **Visual Studio 2022** with the **.NET desktop development** workload.
2. Open **AdultZone.sln**.
3. Press **F5**.

The first build downloads the video player (about 90 MB).

ffmpeg is not bundled. Put `ffmpeg.exe` and `ffprobe.exe` in an `ffmpeg`
folder beside `AdultZone.exe`, or install ffmpeg on PATH.

---

## The player

| Key | |
|---|---|
| `Space` / `K` | play / pause |
| `←` `→` / `J` `L` | back / forward 10 seconds |
| `↑` `↓` | volume |
| `M` | mute |
| `C` | subtitles on / off |
| `A` | next audio track |
| `N` / `P` | next / previous in Up next |
| `F` / `F11` | full screen |
| `Esc` | leave full screen, then close |

Elsewhere: `/` jumps to search, `Backspace`, `Alt+←` or the mouse's back
button go back.

---

## Releases

GitHub Actions builds both downloads. Push a version tag and it publishes a
draft release with `AdultZone-<version>-Setup.exe` and
`AdultZone-<version>-portable.zip`:

```
git tag v3.0.0
git push origin v3.0.0
```

The installer script is `installer\AdultZone.iss` (Inno Setup 6). It keeps
2.x's AppId, so it installs over 2.x in place.

---

## Where things live

```
AdultZone.sln
src\AdultZone.Desktop\   the window, pages, dialogs and player (WPF + LibVLCSharp)
src\AdultZone.Core\      library, scanning, ffmpeg artwork, metadata sources (no packages)
```

The library opens where it already is:

| | |
|---|---|
| `%USERPROFILE%\.adultzone\` | 1.x's folder, and the default for a new library |
| `Documents\Adult Zone\Data\` | the installed 2.x's folder, used when 1.x's has no library |
| `Data\` beside the program | portable copies (a `portable.txt` beside the exe) |

In it: `library.db` (the 1.x schema), `cache\thumbs`, `cache\previews`,
`cache\sprites` (the seek-bar frames), `images\actors`, `images\studios`, and
`adultzone.log`. 2.x's PIN and ThePornDB key (`secrets.json`) are read once
into the library.
