namespace AppleMusicLyrics.Core.Configuration;

public sealed class AppSettings
{
    public string UiLanguage { get; set; } = "system";

    public double PlayerPollInterval { get; set; } = 0.2;

    // Calibrates how far SMTC's reported position lags the audio you actually hear. Since the
    // playback clock now measures the sub-second phase instead of guessing it, this is the only
    // remaining hand-tuned term.
    //
    // It used to default to 0.28 while the clock also pushed the estimate forward by a guessed
    // ~0.48s. That guess is gone, so the default absorbs it to keep the timing people already
    // calibrated by ear; an existing settings.ini still holding 0.28 wants roughly +0.5.
    //
    // Measured frame by frame against Apple Music's own lyrics (new line half faded in vs Apple's
    // first word half highlighted): at 0.78 lines were a median 0.20–0.26 s late (three songs, one
    // session); at 1.0 they were 0.12 s early (30 lines, another session). The two sessions point
    // to about 1.03 and 0.88; this sits between them, as sessions differ by about 0.15 s.
    public double LyricsOffsetSeconds { get; set; } = 0.9;

    // Honour the lyricOffset Apple ships inside a TTML document, which corrects lyric timings for
    // the particular master being played. Only a minority of documents carry one.
    public bool ApplyNativeLyricOffset { get; set; } = true;

    // Remember the offset adjusted for each song and reapply it when that song plays again.
    // Songs without a saved offset keep using LyricsOffsetSeconds.
    public bool PerSongOffsetEnabled { get; set; } = true;

    // Other SMTC applications can publish plausible metadata and previously hijacked matching.
    // Keep the fallback opt-in because this application targets Apple Music by default.
    public bool AllowNonAppleMediaSessions { get; set; }

    // Low-confidence local candidates are hidden by default. Users who prefer coverage over
    // correctness can explicitly opt in to duration-only or otherwise unverifiable matches.
    public bool AllowLowConfidenceLyrics { get; set; }

    // Cached lyrics files carry an "AP_<catalog song id>" id, so when the local duration check
    // cannot separate two cached songs the playing track is looked up in the public iTunes catalog
    // and matched on that id instead. Disable to stay fully offline at the cost of accuracy.
    public bool CatalogLookupEnabled { get; set; } = true;

    // Comma-separated storefronts, tried in order until one gives a confident title+artist hit.
    public string CatalogStorefronts { get; set; } = "us,cn,jp,gb";

    public double CatalogLookupTimeoutSeconds { get; set; } = 3.0;

    // Query LRCLIB, QQ Music, and NetEase Cloud Music on background tasks when Apple's cache has
    // no usable lyrics or lacks requested word timings/translations. Disabling this setting disables every external source.
    public bool ExternalLyricsEnabled { get; set; } = true;

    public double ExternalLyricsTimeoutSeconds { get; set; } = 6.0;

    // Keep completed external lyric documents across app restarts.
    public bool PersistExternalLyricsCache { get; set; } = true;

    // One shared disk quota for all external sources; the cache clamps this to 1..1024 MiB.
    public int ExternalLyricsCacheMaxMegabytes { get; set; } = 64;

    // Ask GitHub Releases for a newer version at startup and once a day. The request carries only
    // the app name and version; turning this off leaves the manual "Check for updates" action.
    public bool CheckForUpdates { get; set; } = true;

    // A release the user chose to skip. Automatic checks stay quiet about exactly this version;
    // a manual check still offers it, and any later release is offered again.
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    // Normal mode window position and size
    public int WindowX { get; set; } = 100;

    public int WindowY { get; set; } = 100;

    public int WindowWidth { get; set; } = 1336;

    public int WindowHeight { get; set; } = 296;

    // Pure mode window position and size (independent from normal mode)
    public int PureModeWindowX { get; set; } = 100;

    public int PureModeWindowY { get; set; } = 100;

    // Versioned per-monitor placement: the window's center as a fraction of the monitor's work area.
    // A fresh install starts at the bottom center of the primary monitor. Negative anchors mean an
    // older settings file that should be migrated from the legacy absolute coordinates above.
    public int WindowPlacementVersion { get; set; }

    public const double DefaultRelativeCenterX = 0.5;

    public const double DefaultRelativeCenterY = 0.9;

    public string WindowMonitorId { get; set; } = string.Empty;

    public double WindowRelativeCenterX { get; set; } = DefaultRelativeCenterX;

    public double WindowRelativeCenterY { get; set; } = DefaultRelativeCenterY;

    public string PureModeMonitorId { get; set; } = string.Empty;

    public double PureModeRelativeCenterX { get; set; } = DefaultRelativeCenterX;

    public double PureModeRelativeCenterY { get; set; } = DefaultRelativeCenterY;

    public int MinWindowWidth { get; set; } = 280;

    public int MinWindowHeight { get; set; } = 80;

    public double MaxCurrentFontSize { get; set; } = 38.0;

    public double ContextFontSize { get; set; } = 24.0;

