using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    public MainWindow()
    {
        _settingsStore = new IniSettingsStore(GetSettingsPath());
        _settings = _settingsStore.Load();
        _windowInteropService = new WindowInteropService();

        InitializeComponent();
        (_notifyIcon, _showHideMenuItem, _clickThroughMenuItem, _pureModeMenuItem, _singleLineMenuItem, _twoLineMenuItem, _debugPanelMenuItem) = CreateNotifyIcon();
        ApplyWindowBounds();
        ApplyAppearanceSettings();

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;
        LocationChanged += OnWindowLocationChanged;

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
        ApplyClickThrough();
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
        var shouldAnimate = animate && !string.Equals(_lastLyricAnimationKey, lyricKey, StringComparison.Ordinal);

        if (shouldAnimate)
        {
            PrepareCurrentLyricAnimation();
        }
        else
        {
            ResetCurrentLyricVisualState();
        }

        PreviousLyricText.Text = _settings.ShowPreviousLine ? previousText ?? string.Empty : string.Empty;
        CurrentLyricText.Text = currentText;
        NextLyricText.Text = _settings.ShowNextLine ? nextText ?? string.Empty : string.Empty;

        CurrentLyricText.Foreground = CreateBrush(isPlaying ? _settings.CurrentLineColor : _settings.PausedLineColor, Colors.White);
        PreviousLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));
        NextLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));

        if (shouldAnimate)
        {
            AnimateCurrentLyric();
        }

        _lastLyricAnimationKey = lyricKey;
        RefreshLyricLayout();
    }

    private void PrepareCurrentLyricAnimation()
    {
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CurrentLyricText.BeginAnimation(OpacityProperty, null);
        CurrentLyricTransform.Y = 10;
        CurrentLyricText.Opacity = 0.0;
    }

    private void ResetCurrentLyricVisualState()
    {
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CurrentLyricText.BeginAnimation(OpacityProperty, null);
        CurrentLyricTransform.Y = 0;
        CurrentLyricText.Opacity = 1.0;
    }

    private void AnimateCurrentLyric()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        CurrentLyricTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
        CurrentLyricText.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
    }

    private void UpdateOverlayVisibility(RuntimeSnapshot snapshot)
    {
        if (_settings.AutoHideNoLyrics && snapshot.Document is null)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible && !_overlayHiddenByUser)
        {
            Show();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        PersistCurrentSettings();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApplyingPureModeAutoSize)
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
        // Update saved position when user moves the window (but not during auto-size animation)
        if (_isApplyingPureModeAutoSize)
        {
            return;
        }

        // Update PureMode position immediately when window is moved
        if (_settings.PureMode && WindowState == WindowState.Normal)
        {
            _settings.PureModeWindowX = (int)Math.Round(Left);
            _settings.PureModeWindowY = (int)Math.Round(Top);
        }
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
            // PureMode uses its own independent position
            Left = Math.Max(_settings.PureModeWindowX, -32000);
            Top = Math.Max(_settings.PureModeWindowY, -32000);
            // Width and height are calculated dynamically by ApplyPureModeAutoSize
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
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);

        // Hide resize controls in PureMode
        ResizeLayer.Visibility = _settings.PureMode ? Visibility.Collapsed : Visibility.Visible;

        ShellBorder.Padding = _settings.PureMode ? new Thickness(6) : new Thickness(12);
        ShellBorder.Margin = _settings.PureMode ? new Thickness(6) : new Thickness(14);
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
            PreviousLyricRow.Height = new GridLength(1, GridUnitType.Star);
            CurrentLyricRow.Height = GridLength.Auto;
            NextLyricRow.Height = new GridLength(1, GridUnitType.Star);
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
                _settings.PureModeWindowX = (int)Math.Round(Left);
                _settings.PureModeWindowY = (int)Math.Round(Top);
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
                // PureMode: only save position, size is auto-calculated
                _settings.PureModeWindowX = (int)Math.Round(Left);
                _settings.PureModeWindowY = (int)Math.Round(Top);
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

        // Calculate new size based on content
        var targetSize = MeasurePureModeWindowSize();
        var clampedWidth = targetSize.Width;
        var clampedHeight = targetSize.Height;
        var centerX = Left + (Width / 2.0);
        var centerY = Top + (Height / 2.0);

        var widthDelta = Math.Abs(clampedWidth - Width);
        var heightDelta = Math.Abs(clampedHeight - Height);
        if (widthDelta < 4 && heightDelta < 4)
        {
            _isApplyingPureModeAutoSize = true;
            try
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(HeightProperty, null);
                Width = clampedWidth;
                Height = clampedHeight;
                Left = centerX - (clampedWidth / 2.0);
                Top = centerY - (clampedHeight / 2.0);
                _settings.PureModeWindowX = (int)Math.Round(Left);
                _settings.PureModeWindowY = (int)Math.Round(Top);
                UpdateTextMaxWidths(clampedWidth);
                ApplyAdaptiveFontSizes(clampedWidth, clampedHeight);
            }
            finally
            {
                _isApplyingPureModeAutoSize = false;
            }
            return;
        }

        _isApplyingPureModeAutoSize = true;
        try
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = clampedWidth;
            Height = clampedHeight;
            Left = centerX - (clampedWidth / 2.0);
            Top = centerY - (clampedHeight / 2.0);
            _settings.PureModeWindowX = (int)Math.Round(Left);
            _settings.PureModeWindowY = (int)Math.Round(Top);
            UpdateTextMaxWidths(clampedWidth);
            ApplyAdaptiveFontSizes(clampedWidth, clampedHeight);
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

        if (PreviousLyricText.Visibility == Visibility.Visible)
        {
            widths.Add(MeasureText(PreviousLyricText.Text, PreviousLyricText, contextFontSize, null, 1).Width);
        }

        if (NextLyricText.Visibility == Visibility.Visible)
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

        if (_settings.TwoLineMode && !_settings.SingleLineMode && NextLyricText.Visibility == Visibility.Visible)
        {
            totalHeight += currentMarginBottom;
            totalHeight += MeasureText(NextLyricText.Text, NextLyricText, contextFontSize, contentWidth, 2).Height;
        }
        else
        {
            if (PreviousLyricText.Visibility == Visibility.Visible)
            {
                totalHeight += MeasureText(PreviousLyricText.Text, PreviousLyricText, contextFontSize, contentWidth, _settings.PureMode ? 4 : 2).Height;
            }

            totalHeight += currentMarginTop + currentMarginBottom;

            if (NextLyricText.Visibility == Visibility.Visible)
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
            Icon = Drawing.SystemIcons.Application,
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
    }
}
