# Release Notes

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
