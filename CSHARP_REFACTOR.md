# Apple Music Lyrics C# Refactor Plan

## Goal

Migrate the app from the current Python prototype toward a production-oriented Windows app that is closer to LyricsX in:

- visual quality
- synchronization stability
- packaging size
- Windows-native integration
- long-term maintainability

The recommended target stack is:

- backend/core: `C#`
- frontend: `WPF`
- target runtime: `.NET 10`

This is a Windows-only app, so we should optimize for Windows quality instead of cross-platform flexibility.

## Why C# End-to-End

For this project, moving only the frontend to C# would help, but moving both frontend and backend to C# is cleaner.

Reasons:

- one runtime instead of `Python + Qt + native bindings`
- tighter control over timers, threads, rendering, and media session polling
- easier packaging and Windows deployment
- better long-running stability for a resident desktop utility
- simpler debugging when UI and playback logic live in the same language and process

Python was a good prototype vehicle. C# is a better product vehicle.

## Recommendation

Use this architecture:

- `AppleMusicLyrics.App`
  WPF desktop app, tray icon, settings, windows, animations, rendering
- `AppleMusicLyrics.Core`
  pure business logic: models, TTML parsing, synchronization, playback clock, layout state
- `AppleMusicLyrics.Infrastructure.Windows`
  Windows-specific integration: media session, cache discovery, file watching, click-through window helpers
- `AppleMusicLyrics.Tests`
  unit tests for parser, synchronizer, playback clock, config loading

Optional later:

- `AppleMusicLyrics.Benchmarks`
  parser and sync timing experiments

## Proposed Solution Layout

```text
src/
  AppleMusicLyrics.App/
    App.xaml
    App.xaml.cs
    MainWindow.xaml
    MainWindow.xaml.cs
    Tray/
    Settings/
    Rendering/
    ViewModels/
  AppleMusicLyrics.Core/
    Models/
    Parsing/
    Sync/
    Configuration/
    Abstractions/
  AppleMusicLyrics.Infrastructure.Windows/
    Media/
    Cache/
    Interop/
    Configuration/
  AppleMusicLyrics.Tests/
```

## Module Mapping

Map the current Python modules like this:

### Core logic

- `apple_music_lyrics/models.py`
  -> `AppleMusicLyrics.Core/Models/`
- `apple_music_lyrics/ttml_parser.py`
  -> `AppleMusicLyrics.Core/Parsing/TtmlLyricsParser.cs`
- `apple_music_lyrics/synchronizer.py`
  -> `AppleMusicLyrics.Core/Sync/LyricsSynchronizer.cs`
- `apple_music_lyrics/playback_clock.py`
  -> `AppleMusicLyrics.Core/Sync/PlaybackClock.cs`
- `apple_music_lyrics/config.py`
  -> `AppleMusicLyrics.Core/Configuration/AppSettings.cs`

### Windows integration

- `apple_music_lyrics/cache_watcher.py`
  -> `AppleMusicLyrics.Infrastructure.Windows/Cache/AppleMusicCacheScanner.cs`
- `apple_music_lyrics/player_session.py`
  -> `AppleMusicLyrics.Infrastructure.Windows/Media/GlobalMediaSessionProvider.cs`
- `apple_music_lyrics/config_store.py`
  -> `AppleMusicLyrics.Infrastructure.Windows/Configuration/IniSettingsStore.cs`

### App orchestration

- `apple_music_lyrics/runtime.py`
  -> `AppleMusicLyrics.App/Services/LyricsRuntimeService.cs`
- `apple_music_lyrics/main.py`
  -> `App.xaml.cs` + startup composition

### UI

- `apple_music_lyrics/ui_overlay.py`
  -> split across:
  - `MainWindow`
  - `OverlayViewModel`
  - `SettingsWindow`
  - `TrayIconService`
  - `OverlayRenderer`
  - `WindowInteropService`

## UI Technology Choice

Use `WPF`, not WinUI 3, for the first serious rewrite.

Why:

- better fit for transparent overlay windows
- easier desktop utility behavior
- mature text and animation model
- easier click-through and custom chrome handling
- good enough visual fidelity for LyricsX-like glow and layered text

WinUI 3 is still a reasonable option, but for this specific overlay-style app, WPF is the faster path.

## Rendering Direction

To get closer to LyricsX, the WPF layer should support:

