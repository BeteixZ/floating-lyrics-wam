# Lyric sources, matching, and privacy

Where lyrics come from, how the app chooses between them, and what is sent over the network. Back to the [README](../README.md).

## How lyric selection works

1. Read Apple Music playback metadata through Windows System Media Transport Controls.
2. Scan Apple Music's local cache, including legacy `ttmlLyrics` and current localized `syllable-lyrics` responses. Parse native line/word timestamps and keyed translations and readings. Prefer richer native versions of the same Apple song ID without treating duplicate cache versions as different tracks.
3. Accept a close, unambiguous local match or verify candidates against Apple's catalog. If duration matching finds nothing, also look up cached lyrics by the verified Apple song ID: a TTML timeline can end at the last lyric, before the recording finishes. If keyword search omits the release, verify cached IDs through Apple's lookup endpoint, requiring exact title/artist agreement and a matching audio duration.
4. Display a verified local match immediately. If no verified local lyrics exist and online lyrics are enabled, try NetEase Cloud Music, QQ Music, and LRCLIB in that order, showing a usable synchronized result as soon as it is available. Apply title, artist, and available duration checks to external candidates. Explicit featured-artist credits such as `(feat. Twista)` can be omitted by a source's title; matching normalizes those credits while rejecting conflicting guest credits and different live/remix/intro variants.
5. If highlighting or translations are enabled but Apple's result does not cover most of the song with them, check online sources in the background. Sources are compared by how much of the song they cover with the enabled features; close results follow this priority: Apple Music with every enabled feature, NetEase Cloud Music, QQ Music, Apple Music's own lyrics (with gaps filled from other sources on Apple's timeline), and LRCLIB. A NetEase or QQ result replaces Apple's only when it is the same lyrics on the same timeline, even if it splits or words lines differently; it is then re-timed so each line starts where Apple's does, keeping the source's own syllable rhythm. With highlighting and translations both off, Apple's own lyrics stay primary. Without an Apple result, a richer external source may replace another external result without losing enabled features. Word timestamps and translations are used only when supplied by the source.
6. Use LRCLIB plain lyrics if no synchronized external result is available, with an approximate timeline.
7. Keep media/cache/network polling low-frequency while projecting playback and switching lines locally on visual frames. Recheck only the selected Apple catalog ID for later native feature upgrades, so opening Apple's lyrics panel after the initial match can reveal native words/translations without allowing the next song's prefetch to take over.

When romanization is enabled, supplied readings appear under the original lyrics. Japanese lyrics without a supplied reading use the bundled dictionary to generate romaji offline. Unusual readings chosen by the singer can differ from the dictionary reading.

A missing lyric is preferred over displaying a low-confidence wrong lyric unless the user explicitly enables low-confidence candidates.

## Network and privacy

Apple catalog verification, external lyric lookup, and the automatic update check are enabled by default and can each be disabled in Settings for fully offline operation.

- **GitHub Releases (updates)**: at startup and once a day, the app asks the GitHub API for this repository's latest release. The request carries only the app name and version as its User-Agent; no song or personal data is sent. When you choose to update, the release package is downloaded from GitHub and checked against the SHA-256 digest GitHub publishes for it before anything is installed. Turn off **Check for updates automatically** in the Advanced tab to stop the scheduled check; **Check for updates…** in the tray still works on demand.
- **Apple iTunes Search API**: when local candidates are ambiguous, the app sends title and artist to verify the Apple catalog song ID.
- **LRCLIB**: the app sends title, artist, album, and duration for exact lookup, with title/artist search fallback. Synchronized lyrics are used when available; plain-only lyrics are displayed with an approximate evenly projected timeline and identified as `lrclib-plain` in diagnostics.
- **NetEase Cloud Music**: the app sends title and artist to search NetEase, then requests lyrics by the matched song ID. It is queried first for missing lyrics or for enabled word timing/translations missing from Apple's result. When artist names differ across languages, it verifies the candidate's artist ID and aliases through NetEase artist metadata before accepting the recording. YRC and older klyric word timing is preserved, with LRC fallback and supplied translated lyrics.
- **QQ Music**: the app sends title and artist to search QQ Music, then requests lyrics by the matched song ID when NetEase has no usable lyrics or lacks an enabled feature. QRC line and word timing is preserved, along with supplied translated lyrics.

The QQ and NetEase integrations use the request formats from the maintained LyricsKit fork linked below. They require no developer API key or account login. Their endpoints are undocumented platform interfaces and can change or reject requests. Server-issued anonymous cookies may be used in memory; the app does not read browser or player account cookies.

Requests run in cancellable background operations with bounded timeouts. When disk retention is enabled, successful results from LRCLIB, QQ, and NetEase share `%LOCALAPPDATA%\AppleMusicLyrics\external-lyrics-cache`; the size limit applies to these app-owned lyric entries, separately from Apple's own cache and the catalog lookup cache. Legacy `lrclib-cache.json` files are left untouched and are no longer written by the app. The app does not upload lyric cache files. Advanced storefront and timeout values remain available in `%LOCALAPPDATA%\AppleMusicLyrics\settings.ini`.
