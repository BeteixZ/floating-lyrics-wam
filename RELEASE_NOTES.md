# Release Notes

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
