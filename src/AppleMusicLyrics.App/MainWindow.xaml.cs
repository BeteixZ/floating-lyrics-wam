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
using AppleMusicLyrics.App.Localization;
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
    private readonly IReadOnlyList<IExternalLyricsProvider> _externalLyricsProviders;
    private readonly ExternalLyricsCache? _externalLyricsCache;
    private readonly RuntimePollingController _runtimePollingController;
    private readonly AppSettings _settings;
    private readonly IniSettingsStore _settingsStore;
    private readonly WindowInteropService _windowInteropService;
    private readonly WindowPlacementCoordinator _windowPlacementCoordinator;
    private readonly TrayIconController _trayIconController;
    private readonly UpdateCoordinator _updates;
    private UpdateWindow? _updateWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private LyricsSelectionWindow? _lyricsSelectionWindow;
    private bool _canManageLyrics;
    private readonly LyricLineView _currentLyric;
    private readonly LyricLineView _duetLyric;
    private readonly LyricLineView[] _lyricViews;
    private LyricsDocument? _displayedLyricDocument;
    private bool _sourceInitialized;
    private bool _isClosed;
    private bool _runtimeRefreshFailed;
    private Exception? _lastRuntimeError;
    private bool _hasRuntimeSnapshot;
    private bool _hasFrameLyricState;
    private LyricsDocument? _lastFrameDocument;
    private ActiveLyricState? _lastFrameLyricState;
    private bool _lastFramePlaying;
    private RuntimeSnapshot? _pendingFrameSnapshot;
    private bool _frameSnapshotApplyScheduled;
    private bool _diagnosticsLayoutRefreshScheduled;
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
    // Pure Mode draws its card inside a larger transparent "envelope" window. Line changes only
    // move the drawn card; the HWND is resized only when a card no longer fits. Resizing a
    // layered window shows a stale frame at the new position, which read as the card jumping.
    // _pureBoundsAnimTarget is the settled card rect; the card is always centered on the anchor.
    private bool _pureEnvelopeActive;
    private PixelRect _pureEnvelopeRect;
    private double _pureCardCenterX;
    private double _pureCardCenterY;
    private readonly RectangleGeometry _pureCardClip = new();
    // Before shrinking, look at the next few lines: if one of them needs the current size again
    // (a duet part ending just before another begins), keep it instead of pumping in and out.
    private readonly LyricsSynchronizer _lookaheadSynchronizer = new();
    private const double LookaheadSeconds = 6.0;
    private const double LookaheadStepSeconds = 0.2;
    private const int LookaheadLineChanges = 2;
    private const double PureEnvelopePaddingXDip = 220.0;
    private const double PureEnvelopePaddingYDip = 80.0;

    public MainWindow()
    {
        var composition = AppCompositionRoot.Create();
        _settingsStore = composition.SettingsStore;
        _settings = composition.Settings;
        _externalLyricsCache = composition.ExternalLyricsCache;
        LocalizationManager.Instance.SetLanguage(_settings.UiLanguage);
        _playerProvider = composition.PlayerProvider;
        _catalogResolver = composition.CatalogResolver;
        _externalLyricsProviders = composition.ExternalLyricsProviders;
        _runtimeService = composition.RuntimeService;
        _windowInteropService = new WindowInteropService();
        _windowPlacementCoordinator = new WindowPlacementCoordinator();

        InitializeComponent();
        HeaderPanel.SizeChanged += OnDiagnosticContentSizeChanged;
        _currentLyric = new LyricLineView(CurrentLyricText, BackgroundVocalPanel, RomanizationLyricText, TranslationLyricText);
        _duetLyric = new LyricLineView(DuetLyricText, DuetBackgroundVocalPanel, DuetRomanizationLyricText, DuetTranslationLyricText);
        _lyricViews = [_currentLyric, _duetLyric];
        _currentLyric.Bind(LocalizationManager.Instance.Text("Lyrics.PlaceholderWaiting"), null, null, null, false, Colors.White);
        DuetLyricText.Effect = CurrentLyricGlowEffect;
        Icon = LoadAppIcon();
        _trayIconController = new TrayIconController(
            Dispatcher,
            ToggleOverlayVisibility,
            () => SetClickThrough(!_settings.ClickThrough),
            () => SetPureMode(!_settings.PureMode),
            () => SetSingleLineMode(!_settings.SingleLineMode),
            () => SetTwoLineMode(!_settings.TwoLineMode),
            () => SetDebugPanelVisibility(!_settings.ShowDebugPanel),
            OpenDiagnosticsWindow,
            RefreshCurrentLyrics,
            OpenLyricsSelection,
            OpenSettingsWindow,
            Close,
            () => OpenUpdateWindow(checkNow: false));
        _updates = new UpdateCoordinator(_settings, PersistCurrentSettings, Dispatcher);
        _updates.AvailabilityChanged += () => _trayIconController.SetAvailableUpdate(_updates.Available?.Version.ToString());
        _updates.NewReleaseFound += release => _trayIconController.ShowNotification(
            LocalizationManager.Instance.Format("Update.Balloon.Available.Title", release.Version),
            LocalizationManager.Instance.Text("Update.Balloon.Available.Text"),
            () => OpenUpdateWindow(checkNow: false));
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
        LocalizationManager.Instance.LanguageChanged += OnUiLanguageChanged;
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
    // bounds animation or lyrics advancing during playback. The compact summary is static.
    private void UpdateRenderingSubscription()
    {
        var needed = !_isClosed
            && (_isAnimatingHeight
                || (IsVisible && _hasRuntimeSnapshot && !_runtimeRefreshFailed && _lastPlaying && _lastHasLyrics));
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
                TimingText.Text = $"{LocalizationManager.Instance.Text("Runtime.RefreshFailed")} | {FormatRenderRate()} | {FormatPollRate()}";
            }

            return;
        }

        if (!_hasRuntimeSnapshot)
        {
            return;
        }

        var snapshot = _runtimeService.GetFrameSnapshot();
        QueueFrameSnapshot(snapshot);
        UpdateWordHighlight(snapshot);
        if (publishedFrameRate)
        {
            UpdateTimingText(snapshot);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartUpdates();
        ApplyAdaptiveFontSizes();
        ApplyClickThrough();
        QueueDiagnosticsLayoutRefresh();

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
                    ExitPureEnvelope(resizeWindowToCard: false);
                    QueuePureModeLayoutRefresh();
                    QueueWindowStateCommit(reconcileDisplayTopology: false);
                }

                break;

            case WM_DISPLAYCHANGE:
                ExitPureEnvelope(resizeWindowToCard: true);
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
        _lastRuntimeError = null;
        _hasRuntimeSnapshot = true;
        _canManageLyrics = snapshot.CanManageLyrics;
        _lyricsSelectionWindow?.ObserveTrack(snapshot);
        ApplySnapshot(snapshot);
        UpdateOverlayVisibility(snapshot);
        UpdateDiagnosticsWindow(snapshot);
        UpdateRenderingSubscription();
    }

    private void HandleRuntimeError(Exception ex)
    {
        _runtimeRefreshFailed = true;
        _lastRuntimeError = ex;
        _canManageLyrics = false;
        UpdateRenderingSubscription();
        _pendingFrameSnapshot = null;
        _lastLyricAnimationKey = null;
        var text = LocalizationManager.Instance;
        SubtitleText.Text = text.Text("Runtime.RefreshFailed");
        PlayerText.Text = text.Text("Runtime.CheckPlayer");
        TimingText.Text = $"{text.Text("Runtime.RefreshFailed")} | {FormatRenderRate()} | {FormatPollRate()}";
        StatusText.Text = text.Text("Runtime.Error");
        PathText.Text = text.Text("Runtime.RefreshFailed");
        _currentLyric.Bind(text.Text("Runtime.RefreshFailedShort"), null, null, null, false, Colors.White);
        _duetLyric.Bind(string.Empty, null, null, null, false, Colors.White);
        DuetLyricContainer.Visibility = Visibility.Collapsed;
        _displayedLyricDocument = null;
        PreviousLyricText.Text = string.Empty;
        NextLyricText.Visibility = CanShowNextLyric ? Visibility.Visible : Visibility.Collapsed;
        NextLyricText.Text = text.Text("Runtime.CheckCache");
        UpdateLyricRowHeights(false, CanShowNextLyric);
        QueueDiagnosticsLayoutRefresh();
        UpdateDiagnosticsWindow();
        UpdateTrayState();
    }

    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnUiLanguageChanged(sender, e));
            return;
        }

        if (_runtimeRefreshFailed && _lastRuntimeError is { } error)
        {
            HandleRuntimeError(error);
            UpdateTrayState();
            return;
        }

        // Refresh status text even while the current track is paused on the same line.
        var snapshot = _runtimeService.GetFrameSnapshot();
        ApplySnapshot(snapshot);
        UpdateDiagnosticsWindow(snapshot);
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
            && ReferenceEquals(_lastFrameLyricState, snapshot.ActiveLyric)
            && _lastFramePlaying == isPlaying)
        {
            _pendingFrameSnapshot = null;
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
            && ReferenceEquals(_lastFrameLyricState, snapshot.ActiveLyric)
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
        _lastFrameLyricState = snapshot.ActiveLyric;
        _lastFramePlaying = snapshot.Player?.Playing ?? false;
        _hasFrameLyricState = true;
    }

    private void UpdatePlayerSection(RuntimeSnapshot snapshot)
    {
        var presentation = OverlaySnapshotPresenter.CreatePlayer(snapshot);
        var changed = SubtitleText.Text != presentation.Subtitle || PlayerText.Text != presentation.PlayerText;
        Title = presentation.WindowTitle;
        SubtitleText.Text = presentation.Subtitle;
        PlayerText.Text = presentation.PlayerText;
        UpdateTimingText(snapshot);
        if (changed) QueueDiagnosticsLayoutRefresh();
    }

    private void UpdateTimingText(RuntimeSnapshot snapshot)
        => TimingText.Text = FormatTimingText(snapshot);

    private string FormatTimingText(RuntimeSnapshot snapshot)
    {
        var clockText = snapshot.Player is null
            ? LocalizationManager.Instance.Text("Runtime.NoClock")
            : LocalizationManager.Instance.Format("Runtime.Clock",
                TimeParser.FormatTimestamp(snapshot.RawPositionSeconds ?? 0),
                TimeParser.FormatTimestamp(snapshot.EstimatedPositionSeconds ?? 0));
        return $"{clockText} | {FormatRenderRate()} | {FormatPollRate()}";
    }

    private string FormatRenderRate()
    {
        return _visualFrameStatistics.FramesPerSecond is double framesPerSecond
            ? LocalizationManager.Instance.Format("Runtime.RenderFps", framesPerSecond.ToString("F1", CultureInfo.InvariantCulture))
            : LocalizationManager.Instance.Text("Runtime.RenderMeasuring");
    }

    private string FormatPollRate()
    {
        var pollsPerSecond = 1.0 / Math.Max(0.001, _runtimePollingController.Interval.TotalSeconds);
        return LocalizationManager.Instance.Format("Runtime.PollHz", pollsPerSecond.ToString("F1", CultureInfo.InvariantCulture));
    }

    private void UpdateLyricSection(RuntimeSnapshot snapshot)
    {
        var presentation = OverlaySnapshotPresenter.CreateLyrics(snapshot, showTranslation: _settings.ShowTranslation,
            includeDetailedDiagnostics: false, showRomanization: _settings.ShowRomanization);
        var summary = presentation.DebugSummaryText ?? string.Empty;
        var diagnosticsChanged = StatusText.Text != presentation.StatusText || PathText.Text != summary;
        StatusText.Text = presentation.StatusText;
        PathText.Text = summary;
        SetLyrics(presentation);
        _displayedLyricDocument = snapshot.Document;
        UpdateWordHighlight(snapshot);
        if (diagnosticsChanged) QueueDiagnosticsLayoutRefresh();
    }

    private void UpdateWordHighlight(RuntimeSnapshot snapshot)
    {
        if ((!_currentLyric.HasTimedWords && !_duetLyric.HasTimedWords)
            || !ReferenceEquals(_displayedLyricDocument, snapshot.Document) || snapshot.Document is null) return;
        var line = snapshot.ActiveLyric.CurrentLine ?? snapshot.ActiveLyric.NextLine
            ?? snapshot.Document.Lines.FirstOrDefault();
        // The frame computed here reaches the screen a couple of compositor frames later; lead the
        // highlight by that much so it lines up with what is heard.
        var position = LyricsTiming.GetPosition(snapshot.Document, snapshot.Player,
            _runtimeService.LyricsOffsetSeconds + WordHighlightLeadSeconds, _settings.ApplyNativeLyricOffset);
        if (ReferenceEquals(_currentLyric.Line, line)) _currentLyric.UpdateHighlight(position);
        if (snapshot.ActiveLyric.DuetLine is { } duet && ReferenceEquals(_duetLyric.Line, duet))
            _duetLyric.UpdateHighlight(position);
    }

    private void BindLyricViews(LyricsPresentation presentation)
    {
        var sung = ParseColorOrFallback(presentation.IsPlaying ? _settings.CurrentLineColor : _settings.PausedLineColor, Colors.White);
        var duet = presentation.DuetLine;
        _currentLyric.Bind(presentation.CurrentText, presentation.DisplayedLine, presentation.RomanizationText,
            _settings.ShowTranslation ? presentation.TranslationText : null, _settings.WordHighlightEnabled, sung);
        _duetLyric.Bind(duet?.Text ?? string.Empty, duet, presentation.DuetRomanizationText,
            _settings.ShowTranslation ? duet?.Translation : null, _settings.WordHighlightEnabled, sung);
        DuetLyricContainer.Visibility = duet is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLyrics(LyricsPresentation presentation)
    {
        var line = presentation.DisplayedLine;
        var duet = presentation.DuetLine;
        var lyricKey = $"{presentation.PreviousText}|{presentation.CurrentText}|{presentation.NextText}|{presentation.IsPlaying}|{line?.Begin:R}|{line?.End:R}|{presentation.TranslationText}|{_settings.ShowTranslation}|{presentation.RomanizationText}|{_settings.ShowRomanization}|{duet?.Text}|{duet?.Begin:R}|{duet?.End:R}|{duet?.Translation}|{presentation.DuetRomanizationText}";
        var isRepeat = string.Equals(_lastLyricAnimationKey, lyricKey, StringComparison.Ordinal)
            && HaveSameBackgroundLyrics(_currentLyric.Line, line)
            && HaveSameBackgroundLyrics(_duetLyric.Line, duet);

        // Same content as last time (a poll tick with no change): the lyrics are already on
        // screen — do nothing, so a crossfade in progress isn't cut short.
        if (isRepeat)
        {
            // A replacement document can have different word timing despite identical text.
            BindLyricViews(presentation);
            return;
        }

        // When a duet part starts, ends, or takes over the first slot, keep the continuing
        // vocal visible while the window adjusts its height.
        var duetTransition = (_duetLyric.Line is not null || duet is not null)
            && line is not null
            && (ReferenceEquals(_currentLyric.Line, line) || ReferenceEquals(_duetLyric.Line, line));
        var shouldAnimate = presentation.Animate && !duetTransition;
        _lastLyricAnimationKey = lyricKey;

        if (!shouldAnimate)
        {
            ResetLyricVisualState();
            ApplyLyricContent(presentation);
            RefreshLyricLayout();
            Dispatcher.BeginInvoke(() => StartHeightAnimation(), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        // Freeze the outgoing lyrics so the original crossfade remains visually unchanged:
        // old lyrics fade out while the new lyrics fade in from transparent and move upward.
        // This work now runs after the render callback, so it no longer blocks the boundary frame.
        SnapshotLyricGhost();
        ApplyLyricContent(presentation);
        RefreshLyricLayout();

        // Staggered crossfade: the outgoing line drops most of its opacity before the incoming one
        // starts to appear, so the two never sit on top of each other at full strength.
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = TimeSpan.FromMilliseconds(LyricFadeInDurationMs);
        var fadeInDelay = TimeSpan.FromMilliseconds(LyricFadeInDelayMs);

        LyricPanel.BeginAnimation(OpacityProperty, null);
        LyricPanel.Opacity = 0.0;
        LyricPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.0, 1.0, fadeIn) { EasingFunction = easing, BeginTime = fadeInDelay });
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty, null);
        CurrentLyricTransform.Y = 10;
        CurrentLyricTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, fadeIn) { EasingFunction = easing, BeginTime = fadeInDelay });

        // Outgoing snapshot fades out quickly (ease-out: most of the drop happens up front) and
        // drifts slightly upward, so the line visibly leaves instead of lingering under the new one.
        var fadeOut = TimeSpan.FromMilliseconds(LyricFadeOutDurationMs);
        var ghostOut = new DoubleAnimation(0.0, fadeOut)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        LyricGhostTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -8, fadeOut) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        ghostOut.Completed += (_, _) => HideLyricGhost();
        LyricGhostImage.BeginAnimation(OpacityProperty, ghostOut);

        Dispatcher.BeginInvoke(() => StartHeightAnimation(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static bool HaveSameBackgroundLyrics(LyricsLine? previous, LyricsLine? next)
    {
        var before = previous?.BackgroundVocals;
        var after = next?.BackgroundVocals;
        return ReferenceEquals(before, after)
            || (before ?? []).Select(vocal => vocal.Text).SequenceEqual((after ?? []).Select(vocal => vocal.Text));
    }

    private void ApplyLyricContent(LyricsPresentation presentation)
    {
        var showPrevious = _settings.ShowPreviousLine && !string.IsNullOrWhiteSpace(presentation.PreviousText);
        var showNext = _settings.ShowNextLine && !string.IsNullOrWhiteSpace(presentation.NextText);

        _currentPreviousText = presentation.PreviousText;
        _currentNextText = presentation.NextText;

        PreviousLyricText.Text = showPrevious ? presentation.PreviousText : string.Empty;
        BindLyricViews(presentation);
        NextLyricText.Text = showNext ? presentation.NextText : string.Empty;
        NextLyricText.Visibility = CanShowNextLyric ? Visibility.Visible : Visibility.Collapsed;

        PreviousLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));
        NextLyricText.Foreground = CreateBrush(_settings.ContextLineColor, MediaColor.FromRgb(143, 148, 153));

        UpdateLyricRowHeights(showPrevious, showNext && CanShowNextLyric);
    }

    private bool CanShowNextLyric => _settings.ShowNextLine && !_settings.SingleLineMode
        && (!_settings.TwoLineMode || _duetLyric.Line is null);

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

        // Normal mode cannot cheaply measure future lines, but a returning duet is the case that
        // matters: keep the height when a second vocalist comes back within the next few lines.
        var shownHeight = _isAnimatingHeight ? _targetHeight : Height;
        if (targetHeight < shownHeight - 1 && _duetLyric.Line is null && UpcomingStates().Any(state => state.DuetLine is not null))
        {
            targetHeight = shownHeight;
        }

        if (Math.Abs(targetHeight - Height) < 1)
        {
            return;
        }
        if (_isAnimatingHeight && Math.Abs(targetHeight - _targetHeight) < 1)
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

    private void OnDiagnosticContentSizeChanged(object sender, SizeChangedEventArgs e)
        => QueueDiagnosticsLayoutRefresh();

    private void QueueDiagnosticsLayoutRefresh()
    {
        if (_isClosed || !IsLoaded || _settings.PureMode || _diagnosticsLayoutRefreshScheduled) return;
        _diagnosticsLayoutRefreshScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _diagnosticsLayoutRefreshScheduled = false;
            if (_isClosed || _settings.PureMode) return;
            ApplyAdaptiveFontSizes();
            StartHeightAnimation();
        }, DispatcherPriority.Loaded);
    }

    private const double WindowAnimDurationMs = 240.0;
    // Render-to-screen latency of a layered WPF window: the render thread plus DWM composition.
    private const double WordHighlightLeadSeconds = 0.06;
    private const double LyricFadeOutDurationMs = 200.0;
    private const double LyricFadeInDelayMs = 110.0;
    private const double LyricFadeInDurationMs = 300.0;
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

            if (progress >= 1.0)
            {
                _isAnimatingHeight = false;
                _heightAnimStopwatch.Stop();
                _pureBoundsAnimFrom = _pureBoundsAnimTarget;
                LayoutPureCardBackdrop(1.0);
                CaptureCurrentPlacement();
                UpdateRenderingSubscription();
                return;
            }

            // Only the card backdrop moves; the HWND keeps its envelope size, so every frame
            // is a cheap re-render rather than a window resize.
            LayoutPureCardBackdrop(eased);
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
        var showPrevious = PreviousLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(_currentPreviousText);
        var showNext = NextLyricText.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(_currentNextText);
        var chromeHeight = ShellBorder.Margin.Top + ShellBorder.Margin.Bottom
            + ShellBorder.Padding.Top + ShellBorder.Padding.Bottom;
        var chromeWidth = ShellBorder.Margin.Left + ShellBorder.Margin.Right
            + ShellBorder.Padding.Left + ShellBorder.Padding.Right;
        var contentWidth = Math.Max(1, GetCurrentLogicalSize(ActualWidth, Width, MinWidth) - chromeWidth);
        var headerHeight = OverlayDiagnosticsLayout.MeasureHeight(HeaderPanel, contentWidth);
        var footerHeight = OverlayDiagnosticsLayout.MeasureHeight(FooterPanel, contentWidth);
        var contentHeight = LyricPanel.Margin.Top + LyricPanel.Margin.Bottom;
        if (showPrevious)
        {
            contentHeight += Math.Min(PreviousLyricText.MaxHeight,
                MeasureText(_currentPreviousText, PreviousLyricText, PreviousLyricText.FontSize,
                    Math.Min(contentWidth, PreviousLyricText.MaxWidth), 2).Height)
                + PreviousLyricText.Margin.Top + PreviousLyricText.Margin.Bottom;
        }
        var translationSize = TranslationTypography.GetFontSize(_settings.TranslationFontSize);
        foreach (var view in VisibleLyricViews())
        {
            var lyric = view.MainText;
            var lyricWidth = Math.Min(contentWidth, lyric.MaxWidth);
            contentHeight += MeasureText(view.DisplayText, lyric, lyric.FontSize,
                lyricWidth, _settings.SingleLineMode ? 1 : int.MaxValue).Height + lyric.Margin.Top + lyric.Margin.Bottom;
            foreach (var row in view.GetAuxiliaryRows(lyric.FontSize, translationSize))
            {
                contentHeight += MeasureText(row.Text, row.Element, row.FontSize, lyricWidth, 2).Height
                    + row.Element.Margin.Top + row.Element.Margin.Bottom;
            }
        }
        if (showNext)
        {
            contentHeight += Math.Min(NextLyricText.MaxHeight,
                MeasureText(_currentNextText, NextLyricText, NextLyricText.FontSize,
                    Math.Min(contentWidth, NextLyricText.MaxWidth), 2).Height)
                + NextLyricText.Margin.Top + NextLyricText.Margin.Bottom;
        }
        var workHeight = SystemParameters.WorkArea.Height;
        if (_sourceInitialized)
        {
            var monitor = _windowPlacementCoordinator.GetMonitorForWindow(new WindowInteropHelper(this).Handle,
                _windowPlacementCoordinator.GetMonitors());
            if (monitor is not null) workHeight = monitor.WorkAreaPx.Height / monitor.ScaleY;
        }
        return OverlayDiagnosticsLayout.GetWindowHeight(chromeHeight + headerHeight, contentHeight,
            footerHeight, MinHeight, workHeight * 0.9);
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
        LyricGhostTransform.BeginAnimation(TranslateTransform.YProperty, null);
        LyricGhostTransform.Y = 0;
        LyricGhostImage.Visibility = Visibility.Visible;
    }

    private void HideLyricGhost()
    {
        LyricGhostImage.BeginAnimation(OpacityProperty, null);
        LyricGhostTransform.BeginAnimation(TranslateTransform.YProperty, null);
        LyricGhostTransform.Y = 0;
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
        return OverlayDiagnosticsLayout.ResolveOpacity(
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

    // Stops a running card transition and shows the card at its destination. The window itself
    // is not touched: in the envelope the card simply finishes where it was heading.
    private void CancelPureModeBoundsAnimation()
    {
        if (!_settings.PureMode || !_isAnimatingHeight)
        {
            return;
        }

        _isAnimatingHeight = false;
        _heightAnimStopwatch.Stop();
        _pureBoundsAnimFrom = _pureBoundsAnimTarget;
        LayoutPureCardBackdrop(1.0);
        UpdateRenderingSubscription();
    }

    private double CurrentPureAnimationProgress()
        => EaseInOutCubic(Math.Min(1.0, _heightAnimStopwatch.Elapsed.TotalMilliseconds / WindowAnimDurationMs));

    // The card rect visible on screen. Inside the envelope the HWND is larger than the card, so
    // the window rect alone does not describe where the card is.
    private PixelRect GetCurrentPureCardRect()
    {
        if (_pureEnvelopeActive && _isAnimatingHeight)
        {
            return WindowBoundsGeometry.Interpolate(_pureBoundsAnimFrom, _pureBoundsAnimTarget,
                CurrentPureAnimationProgress());
        }

        if (_pureEnvelopeActive)
        {
            return _pureBoundsAnimTarget;
        }

        return _windowPlacementCoordinator.GetWindowRect(new WindowInteropHelper(this).Handle);
    }

    // The settled card rect (the destination while animating), for placement and hover bounds.
    private PixelRect GetPureCardPlacementRect(nint hwnd)
    {
        SyncPureEnvelopeWithWindow(hwnd);
        return _pureEnvelopeActive ? _pureBoundsAnimTarget : _windowPlacementCoordinator.GetWindowRect(hwnd);
    }

    private Rect ToEnvelopeDip(PixelRect rect)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            (rect.Left - _pureEnvelopeRect.Left) / dpi.DpiScaleX,
            (rect.Top - _pureEnvelopeRect.Top) / dpi.DpiScaleY,
            rect.Width / dpi.DpiScaleX,
            rect.Height / dpi.DpiScaleY);
    }

    // A drag moves the whole envelope; carry the card rects and anchor along with it. A size
    // change made by someone else (a DPI switch) invalidates the envelope instead.
    private void SyncPureEnvelopeWithWindow(nint hwnd)
    {
        if (!_pureEnvelopeActive)
        {
            return;
        }

        var window = _windowPlacementCoordinator.GetWindowRect(hwnd);
        if (window == _pureEnvelopeRect || window.Width <= 0 || window.Height <= 0)
        {
            return;
        }

        if (window.Width != _pureEnvelopeRect.Width || window.Height != _pureEnvelopeRect.Height)
        {
            ExitPureEnvelope(resizeWindowToCard: false);
            return;
        }

        var dx = window.Left - _pureEnvelopeRect.Left;
        var dy = window.Top - _pureEnvelopeRect.Top;
        _pureEnvelopeRect = window;
        _pureBoundsAnimFrom = Offset(_pureBoundsAnimFrom, dx, dy);
        _pureBoundsAnimTarget = Offset(_pureBoundsAnimTarget, dx, dy);
        _pureCardCenterX += dx;
        _pureCardCenterY += dy;
        ApplyMouseTrackerBounds();
    }

    private static PixelRect Offset(PixelRect rect, int dx, int dy)
        => new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);

    // Places the content at its final card rect inside the envelope. The text never reflows
    // during a transition; only the backdrop and its clip move.
    private void LayoutPureShell(PixelRect target)
    {
        var margin = _pureShellMargin;
        var final = ToEnvelopeDip(target);
        ShellBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        ShellBorder.VerticalAlignment = VerticalAlignment.Top;
        ShellBorder.Margin = new Thickness(final.X + margin.Left, final.Y + margin.Top, 0, 0);
        ShellBorder.Width = Math.Max(0, final.Width - margin.Left - margin.Right);
        ShellBorder.Height = Math.Max(0, final.Height - margin.Top - margin.Bottom);
        ShellBackdrop.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        ShellBackdrop.VerticalAlignment = VerticalAlignment.Top;
        ShellBorder.Clip = _pureCardClip;
    }

    private Thickness _pureShellMargin;

    private void LayoutPureCardBackdrop(double eased)
    {
        if (!_pureEnvelopeActive)
        {
            return;
        }

        var from = ToEnvelopeDip(_pureBoundsAnimFrom);
        var to = ToEnvelopeDip(_pureBoundsAnimTarget);
        var t = Math.Clamp(eased, 0.0, 1.0);
        var x = from.X + (to.X - from.X) * t;
        var y = from.Y + (to.Y - from.Y) * t;
        var width = from.Width + (to.Width - from.Width) * t;
        var height = from.Height + (to.Height - from.Height) * t;
        var margin = _pureShellMargin;
        var card = new Rect(x + margin.Left, y + margin.Top,
            Math.Max(0, width - margin.Left - margin.Right), Math.Max(0, height - margin.Top - margin.Bottom));

        ShellBackdrop.Margin = new Thickness(card.X, card.Y, 0, 0);
        ShellBackdrop.Width = card.Width;
        ShellBackdrop.Height = card.Height;

        // Clip the (final-size) content to the moving card so text never spills past its edges.
        var radius = ShellBackdrop.CornerRadius.TopLeft;
        _pureCardClip.Rect = new Rect(card.X - ShellBorder.Margin.Left, card.Y - ShellBorder.Margin.Top,
            card.Width, card.Height);
        _pureCardClip.RadiusX = radius;
        _pureCardClip.RadiusY = radius;
    }

    // Grows (or first creates) the envelope so it contains <paramref name="needed"/>. Returns the
    // window rect to apply after the caller has laid out the card for it, or null when the
    // current window already fits. Growing is the only time Pure Mode resizes its HWND.
    private PixelRect? EnsurePureEnvelope(MonitorDescriptor monitor, PixelRect needed)
    {
        if (_pureEnvelopeActive && WindowBoundsGeometry.Contains(_pureEnvelopeRect, needed))
        {
            return null;
        }

        if (!_pureEnvelopeActive)
        {
            _pureShellMargin = ShellBorder.Margin;
        }

        var padX = Math.Max((int)Math.Round(PureEnvelopePaddingXDip * monitor.ScaleX), needed.Width / 3);
        var padY = Math.Max((int)Math.Round(PureEnvelopePaddingYDip * monitor.ScaleY), needed.Height / 2);
        _pureEnvelopeRect = WindowBoundsGeometry.Envelope(needed, monitor, padX, padY);
        _pureEnvelopeActive = true;
        return _pureEnvelopeRect;
    }

    // Leaves the envelope: restores the stretched layout and, optionally, shrinks the HWND to
    // the card so mode switches and placement code see a window that is exactly the card.
    private void ExitPureEnvelope(bool resizeWindowToCard)
    {
        if (!_pureEnvelopeActive)
        {
            return;
        }

        var card = _pureBoundsAnimTarget;
        _isAnimatingHeight = false;
        _heightAnimStopwatch.Stop();
        _pureEnvelopeActive = false;
        ResetPureCanvasLayout();
        ApplyMouseTrackerBounds();
        UpdateRenderingSubscription();
        if (resizeWindowToCard)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_windowPlacementCoordinator.GetWindowRect(hwnd) != card)
            {
                ApplyNativeWindowRect(hwnd, card);
            }
        }
    }

    private void ResetPureCanvasLayout()
    {
        ShellBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ShellBorder.VerticalAlignment = VerticalAlignment.Stretch;
        ShellBorder.Width = double.NaN;
        ShellBorder.Height = double.NaN;
        ShellBorder.Margin = _pureShellMargin;
        ShellBorder.Clip = null;
        ShellBackdrop.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ShellBackdrop.VerticalAlignment = VerticalAlignment.Stretch;
        ShellBackdrop.Width = double.NaN;
        ShellBackdrop.Height = double.NaN;
        ShellBackdrop.Margin = _pureShellMargin;
    }

    // Hover fading must follow the visible card, not the transparent envelope around it.
    private void ApplyMouseTrackerBounds()
    {
        if (_mouseTracker is null)
        {
            return;
        }

        if (_pureEnvelopeActive)
        {
            var card = _pureBoundsAnimTarget;
            _mouseTracker.SetBounds(card.Left, card.Top, card.Right, card.Bottom);
        }
        else
        {
            _mouseTracker.ClearBounds();
        }
    }

    // Legacy center coordinates (DIP) of the card, for the PureModeWindowX/Y settings.
    private (int X, int Y) GetPureCardCenterDip()
    {
        if (!_pureEnvelopeActive)
        {
            return ((int)Math.Round(Left + GetCurrentLogicalSize(ActualWidth, Width, 180) / 2.0),
                (int)Math.Round(Top + GetCurrentLogicalSize(ActualHeight, Height, 70) / 2.0));
        }

        var card = ToEnvelopeDip(_pureBoundsAnimTarget);
        return ((int)Math.Round(Left + card.X + card.Width / 2.0), (int)Math.Round(Top + card.Y + card.Height / 2.0));
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

    private System.Windows.Size GetDesiredPureModeCardSize() => GetDesiredPureModeCardSize(GetShownPureCardContent());

    private System.Windows.Size GetDesiredPureModeCardSize(PureCardContent content)
    {
        var measured = MeasurePureModeWindowSize(content);
        return new System.Windows.Size(
            Math.Clamp(measured.Width, 180.0, PureModeMaxCardWidth),
            Math.Clamp(measured.Height, 70.0, PureModeMaxCardHeight));
    }

    // The texts that determine a Pure Mode card's size.
    private sealed record PureCardContent(
        string Current,
        string? Duet,
        IReadOnlyList<(string Text, TextBlock Element, double FontSize)> Secondary,
        string? Previous,
        string? Next);

    private PureCardContent GetShownPureCardContent()
    {
        var currentFontSize = _settings.MaxCurrentFontSize;
        var translationSize = TranslationTypography.GetFontSize(_settings.TranslationFontSize);
        return new PureCardContent(
            _currentLyric.DisplayText,
            _duetLyric.Line is not null ? _duetLyric.DisplayText : null,
            VisibleLyricViews().SelectMany(view => view.GetAuxiliaryRows(currentFontSize, translationSize))
                .Select(row => (row.Text, row.Element, row.FontSize)).ToArray(),
            PreviousLyricText.Visibility == Visibility.Visible ? PreviousLyricText.Text : null,
            NextLyricText.Visibility == Visibility.Visible ? NextLyricText.Text : null);
    }

    // Mirrors BindLyricViews/ApplyLyricContent for a lyric state that is not on screen yet.
    private PureCardContent GetPureCardContent(LyricsPresentation presentation)
    {
        var currentFontSize = _settings.MaxCurrentFontSize;
        var translationSize = TranslationTypography.GetFontSize(_settings.TranslationFontSize);
        var rows = new List<(string Text, TextBlock Element, double FontSize)>();
        void AddRows(LyricsLine? line, string? romanization, string? translation)
        {
            foreach (var vocal in line?.BackgroundVocals ?? [])
                if (!string.IsNullOrWhiteSpace(vocal.Text)) rows.Add((vocal.Text, CurrentLyricText, currentFontSize * 0.64));
            if (!string.IsNullOrWhiteSpace(romanization)) rows.Add((romanization, RomanizationLyricText, translationSize));
            var cleaned = _settings.ShowTranslation ? LyricsTranslationFilter.Clean(translation) : null;
            if (!string.IsNullOrWhiteSpace(cleaned)) rows.Add((cleaned, TranslationLyricText, translationSize));
        }

        AddRows(presentation.DisplayedLine, presentation.RomanizationText, presentation.TranslationText);
        var duet = presentation.DuetLine;
        if (duet is not null) AddRows(duet, presentation.DuetRomanizationText, duet.Translation);
        var showNext = _settings.ShowNextLine && !_settings.SingleLineMode && (!_settings.TwoLineMode || duet is null);
        return new PureCardContent(
            presentation.CurrentText,
            duet?.Text,
            rows,
            PreviousLyricText.Visibility == Visibility.Visible && _settings.ShowPreviousLine ? presentation.PreviousText : null,
            showNext ? presentation.NextText : null);
    }

    // Distinct lyric states the display will move through in the next few seconds.
    private IEnumerable<(ActiveLyricState State, RuntimeSnapshot Snapshot)> UpcomingSnapshots()
    {
        var snapshot = _runtimeService.GetFrameSnapshot();
        if (snapshot.Document is null || snapshot.Player is not { } player
            || !ReferenceEquals(snapshot.Document, _displayedLyricDocument))
        {
            yield break;
        }

        var lastCurrent = snapshot.ActiveLyric.CurrentLine;
        var lastDuet = snapshot.ActiveLyric.DuetLine;
        var changes = 0;
        for (var ahead = LookaheadStepSeconds; ahead <= LookaheadSeconds && changes < LookaheadLineChanges;
             ahead += LookaheadStepSeconds)
        {
            var future = player with { Position = player.Position + ahead };
            var state = _lookaheadSynchronizer.Resolve(snapshot.Document, future,
                _runtimeService.LyricsOffsetSeconds + _runtimeService.LineLeadSeconds, _settings.ApplyNativeLyricOffset);
            if (ReferenceEquals(state.CurrentLine, lastCurrent) && ReferenceEquals(state.DuetLine, lastDuet))
            {
                continue;
            }

            lastCurrent = state.CurrentLine;
            lastDuet = state.DuetLine;
            changes++;
            yield return (state, snapshot with { Player = future, ActiveLyric = state });
        }
    }

    private IEnumerable<ActiveLyricState> UpcomingStates() => UpcomingSnapshots().Select(item => item.State);

    // The largest card the next few lines will need, or null when nothing is coming.
    private (double Width, double Height)? EstimateUpcomingPureModeCardSize()
    {
        (double Width, double Height)? largest = null;
        foreach (var (_, future) in UpcomingSnapshots())
        {
            var presentation = OverlaySnapshotPresenter.CreateLyrics(future, showTranslation: _settings.ShowTranslation,
                includeDetailedDiagnostics: false, showRomanization: _settings.ShowRomanization);
            var size = GetDesiredPureModeCardSize(GetPureCardContent(presentation));
            largest = largest is { } known
                ? (Math.Max(known.Width, size.Width), Math.Max(known.Height, size.Height))
                : (size.Width, size.Height);
        }

        return largest;
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
        var diagnosticsWindow = _diagnosticsWindow;
        _diagnosticsWindow = null;
        diagnosticsWindow?.Close();
        var lyricsSelectionWindow = _lyricsSelectionWindow;
        _lyricsSelectionWindow = null;
        lyricsSelectionWindow?.Close();
        HeaderPanel.SizeChanged -= OnDiagnosticContentSizeChanged;
        LocalizationManager.Instance.LanguageChanged -= OnUiLanguageChanged;
        UpdateRenderingSubscription();
        _runtimePollingController.Dispose();
        StopMouseTracker();
        PersistCurrentSettings();
        _updateWindow?.Close();
        _updates.Dispose();
        _trayIconController.Dispose();
        _runtimeService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _catalogResolver?.Dispose();
        foreach (var provider in _externalLyricsProviders.OfType<IDisposable>())
        {
            provider.Dispose();
        }
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
            (_settings.PureModeWindowX, _settings.PureModeWindowY) = GetPureCardCenterDip();
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

            PreviousLyricText.FontSize = pureContextFontSize;
            NextLyricText.FontSize = pureContextFontSize;
            ApplyLyricFontSizes(pureCurrentFontSize);
            return;
        }

        var effectiveWindowWidth = Math.Max(windowWidthOverride ?? ActualWidth, MinWidth);
        var effectiveWindowHeight = Math.Max(windowHeightOverride ?? ActualHeight, MinHeight);

        var shellMargin = 14.0;
        var shellPadding = 12.0;
        var totalHorizontalChrome = shellMargin * 2 + shellPadding * 2;
        var contentWidth = Math.Max(140, effectiveWindowWidth - totalHorizontalChrome - 16);

        var currentText = string.IsNullOrWhiteSpace(_currentLyric.DisplayText) ? "Current lyric line" : _currentLyric.DisplayText;
        var contentHeight = singleLine ? 72 : twoLine ? Math.Max(64, effectiveWindowHeight * 0.26) : Math.Max(88, effectiveWindowHeight * 0.48);
        var currentFontSize = FitFontSize(
            currentText,
            _settings.MaxCurrentFontSize,
            14,
            Math.Max(140, contentWidth),
            contentHeight,
            singleLine);
        if (_duetLyric.Line is not null)
        {
            currentFontSize = FitFontSize(_duetLyric.DisplayText, currentFontSize, 14, contentWidth, contentHeight, singleLine);
        }
        var contextScale = twoLine ? 0.72 : 0.56;
        var contextFontSize = Math.Clamp(currentFontSize * contextScale, 12, _settings.ContextFontSize);

        PreviousLyricText.FontSize = contextFontSize;
        NextLyricText.FontSize = contextFontSize;
        ApplyLyricFontSizes(currentFontSize);
    }

    private void ApplyLyricFontSizes(double currentFontSize)
    {
        var translationSize = TranslationTypography.GetFontSize(_settings.TranslationFontSize);
        foreach (var view in _lyricViews) view.SetFontSizes(currentFontSize, translationSize);
    }

    private IEnumerable<LyricLineView> VisibleLyricViews()
    {
        yield return _currentLyric;
        if (_duetLyric.Line is not null) yield return _duetLyric;
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
        var captured = _settings.PureMode
            ? _windowPlacementCoordinator.Capture(handle, GetPureCardPlacementRect(handle))
            : _windowPlacementCoordinator.Capture(handle);
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
        // Leave the envelope first: the shell margins below assume a stretched layout.
        ExitPureEnvelope(resizeWindowToCard: true);
        MinWidth = _settings.PureMode ? 180 : _settings.MinWindowWidth;
        MinHeight = _settings.PureMode ? 70 : _settings.MinWindowHeight;
        ResizeMode = _settings.PureMode ? System.Windows.ResizeMode.NoResize : System.Windows.ResizeMode.CanResize;
        RefreshOverlayOpacity();

        // Hide resize controls in PureMode
        ResizeLayer.Visibility = _settings.PureMode ? Visibility.Collapsed : Visibility.Visible;

        ShellBorder.Padding = _settings.PureMode ? new Thickness(6) : new Thickness(12);
        ShellBorder.Margin = _settings.PureMode ? new Thickness(6) : new Thickness(14);
        ShellBorder.Clip = null;
        ShellBackdrop.Margin = ShellBorder.Margin;
        ShellBackdrop.CornerRadius = ShellBorder.CornerRadius;
        // Pure Mode content sizes to the real card HWND; normal mode stretches to its window.
        ContentRoot.HorizontalAlignment = _settings.PureMode ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Stretch;
        ContentRoot.VerticalAlignment = _settings.PureMode ? System.Windows.VerticalAlignment.Center : System.Windows.VerticalAlignment.Stretch;
        LyricPanel.Margin = _settings.PureMode ? new Thickness(0, 2, 0, 2) : new Thickness(0, 8, 0, 6);
        HeaderPanel.Visibility = _settings.PureMode ? Visibility.Collapsed : Visibility.Visible;
        FooterPanel.Visibility = _settings.PureMode || !_settings.ShowDebugPanel ? Visibility.Collapsed : Visibility.Visible;
        var twoLineMode = _settings.TwoLineMode && !_settings.SingleLineMode;
        PreviousLyricText.Visibility = _settings.ShowPreviousLine && !_settings.SingleLineMode && !twoLineMode ? Visibility.Visible : Visibility.Collapsed;
        NextLyricText.Visibility = CanShowNextLyric ? Visibility.Visible : Visibility.Collapsed;
        PreviousLyricText.MaxHeight = _settings.PureMode ? double.PositiveInfinity : 52;
        NextLyricText.MaxHeight = _settings.PureMode ? double.PositiveInfinity : 52;

        if (twoLineMode)
        {
            PreviousLyricRow.Height = new GridLength(0);
            CurrentLyricRow.Height = GridLength.Auto;
            NextLyricRow.Height = CanShowNextLyric && !string.IsNullOrWhiteSpace(_currentNextText)
                ? GridLength.Auto : new GridLength(0);
            System.Windows.Controls.Grid.SetRow(CurrentLyricContainer, 1);
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
            System.Windows.Controls.Grid.SetRow(CurrentLyricContainer, 1);
            System.Windows.Controls.Grid.SetRow(NextLyricText, 2);
            CurrentLyricText.Margin = _settings.PureMode ? new Thickness(0, 2, 0, 2) : new Thickness(0, 4, 0, 4);
            NextLyricText.Margin = new Thickness(0);
            NextLyricText.VerticalAlignment = VerticalAlignment.Center;
        }

        var alpha = (byte)Math.Clamp(_settings.BackgroundAlpha ?? (_settings.PureMode ? 72 : 200), 0, 255);
        ShellBackdrop.Background = new SolidColorBrush(MediaColor.FromArgb(alpha, 20, 20, 22));

        var fontFamily = FontFamilyResolver.Resolve(_settings.FontFamily, _settings.ChineseFontFamily);
        PreviousLyricText.FontFamily = fontFamily;
        NextLyricText.FontFamily = fontFamily;
        var translationFont = TranslationTypography.ResolveFontFamily(_settings.TranslationFontFamily);
        var translationColor = CreateBrush(_settings.ContextLineColor, Colors.White);
        var sung = ParseColorOrFallback(_lastFramePlaying ? _settings.CurrentLineColor : _settings.PausedLineColor, Colors.White);
        foreach (var view in _lyricViews)
        {
            view.MainText.FontFamily = fontFamily;
            view.MainText.Margin = CurrentLyricText.Margin;
            view.MainText.TextWrapping = _settings.SingleLineMode ? TextWrapping.NoWrap : TextWrapping.Wrap;
            view.MainText.TextTrimming = _settings.SingleLineMode ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            view.SetAuxiliaryAppearance(translationFont, translationColor);
            var line = view.Line;
            var romanization = LyricRomanization.Resolve(_displayedLyricDocument, line, _settings.ShowRomanization);
            view.Bind(view.DisplayText, line, romanization, _settings.ShowTranslation ? line?.Translation : null,
                _settings.WordHighlightEnabled, sung);
        }
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
        QueueDiagnosticsLayoutRefresh();
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
            ApplyMouseTrackerBounds();
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

    private void OpenDiagnosticsWindow()
    {
        if (_isClosed) return;
        if (_diagnosticsWindow is null)
        {
            var window = new DiagnosticsWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_diagnosticsWindow, window)) _diagnosticsWindow = null;
            };
            _diagnosticsWindow = window;
        }

        UpdateDiagnosticsWindow();
        if (_diagnosticsWindow.WindowState == WindowState.Minimized)
            _diagnosticsWindow.WindowState = WindowState.Normal;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }

    private void LyricsContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        RefreshLyricsMenuItem.IsEnabled = _canManageLyrics;
        ChooseLyricsMenuItem.IsEnabled = _canManageLyrics;
    }

    private void RefreshLyricsMenuItem_OnClick(object sender, RoutedEventArgs e) => RefreshCurrentLyrics();

    private void ChooseLyricsMenuItem_OnClick(object sender, RoutedEventArgs e) => OpenLyricsSelection();

    private void RefreshCurrentLyrics()
    {
        if (!_isClosed && _canManageLyrics)
            _runtimeService.RequestLyricsRefresh(_runtimeService.GetFrameSnapshot().TrackGeneration);
    }

    private void OpenLyricsSelection()
    {
        if (_isClosed || !_canManageLyrics) return;
        var snapshot = _runtimeService.GetFrameSnapshot();
        if (!snapshot.CanManageLyrics) return;
        if (_lyricsSelectionWindow is null || _lyricsSelectionWindow.TrackGeneration != snapshot.TrackGeneration)
        {
            _lyricsSelectionWindow?.Close();
            var window = new LyricsSelectionWindow(_runtimeService, snapshot);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_lyricsSelectionWindow, window)) _lyricsSelectionWindow = null;
            };
            _lyricsSelectionWindow = window;
        }

        if (_lyricsSelectionWindow.WindowState == WindowState.Minimized)
            _lyricsSelectionWindow.WindowState = WindowState.Normal;
        _lyricsSelectionWindow.Show();
        _lyricsSelectionWindow.Activate();
    }

    private void UpdateDiagnosticsWindow(RuntimeSnapshot? snapshot = null)
    {
        if (_diagnosticsWindow is not { } window) return;
        snapshot ??= _runtimeService.GetFrameSnapshot();
        var player = OverlaySnapshotPresenter.CreatePlayer(snapshot);
        var text = LocalizationManager.Instance;
        if (_runtimeRefreshFailed && _lastRuntimeError is { } error)
        {
            window.UpdateDiagnostics(player.WindowTitle, text.Text("Runtime.RefreshFailed"),
                $"{text.Text("Runtime.CheckPlayer")}{Environment.NewLine}{FormatRenderRate()} | {FormatPollRate()}"
                + $"{Environment.NewLine}{error.GetType().Name}: {error.Message}");
            return;
        }

        var lyrics = OverlaySnapshotPresenter.CreateLyrics(snapshot, showTranslation: _settings.ShowTranslation);
        window.UpdateDiagnostics(player.WindowTitle, lyrics.DebugSummaryText,
            $"{player.PlayerText}{Environment.NewLine}{FormatTimingText(snapshot)}{Environment.NewLine}{lyrics.PathText}");
    }

    private void StartUpdates()
    {
        // Remove packages left by earlier updates, then start the automatic schedule.
        _updates.Installer.CleanUp(AppContext.BaseDirectory);
        _updates.ApplySchedule();
        var text = LocalizationManager.Instance;
        switch (System.Windows.Application.Current.Properties[App.UpdateOutcomeKey] as string)
        {
            case App.UpdateOutcomeInstalled:
                _trayIconController.ShowNotification(text.Format("Update.Balloon.Updated.Title", _updates.CurrentVersion),
                    text.Text("Update.Balloon.Updated.Text"));
                break;
            case App.UpdateOutcomeFailed:
                _trayIconController.ShowNotification(text.Text("Update.Balloon.Failed.Title"),
                    text.Text("Update.Balloon.Failed.Text"), () => UpdateCoordinator.OpenReleasePage(null));
                break;
        }
    }

    // A known release opens directly; otherwise (or when asked to) the window checks first.
    private void OpenUpdateWindow(bool checkNow)
    {
        if (_isClosed) return;
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        _updateWindow = new UpdateWindow(_updates, checkNow ? null : _updates.Available);
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
        _updateWindow.Activate();
    }

    private void OpenSettingsWindow()
    {
        var cacheGeneration = _externalLyricsCache?.Generation;
        // Show the offset actually in effect so the slider reflects a per-song calibration.
        var dialogSettings = _settings;
        if (_settings.PerSongOffsetEnabled)
        {
            dialogSettings = _settings.Clone();
            dialogSettings.LyricsOffsetSeconds = _runtimeService.LyricsOffsetSeconds;
        }

        var settingsWindow = new SettingsWindow(
            dialogSettings,
            previewSettings => ApplySettings(previewSettings, persist: true, cacheGeneration),
            lyricsCache: _externalLyricsCache,
            openDiagnostics: OpenDiagnosticsWindow,
            checkForUpdates: () => OpenUpdateWindow(checkNow: true))
        {
            Owner = IsVisible ? this : null,
        };

        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        ApplySettings(settingsWindow.Settings, persist: true, cacheGeneration);
    }

    private void SetPureMode(bool enabled, bool persist = true)
    {
        // Shrink back to the card so the captured placement is the card, not the envelope.
        ExitPureEnvelope(resizeWindowToCard: true);
        // Save both legacy coordinates and per-monitor placement before switching mode.
        CaptureCurrentPlacement();
        if (WindowState == WindowState.Normal)
        {
            if (_settings.PureMode)
            {
                (_settings.PureModeWindowX, _settings.PureModeWindowY) = GetPureCardCenterDip();
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
        if (!enabled)
        {
            Dispatcher.BeginInvoke(() => StartHeightAnimation(), DispatcherPriority.Loaded);
        }

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

    private void ApplySettings(AppSettings sourceSettings, bool persist, long? expectedCacheGeneration = null)
    {
        var currentLeft = Left;
        var currentTop = Top;
        var currentWidth = Width;
        var currentHeight = Height;
        var currentPureMode = _settings.PureMode;
        var cacheWasEnabled = _settings.PersistExternalLyricsCache;
        var offsetChanged = sourceSettings.LyricsOffsetSeconds != _settings.LyricsOffsetSeconds;
        var perSongOffsetWasEnabled = _settings.PerSongOffsetEnabled;
        var requestedPureMode = sourceSettings.PureMode;

        CancelPureModeBoundsAnimation();
        CopySettings(sourceSettings, _settings);
        _updates.ApplySchedule();
        _externalLyricsCache?.Configure(_settings.PersistExternalLyricsCache, _settings.ExternalLyricsCacheMaxMegabytes);
        // Applying unrelated edits after Clear must not repopulate the current song.
        if (!cacheWasEnabled && _settings.PersistExternalLyricsCache)
        {
            CacheCurrentExternalLyrics(expectedCacheGeneration);
        }
        LocalizationManager.Instance.SetLanguage(_settings.UiLanguage);
        // Keep the source mode active until SetPureMode captures its placement. The settings
        // dialog carries both modes' saved anchors, so the target placement remains available.
        _settings.PureMode = currentPureMode;

        _playerProvider.AllowNonAppleMediaSessions = _settings.AllowNonAppleMediaSessions;
        _runtimeService.PerSongOffsetEnabled = _settings.PerSongOffsetEnabled;
        if (offsetChanged || !_settings.PerSongOffsetEnabled)
        {
            _runtimeService.LyricsOffsetSeconds = _settings.LyricsOffsetSeconds;
            if (offsetChanged)
            {
                _runtimeService.RememberCurrentOffset();
            }
        }
        else if (!perSongOffsetWasEnabled)
        {
            _runtimeService.ApplyPerSongOffset();
        }

        _runtimeService.ApplyNativeLyricOffset = _settings.ApplyNativeLyricOffset;
        _runtimeService.AllowLowConfidenceLyrics = _settings.AllowLowConfidenceLyrics;
        _runtimeService.PreferWordTimings = _settings.WordHighlightEnabled;
        _runtimeService.PreferTranslations = _settings.ShowTranslation;
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
            // Font edits must resize the current line even when playback is paused.
            Dispatcher.BeginInvoke(() => StartHeightAnimation(), DispatcherPriority.Loaded);
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
                (_settings.PureModeWindowX, _settings.PureModeWindowY) = GetPureCardCenterDip();
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

    private void CacheCurrentExternalLyrics(long? expectedGeneration)
    {
        if (!_settings.PersistExternalLyricsCache || _externalLyricsCache is null) return;
        var snapshot = _runtimeService.GetFrameSnapshot();
        if (snapshot.Player is not { } player || snapshot.Document is not { } document) return;

        var source = document.SourceFile.StartsWith("qqmusic:", StringComparison.OrdinalIgnoreCase) ? "QQ Music"
            : document.SourceFile.StartsWith("netease:", StringComparison.OrdinalIgnoreCase) ? "NetEase Cloud Music"
            : document.SourceFile.StartsWith("lrclib:", StringComparison.OrdinalIgnoreCase)
                || document.SourceFile.StartsWith("lrclib-plain:", StringComparison.OrdinalIgnoreCase) ? "LRCLIB"
            : null;
        if (source is not null) _externalLyricsCache.Store(source, player, document, expectedGeneration);
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
            SyncPureEnvelopeWithWindow(hwnd);
            var currentRect = GetCurrentPureCardRect();
            var monitors = _windowPlacementCoordinator.GetMonitors();
            if (currentRect.Width <= 0 || currentRect.Height <= 0 || monitors.Count == 0)
            {
                return;
            }

            var monitor = _windowPlacementCoordinator.GetMonitorForWindow(hwnd, monitors)
                ?? WindowPlacementService.FindMonitorForRect(monitors, currentRect);
            if (!_pureEnvelopeActive)
            {
                // Entering the envelope: the window is the card, so its center is the anchor.
                _pureCardCenterX = currentRect.Left + currentRect.Width / 2.0;
                _pureCardCenterY = currentRect.Top + currentRect.Height / 2.0;
            }

            // Every card is centered on the same anchor. Re-centering on the previous (rounded or
            // mid-animation) rect drifted by a pixel, which restarted or snapped transitions.
            var requestedSize = GetDesiredPureModeCardSize();
            var shownRect = _pureEnvelopeActive ? _pureBoundsAnimTarget : currentRect;
            var shownSize = (shownRect.Width / monitor.ScaleX, shownRect.Height / monitor.ScaleY);
            var desired = (requestedSize.Width, requestedSize.Height);
            var shrinking = desired.Width < shownSize.Item1 - 1 || desired.Height < shownSize.Item2 - 1;
            // Only a shrink needs the forecast; growth is always applied at once.
            var heldSize = LookaheadSizing.Resolve(desired, shownSize,
                _pureEnvelopeActive && shrinking ? EstimateUpcomingPureModeCardSize() : null);
            var targetRect = WindowBoundsGeometry.CenterAt(
                _pureCardCenterX,
                _pureCardCenterY,
                monitor,
                heldSize.Width,
                heldSize.Height);
            var targetWidthDip = targetRect.Width / monitor.ScaleX;
            var targetHeightDip = targetRect.Height / monitor.ScaleY;

            UpdateTextMaxWidths(targetWidthDip);
            ApplyAdaptiveFontSizes(targetWidthDip, targetHeightDip);

            if (_pureEnvelopeActive && targetRect == _pureBoundsAnimTarget)
            {
                // Already showing (or heading to) this card; let a running transition finish.
                return;
            }

            var animate = Math.Abs(currentRect.Left - targetRect.Left) > 1
                || Math.Abs(currentRect.Top - targetRect.Top) > 1
                || Math.Abs(currentRect.Right - targetRect.Right) > 1
                || Math.Abs(currentRect.Bottom - targetRect.Bottom) > 1;
            var windowRect = EnsurePureEnvelope(monitor, WindowBoundsGeometry.Union(currentRect, targetRect));
            _pureBoundsAnimFrom = animate ? currentRect : targetRect;
            _pureBoundsAnimTarget = targetRect;
            LayoutPureShell(targetRect);
            LayoutPureCardBackdrop(0.0);
            ApplyMouseTrackerBounds();
            if (windowRect is { } grown && _windowPlacementCoordinator.GetWindowRect(hwnd) != grown)
            {
                // The layout above is already arranged for the new window rect.
                ApplyNativeWindowRect(hwnd, grown);
            }

            if (animate)
            {
                _heightAnimStopwatch.Restart();
                _isAnimatingHeight = true;
            }
            else
            {
                _isAnimatingHeight = false;
                _heightAnimStopwatch.Stop();
            }

            UpdateRenderingSubscription();
        }
        finally
        {
            _isApplyingPureModeAutoSize = false;
        }
    }

    // Matches UpdateTextMaxWidths: lyric rows wrap 16 DIP inside the content, context rows 24.
    private const double PureModeLyricWrapInset = 16.0;
    private const double PureModeContextWrapInset = 24.0;

    private double GetPureModeMaxCardWidth()
    {
        if (!_sourceInitialized)
        {
            return PureModeMaxCardWidth;
        }

        var monitor = _windowPlacementCoordinator.GetMonitorForWindow(new WindowInteropHelper(this).Handle,
            _windowPlacementCoordinator.GetMonitors());
        return monitor is null
            ? PureModeMaxCardWidth
            : Math.Min(PureModeMaxCardWidth, Math.Floor(monitor.WorkAreaPx.Width / monitor.ScaleX));
    }

    private System.Windows.Size MeasurePureModeWindowSize(PureCardContent content)
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

        // The card can never be wider than the monitor's work area; measure for the width it gets.
        var maxContentWidth = GetPureModeMaxCardWidth() - extraWidthPadding;
        var maxContentHeight = (PureModeMaxCardHeight - 40) - extraHeightPadding;
        var minContentWidth = 120;

        var currentFontSize = _settings.MaxCurrentFontSize;
        var contextScale = _settings.TwoLineMode ? 0.72 : 0.56;
        var contextFontSize = Math.Clamp(currentFontSize * contextScale, 12, _settings.ContextFontSize);

        // Shape each text once; the width search below only re-wraps the cached glyphs.
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var current = CreateLyricTextMeasurement(content.Current, CurrentLyricText, currentFontSize, pixelsPerDip);
        var duet = content.Duet is not null
            ? CreateLyricTextMeasurement(content.Duet, DuetLyricText, currentFontSize, pixelsPerDip)
            : null;
        var secondary = content.Secondary
            .Select(row => CreateLyricTextMeasurement(row.Text, row.Element, row.FontSize, pixelsPerDip)).ToArray();
        var previous = !string.IsNullOrWhiteSpace(content.Previous)
            ? CreateLyricTextMeasurement(content.Previous, PreviousLyricText, contextFontSize, pixelsPerDip)
            : null;
        var next = !string.IsNullOrWhiteSpace(content.Next)
            ? CreateLyricTextMeasurement(content.Next, NextLyricText, contextFontSize, pixelsPerDip)
            : null;

        var desiredContentWidth = Math.Max(Math.Max(current.NaturalWidth, duet?.NaturalWidth ?? 0),
            Math.Max(secondary.Select(text => text.NaturalWidth).DefaultIfEmpty().Max(),
                Math.Max(previous?.NaturalWidth ?? 0, next?.NaturalWidth ?? 0))) + PureModeContextWrapInset + 2;
        var contentWidth = Math.Clamp(desiredContentWidth, minContentWidth, maxContentWidth);
        var contentHeight = MeasureContentHeight(contentWidth, current, duet, secondary, previous, next);

        if (contentHeight > maxContentHeight)
        {
            var low = contentWidth;
            var high = maxContentWidth;
            var bestWidth = maxContentWidth;

            for (var iteration = 0; iteration < 12; iteration++)
            {
                var mid = (low + high) / 2.0;
                var midHeight = MeasureContentHeight(mid, current, duet, secondary, previous, next);
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
            contentHeight = MeasureContentHeight(contentWidth, current, duet, secondary, previous, next);
        }

        return new System.Windows.Size(
            Math.Ceiling(contentWidth + extraWidthPadding),
            Math.Ceiling(Math.Min(contentHeight, maxContentHeight) + extraHeightPadding));
    }

    private double MeasureContentHeight(double contentWidth, LyricTextMeasurement current, LyricTextMeasurement? duet,
        IReadOnlyList<LyricTextMeasurement> secondary,
        LyricTextMeasurement? previous, LyricTextMeasurement? next)
    {
        var lyricMaxLines = _settings.SingleLineMode ? 1 : (_settings.PureMode ? 4 : 2);
        // Wrap at the same widths UpdateTextMaxWidths gives the real TextBlocks. Measuring at the
        // full content width let a line that only just fit there wrap on screen, pushing the
        // translation outside the card.
        var lyricWidth = Math.Max(1, contentWidth - PureModeLyricWrapInset);
        var contextWidth = Math.Max(1, contentWidth - PureModeContextWrapInset);
        var totalHeight = current.MeasureHeight(lyricWidth, lyricMaxLines);
        if (duet is not null)
        {
            totalHeight += duet.MeasureHeight(lyricWidth, lyricMaxLines) + DuetLyricText.Margin.Top + DuetLyricText.Margin.Bottom;
        }
        foreach (var text in secondary)
        {
            totalHeight += text.MeasureHeight(lyricWidth, 2) + 4;
        }

        // Use PureMode-specific margin values
        var currentMarginTop = _settings.PureMode ? 2.0 : 4.0;
        var currentMarginBottom = _settings.PureMode ? (_settings.TwoLineMode ? 4.0 : 2.0) : (_settings.TwoLineMode ? 10.0 : 4.0);

        var showNext = _settings.TwoLineMode && !_settings.SingleLineMode && next is not null;
        var showPrevious = !_settings.TwoLineMode && !_settings.SingleLineMode && previous is not null;

        if (showNext)
        {
            totalHeight += currentMarginBottom;
            totalHeight += next!.MeasureHeight(contextWidth, 2);
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
                totalHeight += previous!.MeasureHeight(contextWidth, _settings.PureMode ? 4 : 2);
            }

            totalHeight += currentMarginTop + currentMarginBottom;

            if (next is not null)
            {
                totalHeight += next.MeasureHeight(contentWidth, _settings.PureMode ? 4 : 2);
            }
        }

        return totalHeight;
    }

    private LyricTextMeasurement CreateLyricTextMeasurement(string? text, TextBlock reference,
        double fontSize, double pixelsPerDip)
    {
        var typeface = new Typeface(
            reference.FontFamily,
            reference.FontStyle,
            reference.FontWeight,
            reference.FontStretch);
        var lineHeight = double.IsFinite(reference.LineHeight) && reference.LineHeight > 0 && reference.FontSize > 0
            ? reference.LineHeight * fontSize / reference.FontSize
            : 0;
        return new LyricTextMeasurement(text, typeface, fontSize, lineHeight, pixelsPerDip);
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

        if (double.IsFinite(reference.LineHeight) && reference.LineHeight > 0 && reference.FontSize > 0)
        {
            formattedText.LineHeight = reference.LineHeight * fontSize / reference.FontSize;
        }

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
        foreach (var view in _lyricViews) view.SetMaxWidth(currentMaxWidth);
        PreviousLyricText.MaxWidth = contextMaxWidth;
        NextLyricText.MaxWidth = contextMaxWidth;
        StatusText.MaxWidth = OverlayDiagnosticsLayout.GetStatusMaxWidth(windowWidth - totalHorizontalChrome);
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
            _currentLyric.DisplayText,
            _canManageLyrics);
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
        var fontFamily = CurrentLyricText.FontFamily
            ?? FontFamilyResolver.Resolve(_settings.FontFamily, _settings.ChineseFontFamily);
        var typeface = new Typeface(
            fontFamily,
            CurrentLyricText.FontStyle,
            CurrentLyricText.FontWeight,
            CurrentLyricText.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        return LyricFontSizeFitter.FindLargestFittingSize(text, typeface, maxSize, minSize,
            maxWidth, maxHeight, singleLine, pixelsPerDip, CultureInfo.CurrentUICulture);
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
        target.PerSongOffsetEnabled = copy.PerSongOffsetEnabled;
        target.AllowNonAppleMediaSessions = copy.AllowNonAppleMediaSessions;
        target.AllowLowConfidenceLyrics = copy.AllowLowConfidenceLyrics;
        target.CatalogLookupEnabled = copy.CatalogLookupEnabled;
        target.CatalogStorefronts = copy.CatalogStorefronts;
        target.CatalogLookupTimeoutSeconds = copy.CatalogLookupTimeoutSeconds;
        target.ExternalLyricsEnabled = copy.ExternalLyricsEnabled;
        target.ExternalLyricsTimeoutSeconds = copy.ExternalLyricsTimeoutSeconds;
        target.PersistExternalLyricsCache = copy.PersistExternalLyricsCache;
        target.ExternalLyricsCacheMaxMegabytes = copy.ExternalLyricsCacheMaxMegabytes;
        target.CheckForUpdates = copy.CheckForUpdates;
        target.SkippedUpdateVersion = copy.SkippedUpdateVersion;
        target.UiLanguage = copy.UiLanguage;
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
        target.ChineseFontFamily = copy.ChineseFontFamily;
        target.TranslationFontFamily = copy.TranslationFontFamily;
        target.TranslationFontSize = copy.TranslationFontSize;
        target.ShowPreviousLine = copy.ShowPreviousLine;
        target.ShowNextLine = copy.ShowNextLine;
        target.WordHighlightEnabled = copy.WordHighlightEnabled;
        target.ShowTranslation = copy.ShowTranslation;
        target.ShowRomanization = copy.ShowRomanization;
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
