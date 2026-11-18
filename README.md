<div align="center">

<img src="icon.svg" width="112" alt="Apple Music Lyrics icon">

# Apple Music Lyrics

**Synchronized, word-by-word floating lyrics for Apple Music on Windows.**

[![Latest release](https://img.shields.io/github/v/release/BeteixZ/floating-lyrics-wam?label=release&color=ff2f5f)](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/BeteixZ/floating-lyrics-wam/total?color=ff4f8b)](https://github.com/BeteixZ/floating-lyrics-wam/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/BeteixZ/floating-lyrics-wam/build.yml?branch=main&label=build)](https://github.com/BeteixZ/floating-lyrics-wam/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

**English** · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

[**Download**](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest) · [Features](#features) · [Quick start](#quick-start) · [FAQ](#faq) · [Release notes](RELEASE_NOTES.md)

<img src=".github/assets/demo.gif" alt="Floating lyrics overlay showing a synchronized line with its translation" width="720">

</div>

Apple Music for Windows has no desktop lyrics. Apple Music Lyrics adds an always-on-top overlay that follows playback: it reads the lyrics Apple Music has already cached, highlights them word by word, shows translations and romanization, and fills the gaps from NetEase Cloud Music, QQ Music, and LRCLIB when Apple's lyrics are missing or incomplete.

> [!NOTE]
> The project is in active development (0.x). Features and settings may still change between releases.

## Features

**Lyrics**

- Reads Apple Music's own lyric cache, including word timing, translations, romanization, duet parts, and background vocals
- Optional online lyrics from NetEase Cloud Music, QQ Music, and LRCLIB, preferring whichever source covers the song with word timing and translations
- Online lyrics are re-timed onto Apple's line starts when Apple has the song, fixing providers whose lines start early or late
- Strict matching with Apple catalog verification: no lyrics is preferred over the wrong lyrics
- Right-click to search again or pick a different lyric version for the current song

**Display**

- Word-by-word highlighting; held words swell and glow while they are sung, like Apple Music
- Smooth line transitions that switch slightly ahead of the vocal so the new line is readable on time
- Duets shown as two full-size lines, each with its own highlighting and translation
- Romanization below the original line, with offline romaji for Japanese lyrics that have none
- Single-line, two-line, and context layouts

**Window**

- Pure Mode: a borderless card sized to the current lyrics, resizing smoothly and looking ahead before it shrinks
- Click-through, hover fading, and automatic hiding when paused
- Per-monitor DPI awareness and independent positions for normal and Pure Mode

**Customization and privacy**

- Fonts (separately for Latin, Chinese, and translations), colors, glow, opacity, and timing offsets, with per-song offsets
- English, Simplified Chinese, and Traditional Chinese interface; more languages via [JSON language packs](docs/localization.md)
- Works fully offline when online features are disabled; no accounts, logins, or API keys

## Quick start

**Requirements:** Windows 10 version 19041 or later (or Windows 11), Apple Music for Windows from the Microsoft Store, and the x64 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Download `AppleMusicLyrics-vX.Y.Z-win-x64.zip` from the [latest release](https://github.com/BeteixZ/floating-lyrics-wam/releases/latest).
2. Extract it anywhere and run `AppleMusicLyrics.App.exe`.
3. Play a song in Apple Music. The lyrics appear in the overlay, and the app lives in the system tray.

When upgrading, exit the previous version from the tray first. Settings are kept in `%LOCALAPPDATA%\AppleMusicLyrics`.

## Using the overlay

| Action | How |
| --- | --- |
| Show/hide, Pure Mode, single-/two-line mode, click-through, Settings | Tray icon menu |
| Move the overlay | Drag it (turn off **Click through** first) |
| Search again or choose another lyric version | Right-click the lyrics |
| Lyrics too early or late | **Settings → Lyrics → Lyrics advance**, optionally **Remember lyric offset per song** |
| Turn word highlighting, translations, or romanization on/off | **Settings → Lyrics** |
| Disable all network access | **Settings → Advanced** |

Every setting is described in [Settings and usage](docs/settings.md).

## Where lyrics come from

When a song plays, the app picks the best lyrics it can find, in this order of preference:

| Priority | Source | Used when |
| :---: | --- | --- |
| 1 | Apple Music (cached) | Apple has the enabled features (word timing, translations) for most of the song |
| 2 | NetEase Cloud Music | It covers clearly more of the song with those features, and is the same lyrics |
| 3 | QQ Music | Same as NetEase, if NetEase has no better result |
| 4 | Apple Music (cached) | Otherwise; missing features may be filled in from other sources on Apple's timeline |
| 5 | LRCLIB | Nothing above is available |

With word highlighting and translations both off, Apple's own lyrics are always used when present. Lyrics from NetEase or QQ only replace Apple's when they are verifiably the same lyrics, and are then aligned to Apple's line timing. The full rules, and exactly what each service receives, are in [Lyric sources, matching, and privacy](docs/lyric-sources.md).

## FAQ

<details>
<summary><b>No lyrics appear for a song.</b></summary>

The app first looks for lyrics Apple Music has cached. If none match and online lyrics are enabled, it searches NetEase, QQ Music, and LRCLIB. A song can still have no lyrics when none of these sources has it, or when the match is too uncertain to show. Try **Search lyrics again** from the right-click menu, or **Choose lyrics…** to pick a candidate yourself. **Allow low-confidence matches** in Advanced settings trades accuracy for coverage.
</details>

<details>
<summary><b>The lyrics are slightly early or late.</b></summary>

Adjust **Lyrics advance** in the Lyrics tab. It covers your audio output latency and personal preference. Turn on **Remember lyric offset per song** to keep a correction for individual songs.
</details>

<details>
<summary><b>There is no translation or word-by-word highlighting.</b></summary>

Both depend on what the sources supply; the app never generates translations or invents word timing. Make sure **Word highlighting** and **Show lyric translations** are on, and that online lyrics are enabled so the app can look for a source that has them.
</details>

<details>
<summary><b>Windows SmartScreen warns about the app.</b></summary>

Release builds are not code-signed, so Windows may show "Windows protected your PC" for a newly downloaded version. Choose **More info → Run anyway** if you downloaded it from this repository's Releases page. The source and the build workflow are public.
</details>

<details>
<summary><b>The app reacts to another player or a browser.</b></summary>

By default it only follows Apple Music's media session. **Allow media sessions outside Apple Music** (Advanced settings) lets other players in, and is off by default for this reason.
</details>

<details>
<summary><b>Does it work offline? What does it send online?</b></summary>

With online features disabled, it only reads Apple Music's local cache and sends nothing. With them enabled, it sends the song title and artist (and, for some services, album and duration) to Apple's iTunes Search API and the enabled lyric services. Details: [Network and privacy](docs/lyric-sources.md#network-and-privacy).
</details>

## Known limitations

- Windows only, and Apple Music must be running for playback tracking.
- NetEase Cloud Music and QQ Music use undocumented interfaces that may change, be region-restricted, or be rate-limited.
- Word timing and translations depend on each source; when missing or mismatched, the app falls back to plain line display.
- Layered transparent windows can show driver-specific edge flicker while Pure Mode resizes.

## Building from source

```powershell
dotnet build AppleMusicLyrics.sln -c Release
dotnet test AppleMusicLyrics.sln -c Release --no-build
./build.ps1   # publishes the app to artifacts/publish/
```

Prerequisites, the publish layout, and the project structure are described in [Development](docs/development.md).

## Contributing

Issues and pull requests are welcome. When reporting a lyrics problem, include the song, artist, and the diagnostic report (tray → **Show diagnostic summary**, or **View diagnostics** for the full report). Keep matching changes accuracy-first and avoid per-frame media, disk, or network I/O.

## License

Project-authored code is licensed under the [MIT License](LICENSE). The QQ Music and NetEase Cloud Music port includes files derived from LyricsKit under the [Mozilla Public License 2.0](licenses/MPL-2.0.txt). See [Third-party notices](THIRD_PARTY_NOTICES.md) for the covered files, fixed upstream revision, credits, and source availability instructions.

Software licenses cover the code. They do not grant authorization to use a music service's private interfaces or rights to reproduce, redistribute, or commercially use returned lyrics.

## Acknowledgments

Inspired by [ddddxxx's LyricsX](https://github.com/ddddxxx/LyricsX) for macOS. The QQ Music and NetEase Cloud Music integrations are ported from [MxIris-LyricsX-Project's maintained LyricsKit fork](https://github.com/MxIris-LyricsX-Project/LyricsKit), building on [ddddxxx's LyricsKit](https://github.com/ddddxxx/LyricsKit). The selected baseline is LyricsKit 1.8.3, commit [`6f071990d0c9c6f48d29284db805990bffb912ed`](https://github.com/MxIris-LyricsX-Project/LyricsKit/tree/6f071990d0c9c6f48d29284db805990bffb912ed), used by the maintained LyricsX v1.8.9 release. Credits and license details are preserved in [Third-party notices](THIRD_PARTY_NOTICES.md).
