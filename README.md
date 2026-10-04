# Adult Zone

A private library for the videos you already own, for Windows.

Point it at your folders and it builds a browsable library: studios,
performers, tags, looping previews, and a player that plays everything.
Nothing is uploaded, and nothing is moved or renamed.

## Updates and FFmpeg

Settings has **Check for updates**, which looks at this repository's latest
published release and can download and run its installer, and an **FFmpeg**
panel that shows where ffmpeg was found and installs a copy when there is none.

## Download

| | |
|---|---|
| **Setup.exe** | Installs normally, with a Start menu entry and an uninstaller. |
| **Portable zip** | Unzip and run. Everything stays in its own folder. |

Windows 10 or 11, 64-bit. Both are on the [Releases](../../releases) page.

### ffmpeg

Needed for thumbnails, previews and seek-bar frames. Browsing and playback
work without it. Install it so it is on your PATH, or put `ffmpeg.exe` and
`ffprobe.exe` in a folder called `ffmpeg` next to the program.

## What it does

- **Reads your existing folders.** Studio, cast, date and title are read from
  file names, and everything stays editable.
- **Scenes and movies.** Mark a folder as movies and its films get their own
  tab and home row, with box covers.
- **Looping previews** on every card, and behind each video's page.
- **Plays anything** with VLC, with frames that follow the mouse along the
  seek bar, and a pop-out window that stays on top.
- **Performers and studios** get their own pages, with photos, biographies,
  nationality, career status and adjustable logos.
- **Find info** from Wikipedia, any page address, or ThePornDB with your own
  API key. You tick exactly which fields to keep.
- **Batch scrape** a whole library at once. Clear matches are applied, close
  calls wait for you to pick with the video playing beside them, and duplicate
  performer profiles can be merged.
- **Your home screen.** Choose which rows it shows, including a row for any tag.
- **PIN lock.** The library is blurred and closed until the PIN is entered.

Upgrading from 1.x or 2.x? Your library, PIN and API key carry over.

## Privacy

Everything stays on your computer. There is no telemetry. The internet is used
only when you ask Find info to look something up.

## Supporting it

Adult Zone is free. If it is useful to you:
[Ko-fi](https://ko-fi.com/fwocemedia) ·
[Buy Me a Coffee](https://buymeacoffee.com/Fwoce_Media)

## Building it yourself

Visual Studio 2022 with **.NET desktop development**: open `AdultZone.sln`
and press F5. See [DEVELOPING.md](DEVELOPING.md).

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Adult Zone is a library manager. It contains no media, indexes no websites and
downloads nothing but the details you ask it to find.
