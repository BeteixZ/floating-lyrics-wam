using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.App.Controllers;
using AppleMusicLyrics.App.Composition;
using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Display;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using AppleMusicLyrics.Infrastructure.Windows.Configuration;
using AppleMusicLyrics.Infrastructure.Windows.Display;
using AppleMusicLyrics.Infrastructure.Windows.External;
using AppleMusicLyrics.Infrastructure.Windows.Interop;
using AppleMusicLyrics.Infrastructure.Windows.Media;
using IOPath = System.IO.Path;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace AppleMusicLyrics.App;

public partial class MainWindow : Window
{
    private readonly LyricsRuntimeService _runtimeService;
    private readonly GlobalMediaSessionProvider _playerProvider;
    private readonly ITunesCatalogSongResolver? _catalogResolver;
    private readonly LrcLibLyricsProvider? _lrcLibProvider;
    private readonly RuntimePollingController _runtimePollingController;
    private readonly AppSettings _settings;
    private readonly IniSettingsStore _settingsStore;
    private readonly WindowInteropService _windowInteropService;
    private readonly WindowPlacementCoordinator _windowPlacementCoordinator;
    private readonly TrayIconController _trayIconController;
    private bool _sourceInitialized;
    private bool _isClosed;
    private bool _runtimeRefreshFailed;
    private bool _hasRuntimeSnapshot;
    private bool _hasFrameLyricState;
    private LyricsDocument? _lastFrameDocument;
    private int? _lastFrameLyricIndex;
    private bool _lastFramePlaying;
    private RuntimeSnapshot? _pendingFrameSnapshot;
    private bool _frameSnapshotApplyScheduled;
    private readonly VisualFrameStatistics _visualFrameStatistics = new();
    private bool _isRenderingSubscribed;
    private bool _isApplyingPureModeAutoSize;
    private bool _isApplyingWindowPlacement;
    private readonly WindowMessageCoordinator _windowMessageCoordinator = new();
    private bool _overlayHiddenByUser;
    private string? _lastLyricAnimationKey;
    private bool _isHovering;
    private bool _isWindowDragging;
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
    private PixelRect _pureBoundsAnimFrom;
    private PixelRect _pureBoundsAnimTarget;

    public MainWindow()
    {
        var composition = AppCompositionRoot.Create();
        _settingsStore = composition.SettingsStore;
        _settings = composition.Settings;
        _playerProvider = composition.PlayerProvider;
        _catalogResolver = composition.CatalogResolver;
        _lrcLibProvider = composition.ExternalLyricsProvider;
        _runtimeService = composition.RuntimeService;
        _windowInteropService = new WindowInteropService();
        _windowPlacementCoordinator = new WindowPlacementCoordinator();

        InitializeComponent();
        Icon = LoadAppIcon();
        _trayIconController = new TrayIconController(
            Dispatcher,
            ToggleOverlayVisibility,
            () => SetClickThrough(!_settings.ClickThrough),
            () => SetPureMode(!_settings.PureMode),
            () => SetSingleLineMode(!_settings.SingleLineMode),
            () => SetTwoLineMode(!_settings.TwoLineMode),
            () => SetDebugPanelVisibility(!_settings.ShowDebugPanel),
            OpenSettingsWindow,
            Close);
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
        IsVisibleChanged += OnIsVisibleChanged;

        _runtimePollingController = new RuntimePollingController(
            _runtimeService,
            TimeSpan.FromSeconds(Math.Clamp(_settings.PlayerPollInterval, 0.05, 1.0)),
            ApplyRuntimeSnapshot,
            HandleRuntimeError);
    }

