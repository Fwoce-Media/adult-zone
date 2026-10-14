# Third-party notices

Adult Zone is MIT licensed (see [LICENSE](LICENSE)). It includes or uses:

| | Licence | Used for |
|---|---|---|
| [LibVLC](https://www.videolan.org/vlc/libvlc.html) | LGPL 2.1 or later | playback |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | LGPL 2.1 or later | connecting the app to LibVLC |
| [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | Microsoft Software License (BSD-style SDK) | showing Babepedia pages |
| [flag-icons](https://github.com/lipis/flag-icons) | MIT | nationality flags |

LibVLC and LibVLCSharp are included unmodified, as separate libraries in the
`libvlc` folder and `LibVLCSharp*.dll`. Their source is available from
VideoLAN at the links above, and you may replace them with your own builds.

[ffmpeg](https://ffmpeg.org/) is not included. When you install it, Adult
Zone runs it to make thumbnails, previews and seek-bar frames.

## Metadata sources

Details are only fetched when you ask for them:

- [Wikipedia](https://www.wikipedia.org/) — text under
  [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/); dates of
  birth from [Wikidata](https://www.wikidata.org/) (CC0).
- [ThePornDB](https://theporndb.net/) — with your own API key.
- Any page address you give — its own published metadata tags.
