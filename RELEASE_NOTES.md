# Release Notes

## v1.0.0-rc.2 - Release candidate for 1.0

This is a test version of 1.0. It focuses on reliability: the app keeps working, or tells you what went wrong, when Windows, the settings file or an update misbehaves. It replaces v1.0.0-rc.1.

**Display**

- The bottom of letters such as g, p and y is no longer cut off on lines with word-by-word timing.
- Instrumental tracks whose lyrics file has no lines are treated like tracks without lyrics: the overlay fades out (with **Fade out when lyrics are unavailable** on) instead of showing "Lyrics file has no displayable lines."
- In Pure mode, the lyrics card can be dragged all the way to the top of the screen. It used to stop well below it.
- In Pure mode, applying settings no longer moves the lyrics card.

**Reliability**

- When Windows' media controls stop answering, the overlay no longer freezes. The app keeps retrying in the background and picks up playback again once Windows responds.
- Errors are now written to `%LOCALAPPDATA%\AppleMusicLyrics\logs\errors.log`. The tray menu has **Open log folder**. If the app has to close because of an unexpected error, it says so and shows where the details were saved.
- If the settings file cannot be read (for example while another program has it locked), the app runs on defaults and does not overwrite your file. If your changes cannot be saved, a notification says so.
- Values in `settings.ini` that are out of range or not a number (from a hand edit or a damaged file) fall back to their defaults instead of breaking the overlay.
- Clearing the lyrics cache in Settings no longer blocks the window.

**Updates**

- The updater installs only the release's own package and only after its SHA-256 checksum matches the one GitHub recorded.
- The app hands over to the installer only after the installer confirms it has started and checked the downloaded files. If it cannot start, the update window says so and the current version keeps running.
- If an update was cut off part-way (for example by a power loss), the app shows a notification at the next start that opens the backup of the previous files. `RECOVERY.txt` in the app folder explains how to repair the installation.
- **Check now** in Settings checks in place. When no update is available, the result, the time of the last check and the next automatic check are shown next to the button instead of in a new window. An available update opens the update window, which now stays in front of the lyrics and Settings windows.
- Stable versions are never offered a test version such as this one.

**Installing this test version:** it is not offered by the in-app updater. Exit the app from the tray, download `AppleMusicLyrics-v1.0.0-rc.2-win-x64.zip` from this release, extract it over your app folder replacing all files, and start `AppleMusicLyrics.App.exe`. Settings, saved positions, per-song offsets, and the lyrics cache are kept. When 1.0.0 is published, this version updates to it from the tray as usual.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime.

---

## v0.10.1 - New default lyric font

**Display**

- The default lyric font is now Marcellus, an open-licensed (SIL OFL) font with a similar classical feel. Optima is a commercial font and is no longer included with the app.
- If Optima is installed on your computer and selected in Settings, it is still used. Otherwise the overlay falls back to Marcellus automatically; no setting needs to change.

**Upgrading from 0.9.0 or 0.10.0:** use **Update to v0.10.1** in the tray or **Check for updates…**. Settings, saved positions, per-song offsets, and the lyrics cache are kept.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime.

---

## v0.10.0 - Tighter sync, duets and interludes, and remembered lyric choices

**Timing**

- Lyrics now line up with Apple Music's own lyrics view. The playback clock locks onto the true position about a second after a track starts, a seek, or a resume, where it used to take up to 45 seconds, and it ignores the burst of early reports Apple Music sends while a track starts.
- The default lyrics advance is now 0.90 s, measured frame by frame against Apple Music's own lyrics. **Existing settings keep their saved value; 0.9.0's default was 0.78 s, which shows lines about 0.2 s late. Set Settings → Default lyrics advance to 0.90 s to use the new calibration.**
- When a singer's next line starts while the previous one is still fading, the overlay moves on at once instead of holding the earlier line.
- In an instrumental break longer than 4 seconds, the overlay previews the next line instead of holding the last one.
- In duets, a reply that comes in as the lead's last note fades takes over the line, while a reply sung over the lead is shown alongside it.
- **Current track advance** in Settings saves a calibration for just the playing song when **Remember lyric offset per song** is on.

