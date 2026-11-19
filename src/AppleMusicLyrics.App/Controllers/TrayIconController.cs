using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using AppleMusicLyrics.App.Localization;
using Forms = System.Windows.Forms;

namespace AppleMusicLyrics.App.Controllers;

public sealed class TrayIconController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Forms.ToolStripMenuItem _showHideMenuItem;
    private readonly Forms.ToolStripMenuItem _clickThroughMenuItem;
    private readonly Forms.ToolStripMenuItem _pureModeMenuItem;
    private readonly Forms.ToolStripMenuItem _singleLineMenuItem;
    private readonly Forms.ToolStripMenuItem _twoLineMenuItem;
    private readonly Forms.ToolStripMenuItem _debugPanelMenuItem;
    private readonly Forms.ToolStripMenuItem _diagnosticsMenuItem;
    private readonly Forms.ToolStripMenuItem _refreshLyricsMenuItem;
    private readonly Forms.ToolStripMenuItem _chooseLyricsMenuItem;
    private readonly Forms.ToolStripMenuItem _updateMenuItem;
    private readonly Forms.ToolStripMenuItem _settingsMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;

    public TrayIconController(
        Dispatcher dispatcher,
        Action toggleOverlay,
        Action toggleClickThrough,
        Action togglePureMode,
        Action toggleSingleLine,
        Action toggleTwoLine,
        Action toggleDebugPanel,
        Action openDiagnostics,
        Action refreshLyrics,
        Action chooseLyrics,
        Action openSettings,
        Action exit,
        Action openUpdates)
    {
        var menu = new Forms.ContextMenuStrip();
        var text = LocalizationManager.Instance;
        _showHideMenuItem = AddItem(menu, text.Text("Tray.Hide"), dispatcher, toggleOverlay);
        _clickThroughMenuItem = AddItem(menu, text.Text("Tray.ClickThrough"), dispatcher, toggleClickThrough);
        _pureModeMenuItem = AddItem(menu, text.Text("Tray.PureMode"), dispatcher, togglePureMode);
        _singleLineMenuItem = AddItem(menu, text.Text("Tray.SingleLine"), dispatcher, toggleSingleLine);
        _twoLineMenuItem = AddItem(menu, text.Text("Tray.TwoLine"), dispatcher, toggleTwoLine);
        _debugPanelMenuItem = AddItem(menu, text.Text("Tray.Debug"), dispatcher, toggleDebugPanel);
        _diagnosticsMenuItem = AddItem(menu, text.Text("Diagnostics.Open"), dispatcher, openDiagnostics);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _refreshLyricsMenuItem = AddItem(menu, text.Text("Lyrics.Menu.Rescan"), dispatcher, refreshLyrics);
        _chooseLyricsMenuItem = AddItem(menu, text.Text("Lyrics.Menu.Choose"), dispatcher, chooseLyrics);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _updateMenuItem = AddItem(menu, text.Text("Update.Tray.Check"), dispatcher, openUpdates);
        _settingsMenuItem = AddItem(menu, text.Text("Tray.Settings"), dispatcher, openSettings);
        _exitMenuItem = AddItem(menu, text.Text("Tray.Exit"), dispatcher, exit);

        _icon = LoadAppIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "Apple Music Lyrics",
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => dispatcher.Invoke(toggleOverlay);
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            if (action is not null) dispatcher.Invoke(action);
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonAction = null;
    }

    private Action? _balloonAction;
    private string? _availableUpdateVersion;

    /// <summary>Shows a newer version in the tray menu (null: the plain "Check for updates" item).</summary>
    public void SetAvailableUpdate(string? version)
    {
        _availableUpdateVersion = version;
        var text = LocalizationManager.Instance;
        _updateMenuItem.Text = version is null ? text.Text("Update.Tray.Check") : text.Format("Update.Tray.Available", version);
        _updateMenuItem.Font = version is null
            ? new Font(_updateMenuItem.Font, System.Drawing.FontStyle.Regular)
            : new Font(_updateMenuItem.Font, System.Drawing.FontStyle.Bold);
    }

    /// <summary>Shows a Windows notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void ShowNotification(string title, string text, Action? onClick = null)
    {
        _balloonAction = onClick;
        _notifyIcon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Info);
    }

    public void Update(
        bool isOverlayVisible,
        bool clickThrough,
        bool pureMode,
        bool singleLine,
        bool twoLine,
        bool showDebugPanel,
        string? currentLyric,
        bool canManageLyrics)
    {
        var text = LocalizationManager.Instance;
        _showHideMenuItem.Text = text.Text(isOverlayVisible ? "Tray.Hide" : "Tray.Show");
        _clickThroughMenuItem.Text = text.Text("Tray.ClickThrough");
        _pureModeMenuItem.Text = text.Text("Tray.PureMode");
        _singleLineMenuItem.Text = text.Text("Tray.SingleLine");
        _twoLineMenuItem.Text = text.Text("Tray.TwoLine");
        _debugPanelMenuItem.Text = text.Text("Tray.Debug");
        _diagnosticsMenuItem.Text = text.Text("Diagnostics.Open");
        _refreshLyricsMenuItem.Text = text.Text("Lyrics.Menu.Rescan");
        _chooseLyricsMenuItem.Text = text.Text("Lyrics.Menu.Choose");
        _refreshLyricsMenuItem.Enabled = canManageLyrics;
        _chooseLyricsMenuItem.Enabled = canManageLyrics;
        _updateMenuItem.Text = _availableUpdateVersion is null
            ? text.Text("Update.Tray.Check")
            : text.Format("Update.Tray.Available", _availableUpdateVersion);
        _settingsMenuItem.Text = text.Text("Tray.Settings");
        _exitMenuItem.Text = text.Text("Tray.Exit");
        _clickThroughMenuItem.Checked = clickThrough;
        _pureModeMenuItem.Checked = pureMode;
        _singleLineMenuItem.Checked = singleLine;
        _twoLineMenuItem.Checked = twoLine;
        _debugPanelMenuItem.Checked = showDebugPanel;
        _notifyIcon.Text = BuildTrayText(currentLyric);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    internal static string BuildTrayText(string? currentLyric)
    {
        if (string.IsNullOrWhiteSpace(currentLyric))
        {
            return "Apple Music Lyrics";
        }

        var baseText = currentLyric.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        const string prefix = "Apple Music Lyrics - ";
        const int maxLength = 63;
        var available = maxLength - prefix.Length;
        var trimmed = baseText.Length <= available
            ? baseText
            : $"{baseText[..(available - 3)]}...";
        return $"{prefix}{trimmed}";
    }

    private static Forms.ToolStripMenuItem AddItem(
        Forms.ContextMenuStrip menu,
        string text,
        Dispatcher dispatcher,
        Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => dispatcher.Invoke(action);
        menu.Items.Add(item);
        return item;
    }

    private static Icon LoadAppIcon()
    {
        var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/icon.ico", UriKind.Absolute))?.Stream;
        return stream is not null
            ? new Icon(stream, new System.Drawing.Size(16, 16))
            : (Icon)SystemIcons.Application.Clone();
    }
}
