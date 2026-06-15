using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using AppleMusicLyrics.Infrastructure.Windows.Configuration;
using AppleMusicLyrics.Infrastructure.Windows.Interop;
using AppleMusicLyrics.Infrastructure.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App;

public partial class MainWindow : Window
{
    private readonly LyricsRuntimeService _runtimeService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly AppSettings _settings;
    private readonly IniSettingsStore _settingsStore;
    private readonly WindowInteropService _windowInteropService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showHideMenuItem;
    private readonly Forms.ToolStripMenuItem _clickThroughMenuItem;
    private readonly Forms.ToolStripMenuItem _pureModeMenuItem;
    private readonly Forms.ToolStripMenuItem _singleLineMenuItem;
    private readonly Forms.ToolStripMenuItem _twoLineMenuItem;
    private readonly Forms.ToolStripMenuItem _debugPanelMenuItem;
    private bool _sourceInitialized;
    private bool _isRefreshing;
    private bool _isApplyingPureModeAutoSize;
    private bool _overlayHiddenByUser;
    private string? _lastLyricAnimationKey;
    private bool _isHovering;
    private bool _lastHasLyrics;
    private bool _lastPlaying;
    private double _opacityAnimTarget = -1;
    private MouseTracker? _mouseTracker;
    private string? _currentPreviousText;
    private string? _currentNextText;
    private double _targetHeight;
    private double _heightAnimFrom;
    private System.Diagnostics.Stopwatch _heightAnimStopwatch = new();
    private bool _isAnimatingHeight;
    private double _heightAnimCenterY;
    private double _widthAnimFrom;
    private double _targetWidth;
    private readonly RectangleGeometry _clipGeometry = new() { RadiusX = 16, RadiusY = 16 };
    // PureMode keeps the HWND at a fixed, generous size and animates only the ShellBorder clip.
    // Resizing a layered (transparent) window is never atomic with the WPF clip, so any HWND
    // resize during a transition flashes; a fixed window + clip animation is flash-proof.
    private double _pureWindowWidth;
    private double _pureWindowHeight;
    private double _lastCardWidth;
    private double _lastCardHeight;

