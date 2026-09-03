namespace AppleMusicLyrics.Core.Configuration;

public sealed class AppSettings
{
    public double LyricsPollInterval { get; set; } = 1.0;

    public double PlayerPollInterval { get; set; } = 0.2;

    public double CliRenderInterval { get; set; } = 0.2;

    public double UiRefreshInterval { get; set; } = 0.033;

    // Calibrates how far SMTC's reported position lags the audio you actually hear. Since the
    // playback clock now measures the sub-second phase instead of guessing it, this is the only
    // remaining hand-tuned term.
    //
    // It used to default to 0.28 while the clock also pushed the estimate forward by a guessed
    // ~0.48s. That guess is gone, so the default absorbs it to keep the timing people already
    // calibrated by ear; an existing settings.ini still holding 0.28 wants roughly +0.5.
    public double LyricsOffsetSeconds { get; set; } = 0.78;

    // Honour the lyricOffset Apple ships inside a TTML document, which corrects lyric timings for
    // the particular master being played. Only a minority of documents carry one.
    public bool ApplyNativeLyricOffset { get; set; } = true;

    public int PreviewLineCount { get; set; } = 8;

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

    // For songs Apple's own cache has no lyrics for at all, fall back to the community database at
    // lrclib.net. Fetched on a background task, so a slow lookup never stalls the display.
    public bool ExternalLyricsEnabled { get; set; } = true;

    public double ExternalLyricsTimeoutSeconds { get; set; } = 6.0;

    // Normal mode window position and size
    public int WindowX { get; set; } = 100;

    public int WindowY { get; set; } = 100;

    public int WindowWidth { get; set; } = 1336;

    public int WindowHeight { get; set; } = 296;

    // Pure mode window position and size (independent from normal mode)
    public int PureModeWindowX { get; set; } = 100;

    public int PureModeWindowY { get; set; } = 100;

    // Versioned per-monitor placement. Negative anchors mean an older settings file that should be
    // migrated from the legacy absolute coordinates above on the next successful capture.
    public int WindowPlacementVersion { get; set; }

    public string WindowMonitorId { get; set; } = string.Empty;

    public double WindowRelativeCenterX { get; set; } = -1.0;

    public double WindowRelativeCenterY { get; set; } = -1.0;

    public string PureModeMonitorId { get; set; } = string.Empty;

    public double PureModeRelativeCenterX { get; set; } = -1.0;

    public double PureModeRelativeCenterY { get; set; } = -1.0;

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

    public bool ShowPreviousLine { get; set; } = true;

    public bool ShowNextLine { get; set; } = true;

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
            LyricsPollInterval = LyricsPollInterval,
            PlayerPollInterval = PlayerPollInterval,
            CliRenderInterval = CliRenderInterval,
            UiRefreshInterval = UiRefreshInterval,
            LyricsOffsetSeconds = LyricsOffsetSeconds,
            ApplyNativeLyricOffset = ApplyNativeLyricOffset,
            PreviewLineCount = PreviewLineCount,
            AllowNonAppleMediaSessions = AllowNonAppleMediaSessions,
            AllowLowConfidenceLyrics = AllowLowConfidenceLyrics,
            CatalogLookupEnabled = CatalogLookupEnabled,
            CatalogStorefronts = CatalogStorefronts,
            CatalogLookupTimeoutSeconds = CatalogLookupTimeoutSeconds,
            ExternalLyricsEnabled = ExternalLyricsEnabled,
            ExternalLyricsTimeoutSeconds = ExternalLyricsTimeoutSeconds,
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
            ShowPreviousLine = ShowPreviousLine,
            ShowNextLine = ShowNextLine,
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
