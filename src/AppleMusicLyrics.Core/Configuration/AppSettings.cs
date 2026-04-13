namespace AppleMusicLyrics.Core.Configuration;

public sealed class AppSettings
{
    public double LyricsPollInterval { get; set; } = 1.0;

    public double PlayerPollInterval { get; set; } = 0.2;

    public double CliRenderInterval { get; set; } = 0.2;

    public double UiRefreshInterval { get; set; } = 0.033;

    public double LyricsOffsetSeconds { get; set; } = 0.28;

    public int PreviewLineCount { get; set; } = 8;

    public double RecentFileGraceSeconds { get; set; } = 2.0;

    // Normal mode window position and size
    public int WindowX { get; set; } = 100;

    public int WindowY { get; set; } = 100;

    public int WindowWidth { get; set; } = 800;

    public int WindowHeight { get; set; } = 220;

    // Pure mode window position and size (independent from normal mode)
    public int PureModeWindowX { get; set; } = 100;

    public int PureModeWindowY { get; set; } = 100;

    public int MinWindowWidth { get; set; } = 280;

    public int MinWindowHeight { get; set; } = 80;

    public double MaxCurrentFontSize { get; set; } = 38.0;

    public double ContextFontSize { get; set; } = 24.0;

    public string CurrentLineColor { get; set; } = "#FFFFFF";

    public string ContextLineColor { get; set; } = "#C8C8C8";

    public string PausedLineColor { get; set; } = "#AAAAAA";

    public string GlowColor { get; set; } = "#FFFFFF";

    public double GlowOpacity { get; set; } = 0.42;

    public string FontFamily { get; set; } = "Segoe UI";

    public bool ShowPreviousLine { get; set; } = true;

    public bool ShowNextLine { get; set; } = true;

    public bool SingleLineMode { get; set; }

    public bool TwoLineMode { get; set; }

    public bool ShowDebugPanel { get; set; }

    public bool AutoHideNoLyrics { get; set; }

    public bool PureMode { get; set; }

    public bool ClickThrough { get; set; }

    public double OverlayOpacity { get; set; } = 1.0;

    public int? BackgroundAlpha { get; set; }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            LyricsPollInterval = LyricsPollInterval,
            PlayerPollInterval = PlayerPollInterval,
            CliRenderInterval = CliRenderInterval,
            UiRefreshInterval = UiRefreshInterval,
            LyricsOffsetSeconds = LyricsOffsetSeconds,
            PreviewLineCount = PreviewLineCount,
            RecentFileGraceSeconds = RecentFileGraceSeconds,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            PureModeWindowX = PureModeWindowX,
            PureModeWindowY = PureModeWindowY,
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
            PureMode = PureMode,
            ClickThrough = ClickThrough,
            OverlayOpacity = OverlayOpacity,
            BackgroundAlpha = BackgroundAlpha,
        };
    }
}