- single-line mode
- multi-line mode
- taskbar mode
- pure mode
- custom font family
- max font size with automatic shrink
- glow and soft shadow
- current line vs context line hierarchy
- smooth slide/fade transitions

Suggested rendering approach:

- current line rendered with layered `TextBlock` strategy
  one crisp foreground layer + one glow/shadow layer
- context lines rendered separately with lower opacity and smaller size
- adaptive font sizing based on real measured text, not heuristics
- use `FormattedText` or measured `TextBlock` layout passes for precise fit

## Synchronization Strategy

This part should be redesigned, not just translated.

### Current problem

The current prototype already works, but LyricsX-level feel needs tighter control over:

- media session sampling
- quantized timeline values
- extrapolated playback position
- song switching boundaries
- visual switch timing

### C# design

- poll media session with a dedicated background service
- keep a `Stopwatch`-based extrapolated playback clock
- separate:
  - raw system position
  - estimated playback position
  - final lyric selection position
- maintain a lightweight session state machine:
  - no session
  - loading
  - playing
  - paused
  - seeking
  - switching track
- keep UI refresh independent from polling refresh

Recommended timing model:

- media polling: `100-200ms`
- UI rendering tick: `16-33ms`
- lyric selection based on estimated position, not raw polled value

## Configuration Strategy

Keep settings persistence simple and compatible with the current user expectation:

- continue using `settings.ini`

Store:

- window position and size
- mode flags
- transparency
- font family
- max font size
- lyric colors
- click-through state
- taskbar mode settings
- manual offset

Implementation:

- `ISettingsStore`
- `IniSettingsStore`
- `AppSettings` as strongly typed model

## Packaging Strategy

Target a more professional Windows distribution story.

Recommended first shipping mode:

- `framework-dependent` WPF build

Why:

- smaller package
- good enough for internal or enthusiast distribution
- better chance to stay close to the `50MB` goal

Later options:

- self-contained single-folder release
- MSIX installer
- signed installer

Important constraint:

- `10MB` class size is not a realistic target for a polished `WPF` app unless we aggressively optimize and rely on system runtime presence.
- `50MB` is much more realistic.

## Migration Plan

### Phase 1: Freeze Python prototype

Goal:

- stop expanding Python UI features
- keep Python version as behavior reference

Tasks:

- keep current Python app runnable
- document current feature set and edge cases
- use it as reference during C# rewrite

### Phase 2: Build C# core

Goal:

- port non-UI logic first

Tasks:

- create C# solution and projects
- port models
- port TTML parser
- port synchronizer
- port playback clock
- port INI config model
- port tests

Acceptance:

- parser and synchronizer tests pass
- sample lyric JSON files parse identically to Python version

### Phase 3: Build Windows integration layer

Goal:

- get real runtime data in C#

Tasks:

- implement Apple Music cache discovery
- implement media session provider
- implement runtime service
- implement settings store

Acceptance:

- can detect Apple Music session
- can load latest cache file
- can produce current lyric line without UI

### Phase 4: Build minimal WPF shell

Goal:

- replicate current feature floor

Tasks:

- transparent overlay window
- tray icon
- click-through
- settings window
- single-line and multi-line display
- resize and move persistence

Acceptance:

- reaches current Python GUI feature parity

### Phase 5: LyricsX-style polish

Goal:

- move beyond parity into product quality

Tasks:

- glow rendering
- better typography controls
- adaptive font system
- taskbar mode
- refined transitions
- stronger synchronization tuning

Acceptance:

- visual quality clearly exceeds current Python version
- taskbar placement is usable
- lyric switches feel near-instant

## First Build Target

The first C# milestone should not be "full rewrite".

It should be:

- C# core
- C# media/cache integration
- very minimal WPF overlay

In other words:

`play song -> detect lyrics -> show current line`

Once that works, we can iterate the visuals aggressively.

## Suggested Immediate Next Step

Start with solution scaffolding:

1. create `.NET 10` solution
2. create `Core`, `Infrastructure.Windows`, `App`, `Tests`
3. port models and parser first
4. add snapshot-based runtime service
5. only then start rebuilding the overlay

## Decision Summary

If the product goal is:

- close to LyricsX visual quality
- better sync
- smaller release size
- stronger Windows-native experience

Then the best next architecture is:

- `C# backend`
- `WPF frontend`
- `settings.ini`
- Python version retained only as migration reference
