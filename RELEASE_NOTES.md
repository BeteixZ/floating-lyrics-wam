# Release Notes

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
