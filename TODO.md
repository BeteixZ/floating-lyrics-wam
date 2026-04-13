# TODO

## Current Focus

Building a production-quality Windows lyrics app that matches LyricsX in visual quality, sync stability, and user experience.

## High Priority

### Visual Polish
- [ ] Implement smooth lyric transition animations
- [ ] Add glow/shadow effects for current line emphasis
- [ ] Improve visual hierarchy between current and context lines
- [ ] Design taskbar-friendly compact mode
- [ ] Add fade-in/fade-out animations for show/hide

### Synchronization Improvements
- [ ] Refine playback clock drift compensation
- [ ] Improve lyric-to-song matching accuracy
- [ ] Handle playback state changes more gracefully (pause/seek/skip)
- [ ] Add per-song sync offset persistence
- [ ] Reduce visual lag during lyric transitions

### Adaptive Layout
- [ ] Implement true responsive font scaling
- [ ] Fix text overflow/truncation issues
- [ ] Improve multi-line layout stability
- [ ] Add proper text measurement for dynamic sizing
- [ ] Handle extremely long lyrics gracefully

## Medium Priority

### User Experience
- [ ] Add keyboard shortcuts for common actions
- [ ] Implement drag-to-adjust sync offset
- [ ] Add "jump to current lyric" when seeking
- [ ] Show loading state when scanning cache
- [ ] Display helpful message when no lyrics found
- [ ] Add first-run setup wizard

### Settings & Configuration
- [ ] Expand settings panel with more options
- [ ] Add preset themes (dark, light, colorful)
- [ ] Allow custom color schemes
- [ ] Add import/export settings
- [ ] Implement settings search/filter

### Performance
- [ ] Optimize cache scanning performance
- [ ] Reduce memory footprint
- [ ] Improve startup time
- [ ] Add lazy loading for large lyric files
- [ ] Profile and optimize rendering pipeline

## Low Priority

### Advanced Features
- [ ] Support for multiple monitor setups
- [ ] Add mini-player controls in overlay
- [ ] Implement lyric search/jump
- [ ] Add romanization support for non-Latin scripts
- [ ] Support custom lyric sources
- [ ] Add lyric editing/correction interface

### Developer Experience
- [ ] Add comprehensive unit test coverage
- [ ] Implement integration tests
- [ ] Add performance benchmarks
- [ ] Improve error logging and diagnostics
- [ ] Create developer documentation

### Packaging & Distribution
- [ ] Create installer (MSI/MSIX)
- [ ] Add auto-update mechanism
- [ ] Implement crash reporting
- [ ] Add telemetry (opt-in)
- [ ] Create portable version

## Technical Debt

### Code Quality
- [ ] Refactor MainWindow.xaml.cs (currently 1152 lines)
- [ ] Extract reusable UI components
- [ ] Improve separation of concerns
- [ ] Add XML documentation comments
- [ ] Standardize error handling patterns

### Architecture
- [ ] Implement proper MVVM pattern
- [ ] Add dependency injection container
- [ ] Create abstraction for settings persistence
- [ ] Improve testability of UI components
- [ ] Add proper logging framework

### Testing
- [ ] Add tests for TTML parser
- [ ] Add tests for synchronization logic
- [ ] Add tests for cache scanner
- [ ] Add UI automation tests
- [ ] Add performance regression tests

## Future Considerations

### Potential Features
- [ ] Plugin system for custom lyric providers
- [ ] Lyrics translation support
- [ ] Karaoke mode with word-level highlighting
- [ ] Lyrics export functionality
- [ ] Social features (share lyrics, etc.)

### Platform Expansion
- [ ] Evaluate feasibility of macOS port
- [ ] Consider support for other music players (Spotify, etc.)
- [ ] Explore web-based remote control interface

## Completed ✓

- [x] C# rewrite with WPF frontend
- [x] Basic floating overlay window
- [x] System tray integration
- [x] TTML parser implementation
- [x] Windows Media Session integration
- [x] Apple Music cache scanner
- [x] Settings persistence (INI format)
- [x] Click-through mode
- [x] Pure mode (transparent background)
- [x] Single-line and multi-line modes
- [x] Basic settings window
- [x] Playback clock with drift compensation
- [x] GitHub Actions CI/CD pipeline
- [x] Single-file executable publishing
- [x] Optimized release build (25MB)

## Notes

- Focus on stability and polish before adding new features
- Prioritize user-facing improvements over internal refactoring
- Keep the app lightweight and responsive
- Maintain compatibility with .NET 10 and Windows 10+