    // A hidden overlay needs neither visual frames nor hover sampling.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderingSubscription();
        if (_sourceInitialized && !_isClosed)
        {
            UpdateMouseTracker(new WindowInteropHelper(this).Handle);
        }
    }

    // While any handler is attached, CompositionTarget.Rendering keeps WPF composing at the display
    // refresh rate even when nothing changes. Attach only while a frame can change the screen: a
    // bounds animation, lyrics advancing during playback, or the live debug readout.
    private void UpdateRenderingSubscription()
    {
        var needed = !_isClosed
            && (_isAnimatingHeight
                || (IsVisible
                    && ((_hasRuntimeSnapshot && !_runtimeRefreshFailed && _lastPlaying && _lastHasLyrics)
                        || FooterPanel.Visibility == Visibility.Visible)));
        if (needed == _isRenderingSubscribed)
        {
            return;
        }

        _isRenderingSubscribed = needed;
        if (needed)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var publishedFrameRate = e is RenderingEventArgs renderingEventArgs
            && _visualFrameStatistics.RecordFrame(renderingEventArgs.RenderingTime);

        TickHeightAnimation();

        if (_runtimeRefreshFailed)
        {
            if (publishedFrameRate)
            {
                TimingText.Text = $"Playback clock refresh failed | {FormatRenderRate()} | {FormatPollRate()}";
            }

            return;
        }

        if (!_hasRuntimeSnapshot)
        {
            return;
        }

        var snapshot = _runtimeService.GetFrameSnapshot();
        QueueFrameSnapshot(snapshot);
        if (publishedFrameRate)
        {
            UpdateTimingText(snapshot);
        }
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

        await _runtimePollingController.StartAsync();
        if (_isClosed)
        {
            return;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _sourceInitialized = true;

        // Install the native message hook used for DPI, display-topology, and drag state.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProcHook);
        }

        ApplyRestoredPlacement();
        if (_settings.PureMode)
        {
            RefreshLyricLayout();
        }

        ApplyClickThrough();
    }

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_DPICHANGED = 0x02E0;

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_DPICHANGED:
                if (!_isApplyingWindowPlacement
                    && _windowPlacementCoordinator.TryReadSuggestedRect(lParam, out _))
                {
                    // PerMonitorV2 WPF owns DPI layout and applies the suggested physical RECT.
                    // Recalculate the content-sized Pure Mode bounds after adopting the new DPI.
                    CancelPureModeBoundsAnimation();
                    QueuePureModeLayoutRefresh();
                    QueueWindowStateCommit(reconcileDisplayTopology: false);
                }

                break;

            case WM_DISPLAYCHANGE:
                CancelPureModeBoundsAnimation();
                QueueWindowStateCommit(reconcileDisplayTopology: true);
                break;

            case WM_ENTERSIZEMOVE:
                CancelPureModeBoundsAnimation();
                _windowMessageCoordinator.EnterSizeMove();
                SetWindowDragging(true);
                break;

            case WM_EXITSIZEMOVE:
                SetWindowDragging(false);
                if (_windowMessageCoordinator.ExitSizeMove())
                {
                    ScheduleWindowStateCommit();
                }

                break;
        }

        return IntPtr.Zero;
    }

    private void ApplyNativeWindowRect(nint hwnd, PixelRect rect)
    {
        if (_isApplyingWindowPlacement)
        {
            return;
        }

        _isApplyingWindowPlacement = true;
        try
        {
            _windowPlacementCoordinator.SetWindowRect(hwnd, rect);
        }
        finally
        {
            _isApplyingWindowPlacement = false;
        }
    }

    private void QueueWindowStateCommit(bool reconcileDisplayTopology)
    {
        if (_windowMessageCoordinator.RequestCommit(reconcileDisplayTopology))
        {
            ScheduleWindowStateCommit();
        }
    }

    private void ScheduleWindowStateCommit()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_sourceInitialized)
            {
                return;
            }

            var request = _windowMessageCoordinator.TakeScheduledCommit();
            if (request == WindowStateCommitRequest.None)
            {
                return;
            }

            var shouldPersist = request != WindowStateCommitRequest.ReconcileDisplayTopologyAndPersist
                || ReconcileDisplayTopology();
            if (shouldPersist)
            {
                PersistCurrentSettings();
            }
        }, DispatcherPriority.Loaded);
    }

    private bool ReconcileDisplayTopology()
    {
        var monitors = _windowPlacementCoordinator.GetMonitors();
        if (monitors.Count == 0)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var currentRect = _windowPlacementCoordinator.GetWindowRect(hwnd);
        var pureModeSize = _settings.PureMode ? GetDesiredPureModeCardSize() : default;
        var desiredWidth = _settings.PureMode
            ? pureModeSize.Width
            : GetCurrentLogicalSize(ActualWidth, Width, _settings.WindowWidth);
        var desiredHeight = _settings.PureMode
            ? pureModeSize.Height
            : GetCurrentLogicalSize(ActualHeight, Height, _settings.WindowHeight);
        var placement = _windowPlacementCoordinator.Resolve(
            _settings,
            _settings.PureMode,
            desiredWidth,
            desiredHeight,
            currentRect);

        Width = placement.WidthDip;
        Height = placement.HeightDip;
        WindowPlacementCoordinator.StoreAnchor(_settings, _settings.PureMode, placement);
        if (_settings.PureMode)
        {
            UpdateTextMaxWidths(placement.WidthDip);
        }
        ApplyNativeWindowRect(hwnd, placement.WindowRectPx);
        return true;
    }

    private static double GetCurrentLogicalSize(double actual, double requested, double fallback)
    {
        if (double.IsFinite(actual) && actual > 0)
        {
            return actual;
        }

        return double.IsFinite(requested) && requested > 0 ? requested : fallback;
    }

    private void ApplyRuntimeSnapshot(RuntimeSnapshot snapshot)
    {
        _runtimeRefreshFailed = false;
        _hasRuntimeSnapshot = true;
        ApplySnapshot(snapshot);
        UpdateOverlayVisibility(snapshot);
        UpdateRenderingSubscription();
    }

    private void HandleRuntimeError(Exception ex)
    {
        _runtimeRefreshFailed = true;
        UpdateRenderingSubscription();
        _pendingFrameSnapshot = null;
        _lastLyricAnimationKey = null;
        SubtitleText.Text = "Failed to refresh runtime state.";
        PlayerText.Text = "Check Apple Music and media session availability.";
        TimingText.Text = $"Playback clock refresh failed | {FormatRenderRate()} | {FormatPollRate()}";
        StatusText.Text = "Runtime error";
        PathText.Text = ex.Message;
        CurrentLyricText.Text = "Refresh failed.";
        PreviousLyricText.Text = string.Empty;
        NextLyricText.Text = "Check Apple Music, cache state, or media session access.";
    }

    private void ApplySnapshot(RuntimeSnapshot snapshot)
    {
        // A completed poll is newer and more authoritative than any visual-frame update that
        // is still queued behind rendering. Dropping the queued snapshot also prevents a stale
        // lyric from being applied after this poll has already advanced the UI.
        _pendingFrameSnapshot = null;
        UpdatePlayerSection(snapshot);
        UpdateLyricSection(snapshot);
        RememberFrameLyricState(snapshot);
        UpdateTrayState();
    }

    private void QueueFrameSnapshot(RuntimeSnapshot snapshot)
    {
        var isPlaying = snapshot.Player?.Playing ?? false;
        if (_hasFrameLyricState
            && ReferenceEquals(_lastFrameDocument, snapshot.Document)
            && _lastFrameLyricIndex == snapshot.ActiveLyric.CurrentIndex
            && _lastFramePlaying == isPlaying)
        {
            return;
        }

        // CompositionTarget.Rendering runs inside WPF's render pass. Text measurement, the
        // outgoing-lyric snapshot, and layout invalidation must not run there or the current
        // frame misses its presentation deadline. Keep only the newest request and apply it
        // right after the render pass yields, at the same priority the height animation uses.
        _pendingFrameSnapshot = snapshot;
        if (_frameSnapshotApplyScheduled)
        {
            return;
        }

        _frameSnapshotApplyScheduled = true;
        Dispatcher.BeginInvoke(ApplyPendingFrameSnapshot, DispatcherPriority.Loaded);
    }

    private void ApplyPendingFrameSnapshot()
    {
        _frameSnapshotApplyScheduled = false;
        var snapshot = _pendingFrameSnapshot;
        _pendingFrameSnapshot = null;
        if (snapshot is not null)
        {
            ApplyFrameSnapshot(snapshot);
        }
    }

    private void ApplyFrameSnapshot(RuntimeSnapshot snapshot)
    {
        var isPlaying = snapshot.Player?.Playing ?? false;
        if (_hasFrameLyricState
            && ReferenceEquals(_lastFrameDocument, snapshot.Document)
            && _lastFrameLyricIndex == snapshot.ActiveLyric.CurrentIndex
            && _lastFramePlaying == isPlaying)
        {
            return;
        }

        UpdateLyricSection(snapshot);
        RememberFrameLyricState(snapshot);
    }

    private void RememberFrameLyricState(RuntimeSnapshot snapshot)
    {
        _lastFrameDocument = snapshot.Document;
        _lastFrameLyricIndex = snapshot.ActiveLyric.CurrentIndex;
        _lastFramePlaying = snapshot.Player?.Playing ?? false;
        _hasFrameLyricState = true;
    }

    private void UpdatePlayerSection(RuntimeSnapshot snapshot)
    {
        var presentation = OverlaySnapshotPresenter.CreatePlayer(snapshot);
        Title = presentation.WindowTitle;
        SubtitleText.Text = presentation.Subtitle;
        PlayerText.Text = presentation.PlayerText;
        UpdateTimingText(snapshot);
    }

    private void UpdateTimingText(RuntimeSnapshot snapshot)
    {
        var clockText = snapshot.Player is null
            ? "No playback clock data"
            : $"raw {TimeParser.FormatTimestamp(snapshot.RawPositionSeconds ?? 0)} | est {TimeParser.FormatTimestamp(snapshot.EstimatedPositionSeconds ?? 0)}";
        TimingText.Text = $"{clockText} | {FormatRenderRate()} | {FormatPollRate()}";
    }

    private string FormatRenderRate()
    {
        return _visualFrameStatistics.FramesPerSecond is double framesPerSecond
            ? $"render {framesPerSecond.ToString("F1", CultureInfo.InvariantCulture)} fps"
            : "render measuring";
    }

    private string FormatPollRate()
    {
        var pollsPerSecond = 1.0 / Math.Max(0.001, _runtimePollingController.Interval.TotalSeconds);
        return $"poll {pollsPerSecond.ToString("F1", CultureInfo.InvariantCulture)} Hz";
    }

    private void UpdateLyricSection(RuntimeSnapshot snapshot)
    {
        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot);
        StatusText.Text = presentation.StatusText;
        PathText.Text = presentation.PathText;
        SetLyrics(
            presentation.PreviousText,
            presentation.CurrentText,
            presentation.NextText,
            presentation.IsPlaying,
            presentation.Animate);
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

        // Freeze the outgoing lyrics so the original crossfade remains visually unchanged:
        // old lyrics fade out while the new lyrics fade in from transparent and move upward.
        // This work now runs after the render callback, so it no longer blocks the boundary frame.
        SnapshotLyricGhost();
        ApplyLyricContent(previousText, currentText, nextText, isPlaying);
        RefreshLyricLayout();

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(LyricFadeDurationMs);

        LyricPanel.BeginAnimation(OpacityProperty, null);
        LyricPanel.Opacity = 0.0;
        LyricPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = easing });
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });

        // Outgoing snapshot fades out over the same window.
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
        if (_settings.TwoLineMode && !_settings.SingleLineMode)
        {
            // In two-line layout the next line sits in an Auto row. When there is no next line
            // (the final lyric) that row must collapse to zero height — otherwise the empty
            // TextBlock still reserves a full line of font leading and, because ContentRoot is
            // vertically centered, pushes the current line visibly above center.
            NextLyricRow.Height = showNext ? GridLength.Auto : new GridLength(0);
            PreviousLyricRow.Height = new GridLength(0);
            return;
        }

        if (_settings.PureMode)
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
        _heightAnimStopwatch.Restart();
        _isAnimatingHeight = true;
        UpdateRenderingSubscription();
    }

    private const double WindowAnimDurationMs = 240.0;
    private const double LyricFadeDurationMs = 360.0;
    private const double PureModeMaxCardWidth = 1400.0;
    private const double PureModeMaxCardHeight = 560.0;

    private void TickHeightAnimation()
    {
        if (!_isAnimatingHeight)
        {
            return;
        }

        var progress = Math.Min(1.0, _heightAnimStopwatch.Elapsed.TotalMilliseconds / WindowAnimDurationMs);
        var eased = EaseInOutCubic(progress);

        if (_settings.PureMode)
        {
            if (_isWindowDragging || !_sourceInitialized)
            {
                CancelPureModeBoundsAnimation();
                return;
            }

            // The card is centered inside the HWND, so its on-screen text position follows the
            // window rect. Update the rect on every composition frame: skipping frames makes the
            // window advance in coarse steps and the centered lyrics visibly jitter.
            var hwnd = new WindowInteropHelper(this).Handle;
            var frameRect = WindowBoundsGeometry.Interpolate(
                _pureBoundsAnimFrom,
                _pureBoundsAnimTarget,
                eased);
            ApplyNativeWindowRect(hwnd, frameRect);

            if (progress >= 1.0)
            {
                _isAnimatingHeight = false;
                _heightAnimStopwatch.Stop();
                CaptureCurrentPlacement();
                UpdateRenderingSubscription();
            }

            return;
        }

        // Normal mode keeps its existing vertically centered height animation.
        var newHeight = _heightAnimFrom + (_targetHeight - _heightAnimFrom) * eased;
        if (progress >= 1.0)
        {
            _isAnimatingHeight = false;
            Height = _targetHeight;
            Top = _heightAnimCenterY - _targetHeight / 2.0;
            UpdateRenderingSubscription();
            return;
        }

        Height = newHeight;
        Top = _heightAnimCenterY - newHeight / 2.0;
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

    // Single source of truth for the window opacity. Automatic fade rules stay authoritative;
    // Pure Mode drag opacity then overrides hover dimming, followed by the base opacity.
    private double ResolveTargetOpacity()
    {
        return OverlayOpacityResolver.Resolve(
            _settings,
            _lastHasLyrics,
            _lastPlaying,
            _isHovering,
            _isWindowDragging);
    }

    private void RefreshOverlayOpacity()
    {
        AnimateOverlayOpacity(ResolveTargetOpacity());
    }

    private void SetWindowDragging(bool isDragging)
    {
        if (_isWindowDragging == isDragging)
        {
            return;
        }

        _isWindowDragging = isDragging;
        if (_settings.PureMode)
        {
            if (isDragging)
            {
                CancelPureModeBoundsAnimation();
            }
            else
            {
                QueuePureModeLayoutRefresh();
            }
        }

        RefreshOverlayOpacity();
    }

    private void CancelPureModeBoundsAnimation()
    {
        if (!_settings.PureMode || !_isAnimatingHeight)
        {
            return;
        }

        _isAnimatingHeight = false;
        _heightAnimStopwatch.Stop();
        UpdateRenderingSubscription();
    }

    private void QueuePureModeLayoutRefresh()
    {
        if (!_settings.PureMode || !_sourceInitialized)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_settings.PureMode && !_isWindowDragging)
            {
                RefreshLyricLayout();
            }
        }, DispatcherPriority.Loaded);
    }

    private System.Windows.Size GetDesiredPureModeCardSize()
    {
        var measured = MeasurePureModeWindowSize();
        return new System.Windows.Size(
            Math.Clamp(measured.Width, 180.0, PureModeMaxCardWidth),
            Math.Clamp(measured.Height, 70.0, PureModeMaxCardHeight));
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
        _isClosed = true;
        UpdateRenderingSubscription();
        _runtimePollingController.Dispose();
        StopMouseTracker();
        PersistCurrentSettings();
        _trayIconController.Dispose();
        _runtimeService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _catalogResolver?.Dispose();
        _lrcLibProvider?.Dispose();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Pure Mode applies its content-sized native bounds in one operation; WM_SIZE must not
        // feed that change back into text measurement and start a recursive layout pass.
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
        if (_isApplyingPureModeAutoSize || _isApplyingWindowPlacement || _isAnimatingHeight)
        {
            return;
        }

        if (_settings.PureMode && WindowState == WindowState.Normal)
        {
            _settings.PureModeWindowX = (int)Math.Round(Left + GetCurrentLogicalSize(ActualWidth, Width, 180) / 2.0);
            _settings.PureModeWindowY = (int)Math.Round(Top + GetCurrentLogicalSize(ActualHeight, Height, 70) / 2.0);
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

    private void ApplyRestoredPlacement()
    {
        if (!_sourceInitialized)
        {
            return;
        }

        var monitors = _windowPlacementCoordinator.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var pureModeSize = _settings.PureMode ? GetDesiredPureModeCardSize() : default;
        var desiredWidth = _settings.PureMode
            ? pureModeSize.Width
            : Math.Max(_settings.WindowWidth, _settings.MinWindowWidth);
        var desiredHeight = _settings.PureMode
            ? pureModeSize.Height
            : Math.Max(_settings.WindowHeight, _settings.MinWindowHeight);
        var legacyLeft = _settings.PureMode
            ? _settings.PureModeWindowX - desiredWidth / 2.0
            : _settings.WindowX;
        var legacyTop = _settings.PureMode
            ? _settings.PureModeWindowY - desiredHeight / 2.0
            : _settings.WindowY;
        var legacyRect = new PixelRect(
            (int)Math.Round(legacyLeft),
            (int)Math.Round(legacyTop),
            (int)Math.Round(legacyLeft + desiredWidth),
            (int)Math.Round(legacyTop + desiredHeight));

        var placement = _windowPlacementCoordinator.Resolve(
            _settings,
            _settings.PureMode,
            desiredWidth,
            desiredHeight,
            legacyRect);

        Width = placement.WidthDip;
        Height = placement.HeightDip;
        WindowPlacementCoordinator.StoreAnchor(_settings, _settings.PureMode, placement);
        if (_settings.PureMode)
        {
            UpdateTextMaxWidths(placement.WidthDip);
        }
        var handle = new WindowInteropHelper(this).Handle;
        ApplyNativeWindowRect(handle, placement.WindowRectPx);
    }

    private void CaptureCurrentPlacement()
    {
        if (!_sourceInitialized || WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var captured = _windowPlacementCoordinator.Capture(handle);
        if (captured is null)
        {
            return;
        }
        WindowPlacementCoordinator.StoreAnchor(_settings, _settings.PureMode, captured);
    }

    private void ApplyWindowBounds()
    {
        MinWidth = _settings.PureMode ? 180 : _settings.MinWindowWidth;
        MinHeight = _settings.PureMode ? 70 : _settings.MinWindowHeight;

        if (_settings.PureMode)
        {
            // Pure Mode uses the measured card size as the actual HWND size. Keeping the
            // saved center fixed prevents lyric-length changes from walking the window.
            var size = GetDesiredPureModeCardSize();
            var centerX = Math.Max(_settings.PureModeWindowX, -32000);
            var centerY = Math.Max(_settings.PureModeWindowY, -32000);
            Width = size.Width;
            Height = size.Height;
            Left = centerX - size.Width / 2.0;
            Top = centerY - size.Height / 2.0;
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
        ShellBorder.Clip = null;
        // Pure Mode content sizes to the real card HWND; normal mode stretches to its window.
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

        var fontFamily = FontFamilyResolver.Resolve(_settings.FontFamily);
        CurrentLyricText.FontFamily = fontFamily;
        PreviousLyricText.FontFamily = fontFamily;
        NextLyricText.FontFamily = fontFamily;
        SubtitleText.FontFamily = fontFamily;

        CurrentLyricGlowEffect.Color = ParseColorOrFallback(_settings.GlowColor, Colors.White);
        CurrentLyricGlowEffect.Opacity = Math.Clamp(_settings.GlowOpacity, 0.0, 1.0);

        UpdateTrayState();
        ApplyClickThrough();
        var layoutWidth = _settings.PureMode
            ? GetCurrentLogicalSize(ActualWidth, Width, PureModeMaxCardWidth)
            : Width;
        UpdateTextMaxWidths(layoutWidth);
        RefreshLyricLayout();
        UpdateRenderingSubscription();
    }

    private bool IsClickThroughEnabled => _settings.ClickThrough;

    private void ApplyClickThrough()
    {
        if (!_sourceInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _windowInteropService.SetClickThrough(handle, IsClickThroughEnabled);
        UpdateMouseTracker(handle);
    }

    private void UpdateMouseTracker(nint hwnd)
    {
        if (IsClickThroughEnabled && _settings.HoverFadeEnabled && IsVisible)
        {
            if (_mouseTracker is null)
            {
                var tracker = new MouseTracker();
                tracker.MouseOverChanged += isOver => Dispatcher.BeginInvoke(
                    () => OnMouseTrackerOverChanged(tracker, isOver));
                _mouseTracker = tracker;
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
        if (_mouseTracker is null)
        {
            return;
        }

        _mouseTracker.Dispose();
        _mouseTracker = null;

        // Nothing reports the cursor leaving once sampling stops. A restarted tracker reports a
        // cursor that is still over the window, and without click-through WPF's own
        // MouseEnter/MouseLeave take over.
        _isHovering = false;
        if (!_isClosed)
        {
            RefreshOverlayOpacity();
        }
    }

    // Samples arrive from a background thread and can still be queued after their tracker stopped.
    private void OnMouseTrackerOverChanged(MouseTracker source, bool isOver)
    {
        if (!ReferenceEquals(source, _mouseTracker))
        {
            return;
        }

        _isHovering = isOver;
        RefreshOverlayOpacity();
    }

    private void ShellBorder_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsClickThroughEnabled)
        {
            return;
        }

        if (_settings.PureMode)
        {
            SetWindowDragging(true);
        }

        try
        {
            DragMove();
        }
        finally
        {
            SetWindowDragging(false);
        }
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

    private void SetPureMode(bool enabled, bool persist = true)
    {
        // Save both legacy coordinates and per-monitor placement before switching mode.
        CaptureCurrentPlacement();
        if (WindowState == WindowState.Normal)
        {
            if (_settings.PureMode)
            {
                _settings.PureModeWindowX = (int)Math.Round(Left + GetCurrentLogicalSize(ActualWidth, Width, 180) / 2.0);
                _settings.PureModeWindowY = (int)Math.Round(Top + GetCurrentLogicalSize(ActualHeight, Height, 70) / 2.0);
            }
            else
            {
                _settings.WindowX = (int)Math.Round(Left);
                _settings.WindowY = (int)Math.Round(Top);
                _settings.WindowWidth = (int)Math.Round(Width);
                _settings.WindowHeight = (int)Math.Round(Height);
            }
        }

        CancelPureModeBoundsAnimation();
        _settings.PureMode = enabled;
        if (!enabled)
        {
            SetWindowDragging(false);
        }

        ApplyWindowBounds();
        ApplyRestoredPlacement();
        ApplyAppearanceSettings();
        if (persist)
        {
            PersistCurrentSettings();
        }
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
        var currentLeft = Left;
        var currentTop = Top;
        var currentWidth = Width;
        var currentHeight = Height;
        var currentPureMode = _settings.PureMode;
        var requestedPureMode = sourceSettings.PureMode;

        CancelPureModeBoundsAnimation();
        CopySettings(sourceSettings, _settings);
        // Keep the source mode active until SetPureMode captures its placement. The settings
        // dialog carries both modes' saved anchors, so the target placement remains available.
        _settings.PureMode = currentPureMode;

        _playerProvider.AllowNonAppleMediaSessions = _settings.AllowNonAppleMediaSessions;
        _runtimeService.LyricsOffsetSeconds = _settings.LyricsOffsetSeconds;
        _runtimeService.ApplyNativeLyricOffset = _settings.ApplyNativeLyricOffset;
        _runtimeService.AllowLowConfidenceLyrics = _settings.AllowLowConfidenceLyrics;
        _runtimePollingController.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PlayerPollInterval, 0.05, 1.0));

        if (requestedPureMode != currentPureMode)
        {
            SetPureMode(requestedPureMode, persist);
            return;
        }

        if (!_settings.PureMode)
        {
            SetWindowDragging(false);
        }

        // Apply appearance settings but preserve the active mode's current placement.
        ApplyAppearanceSettings();
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
        CaptureCurrentPlacement();
        if (WindowState == WindowState.Normal)
        {
            if (_settings.PureMode)
            {
                // PureMode: save center point — ApplyWindowBounds starts at Width=0 so Left=center
                _settings.PureModeWindowX = (int)Math.Round(Left + GetCurrentLogicalSize(ActualWidth, Width, 180) / 2.0);
                _settings.PureModeWindowY = (int)Math.Round(Top + GetCurrentLogicalSize(ActualHeight, Height, 70) / 2.0);
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
        if (_isApplyingPureModeAutoSize || !_sourceInitialized || _isWindowDragging)
        {
            return;
        }

        _isApplyingPureModeAutoSize = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var currentRect = _windowPlacementCoordinator.GetWindowRect(hwnd);
            var monitors = _windowPlacementCoordinator.GetMonitors();
            if (currentRect.Width <= 0 || currentRect.Height <= 0 || monitors.Count == 0)
            {
                return;
            }

            // Anchor on the live window center. Round-tripping through the stored
            // monitor-relative center re-quantizes the position on every lyric, which shifts the
            // card by a pixel or two each line and reads as jitter.
            var monitor = _windowPlacementCoordinator.GetMonitorForWindow(hwnd, monitors)
                ?? WindowPlacementService.FindMonitorForRect(monitors, currentRect);
            var requestedSize = GetDesiredPureModeCardSize();
            var targetRect = WindowBoundsGeometry.ResizeAroundCenter(
                currentRect,
                monitor,
                requestedSize.Width,
                requestedSize.Height);
            var targetWidthDip = targetRect.Width / monitor.ScaleX;
            var targetHeightDip = targetRect.Height / monitor.ScaleY;

            UpdateTextMaxWidths(targetWidthDip);
            ApplyAdaptiveFontSizes(targetWidthDip, targetHeightDip);
            ShellBorder.Clip = null;

            if (currentRect.Left == targetRect.Left
                && currentRect.Top == targetRect.Top
                && currentRect.Right == targetRect.Right
                && currentRect.Bottom == targetRect.Bottom)
            {
                CancelPureModeBoundsAnimation();
                return;
            }

            // Differences of 1 pixel or less do not warrant a visible animated transition;
            // apply the target rect immediately to avoid a jarring snap.
            if (Math.Abs(currentRect.Left - targetRect.Left) <= 1
                && Math.Abs(currentRect.Top - targetRect.Top) <= 1
                && Math.Abs(currentRect.Right - targetRect.Right) <= 1
                && Math.Abs(currentRect.Bottom - targetRect.Bottom) <= 1)
            {
                CancelPureModeBoundsAnimation();
                ApplyNativeWindowRect(hwnd, targetRect);
                return;
            }

            // A new lyric can interrupt an active transition. The actual HWND rect is always
            // the new start, so restarting cannot jump back to an earlier target.
            _pureBoundsAnimFrom = currentRect;
            _pureBoundsAnimTarget = targetRect;
            _heightAnimStopwatch.Restart();
            _isAnimatingHeight = true;
            UpdateRenderingSubscription();
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

        var maxContentWidth = PureModeMaxCardWidth - extraWidthPadding;
        var maxContentHeight = (PureModeMaxCardHeight - 40) - extraHeightPadding;
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
        else if (_settings.TwoLineMode)
        {
            // Two-line mode with no next line (the final lyric): the next row collapses to zero
            // (see UpdateLyricRowHeights), so only the current line's own bottom margin counts.
            // Its top margin is 0 in two-line layout; mirroring that here keeps the measured card
            // height equal to the rendered height so the line stays vertically centered.
            totalHeight += currentMarginBottom;
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

    private void UpdateTrayState()
    {
        _trayIconController.Update(
            IsVisible,
            _settings.ClickThrough,
            _settings.PureMode,
            _settings.SingleLineMode,
            _settings.TwoLineMode,
            _settings.ShowDebugPanel,
            CurrentLyricText.Text);
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
        var fontFamily = CurrentLyricText.FontFamily ?? FontFamilyResolver.Resolve(_settings.FontFamily);
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

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        var copy = source.Clone();
        target.PlayerPollInterval = copy.PlayerPollInterval;
        target.LyricsOffsetSeconds = copy.LyricsOffsetSeconds;
        target.ApplyNativeLyricOffset = copy.ApplyNativeLyricOffset;
        target.AllowNonAppleMediaSessions = copy.AllowNonAppleMediaSessions;
        target.AllowLowConfidenceLyrics = copy.AllowLowConfidenceLyrics;
        target.CatalogLookupEnabled = copy.CatalogLookupEnabled;
        target.CatalogStorefronts = copy.CatalogStorefronts;
        target.CatalogLookupTimeoutSeconds = copy.CatalogLookupTimeoutSeconds;
        target.ExternalLyricsEnabled = copy.ExternalLyricsEnabled;
        target.ExternalLyricsTimeoutSeconds = copy.ExternalLyricsTimeoutSeconds;
        target.PersistExternalLyricsCache = copy.PersistExternalLyricsCache;
        target.WindowX = copy.WindowX;
        target.WindowY = copy.WindowY;
        target.WindowWidth = copy.WindowWidth;
        target.WindowHeight = copy.WindowHeight;
        target.PureModeWindowX = copy.PureModeWindowX;
        target.PureModeWindowY = copy.PureModeWindowY;
        target.WindowPlacementVersion = copy.WindowPlacementVersion;
        target.WindowMonitorId = copy.WindowMonitorId;
        target.WindowRelativeCenterX = copy.WindowRelativeCenterX;
        target.WindowRelativeCenterY = copy.WindowRelativeCenterY;
        target.PureModeMonitorId = copy.PureModeMonitorId;
        target.PureModeRelativeCenterX = copy.PureModeRelativeCenterX;
        target.PureModeRelativeCenterY = copy.PureModeRelativeCenterY;
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
        target.FadeWhenPaused = copy.FadeWhenPaused;
        target.PureMode = copy.PureMode;
        target.ClickThrough = copy.ClickThrough;
        target.OverlayOpacity = copy.OverlayOpacity;
        target.PureModeDragOpacity = copy.PureModeDragOpacity;
        target.BackgroundAlpha = copy.BackgroundAlpha;
        target.HoverFadeEnabled = copy.HoverFadeEnabled;
        target.HoverFadeDuration = copy.HoverFadeDuration;
        target.HoverFadeMinOpacity = copy.HoverFadeMinOpacity;
    }
}