**Lyrics**

- **Choose lyrics…** can remember your pick for a recording (**Remember this version for this track**). It is reused when that recording plays again, also after a restart and after clearing the lyrics cache. **Use automatic selection** removes it.
- For a reissue such as "Butterfly: 25th Anniversary Expanded Edition" or "E=MC2 (Deluxe Version)", online sources are searched again under the original album name when the reissue's lookup finds no lyrics or lacks word timing or translations. Live, remix, acoustic, "Taylor's Version" and clean albums are never treated as the original.
- A reissue's own Apple Music lyrics that are cached only later, when its lyrics view is opened, replace the online lyrics shown meanwhile.
- When the catalog cannot be reached, a lyrics file Apple Music downloads for the playing track (and only that track) is used, so songs with one-word titles such as "360" are no longer left without lyrics.
- Synchronized online lyrics, including LRCLIB, now take precedence over Apple Music lyrics that have no timestamps.
- Backing vocals that some sources write as split-second fragments between the lead lines are shown in one smaller row under the lead line, as Apple Music shows backing vocals, instead of flickering past as lines of their own.
- Japanese romanization reads the Simplified Chinese characters some sources use in place of kanji (离 for 離, 见 for 見) as the kanji.
- The online lyrics cache is on by default for new installs. An existing setting is kept.

**Display**

- As in Apple Music, each sung word floats up slightly behind a soft-edged highlight, and a held word rises, swells and glows letter by letter as the highlight passes through it.
- The line crossfade runs only when the line actually changes.
- On a fresh install the overlay starts at the bottom centre of the primary monitor instead of the top-left corner. Saved positions from earlier versions are kept.
- Settings, Diagnostics and the update window use the Windows 11 Fluent style and follow the system's light or dark mode.

**Updates**

