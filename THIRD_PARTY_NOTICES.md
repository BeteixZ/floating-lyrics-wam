# Third-party notices

Apple Music Lyrics combines project-authored MIT-licensed code with the LyricsKit-derived files described below. The root [LICENSE](LICENSE) applies to the project-authored code; it does not replace the licenses attached to third-party or derived files.

## LyricsKit: QQ Music and NetEase Cloud Music port

Credit goes to **ddddxxx and the LyricsKit contributors** for the original [LyricsKit](https://github.com/ddddxxx/LyricsKit) and [LyricsX](https://github.com/ddddxxx/LyricsX), and to **MxIris-LyricsX-Project and its contributors** for the [maintained LyricsKit fork](https://github.com/MxIris-LyricsX-Project/LyricsKit) and [LyricsX fork](https://github.com/MxIris-LyricsX-Project/LyricsX).

The selected baseline is **LyricsKit 1.8.3**, commit [`6f071990d0c9c6f48d29284db805990bffb912ed`](https://github.com/MxIris-LyricsX-Project/LyricsKit/tree/6f071990d0c9c6f48d29284db805990bffb912ed), used by the maintained LyricsX [v1.8.9 release](https://github.com/MxIris-LyricsX-Project/LyricsX/releases/tag/v1.8.9) and recorded in that release's [Package.resolved](https://github.com/MxIris-LyricsX-Project/LyricsX/blob/v1.8.9/LyricsX.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved). These fixed links record the version used for the port; updates to upstream branches do not change this attribution.

### Source and local coverage

| Local source files | Fixed upstream source | Work adapted |
| --- | --- | --- |
| `src/AppleMusicLyrics.Infrastructure.Windows/External/QQMusic/*.cs` | [QQMusic.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Provider/Services/QQMusic.swift), [QQResponseSearchResult.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Model/QQMusic/QQResponseSearchResult.swift), [QQMusicQrcDecrypter.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Parser/QQMusicQrcDecrypter.swift), [QQMusicQrcParser.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Parser/QQMusicQrcParser.swift), [RegexPattern.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Utilities/RegexPattern.swift) | QQ search/request formats, response models and handling, QRC decryption, line/word timing, and supplied translations |
| `src/AppleMusicLyrics.Infrastructure.Windows/External/NetEase/*.cs` | [NetEase.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Provider/Services/NetEase.swift), [NetEaseKLyricParser.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Parser/NetEaseKLyricParser.swift), [RegexPattern.swift](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/Sources/LyricsService/Utilities/RegexPattern.swift) | NetEase search/request formats, EAPI request envelope, response handling, line/word timing, and supplied translations |

The covered C# files are a modified port, rather than unmodified Swift source. Modifications include integration with this application's provider abstraction and matching rules, cancellable HTTP requests, bounded response processing, process-local caching, and conversion into the application's line/word lyric model. Original word timestamps and supplied translated lines are retained when usable; timing and translations are not generated. NetEase's MD5/AES operations use .NET cryptography; the Swift CryptoSwift dependency is not copied or linked into this project.

The pinned `QQMusicQrcDecrypter.swift` includes `QrcDecoder` and `QrcDecodeHelper` without a separate author, copyright notice, or third-party origin reference. This notice preserves its actual immediate source and the fork's stated repository license. Earlier provenance of that helper has not been independently established, and no additional owner or license is asserted here.

### License

The covered source files, including this project's modifications to them, are made available under the **Mozilla Public License, version 2.0 (MPL-2.0)**. Each covered file carries its MPL notice and upstream reference. The complete license is included in [licenses/MPL-2.0.txt](licenses/MPL-2.0.txt), copied without modification from the [pinned upstream LICENSE](https://github.com/MxIris-LyricsX-Project/LyricsKit/blob/6f071990d0c9c6f48d29284db805990bffb912ed/LICENSE).

Independent files that contain no LyricsKit-derived code remain under the project's MIT license. In particular, the shared provider orchestration, matching and translation-alignment helpers, core lyric models, and WPF word-highlighting/translation UI outside the `QQMusic` and `NetEase` directories are project-authored code.

### Obtaining the corresponding source

App and debugger build/publish outputs include this notice, the full MPL license, and the corresponding adapted C# sources under:

```text
licenses/
  MPL-2.0.txt
  LyricsKit-port/
    QQMusic/*.cs
    NetEase/*.cs
```

These bundled files are the MPL-covered sources used to build that binary and may be obtained and modified under MPL-2.0. The project repository is [BeteixZ/floating-lyrics-wam](https://github.com/BeteixZ/floating-lyrics-wam); a release's matching tag or commit contains the same source under the paths listed above. Distributors must preserve the notices and make the corresponding MPL-covered source, including their modifications, available to recipients. See MPL sections 3.1, 3.2, and 3.4; Mozilla also provides an [official FAQ](https://www.mozilla.org/en-US/MPL/2.0/FAQ/).

## Music services and lyric content

These acknowledgments concern software implementation. LyricsX, LyricsKit, and their contributors do not endorse this application. The software licenses do not grant platform API authorization or rights in returned lyric content. QQ Music and NetEase Cloud Music names identify the services accessed; their undocumented interfaces may change or restrict access. Use and redistribution of content remain subject to the applicable rights and service terms.
