# Third-party components

Adult Zone uses the following. Their licences apply to those parts.

| Component | Licence | Used for |
|---|---|---|
| [.NET 8](https://github.com/dotnet/runtime) | MIT | the runtime |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | MIT | the library database |
| [Microsoft.Web.WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | [Microsoft terms](https://developer.microsoft.com/microsoft-edge/webview2/) | drawing the interface |
| [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) | [Six Labors Split Licence](https://github.com/SixLabors/ImageSharp/blob/main/LICENSE) | resizing artwork |
| [flag-icons](https://github.com/lipis/flag-icons) | MIT | nationality flags |

**ImageSharp is worth reading about before you publish.** Version 3 is free to
use in projects released under an OSI-approved open source licence — which this
project is, under MIT. A closed-source commercial product would need a paid
licence from Six Labors instead.

**ffmpeg is not included.** The app looks for it on your machine and tells you
if it is missing. Shipping ffmpeg binaries carries its own licence obligations,
so it stays separate.