    public MainWindow()
    {
        _settingsStore = new IniSettingsStore(GetSettingsPath());
        _settings = _settingsStore.Load();
        _windowInteropService = new WindowInteropService();

        InitializeComponent();
        Icon = LoadAppIcon();
        (_notifyIcon, _showHideMenuItem, _clickThroughMenuItem, _pureModeMenuItem, _singleLineMenuItem, _twoLineMenuItem, _debugPanelMenuItem) = CreateNotifyIcon();
        ApplyWindowBounds();
        ApplyAppearanceSettings();

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;
        LocationChanged += OnWindowLocationChanged;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;

        var parser = new TtmlLyricsParser();
        var scanner = new AppleMusicCacheScanner(
            parser,
            startedAt: DateTimeOffset.UtcNow,
            recentFileGraceSeconds: _settings.RecentFileGraceSeconds);
        var playerProvider = new GlobalMediaSessionProvider();
        var synchronizer = new LyricsSynchronizer();
        var playbackClock = new PlaybackClock();
        _runtimeService = new LyricsRuntimeService(
            scanner,
            playerProvider,
            synchronizer,
            playbackClock,
            _settings.LyricsOffsetSeconds);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PlayerPollInterval, 0.05, 1.0)),
        };
        _refreshTimer.Tick += OnRefreshTick;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        TickHeightAnimation();

        // Keep the hover-fade hit region matched to the visible card (the fixed PureMode window
        // is much larger than the card, so the raw window rect would over-trigger).
        if (_mouseTracker is not null)
        {
            UpdateMouseTrackerBounds();
        }
    }

    private void UpdateMouseTrackerBounds()
    {
        if (_mouseTracker is null)
        {
            return;
        }

        if (!_settings.PureMode || !ReferenceEquals(ShellBorder.Clip, _clipGeometry))
        {
            _mouseTracker.ClearBounds();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        const double shellMargin = 6.0;
        var card = _clipGeometry.Rect;
        var left = (Left + shellMargin + card.X) * dpi.DpiScaleX;
        var top = (Top + shellMargin + card.Y) * dpi.DpiScaleY;
        var right = left + card.Width * dpi.DpiScaleX;
        var bottom = top + card.Height * dpi.DpiScaleY;
        _mouseTracker.SetBounds((int)left, (int)top, (int)right, (int)bottom);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveFontSizes();
        ApplyClickThrough();

        // For PureMode, calculate initial size based on placeholder content
        if (_settings.PureMode)
        {
            RefreshLyricLayout();
        }

        await RefreshSnapshotAsync();
        _refreshTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _sourceInitialized = true;

        // Route hit-testing so only the visible card is interactive; the transparent margins
        // of the fixed PureMode window pass clicks through to whatever is behind.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }

        // Now that the screen is known, re-clamp the fixed PureMode window to it.
        if (_settings.PureMode)
        {
            ApplyWindowBounds();
            RefreshLyricLayout();
        }

        ApplyClickThrough();
    }

    private const int WM_NCHITTEST = 0x0084;
    private static readonly IntPtr HTTRANSPARENT = new(-1);

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || !_settings.PureMode || !ReferenceEquals(ShellBorder.Clip, _clipGeometry))
        {
            return IntPtr.Zero;
        }

        var pos = lParam.ToInt64();
        double screenX = unchecked((short)(pos & 0xFFFF));
        double screenY = unchecked((short)((pos >> 16) & 0xFFFF));

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = screenX / dpi.DpiScaleX;
        var y = screenY / dpi.DpiScaleY;

        const double shellMargin = 6.0;
        var card = _clipGeometry.Rect;
        var cardLeft = Left + shellMargin + card.X;
        var cardTop = Top + shellMargin + card.Y;

        if (x < cardLeft || x > cardLeft + card.Width || y < cardTop || y > cardTop + card.Height)
        {
            handled = true;
            return HTTRANSPARENT;
        }

        return IntPtr.Zero;
    }

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        await RefreshSnapshotAsync();
    }

    private async Task RefreshSnapshotAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshot = await _runtimeService.SnapshotAsync();
            ApplySnapshot(snapshot);
            UpdateOverlayVisibility(snapshot);
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "Failed to refresh runtime state.";
            PlayerText.Text = "Check Apple Music and media session availability.";
            TimingText.Text = "Playback clock refresh failed";
            StatusText.Text = "Runtime error";
            PathText.Text = ex.Message;
            CurrentLyricText.Text = "Refresh failed.";
            PreviousLyricText.Text = string.Empty;
            NextLyricText.Text = "Check Apple Music, cache state, or media session access.";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplySnapshot(RuntimeSnapshot snapshot)
    {
        UpdatePlayerSection(snapshot);
        UpdateLyricSection(snapshot);
        UpdateTrayState();
    }

    private void UpdatePlayerSection(RuntimeSnapshot snapshot)
    {
        if (snapshot.Player is null)
        {
            Title = "Apple Music Lyrics";
            SubtitleText.Text = "Waiting for Apple Music session";
            PlayerText.Text = "No active media session";
            TimingText.Text = "No playback clock data";
            return;
        }

        var status = snapshot.Player.Playing ? "Playing" : "Paused";
        var artist = snapshot.Player.Artist ?? "Unknown Artist";
        var title = snapshot.Player.Title ?? "Unknown Title";
        Title = $"{artist} - {title}";
        PlayerText.Text = $"{status}: {artist} - {title}";
        TimingText.Text = $"raw {TimeParser.FormatTimestamp(snapshot.RawPositionSeconds ?? 0)} | est {TimeParser.FormatTimestamp(snapshot.EstimatedPositionSeconds ?? 0)}";
        SubtitleText.Text = snapshot.Player.Album is { Length: > 0 }
            ? $"{artist} | {snapshot.Player.Album}"
            : artist;
    }

    private void UpdateLyricSection(RuntimeSnapshot snapshot)
    {
        if (snapshot.Document is null)
        {
            StatusText.Text = "Waiting for cache";
            PathText.Text = "Play a song with timed lyrics in Apple Music.";
            SetLyrics(
                previousText: string.Empty,
                currentText: snapshot.Player is null
                    ? "Waiting for Apple Music session..."
                    : "Waiting for lyric cache file...",
                nextText: "Timed lyrics will appear here once a cache file is created.",
                isPlaying: snapshot.Player?.Playing ?? false,
                animate: false);
            return;
        }

        StatusText.Text = $"{snapshot.Document.Lines.Count} lines";
        PathText.Text = snapshot.Document.SourceFile;

        var currentText = snapshot.ActiveLyric.CurrentLine?.Text
            ?? snapshot.ActiveLyric.NextLine?.Text
            ?? snapshot.Document.Lines.FirstOrDefault()?.Text
            ?? "Lyrics file has no timed lines.";

        SetLyrics(
            snapshot.ActiveLyric.PreviousLine?.Text,
            currentText,
            snapshot.ActiveLyric.NextLine?.Text,
            snapshot.Player?.Playing ?? false,
            animate: snapshot.ActiveLyric.CurrentLine is not null);
    }

    private void SetLyrics(
        string? previousText,
        string currentText,
        string? nextText,
        bool isPlaying,
        bool animate)
    {
        var lyricKey = $"{previousText}|{currentText}|{nextText}|{isPlaying}";
        var isRepeat = string.Equals(_lastLyricAnimationKey, lyricKey, StringComparison.Ordinal);

        // Same content as last time (a poll tick with no change): the lyrics are already on
        // screen — do nothing, so a crossfade in progress isn't cut short.
        if (isRepeat)
        {
            return;
        }

        var shouldAnimate = animate;
        _lastLyricAnimationKey = lyricKey;

        if (!shouldAnimate)
        {
            ResetLyricVisualState();
            ApplyLyricContent(previousText, currentText, nextText, isPlaying);
            RefreshLyricLayout();
            Dispatcher.BeginInvoke(() => StartHeightAnimation(), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        // True crossfade: freeze the outgoing lyrics into a snapshot that fades OUT while the
        // new lyrics fade IN on top of it. The old text is always visible until the new text
        // has appeared, so there is never an empty (dark) card between the two lines.
        SnapshotLyricGhost();
        ApplyLyricContent(previousText, currentText, nextText, isPlaying);
        RefreshLyricLayout();

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(LyricFadeDurationMs);

        // Incoming lyrics fade in (+ subtle upward slide on the current line)
        LyricPanel.BeginAnimation(OpacityProperty, null);
        LyricPanel.Opacity = 0.0;
        LyricPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easing });
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });

        // Outgoing snapshot fades out over the same window
        var ghostOut = new DoubleAnimation(0.0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        ghostOut.Completed += (_, _) => HideLyricGhost();
        LyricGhostImage.BeginAnimation(OpacityProperty, ghostOut);

        Dispatcher.BeginInvoke(() => StartHeightAnimation(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private (bool showPrevious, bool showNext) ApplyLyricContent(
        string? previousText, string currentText, string? nextText, bool isPlaying)
    {
        var showPrevious = _settings.ShowPreviousLine && !string.IsNullOrWhiteSpace(previousText);
        var showNext = _settings.ShowNextLine && !string.IsNullOrWhiteSpace(nextText);

        _currentPreviousText = previousText;
        _currentNextText = nextText;

        PreviousLyricText.Text = showPrevious ? previousText! : string.Empty;
        CurrentLyricText.Text = currentText;
        NextLyricText.Text = showNext ? nextText! : string.Empty;

        CurrentLyricText.Foreground = CreateBrush(isPlaying ? _settings.CurrentLineColor : _settings.PausedLineColor, Colors.White);
        PreviousLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));
        NextLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));

        UpdateLyricRowHeights(showPrevious, showNext);
        return (showPrevious, showNext);
    }

    private void UpdateLyricRowHeights(bool showPrevious, bool showNext)
    {
        if (_settings.PureMode || _settings.TwoLineMode)
        {
            return;
        }

        PreviousLyricRow.Height = showPrevious
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        NextLyricRow.Height = showNext
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private void StartHeightAnimation()
    {
        if (_settings.PureMode || _isApplyingPureModeAutoSize || !IsLoaded)
        {
            return;
        }

        var targetHeight = MeasureDesiredHeight();
        if (targetHeight < 0)
        {
            return;
        }

        if (Math.Abs(targetHeight - Height) < 1)
        {
            return;
        }

        _heightAnimFrom = Height;
        _targetHeight = targetHeight;
        _heightAnimCenterY = Top + Height / 2.0;
        _widthAnimFrom = Width;
        _targetWidth = Width; // no width change in normal mode
        _heightAnimStopwatch.Restart();
        _isAnimatingHeight = true;
    }

    private const double WindowAnimDurationMs = 240.0;
    private const double LyricFadeDurationMs = 360.0;
    private const double PureModeMaxWindowWidth = 1400.0;
    private const double PureModeMaxWindowHeight = 560.0;

    // Fixed HWND size used in PureMode, clamped to the current screen so it never exceeds it.
    private System.Windows.Size GetPureModeWindowSize()
    {
        var w = PureModeMaxWindowWidth;
        var h = PureModeMaxWindowHeight;
        if (_sourceInitialized)
        {
            var screen = GetCurrentScreenBounds();
            w = Math.Min(w, screen.Width);
            h = Math.Min(h, screen.Height);
        }
        return new System.Windows.Size(w, h);
    }

    private void TickHeightAnimation()
    {
        if (!_isAnimatingHeight)
            return;

        var progress = Math.Min(1.0, _heightAnimStopwatch.Elapsed.TotalMilliseconds / WindowAnimDurationMs);
        var eased = EaseInOutCubic(progress);

        if (_settings.PureMode)
        {
            // The HWND is fixed; only the rounded ShellBorder clip animates between the old
            // and new card size. This is a pure WPF composition change (no DWM resize), so it
            // is always presented atomically — no flash. The clip stays applied at the end so
            // the card keeps its size (clearing it would expose the full fixed-size border).
            ApplyRevealClip(eased);

            if (progress >= 1.0)
            {
                _isAnimatingHeight = false;
            }
        }
        else
        {
            // Normal mode: animate window height directly
            var newHeight = _heightAnimFrom + (_targetHeight - _heightAnimFrom) * eased;

            if (progress >= 1.0)
            {
                _isAnimatingHeight = false;
                ShellBorder.Clip = null;
                Height = _targetHeight;
                Top = _heightAnimCenterY - _targetHeight / 2.0;
                return;
            }

            Height = newHeight;
            Top = _heightAnimCenterY - newHeight / 2.0;
        }
    }

    // Sets the ShellBorder reveal clip for a given eased progress (0 = old visual bounds,
    // 1 = new visual bounds), centered within the current (max-sized) border container.
    private void ApplyRevealClip(double eased)
    {
        const double shellMargin = 6.0;
        var containerW = Math.Max(0, Width - shellMargin * 2);
        var containerH = Math.Max(0, Height - shellMargin * 2);
        var fromW = Math.Max(8, _widthAnimFrom - shellMargin * 2);
        var fromH = Math.Max(8, _heightAnimFrom - shellMargin * 2);
        var toW = Math.Max(8, _targetWidth - shellMargin * 2);
        var toH = Math.Max(8, _targetHeight - shellMargin * 2);

        var curW = fromW + (toW - fromW) * eased;
        var curH = fromH + (toH - fromH) * eased;
        var x = Math.Max(0, (containerW - curW) / 2.0);
        var y = Math.Max(0, (containerH - curH) / 2.0);
        _clipGeometry.Rect = new Rect(x, y, Math.Min(curW, containerW), Math.Min(curH, containerH));
    }

    private static double EaseInOutCubic(double t)
    {
        return t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    private double MeasureDesiredHeight()
    {
        var showPrevious = _settings.ShowPreviousLine && !string.IsNullOrWhiteSpace(_currentPreviousText);
        var showNext = _settings.ShowNextLine && !string.IsNullOrWhiteSpace(_currentNextText);

        var shellMargin = 14.0;
        var shellPadding = 12.0;

        var headerHeight = HeaderPanel.Visibility == Visibility.Visible ? 30.0 : 0.0;
        var footerHeight = FooterPanel.Visibility == Visibility.Visible ? 50.0 : 0.0;

        var currentFontSize = CurrentLyricText.FontSize;
        var currentLineHeight = currentFontSize * 1.4;
        var contextFontSize = PreviousLyricText.FontSize;
        var contextLineHeight = contextFontSize * 1.4;

        var contentHeight = 0.0;
        if (showPrevious)
        {
            contentHeight += Math.Min(contextLineHeight, 52) + 8;
        }

        contentHeight += currentLineHeight + 8;

        if (showNext)
        {
            contentHeight += Math.Min(contextLineHeight, 52) + 8;
        }

        var totalHeight = shellMargin * 2 + shellPadding * 2 + headerHeight + footerHeight + contentHeight + 20;
        return Math.Max(MinHeight, totalHeight);
    }

    private void ResetLyricVisualState()
    {
        LyricPanel.BeginAnimation(OpacityProperty, null);
        LyricPanel.Opacity = 1.0;
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CurrentLyricTransform.Y = 0;
        HideLyricGhost();
    }

    // Render the currently displayed lyrics into a frozen bitmap and show it as an overlay,
    // so the outgoing text can fade out independently of the (already updated) live panel.
    private void SnapshotLyricGhost()
    {
        if (LyricPanel.ActualWidth < 1 || LyricPanel.ActualHeight < 1)
        {
            HideLyricGhost();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(LyricPanel.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(LyricPanel.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        rtb.Render(LyricPanel);
        rtb.Freeze();

        LyricGhostImage.Source = rtb;
        LyricGhostImage.Width = LyricPanel.ActualWidth;
        LyricGhostImage.Height = LyricPanel.ActualHeight;
        LyricGhostImage.BeginAnimation(OpacityProperty, null);
        LyricGhostImage.Opacity = 1.0;
        LyricGhostImage.Visibility = Visibility.Visible;
    }

    private void HideLyricGhost()
    {
        LyricGhostImage.BeginAnimation(OpacityProperty, null);
        LyricGhostImage.Visibility = Visibility.Collapsed;
        LyricGhostImage.Source = null;
    }

    private void UpdateOverlayVisibility(RuntimeSnapshot snapshot)
    {
        _lastHasLyrics = snapshot.Document is not null;
        _lastPlaying = snapshot.Player?.Playing ?? false;

        if (_overlayHiddenByUser)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        RefreshOverlayOpacity();
    }

    // Single source of truth for the window opacity. The overlay fades fully out when paused
    // or when there are no lyrics, dims while hovered (so you can see through it), and is
    // otherwise shown at the configured opacity.
    private double ResolveTargetOpacity()
    {
        if ((_settings.AutoHideNoLyrics && !_lastHasLyrics) ||
            (_settings.FadeWhenPaused && !_lastPlaying))
        {
            return 0.0;
        }

        if (_isHovering && _settings.HoverFadeEnabled)
        {
            return Math.Clamp(_settings.HoverFadeMinOpacity, 0.0, 1.0);
        }

        return Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);
    }

    private void RefreshOverlayOpacity()
    {
        AnimateOverlayOpacity(ResolveTargetOpacity());
    }

    private void AnimateOverlayOpacity(double targetOpacity)
    {
        // Skip if we're already animating toward (or resting at) this target. Otherwise the
        // per-poll refresh would restart the animation every tick — and clearing it to null
        // first would snap opacity back to its base value, producing a continuous flicker.
        if (Math.Abs(_opacityAnimTarget - targetOpacity) < 0.001)
        {
            return;
        }

        _opacityAnimTarget = targetOpacity;
        var duration = TimeSpan.FromSeconds(Math.Clamp(_settings.HoverFadeDuration, 0.05, 2.0));

        // No null-clear: WPF hands the animation off smoothly from the current opacity value.
        BeginAnimation(OpacityProperty, new DoubleAnimation(targetOpacity, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _refreshTimer.Stop();
        StopMouseTracker();
        PersistCurrentSettings();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // In PureMode the HWND is a fixed size; text widths are driven by the card measurement
        // in ApplyPureModeAutoSize, so the window's own size changes must not touch them.
        if (_settings.PureMode || _isApplyingPureModeAutoSize || _isAnimatingHeight)
        {
            return;
        }

        UpdateTextMaxWidths(ActualWidth);
        ApplyAdaptiveFontSizes();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Hide();
            UpdateTrayState();
        }
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_isApplyingPureModeAutoSize || _isAnimatingHeight)
        {
            return;
        }

        if (_settings.PureMode && WindowState == WindowState.Normal)
        {
            _settings.PureModeWindowX = (int)Math.Round(Left + Width / 2.0);
            _settings.PureModeWindowY = (int)Math.Round(Top + Height / 2.0);
        }
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovering = true;
        RefreshOverlayOpacity();
    }

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isHovering = false;
        RefreshOverlayOpacity();
    }

    private void ApplyAdaptiveFontSizes(double? windowWidthOverride = null, double? windowHeightOverride = null)
    {
        var singleLine = _settings.SingleLineMode;
        var twoLine = _settings.TwoLineMode;

        if (_settings.PureMode)
        {
            var pureCurrentFontSize = _settings.MaxCurrentFontSize;
            var pureContextScale = twoLine ? 0.72 : 0.56;
            var pureContextFontSize = Math.Clamp(pureCurrentFontSize * pureContextScale, 12, _settings.ContextFontSize);

            CurrentLyricText.FontSize = pureCurrentFontSize;
            PreviousLyricText.FontSize = pureContextFontSize;
            NextLyricText.FontSize = pureContextFontSize;
            return;
        }

        var effectiveWindowWidth = Math.Max(windowWidthOverride ?? ActualWidth, MinWidth);
        var effectiveWindowHeight = Math.Max(windowHeightOverride ?? ActualHeight, MinHeight);

        var shellMargin = 14.0;
        var shellPadding = 12.0;
        var totalHorizontalChrome = shellMargin * 2 + shellPadding * 2;
        var contentWidth = Math.Max(_settings.MinWindowWidth, effectiveWindowWidth - totalHorizontalChrome - 16);

        var currentText = string.IsNullOrWhiteSpace(CurrentLyricText.Text) ? "Current lyric line" : CurrentLyricText.Text;
        var currentFontSize = FitFontSize(
            currentText,
            _settings.MaxCurrentFontSize,
            14,
            Math.Max(140, contentWidth),
            singleLine ? 72 : twoLine ? Math.Max(64, effectiveWindowHeight * 0.26) : Math.Max(88, effectiveWindowHeight * 0.48),
            singleLine);
        var contextScale = twoLine ? 0.72 : 0.56;
        var contextFontSize = Math.Clamp(currentFontSize * contextScale, 12, _settings.ContextFontSize);

        CurrentLyricText.FontSize = currentFontSize;
        PreviousLyricText.FontSize = contextFontSize;
        NextLyricText.FontSize = contextFontSize;
    }

    private void ApplyWindowBounds()
    {
        MinWidth = _settings.PureMode ? 180 : _settings.MinWindowWidth;
        MinHeight = _settings.PureMode ? 70 : _settings.MinWindowHeight;

        if (_settings.PureMode)
        {
            // PureMode: fixed, generous HWND sized once. The visible card is produced by the
            // ShellBorder clip and animates within this window, so the HWND never resizes
            // during lyric changes (which would flash). Position so the window CENTER sits on
            // the saved center point.
            var size = GetPureModeWindowSize();
            _pureWindowWidth = size.Width;
            _pureWindowHeight = size.Height;
            var centerX = Math.Max(_settings.PureModeWindowX, -32000);
            var centerY = Math.Max(_settings.PureModeWindowY, -32000);
            Width = size.Width;
            Height = size.Height;
            Left = centerX - size.Width / 2.0;
            Top = centerY - size.Height / 2.0;
            _lastCardWidth = 0;
            _lastCardHeight = 0;
        }
        else
        {
            // Normal mode uses saved width/height and position
            Width = Math.Max(_settings.WindowWidth, _settings.MinWindowWidth);
            Height = Math.Max(_settings.WindowHeight, _settings.MinWindowHeight);
            Left = Math.Max(_settings.WindowX, -32000);
            Top = Math.Max(_settings.WindowY, -32000);
        }
    }

    private void ApplyAppearanceSettings()
    {
        MinWidth = _settings.PureMode ? 180 : _settings.MinWindowWidth;
        MinHeight = _settings.PureMode ? 70 : _settings.MinWindowHeight;
        ResizeMode = _settings.PureMode ? System.Windows.ResizeMode.NoResize : System.Windows.ResizeMode.CanResize;
        RefreshOverlayOpacity();

        // Hide resize controls in PureMode
        ResizeLayer.Visibility = _settings.PureMode ? Visibility.Collapsed : Visibility.Visible;

        ShellBorder.Padding = _settings.PureMode ? new Thickness(6) : new Thickness(12);
        ShellBorder.Margin = _settings.PureMode ? new Thickness(6) : new Thickness(14);
        // In PureMode the HWND is fixed and large; the content must size to itself and centre
        // (not stretch to fill) so it stays compact and aligned with the centred reveal clip.
        // Otherwise Auto rows pack to the top and the clip — centred — misses them.
        ContentRoot.HorizontalAlignment = _settings.PureMode ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        ContentRoot.VerticalAlignment = _settings.PureMode ? System.Windows.VerticalAlignment.Center : System.Windows.VerticalAlignment.Stretch;
        LyricPanel.Margin = _settings.PureMode ? new Thickness(0, 2, 0, 2) : new Thickness(0, 8, 0, 6);
        HeaderPanel.Visibility = _settings.PureMode ? Visibility.Collapsed : Visibility.Visible;
        FooterPanel.Visibility = _settings.PureMode || !_settings.ShowDebugPanel ? Visibility.Collapsed : Visibility.Visible;
        var twoLineMode = _settings.TwoLineMode && !_settings.SingleLineMode;
        PreviousLyricText.Visibility = _settings.ShowPreviousLine && !_settings.SingleLineMode && !twoLineMode ? Visibility.Visible : Visibility.Collapsed;
        NextLyricText.Visibility = _settings.ShowNextLine && !_settings.SingleLineMode ? Visibility.Visible : Visibility.Collapsed;
        CurrentLyricText.TextWrapping = _settings.SingleLineMode ? TextWrapping.NoWrap : TextWrapping.Wrap;
        CurrentLyricText.TextTrimming = _settings.SingleLineMode ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        PreviousLyricText.MaxHeight = _settings.PureMode ? double.PositiveInfinity : 52;
        NextLyricText.MaxHeight = _settings.PureMode ? double.PositiveInfinity : 52;

        if (twoLineMode)
        {
            PreviousLyricRow.Height = new GridLength(0);
            CurrentLyricRow.Height = GridLength.Auto;
            NextLyricRow.Height = GridLength.Auto;
            System.Windows.Controls.Grid.SetRow(CurrentLyricText, 1);
            System.Windows.Controls.Grid.SetRow(NextLyricText, 2);
            CurrentLyricText.Margin = _settings.PureMode ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 10);
            NextLyricText.Margin = new Thickness(0);
            NextLyricText.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            var showPrevious = _settings.ShowPreviousLine && !string.IsNullOrWhiteSpace(_currentPreviousText);
            var showNext = _settings.ShowNextLine && !string.IsNullOrWhiteSpace(_currentNextText);
            PreviousLyricRow.Height = showPrevious
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            CurrentLyricRow.Height = GridLength.Auto;
            NextLyricRow.Height = showNext
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            System.Windows.Controls.Grid.SetRow(CurrentLyricText, 1);
            System.Windows.Controls.Grid.SetRow(NextLyricText, 2);
            CurrentLyricText.Margin = _settings.PureMode ? new Thickness(0, 2, 0, 2) : new Thickness(0, 4, 0, 4);
            NextLyricText.Margin = new Thickness(0);
            NextLyricText.VerticalAlignment = VerticalAlignment.Center;
        }

        var alpha = (byte)Math.Clamp(_settings.BackgroundAlpha ?? (_settings.PureMode ? 72 : 200), 0, 255);
        ShellBorder.Background = new SolidColorBrush(MediaColor.FromArgb(alpha, 20, 20, 22));

        try
        {
            var fontFamilyName = string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Segoe UI" : _settings.FontFamily;
            var fontFamily = new MediaFontFamily(fontFamilyName);
            CurrentLyricText.FontFamily = fontFamily;
            PreviousLyricText.FontFamily = fontFamily;
            NextLyricText.FontFamily = fontFamily;
            SubtitleText.FontFamily = fontFamily;
        }
        catch (ArgumentException)
        {
            CurrentLyricText.FontFamily = new MediaFontFamily("Segoe UI");
            PreviousLyricText.FontFamily = new MediaFontFamily("Segoe UI");
            NextLyricText.FontFamily = new MediaFontFamily("Segoe UI");
            SubtitleText.FontFamily = new MediaFontFamily("Segoe UI");
        }

        CurrentLyricGlowEffect.Color = ParseColorOrFallback(_settings.GlowColor, Colors.White);
        CurrentLyricGlowEffect.Opacity = Math.Clamp(_settings.GlowOpacity, 0.0, 1.0);

        UpdateTrayState();
        ApplyClickThrough();
        UpdateTextMaxWidths(Width);
        RefreshLyricLayout();
    }

    private void ApplyClickThrough()
    {
        if (!_sourceInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _windowInteropService.SetClickThrough(handle, _settings.ClickThrough);
        UpdateMouseTracker(handle);
    }

    private void UpdateMouseTracker(nint hwnd)
    {
        if (_settings.ClickThrough && _settings.HoverFadeEnabled)
        {
            if (_mouseTracker is null)
            {
                _mouseTracker = new MouseTracker();
                _mouseTracker.MouseOverChanged += OnMouseTrackerOverChanged;
            }

            _mouseTracker.Start(hwnd);
        }
        else
        {
            StopMouseTracker();
        }
    }

    private void StopMouseTracker()
    {
        if (_mouseTracker is not null)
        {
            _mouseTracker.MouseOverChanged -= OnMouseTrackerOverChanged;
            _mouseTracker.Dispose();
            _mouseTracker = null;
        }
    }

    private void OnMouseTrackerOverChanged(bool isOver)
    {
        Dispatcher.Invoke(() =>
        {
            _isHovering = isOver;
            RefreshOverlayOpacity();
        });
    }

    private void ShellBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough)
        {
            return;
        }

        DragMove();
    }

    private void ResizeThumb_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.ClickThrough || _settings.PureMode || sender is not FrameworkElement element)
        {
            return;
        }

        if (!Enum.TryParse<ResizeDirection>(element.Tag?.ToString(), ignoreCase: true, out var direction))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _windowInteropService.BeginResize(handle, direction);
        e.Handled = true;
    }

    private void ToggleOverlayVisibility()
    {
        if (IsVisible)
        {
            _overlayHiddenByUser = true;
            Hide();
        }
        else
        {
            _overlayHiddenByUser = false;
            Show();
            Activate();
        }

        UpdateTrayState();
    }

    private void ShowOverlay()
    {
        _overlayHiddenByUser = false;
        if (!IsVisible)
        {
            Show();
        }

        Activate();
        UpdateTrayState();
    }

    private void OpenSettingsWindow()
    {
        var settingsWindow = new SettingsWindow(
            _settings,
            previewSettings => ApplySettings(previewSettings, persist: true))
        {
            Owner = IsVisible ? this : null,
        };

        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        ApplySettings(settingsWindow.Settings, persist: true);
    }

    private void SetPureMode(bool enabled)
    {
        // Save current position before switching mode
        if (WindowState == WindowState.Normal)
        {
            if (_settings.PureMode)
            {
                _settings.PureModeWindowX = (int)Math.Round(Left + Width / 2.0);
                _settings.PureModeWindowY = (int)Math.Round(Top + Height / 2.0);
            }
            else
            {
                _settings.WindowX = (int)Math.Round(Left);
                _settings.WindowY = (int)Math.Round(Top);
                _settings.WindowWidth = (int)Math.Round(Width);
                _settings.WindowHeight = (int)Math.Round(Height);
            }
        }

        _settings.PureMode = enabled;
        ApplyWindowBounds();
        ApplyAppearanceSettings();
        PersistCurrentSettings();
    }

    private void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        ApplyAppearanceSettings();
        PersistCurrentSettings();
    }

    private void SetSingleLineMode(bool enabled)
    {
        _settings.SingleLineMode = enabled;
        if (enabled)
        {
            _settings.TwoLineMode = false;
        }

        ApplyAppearanceSettings();
        PersistCurrentSettings();
    }

    private void SetTwoLineMode(bool enabled)
    {
        _settings.TwoLineMode = enabled;
        if (enabled)
        {
            _settings.SingleLineMode = false;
        }

        ApplyAppearanceSettings();
        PersistCurrentSettings();
    }

    private void SetDebugPanelVisibility(bool enabled)
    {
        _settings.ShowDebugPanel = enabled;
        ApplyAppearanceSettings();
        PersistCurrentSettings();
    }

    private void ApplySettings(AppSettings sourceSettings, bool persist)
    {
        // Save current position before applying settings
        var currentLeft = Left;
        var currentTop = Top;
        var currentWidth = Width;
        var currentHeight = Height;

        CopySettings(sourceSettings, _settings);
        _runtimeService.LyricsOffsetSeconds = _settings.LyricsOffsetSeconds;

        // Apply appearance settings but preserve position
        ApplyAppearanceSettings();

        // Restore position (don't reset to saved settings position)
        Left = currentLeft;
        Top = currentTop;
        if (!_settings.PureMode)
        {
            Width = currentWidth;
            Height = currentHeight;
        }

        if (persist)
        {
            PersistCurrentSettings();
        }
    }

    private void PersistCurrentSettings()
    {
        if (WindowState == WindowState.Normal)
        {
            if (_settings.PureMode)
            {
                // PureMode: save center point — ApplyWindowBounds starts at Width=0 so Left=center
                _settings.PureModeWindowX = (int)Math.Round(Left + Width / 2.0);
                _settings.PureModeWindowY = (int)Math.Round(Top + Height / 2.0);
            }
            else
            {
                // Normal mode: save both position and size
                _settings.WindowX = (int)Math.Round(Left);
                _settings.WindowY = (int)Math.Round(Top);
                _settings.WindowWidth = (int)Math.Round(Width);
                _settings.WindowHeight = (int)Math.Round(Height);
            }
        }

        _settingsStore.Save(_settings);
    }

    private void RefreshLyricLayout()
    {
        if (_settings.PureMode && IsLoaded)
        {
            ApplyPureModeAutoSize();
            return;
        }

        ApplyAdaptiveFontSizes();
    }

    private void ApplyPureModeAutoSize()
    {
        if (_isApplyingPureModeAutoSize)
        {
            return;
        }

        // Measure the card (content + chrome) the lyrics need, clamped to the fixed window.
        var targetSize = MeasurePureModeWindowSize();
        var cardWidth = Math.Clamp(targetSize.Width, 144, _pureWindowWidth);
        var cardHeight = Math.Clamp(targetSize.Height, 70, _pureWindowHeight);

        _isApplyingPureModeAutoSize = true;
        try
        {
            UpdateTextMaxWidths(cardWidth);
            ApplyAdaptiveFontSizes(cardWidth, cardHeight);

            // Already animating toward this exact card size — let it finish.
            if (_isAnimatingHeight
                && Math.Abs(_targetWidth - cardWidth) < 2
                && Math.Abs(_targetHeight - cardHeight) < 2)
            {
                return;
            }

            // No meaningful change and nothing in flight — just make sure the clip is correct.
            if (!_isAnimatingHeight
                && Math.Abs(_lastCardWidth - cardWidth) < 2
                && Math.Abs(_lastCardHeight - cardHeight) < 2
                && ReferenceEquals(ShellBorder.Clip, _clipGeometry))
            {
                return;
            }

            // Start (or restart) the clip reveal from the CURRENT visible card size so the
            // transition is continuous even if a previous one is still running.
            const double shellMargin = 6.0;
            var fromW = _lastCardWidth > 0 ? _lastCardWidth : cardWidth;
            var fromH = _lastCardHeight > 0 ? _lastCardHeight : cardHeight;
            if (_isAnimatingHeight && ReferenceEquals(ShellBorder.Clip, _clipGeometry))
            {
                fromW = _clipGeometry.Rect.Width + shellMargin * 2;
                fromH = _clipGeometry.Rect.Height + shellMargin * 2;
            }

            _widthAnimFrom = fromW;
            _heightAnimFrom = fromH;
            _targetWidth = cardWidth;
            _targetHeight = cardHeight;
            _lastCardWidth = cardWidth;
            _lastCardHeight = cardHeight;

            ApplyRevealClip(0.0);
            ShellBorder.Clip = _clipGeometry;

            _heightAnimStopwatch.Restart();
            _isAnimatingHeight = true;
        }
        finally
        {
            _isApplyingPureModeAutoSize = false;
        }
    }

    private System.Windows.Size MeasurePureModeWindowSize()
    {
        // Use PureMode-specific padding values
        var shellMargin = 6.0;
        var shellPadding = 6.0;
        var panelMarginVertical = 2.0;

        var chromeWidth = shellMargin * 2 + shellPadding * 2;
        var chromeHeight = shellMargin * 2 + shellPadding * 2;
        var panelHeightPadding = panelMarginVertical * 2;
        var extraWidthPadding = chromeWidth;
        var extraHeightPadding = chromeHeight + panelHeightPadding;

        var maxContentWidth = 1400 - extraWidthPadding;
        var maxContentHeight = 520 - extraHeightPadding;
        var minContentWidth = 120;

        var currentFontSize = _settings.MaxCurrentFontSize;
        var contextScale = _settings.TwoLineMode ? 0.72 : 0.56;
        var contextFontSize = Math.Clamp(currentFontSize * contextScale, 12, _settings.ContextFontSize);
        var desiredContentWidth = MeasureNaturalContentWidth(currentFontSize, contextFontSize);
        var contentWidth = Math.Clamp(desiredContentWidth, minContentWidth, maxContentWidth);
        var contentHeight = MeasureContentHeight(contentWidth, currentFontSize, contextFontSize);

        if (contentHeight > maxContentHeight)
        {
            var low = contentWidth;
            var high = maxContentWidth;
            var bestWidth = maxContentWidth;

            for (var iteration = 0; iteration < 12; iteration++)
            {
                var mid = (low + high) / 2.0;
                var midHeight = MeasureContentHeight(mid, currentFontSize, contextFontSize);
                if (midHeight <= maxContentHeight)
                {
                    bestWidth = mid;
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }

            contentWidth = bestWidth;
            contentHeight = MeasureContentHeight(contentWidth, currentFontSize, contextFontSize);
        }

        return new System.Windows.Size(
            Math.Ceiling(contentWidth + extraWidthPadding),
            Math.Ceiling(Math.Min(contentHeight, maxContentHeight) + extraHeightPadding));
    }

    private double MeasureNaturalContentWidth(double currentFontSize, double contextFontSize)
    {
        var widths = new List<double>
        {
            MeasureText(CurrentLyricText.Text, CurrentLyricText, currentFontSize, null, 1).Width,
        };

        if (PreviousLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(PreviousLyricText.Text))
        {
            widths.Add(MeasureText(PreviousLyricText.Text, PreviousLyricText, contextFontSize, null, 1).Width);
        }

        if (NextLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(NextLyricText.Text))
        {
            widths.Add(MeasureText(NextLyricText.Text, NextLyricText, contextFontSize, null, 1).Width);
        }

        return widths.Count == 0 ? MinWidth : widths.Max() + 24;
    }

    private double MeasureContentHeight(double contentWidth, double currentFontSize, double contextFontSize)
    {
        var totalHeight = 0.0;
        var currentMeasure = MeasureText(
            CurrentLyricText.Text,
            CurrentLyricText,
            currentFontSize,
            contentWidth,
            _settings.SingleLineMode ? 1 : (_settings.PureMode ? 4 : 2));
        totalHeight += currentMeasure.Height;

        // Use PureMode-specific margin values
        var currentMarginTop = _settings.PureMode ? 2.0 : 4.0;
        var currentMarginBottom = _settings.PureMode ? (_settings.TwoLineMode ? 4.0 : 2.0) : (_settings.TwoLineMode ? 10.0 : 4.0);

        var showNext = _settings.TwoLineMode && !_settings.SingleLineMode && NextLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(NextLyricText.Text);
        var showPrevious = !_settings.TwoLineMode && !_settings.SingleLineMode && PreviousLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(PreviousLyricText.Text);

        if (showNext)
        {
            totalHeight += currentMarginBottom;
            totalHeight += MeasureText(NextLyricText.Text, NextLyricText, contextFontSize, contentWidth, 2).Height;
        }
        else
        {
            if (showPrevious)
            {
                totalHeight += MeasureText(PreviousLyricText.Text, PreviousLyricText, contextFontSize, contentWidth, _settings.PureMode ? 4 : 2).Height;
            }

            totalHeight += currentMarginTop + currentMarginBottom;

            if (NextLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(NextLyricText.Text))
            {
                totalHeight += MeasureText(NextLyricText.Text, NextLyricText, contextFontSize, contentWidth, _settings.PureMode ? 4 : 2).Height;
            }
        }

        return totalHeight;
    }

    private System.Windows.Size MeasureText(
        string? text,
        TextBlock reference,
        double fontSize,
        double? maxWidth,
        int maxLines)
    {
        var content = string.IsNullOrWhiteSpace(text) ? " " : text.Trim();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(
            reference.FontFamily,
            reference.FontStyle,
            reference.FontWeight,
            reference.FontStretch);

        var formattedText = new FormattedText(
            content,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.White,
            pixelsPerDip);

        if (maxWidth.HasValue)
        {
            formattedText.MaxTextWidth = maxWidth.Value;
        }

        formattedText.MaxLineCount = maxLines;
        formattedText.Trimming = maxLines == 1 ? TextTrimming.CharacterEllipsis : TextTrimming.None;

        return new System.Windows.Size(Math.Ceiling(formattedText.Width), Math.Ceiling(formattedText.Height));
    }

    private void UpdateTextMaxWidths(double windowWidth)
    {
        var shellMargin = _settings.PureMode ? 6.0 : 14.0;
        var shellPadding = _settings.PureMode ? 6.0 : 12.0;
        var totalHorizontalChrome = shellMargin * 2 + shellPadding * 2;

        var currentMaxWidth = Math.Max(200, windowWidth - totalHorizontalChrome - 16);
        var contextMaxWidth = Math.Max(180, windowWidth - totalHorizontalChrome - 24);
        CurrentLyricText.MaxWidth = currentMaxWidth;
        PreviousLyricText.MaxWidth = contextMaxWidth;
        NextLyricText.MaxWidth = contextMaxWidth;
    }

    private Rect GetWorkingArea()
    {
        if (_sourceInitialized)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var screen = Forms.Screen.FromHandle(handle);
            var dpiScale = GetDpiScaleFactor();

            // WinForms uses pixel coordinates, WPF uses device-independent units (DIPs)
            // Convert pixel coordinates to DIPs by dividing by DPI scale
            return new Rect(
                screen.WorkingArea.Left / dpiScale,
                screen.WorkingArea.Top / dpiScale,
                screen.WorkingArea.Width / dpiScale,
                screen.WorkingArea.Height / dpiScale);
        }

        return new Rect(
            SystemParameters.WorkArea.Left,
            SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);
    }

    private double GetDpiScaleFactor()
    {
        if (_sourceInitialized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return dpi.DpiScaleX;
        }
        return 1.0;
    }

    private Rect GetCurrentScreenBounds()
    {
        if (_sourceInitialized)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var screen = Forms.Screen.FromHandle(handle);
            var dpiScale = GetDpiScaleFactor();

            return new Rect(
                screen.Bounds.Left / dpiScale,
                screen.Bounds.Top / dpiScale,
                screen.Bounds.Width / dpiScale,
                screen.Bounds.Height / dpiScale);
        }

        // Default to primary screen bounds
        return new Rect(
            0, 0,
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight);
    }

    private void UpdateTrayState()
    {
        _showHideMenuItem.Text = IsVisible ? "Hide overlay" : "Show overlay";
        _clickThroughMenuItem.Checked = _settings.ClickThrough;
        _pureModeMenuItem.Checked = _settings.PureMode;
        _singleLineMenuItem.Checked = _settings.SingleLineMode;
        _twoLineMenuItem.Checked = _settings.TwoLineMode;
        _debugPanelMenuItem.Checked = _settings.ShowDebugPanel;
        _notifyIcon.Text = BuildTrayText();
    }

    private (Forms.NotifyIcon NotifyIcon, Forms.ToolStripMenuItem ShowHideMenuItem, Forms.ToolStripMenuItem ClickThroughMenuItem, Forms.ToolStripMenuItem PureModeMenuItem, Forms.ToolStripMenuItem SingleLineMenuItem, Forms.ToolStripMenuItem TwoLineMenuItem, Forms.ToolStripMenuItem DebugPanelMenuItem) CreateNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();

        var showHideMenuItem = new Forms.ToolStripMenuItem("Hide overlay");
        showHideMenuItem.Click += (_, _) => Dispatcher.Invoke(ToggleOverlayVisibility);

        var clickThroughMenuItem = new Forms.ToolStripMenuItem("Click through");
        clickThroughMenuItem.Click += (_, _) => Dispatcher.Invoke(() => SetClickThrough(!_settings.ClickThrough));

        var pureModeMenuItem = new Forms.ToolStripMenuItem("Pure mode");
        pureModeMenuItem.Click += (_, _) => Dispatcher.Invoke(() => SetPureMode(!_settings.PureMode));

        var singleLineMenuItem = new Forms.ToolStripMenuItem("Single-line mode");
        singleLineMenuItem.Click += (_, _) => Dispatcher.Invoke(() => SetSingleLineMode(!_settings.SingleLineMode));

        var twoLineMenuItem = new Forms.ToolStripMenuItem("Two-line mode");
        twoLineMenuItem.Click += (_, _) => Dispatcher.Invoke(() => SetTwoLineMode(!_settings.TwoLineMode));

        var debugPanelMenuItem = new Forms.ToolStripMenuItem("Show debug panel");
        debugPanelMenuItem.Click += (_, _) => Dispatcher.Invoke(() => SetDebugPanelVisibility(!_settings.ShowDebugPanel));

        var settingsMenuItem = new Forms.ToolStripMenuItem("Settings...");
        settingsMenuItem.Click += (_, _) => Dispatcher.Invoke(OpenSettingsWindow);

        var exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            Close();
        });

        menu.Items.Add(showHideMenuItem);
        menu.Items.Add(clickThroughMenuItem);
        menu.Items.Add(pureModeMenuItem);
        menu.Items.Add(singleLineMenuItem);
        menu.Items.Add(twoLineMenuItem);
        menu.Items.Add(debugPanelMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(settingsMenuItem);
        menu.Items.Add(exitMenuItem);

        var notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIconForTray(),
            Visible = true,
            Text = BuildTrayText(),
            ContextMenuStrip = menu,
        };

        notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowOverlay);
        return (notifyIcon, showHideMenuItem, clickThroughMenuItem, pureModeMenuItem, singleLineMenuItem, twoLineMenuItem, debugPanelMenuItem);
    }

    private string BuildTrayText()
    {
        var baseText = CurrentLyricText.Text;
        if (string.IsNullOrWhiteSpace(baseText))
        {
            return "Apple Music Lyrics";
        }

        baseText = baseText.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        const string prefix = "Apple Music Lyrics - ";
        const int maxLength = 63;
        var available = maxLength - prefix.Length;
        if (available <= 3)
        {
            return "Apple Music Lyrics";
        }

        var trimmed = baseText.Length <= available
            ? baseText
            : $"{baseText[..(available - 3)]}...";
        return $"{prefix}{trimmed}";
    }

    private static System.Windows.Media.Brush CreateBrush(string colorValue, MediaColor fallbackColor)
    {
        return new SolidColorBrush(ParseColorOrFallback(colorValue, fallbackColor));
    }

    private static MediaColor ParseColorOrFallback(string colorValue, MediaColor fallbackColor)
    {
        try
        {
            return (MediaColor)MediaColorConverter.ConvertFromString(colorValue)!;
        }
        catch (FormatException)
        {
            return fallbackColor;
        }
        catch (NotSupportedException)
        {
            return fallbackColor;
        }
    }

    private double FitFontSize(
        string text,
        double maxSize,
        double minSize,
        double maxWidth,
        double maxHeight,
        bool singleLine)
    {
        var fontFamily = CurrentLyricText.FontFamily ?? new MediaFontFamily("Segoe UI");
        var typeface = new Typeface(
            fontFamily,
            CurrentLyricText.FontStyle,
            CurrentLyricText.FontWeight,
            CurrentLyricText.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var size = maxSize; size >= minSize; size -= 1.0)
        {
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                size,
                System.Windows.Media.Brushes.White,
                pixelsPerDip)
            {
                MaxTextWidth = maxWidth,
                MaxTextHeight = maxHeight,
                Trimming = singleLine ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            };

            if (!singleLine)
            {
                formattedText.MaxLineCount = 2;
            }

            if (formattedText.Width <= maxWidth && formattedText.Height <= maxHeight)
            {
                return size;
            }
        }

        return minSize;
    }

    private static System.Windows.Media.ImageSource? LoadAppIcon()
    {
        var uri = new Uri("pack://application:,,,/icon.ico", UriKind.Absolute);
        try
        {
            return new System.Windows.Media.Imaging.BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    private static Drawing.Icon LoadAppIconForTray()
    {
        var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/icon.ico", UriKind.Absolute))?.Stream;
        if (stream is not null)
        {
            return new Drawing.Icon(stream, new Drawing.Size(16, 16));
        }
        return Drawing.SystemIcons.Application;
    }

    private static string GetSettingsPath()
    {
        return IOPath.Combine(AppContext.BaseDirectory, "settings.ini");
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        var copy = source.Clone();
        target.LyricsPollInterval = copy.LyricsPollInterval;
        target.PlayerPollInterval = copy.PlayerPollInterval;
        target.CliRenderInterval = copy.CliRenderInterval;
        target.UiRefreshInterval = copy.UiRefreshInterval;
        target.LyricsOffsetSeconds = copy.LyricsOffsetSeconds;
        target.PreviewLineCount = copy.PreviewLineCount;
        target.RecentFileGraceSeconds = copy.RecentFileGraceSeconds;
        target.WindowX = copy.WindowX;
        target.WindowY = copy.WindowY;
        target.WindowWidth = copy.WindowWidth;
        target.WindowHeight = copy.WindowHeight;
        target.PureModeWindowX = copy.PureModeWindowX;
        target.PureModeWindowY = copy.PureModeWindowY;
        target.MinWindowWidth = copy.MinWindowWidth;
        target.MinWindowHeight = copy.MinWindowHeight;
        target.MaxCurrentFontSize = copy.MaxCurrentFontSize;
        target.ContextFontSize = copy.ContextFontSize;
        target.CurrentLineColor = copy.CurrentLineColor;
        target.ContextLineColor = copy.ContextLineColor;
        target.PausedLineColor = copy.PausedLineColor;
        target.GlowColor = copy.GlowColor;
        target.GlowOpacity = copy.GlowOpacity;
        target.FontFamily = copy.FontFamily;
        target.ShowPreviousLine = copy.ShowPreviousLine;
        target.ShowNextLine = copy.ShowNextLine;
        target.SingleLineMode = copy.SingleLineMode;
        target.TwoLineMode = copy.TwoLineMode;
        target.ShowDebugPanel = copy.ShowDebugPanel;
        target.AutoHideNoLyrics = copy.AutoHideNoLyrics;
        target.PureMode = copy.PureMode;
        target.ClickThrough = copy.ClickThrough;
        target.OverlayOpacity = copy.OverlayOpacity;
        target.BackgroundAlpha = copy.BackgroundAlpha;
        target.HoverFadeEnabled = copy.HoverFadeEnabled;
        target.HoverFadeDuration = copy.HoverFadeDuration;
        target.HoverFadeMinOpacity = copy.HoverFadeMinOpacity;
    }
}
