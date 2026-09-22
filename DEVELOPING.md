# Adult Zone — developer notes

For the public-facing description, see [README.md](README.md).


The same application, rebuilt as a native Windows program: an ASP.NET Core
server and a WebView2 window in one process, with no Python involved.

The interface is carried over unchanged. `wwwroot` is a copy of the Python
build's `static` folder — the same HTML, CSS and JavaScript, the same 79 flag
files — so everything you see behaves exactly as before.

**Your library is untouched.** The schema matches the Python build column for
column, and the database is still `%USERPROFILE%\.adultzone\library.db`. Both
versions can open the same library; run only one at a time.

---

## Installing it

1. Close Adult Zone if it is running.
2. Double-click **`Install Adult Zone.bat`** in this folder.
3. Wait for it to finish, then press **Y** to open the app.

It builds the program into `Documents\Adult Zone`, and puts an **Adult Zone**
shortcut on your desktop. Use that shortcut from now on — Visual Studio is only
needed if you want to change the code.

To update later: replace this `AdultZoneCS` folder with the new one and run
`Install Adult Zone.bat` again. Your library is not in this folder, so replacing
it is always safe.

## Where your data lives

| What | Where |
|---|---|
| The program | `Documents\Adult Zone\AdultZone.exe` |
| Your library — database, thumbnails, previews, photos | `Documents\Adult Zone\Data` |
| Your PIN and ThePornDB key | `%USERPROFILE%\.adultzone\secrets.json` |

The first time the installed program opens, it **moves** your existing library
from `%USERPROFILE%\.adultzone` into the Data folder. If anything goes wrong
part-way, it puts everything back and carries on from the old location.

The PIN and API key are kept apart on purpose: copying the `Adult Zone` folder
to another drive or machine takes your library with it, but not your PIN hash
or API key.

**The old Python version will no longer see your library** after the move,
since it only knows the old location.

## Two things it needs on the machine

- **WebView2 runtime.** Present on Windows 11 and on most Windows 10 machines
  through Edge. If it is missing the app says so rather than failing silently.
- **ffmpeg**, exactly as before: on PATH, or in an `ffmpeg` folder beside the
  executable, or pointed at by `FFMPEG_BIN` and `FFPROBE_BIN`.

---

## What's ported

Everything the Python build does — all 60 of its routes:

- The window, maximised, with the app icon and no browser chrome
- Scanning storage folders, with every filename convention
- ffmpeg thumbnails and preview loops
- Browsing, search, sorting, recommendations, playback with instant seeking
- Editing details, cast, tags and sub-sites
- Uploading portraits, wide photos, studio logos and custom thumbnails
- **Find info**, from Wikipedia, a page address, or ThePornDB
- The **PIN lock**, enforced by the server, not just drawn over the screen
- Preview quality, pruning, hiding performers, and merge-on-rename
- **Instant search**: typing shows matching performers with their photo,
  studios with their logo, and videos with their thumbnail. Arrow keys move
  through them; Enter opens the full results page.

**A PIN you set in the Python version works here too.** Both hash it the same
way, so switching between them does not lock you out.

## How it maps to the Python build

| Python | C# |
|---|---|
| `app/config.py`, `app/paths.py` | `AppPaths.cs` |
| `app/db.py` | `Db.cs` |
| `app/media.py` + Pillow | `Media.cs` |
| `app/scanner.py` | `Scanner.cs` |
| `app/api.py` | `Api.cs`, `Editing.cs` |
| `app/lock.py` | `Lock.cs` |
| `app/scrape.py` | `Scrape.cs` |
| `run.py`, `app/main.py`, `app/browser.py` | `Program.cs` |
| `static/` | `wwwroot/` |

Run the Python build against a copy of your library if you want to compare the
two side by side — both read the same schema.