- A stalled download is retried once before the update gives up.
- Updates from 0.10.0 onward install file by file. If a file cannot be replaced, or the new version exits right after starting, the previous files are restored and the old version is restarted. (The update to 0.10.0 itself still runs 0.9.0's installer.)

**Upgrading from 0.9.0:** use **Update to v0.10.0** in the tray or **Check for updates…**. Settings, saved positions, per-song offsets, and the lyrics cache are kept.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime.

---

## v0.9.0 - In-app updates

**Updates**

- The app now updates itself from GitHub Releases. It checks for a new version 20 seconds after startup and once a day, shows a Windows notification and an **Update to vX…** item in the tray, and opens a window with the release notes.
- **Update and restart** downloads the package, verifies its size and the SHA-256 checksum GitHub publishes, installs it over the current folder, and restarts. Settings, saved positions, and the lyrics cache are kept.
- **Skip this version** stays quiet about that release until a newer one is out; **Check for updates…** in the tray works any time and still offers a skipped version.
- The automatic check can be turned off in Settings → Advanced → **Check for updates automatically**. It sends only the app name and version to GitHub.
- If the app's folder cannot be written to, the window points to the release page instead of installing.

**Other changes**

- Release pages on GitHub now show these release notes instead of an auto-generated commit list.
- The README is available in English, Simplified Chinese, and Traditional Chinese, with a new demo and FAQ.

**Upgrading from 0.8.0 or earlier:** these versions cannot update themselves. Exit the running app from the tray, extract this release over the old folder (or into a new one), and start it. Updates after 0.9.0 are installed from inside the app.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime.

---

## v0.8.0 - Richer lyric sources, smoother overlay, and Apple-style word emphasis

**Lyric sources**

- Prefers word timing and translations when choosing lyrics. Apple Music lyrics that already have both are used as is; otherwise NetEase Cloud Music, then QQ Music, can replace them when they cover clearly more of the song with the missing features and are the same lyrics on the same timeline, even when they split or word lines differently. Apple Music's own lyrics (with gaps filled from other sources) come next, and LRCLIB last. With word highlighting and translations both off, Apple Music's lyrics stay primary. **This reverses v0.7.0, where QQ and NetEase could only fill features on Apple's lyrics.**
- A feature now counts only when it covers most of the song, so lyrics with a few patched-in translated lines no longer stop the search for a better source.
- NetEase and QQ lyrics that replace Apple's are re-timed onto Apple's line starts: each line starts where Apple's does, lines the provider splits are interpolated, and the provider's own syllable rhythm is kept. This fixes QQ Music lines that started noticeably early or late.
- Online lookup now tries NetEase Cloud Music, QQ Music, then LRCLIB.
- Adds right-click options to search again or preview and select lyrics for the current track.
- Matches Apple Music's cached lyrics to the catalog track even when several releases share a lyric asset. Skips external recordings whose known duration is outside the matching window, including older cached results.
- Removes composer, lyricist, and other provider credit rows from the original display timeline as well as translations, including existing cached lyrics. Covers bilingual and compound production/instrument credits, translation attribution such as "以下歌词翻译由文曲大模型提供", promotional links, and provider placeholder notices. Hides standalone section markers and track-verified opening/closing title headers and performer labels. Preserves sung producer tags, valid translations beside metadata in multiline text, lyric timestamps, and word timing.

**Display**

- Held words swell slightly and glow while they are sung, like Apple Music. A word near the start of a line must be held longer to qualify (about 1.8 s) than one ending the phrase (about 0.8 s), and longer holds swell more.
- Reduces visible lyric lag: lines switch about 0.2 s before they are sung so the crossfade has finished in time, and the word highlight leads by the display's render latency.
- Makes the line-change crossfade visible: the outgoing line fades out quickly before the next one fades in, so the two no longer overlap at full strength.
- Shows overlapping parts from different Apple Music vocalists as two full-size lyric lines, each with its own highlighting and translation.
- Displays Apple Music background vocals on smaller lines beneath the lead lyric, with their own word-by-word timing when available.
- Displays Apple Music's keyed romanization tracks and generates offline romaji when Japanese lyrics have no supplied reading.

**Pure Mode and window sizing**

- Stops the Pure Mode card from jumping between lines: the card is drawn inside a larger transparent window and animates there, so the window is resized only when a card no longer fits. Hover fading follows the visible card.
- Looks at the next couple of lines before shrinking: the overlay keeps its size only when an upcoming line (such as a returning duet part) needs it again, and otherwise shrinks right away. Growth is still immediate.
- Fixes long wrapped lines in Pure Mode cutting off the translation below them (for example in "Rap God"): card height is now measured at the same wrap width the lyrics are drawn with, and never wider than the screen.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime. Exit the previous tray instance before launching the new executable.

---

## v0.7.0 - Accurate timing, Apple-first lyrics, and per-song offsets

- Fixes lyrics running late and drifting. Apple Music now publishes whole-second positions on a timer slightly slower than one second, which made the previous estimate lag by 0.2–1.0s in a repeating cycle of about 45 seconds. Playback position now uses each report's publish time and is accurate to a few milliseconds once that cycle has been observed. **Lyrics lead time values tuned on earlier versions will now show lyrics about 0.6s earlier and should be re-calibrated.**
- Keeps Apple Music's own lyrics as the timeline whenever they are cached, including Apple's word timing and translations from the current syllable-lyrics format. QQ Music and NetEase only fill features Apple lacks, never replacing its text or timing, and now attach to many more of Apple's lines: lines are matched by text and start time, a consistently shifted source timeline is corrected first, and imported syllables start at Apple's line onset.
- Removes credit, placeholder, and copyright rows (such as "Lyrics by: X", "作词：X", "//", and translation copyright notices) from translations.
- Adds optional romanization from Apple Music, shown between the original line and the translation.
- Remembers the lyric offset for each song across restarts.
- Recovers from a failed media-session request, an unreadable settings file, or an instance that crashed while holding the single-instance lock.
- Improves matching with LRCLIB artist checks, regional catalog id verification, and more robust QQ Music and NetEase lookups; speeds up font fitting and Pure Mode window sizing.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime. Exit the previous tray instance before launching the new executable.

---

## v0.6.0 - Word highlighting, translations, and multilingual settings

- Adds QQ Music and NetEase Cloud Music lyric sources based on the maintained LyricsKit fork, with QRC/YRC word timing and source-provided translations.
- Checks for missing enabled lyric features in the background while keeping existing lyrics visible; improves title matching for explicit featured-artist credits while preserving version checks.
- Adds word-by-word highlighting and translated lines, with separate original Latin/Chinese fonts and independent translation font and size.
- Organizes Settings into five tabs with live previews and fewer explanatory labels.
- Follows the Windows display language by default, includes English and Simplified/Traditional Chinese, and supports drop-in JSON language packs.
- Adds a shared, optional lyric cache that retains translations and word timing, with a configurable size limit, current usage, and one-click clearing.
- Replaces the large overlay debug panel with a compact optional summary; full query, translation, timing, and failure details open in a separate diagnostics window.
- Reduces idle rendering and cursor-monitoring overhead, backs off repeated empty cache scans, and reopens matching once when the player corrects a track's duration.
- Preserves existing settings and includes LyricsKit attribution, the MPL-2.0 license, and the corresponding adapted provider sources in the release package.

Requirements remain Windows 10 version 19041 or later and the x64 .NET 10 Desktop Runtime. Exit the previous tray instance before launching the new executable. Online lyric interfaces may change; translations and word timing depend on the selected source.

---

## v0.5.0 - More reliable lyrics, optional caching, and diagnostic tools

- Tightens local lyric content matching and catalog ID handling to reduce incorrect song matches.
- Improves LRCLIB selection to prefer synchronized lyrics, with an approximate timeline fallback for plain lyrics.
- Adds opt-in persistent LRCLIB caching in Settings; cached no-lyrics results expire after seven days.
- Makes Click through independently controllable in both Normal mode and Pure Mode.
- Separates runtime orchestration into an Application project and extracts polling, tray, placement, and presentation components.
- Improves cache scanning and adds an overnight diagnostic monitor with online lyric comparison and report logging.
- Expands regression coverage and adds formatting verification and coverage artifacts to CI.

---

## v0.4.0 - Lyric matching precision, SMTC parsing, and Pure Mode rendering polish

This release significantly hardens lyric matching accuracy, fixes Windows SMTC artist/album concatenation issues, and resolves overlay artifacting in Pure Mode.

### Highlights

- **Bundled Optima Default Font**: Includes the elegant Optima font directly within the application package as the default typography, ensuring consistent cross-system aesthetics even if Optima is not installed on the host system.
- **Modernized Default Settings**: Fresh installations now feature tuned defaults aligned with Apple Music aesthetics: Pure Mode enabled, Two-Line layout, pure white text with soft cyan (`#C5FEFE`) ambient glow, and calibrated hover opacity.
- **SMTC Metadata Parsing**: Automatically splits combined `"Artist — Album"` strings produced by Apple Music for Windows into distinct artist and album components, dramatically improving iTunes catalog search and LRCLIB online resolution rates.
- **Lyric Content Verification (`HasContentMatch`)**: Inspects lyric lines for verified song title phrases and censored lyric patterns (e.g. `**** now`), strictly within single lines rather than accumulating scattered common words across unrelated lines.
- **Prioritized Content Matches**: Candidates that verifiably sing the song title are prioritized over unrelated songs that merely happen to have identical or close track lengths.
- **Instrumental Outro Tolerance**: Safely accommodates songs with instrumental outros (up to 45s shorter lyric duration) only when the lyric content is verified against the song title.
- **Confidence Policy Hardening**: Local duration-only candidates without catalog confirmation or lyric title verification are held back as `Low` confidence, allowing external providers (LRCLIB) to resolve the exact song.
- **Pure Mode Rendering Cleanup**: Properly clears frozen snapshot brushes to eliminate ghost/double-rendered overlays when changing modes or transitioning between lyric lines.
- **Safety Geometry Clamping**: Hardened monitor bounds clamping for Pure Mode positioning across heterogeneous display topologies.

---

## v0.3.0 - Reliability, display, and rendering update

This release focuses on accurate lyric selection and predictable overlay behavior across Windows display configurations.

### Highlights

- Selects Apple Music media sessions by default; non-Apple fallback is explicit opt-in.
- Uses confidence-based cache matching, Apple catalog verification, and optional LRCLIB fallback.
- Rejects low-confidence candidates by default to avoid showing another song's lyrics.
- Adds cancellable, track-scoped, bounded retries for online lookups.
- Adds Per-Monitor V2 DPI awareness and monitor-relative placement for normal and Pure Mode windows.
- Responds to DPI and display-topology changes without persisting intermediate drag positions.
- Uses a content-sized Pure Mode window instead of a large transparent hit-test area.
- Adds configurable Pure Mode drag opacity.
- Moves local lyric-line switching and visual animation work to WPF composition frames while keeping media, disk, and network I/O on the low-frequency polling path.
- Displays measured composition callback FPS and polling rate in the debug panel.
- Hardens settings persistence with invariant formatting, malformed-value fallback, duplicate handling, and replacement-based writes.

### Network behavior

Apple catalog verification and LRCLIB fallback are enabled by default and can be disabled in Settings for fully offline operation. Provider changes require a restart. See the README for the metadata sent to each service.

### Requirements

- Windows 10 version 19041 or later, or Windows 11
- Apple Music for Windows
- x64 .NET 10 Desktop Runtime

### Upgrade notes

- Settings and catalog cache data now live under `%LOCALAPPDATA%\AppleMusicLyrics`.
- Existing `settings.ini` and `catalog-cache.json` files beside the executable are copied to the new location on first use when possible.
- The playback clock no longer relies on a fixed sub-second guess. Existing manually calibrated lead-time values may need adjustment; the fresh-install default is `0.78s`.

### Validation

Automated tests cover matching policy, media-session selection, retry/cancellation behavior, playback projection, frame-rate measurement, opacity priority, monitor placement, DPI message coordination, and Pure Mode bounds geometry. Mixed-DPI movement and layered-window resize behavior should also be smoke-tested on real hardware.

Please report problems at [GitHub Issues](https://github.com/BeteixZ/floating-lyrics-wam/issues).

---

## v0.2.0 - Overlay fluidity, visual refresh, and cache accuracy

- **Overlay fluidity**: Flash-free Pure Mode using fixed-size window clip reveal, continuous lyric crossfades via frozen snapshot rendering, and smooth multi-line transitions.
- **Fade transitions**: Unified opacity controller to fade overlay out when paused or when no lyrics are available.
- **Cache accuracy**: Stricter duration matching to prevent incorrect lyrics from prefetched or stale cache entries.
- **Modern Settings UI**: Redesigned settings window with toggle switches, accent sliders, rounded controls, and customizable pause fade.
- **App Icon**: Dedicated tray and application icon assets derived from vector artwork.

---

## v0.1.0 - Initial public release

- Floating synchronized lyric overlay
- Configurable appearance and display modes
- Click-through and system tray controls
- Persistent settings and window position

This project is inspired by [LyricsX](https://github.com/ddddxxx/LyricsX) for macOS.
