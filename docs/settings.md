# Settings and usage

How to control the overlay and what each setting does. Back to the [README](../README.md).

## Usage and settings

Use the tray icon to show/hide the overlay, open Settings, switch display modes, or toggle click-through behavior. In both Normal mode and Pure Mode, enabling Click through lets mouse input pass through to the window underneath, including while hovering. Disable Click through to interact with and drag the visible card. Normal mode and Pure Mode retain independent positions.

Settings are organized into five tabs, with Apply, Cancel, and Save available on every tab:

| Tab | Settings |
| --- | --- |
| Appearance | Original Latin/Chinese fonts, independent translation font and size, previews, colors, and glow |
| Lyrics | Word highlighting, translations, romanization, timing offset, per-song offsets, and native timing correction |
| Window | Layout, Pure Mode, click-through, and window/background opacity |
| Interaction | Automatic hiding, paused fading, and hover behavior |
| Advanced | Online sources, caching, compatibility, matching, and diagnostics |

Chinese glyphs and Chinese punctuation use the selected Chinese font; Latin text retains its own font. Font changes can be checked in the mixed-text preview and applied to the overlay without restarting. Existing settings files remain usable. Apply saves the current choices immediately; Cancel discards only edits made since the last Apply.

The language selector in the top-right corner defaults to following the Windows display language. It also offers explicit Simplified Chinese, Traditional Chinese, and English. Switching languages previews the interface immediately; Apply or Save retains the choice, and Cancel restores the last applied language. Additional languages can be installed as JSON files in the app's `Languages` folder; see [language packs](localization.md).

Important settings include:

- **Lyrics lead time**: calibrates lyrics against audible playback. The playback clock already places Apple Music's whole-second reports on the real timeline using their publish time, so this only needs to cover output latency and personal preference.
- **Apply native timing correction**: honors Apple Music's TTML `lyricOffset` metadata.
- **Compatibility mode**: allows non-Apple media sessions; disabled by default to prevent another player or browser from taking over.
- **Low-confidence candidates**: trades matching accuracy for coverage; disabled by default.
- **Word-by-word highlighting**: uses Apple Music's native TTML word timestamps when Apple supplies them for the whole song; otherwise NetEase YRC or QQ QRC timing is used (see [How lyric selection works](lyric-sources.md#how-lyric-selection-works)). A word held for a while swells and glows as it is sung; words early in a line need a longer hold than the word that ends a phrase. Lyrics without usable word timestamps keep the ordinary line display; word clocks are never synthesized.
- **Show translations**: prioritizes Apple's supplied translation, associated with the original line by its native key. When Apple's lyrics lack translations, a NetEase or QQ result with translations can be used instead. The app does not generate translations. Availability depends on what each source supplies; not every track has translations or word timing.
- **Online lyrics**: the online settings control Apple catalog verification and external lyric lookup. Lookup tries NetEase Cloud Music, QQ Music, and LRCLIB synchronized lyrics before using LRCLIB plain lyrics. Missing enabled features can trigger further background checks even when Apple already supplies synchronized lines. Online service changes require an app restart; highlighting and translation toggles apply immediately.
- **Keep lyric cache**: the Advanced tab can retain completed lyrics from all three external providers across restarts, including supplied translations, line timing, and word timing. The shared capacity defaults to 64 MB and can be set from 1 to 1024 MB. Older, least recently used entries are removed when the quota is reached or reduced. Current disk usage is shown beside a one-click Clear cache action, which also works when retention or online lyrics are disabled. Clearing refreshes the usage immediately and preserves settings and the current lyric display. The retention switch and capacity apply immediately; retention remains disabled by default, preserving existing settings.
- **Pure mode drag opacity**: controls visibility while repositioning the card.
- **Diagnostic summary**: adds one compact line below the lyrics with the selected source and word/translation coverage. It has no scroll area or log panel. Use **View diagnostics** in the tray or Advanced settings to open the full report in a separate resizable window. That window shows missing-translation reasons and each source's query/retry/failure/selection results, remains independent of overlay opacity and click-through, and opens only when requested.

`CompositionTarget.Rendering` normally tracks the WPF/DWM composition cadence of the active display. The debug FPS value measures actual callbacks; it is not a promise that every system or driver will exactly match the monitor's advertised refresh rate.
