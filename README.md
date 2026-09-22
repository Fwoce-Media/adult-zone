# Adult Zone

A private media library for videos you already own, on your own machine. It
looks like a streaming service, and nothing ever leaves your computer.

Point it at your folders and it reads the files, builds thumbnails and short
preview clips, and organises everything by performer, studio and tag.

---

## Download

| | |
|---|---|
| **Setup.exe** | Installs normally, with a Start menu entry and an uninstaller. |
| **Portable zip** | Unzip and run. Everything stays in its own folder — good for a USB drive. |

Windows 10 or 11, 64-bit. Both are on the [Releases](../../releases) page.

### ffmpeg

Needed for thumbnails and preview clips. Playback and browsing work without it.

Either install it so it sits on your PATH, or drop `ffmpeg.exe` and
`ffprobe.exe` into a folder called `ffmpeg` next to the program.

---

## What it does

- **Reads your existing folders.** Nothing is moved, renamed or copied.
- **Works out what it can from filenames** — studio, performers, date, title —
  and everything stays editable by hand.
- **Preview clips.** Hovering a card plays a short loop stitched from moments
  across the video.
- **Performers and studios** get their own pages, with photos, biographies,
  nationality and career status.
- **Fills in details automatically** from Wikipedia, any page address, or
  ThePornDB with your own API key.
- **Instant search** across performers, studios, tags and titles.
- **PIN lock.** While locked the server refuses every request for a video or an
  image, so it is not merely a screen cover.
- **Plays in place.** No external player, and seeking is instant.

## Where your things are kept

| | Installed | Portable |
|---|---|---|
| Library, artwork, previews | `Documents\Adult Zone\Data` | `Data` beside the program |
| PIN and API key | `%USERPROFILE%\.adultzone` | beside the program |

Removing the program never touches your library, and never touches your videos.

## Privacy

It runs a small server on your own machine, reachable only from that machine.
Nothing is uploaded and there is no telemetry of any kind. The only time it
reaches the internet is when you ask it to look up details for a performer or
a scene.

---

## Supporting it

If it is useful to you, you can support development on Patreon. The app is
free, and stays free.

## Building it yourself

Needs Visual Studio 2022 with **.NET desktop development** and **ASP.NET and
web development**, or just the .NET 8 SDK.

```
dotnet run --project src\AdultZone\AdultZone.csproj
```

Releases are built by GitHub Actions: push a tag such as `v2.3.0` and it
produces both downloads and opens a draft release. See
[DEVELOPING.md](DEVELOPING.md) for the details.

## Licence

MIT — see [LICENSE](LICENSE). Third-party components are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Adult Zone is a library manager. It contains no media, indexes no websites and
downloads nothing. What you point it at is your own business.