    public string CurrentLineColor { get; set; } = "#FFFFFF";

    public string ContextLineColor { get; set; } = "#FFFFFF";

    public string PausedLineColor { get; set; } = "#AAAAAA";

    public string GlowColor { get; set; } = "#C5FEFE";

    public double GlowOpacity { get; set; } = 1.0;

    public string FontFamily { get; set; } = "Optima";

    public string ChineseFontFamily { get; set; } = "Microsoft YaHei UI";

    public string TranslationFontFamily { get; set; } = "Microsoft YaHei UI";

    public double TranslationFontSize { get; set; } = 20.0;

    public bool ShowPreviousLine { get; set; } = true;

    public bool ShowNextLine { get; set; } = true;

    public bool WordHighlightEnabled { get; set; } = true;

    public bool ShowTranslation { get; set; } = true;

    // Show supplied romanization or generate a reading for Japanese lyrics.
    public bool ShowRomanization { get; set; }

    public bool SingleLineMode { get; set; }

    public bool TwoLineMode { get; set; } = true;

    public bool ShowDebugPanel { get; set; }

    public bool AutoHideNoLyrics { get; set; } = true;

    public bool FadeWhenPaused { get; set; } = true;

    public bool PureMode { get; set; } = true;

    public bool ClickThrough { get; set; } = true;

    public double OverlayOpacity { get; set; } = 1.0;

    public double PureModeDragOpacity { get; set; } = 0.79;

    public int? BackgroundAlpha { get; set; } = 181;

    public bool HoverFadeEnabled { get; set; } = true;

    public double HoverFadeDuration { get; set; } = 0.3;

    public double HoverFadeMinOpacity { get; set; } = 0.05;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            UiLanguage = UiLanguage,
            PlayerPollInterval = PlayerPollInterval,
            LyricsOffsetSeconds = LyricsOffsetSeconds,
            ApplyNativeLyricOffset = ApplyNativeLyricOffset,
            PerSongOffsetEnabled = PerSongOffsetEnabled,
            AllowNonAppleMediaSessions = AllowNonAppleMediaSessions,
            AllowLowConfidenceLyrics = AllowLowConfidenceLyrics,
            CatalogLookupEnabled = CatalogLookupEnabled,
            CatalogStorefronts = CatalogStorefronts,
            CatalogLookupTimeoutSeconds = CatalogLookupTimeoutSeconds,
            ExternalLyricsEnabled = ExternalLyricsEnabled,
            ExternalLyricsTimeoutSeconds = ExternalLyricsTimeoutSeconds,
            PersistExternalLyricsCache = PersistExternalLyricsCache,
            ExternalLyricsCacheMaxMegabytes = ExternalLyricsCacheMaxMegabytes,
            CheckForUpdates = CheckForUpdates,
            SkippedUpdateVersion = SkippedUpdateVersion,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            PureModeWindowX = PureModeWindowX,
            PureModeWindowY = PureModeWindowY,
            WindowPlacementVersion = WindowPlacementVersion,
            WindowMonitorId = WindowMonitorId,
            WindowRelativeCenterX = WindowRelativeCenterX,
            WindowRelativeCenterY = WindowRelativeCenterY,
            PureModeMonitorId = PureModeMonitorId,
            PureModeRelativeCenterX = PureModeRelativeCenterX,
            PureModeRelativeCenterY = PureModeRelativeCenterY,
            MinWindowWidth = MinWindowWidth,
            MinWindowHeight = MinWindowHeight,
            MaxCurrentFontSize = MaxCurrentFontSize,
            ContextFontSize = ContextFontSize,
            CurrentLineColor = CurrentLineColor,
            ContextLineColor = ContextLineColor,
            PausedLineColor = PausedLineColor,
            GlowColor = GlowColor,
            GlowOpacity = GlowOpacity,
            FontFamily = FontFamily,
            ChineseFontFamily = ChineseFontFamily,
            TranslationFontFamily = TranslationFontFamily,
            TranslationFontSize = TranslationFontSize,
            ShowPreviousLine = ShowPreviousLine,
            ShowNextLine = ShowNextLine,
            WordHighlightEnabled = WordHighlightEnabled,
            ShowTranslation = ShowTranslation,
            ShowRomanization = ShowRomanization,
            SingleLineMode = SingleLineMode,
            TwoLineMode = TwoLineMode,
            ShowDebugPanel = ShowDebugPanel,
            AutoHideNoLyrics = AutoHideNoLyrics,
            FadeWhenPaused = FadeWhenPaused,
            PureMode = PureMode,
            ClickThrough = ClickThrough,
            OverlayOpacity = OverlayOpacity,
            PureModeDragOpacity = PureModeDragOpacity,
            BackgroundAlpha = BackgroundAlpha,
            HoverFadeEnabled = HoverFadeEnabled,
            HoverFadeDuration = HoverFadeDuration,
            HoverFadeMinOpacity = HoverFadeMinOpacity,
        };
    }
}
