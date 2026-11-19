using System.ComponentModel;
using System.Text;
using System.Windows;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Updates;
using AppleMusicLyrics.Infrastructure.Windows.Updates;

namespace AppleMusicLyrics.App;

/// <summary>Shows an available release (or checks for one) and installs it on request.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateCoordinator _updates;
    private readonly LocalizationManager _text = LocalizationManager.Instance;
    private readonly CancellationTokenSource _lifetime = new();
    private ReleaseInfo? _release;
    private bool _installing;

    public UpdateWindow(UpdateCoordinator updates, ReleaseInfo? release)
    {
        _updates = updates;
        InitializeComponent();
        if (release is not null)
        {
            ShowRelease(release);
        }
        else
        {
            ShowState("Update.Headline.Checking", null);
            Loaded += CheckOnLoad;
        }
    }

    private async void CheckOnLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= CheckOnLoad;
        var result = await _updates.CheckAsync(manual: true, _lifetime.Token);
        if (_lifetime.IsCancellationRequested) return;
        if (result.Failed)
            ShowState("Update.Headline.Failed", _text.Text("Update.Status.CheckFailed"));
        else if (result.Offered && result.Release is { } release)
            ShowRelease(release);
        else
            ShowState("Update.Headline.UpToDate", _text.Format("Update.Status.Current", _updates.CurrentVersion));
    }

    private void ShowState(string headlineKey, string? status)
    {
        _release = null;
        HeadlineText.Text = _text.Text(headlineKey);
        StatusText.Text = status ?? string.Empty;
        NotesTitleText.Visibility = NotesTextBox.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = SkipButton.Visibility = Visibility.Collapsed;
        LaterButton.Content = _text.Text("Diagnostics.Close");
    }

    private void ShowRelease(ReleaseInfo release)
    {
        _release = release;
        HeadlineText.Text = _text.Format("Update.Headline.Available", release.Version);
        StatusText.Text = release.Package is null
            ? _text.Text("Update.Status.NoPackage")
            : _text.Format("Update.Status.Current", _updates.CurrentVersion);
        var notes = CleanNotes(release.Notes);
        NotesTextBox.Text = notes.Length > 0 ? notes : _text.Text("Update.Notes.Empty");
        NotesTitleText.Visibility = NotesTextBox.Visibility = Visibility.Visible;
        InstallButton.Visibility = SkipButton.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = release.Package is not null;
        LaterButton.Content = _text.Text("Update.Action.Later");
    }

    private async void InstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_release is not { } release || _installing) return;
        if (!_updates.CanInstallInPlace)
        {
            StatusText.Text = _text.Text("Update.Status.NotWritable");
            InstallButton.IsEnabled = false;
            return;
        }

        _installing = true;
        InstallButton.IsEnabled = SkipButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = _text.Format("Update.Status.Downloading", 0);
        var progress = new Progress<double>(value =>
        {
            DownloadProgress.Value = value;
            StatusText.Text = _text.Format("Update.Status.Downloading", (int)Math.Round(value * 100));
        });

        string staging;
        try
        {
            staging = await _updates.Installer.DownloadAndStageAsync(_updates.HttpClient, release, progress, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (UpdateException error)
        {
            StatusText.Text = _text.Text(error.Failure switch
            {
                UpdateFailure.Verification => "Update.Status.Verification",
                UpdateFailure.InvalidPackage => "Update.Status.InvalidPackage",
                UpdateFailure.NoPackage => "Update.Status.NoPackage",
                _ => "Update.Status.Network",
            });
            ResetAfterFailure();
            return;
        }

        StatusText.Text = _text.Text("Update.Status.Installing");
        try
        {
            _updates.LaunchInstaller(staging);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = _text.Text("Update.Status.LaunchFailed");
            ResetAfterFailure();
            return;
        }

        // The staged installer waits for this process to exit. Close normally so settings and
        // window placement are saved first.
        var app = System.Windows.Application.Current;
        Close();
        app.MainWindow?.Close();
        app.Shutdown();
    }

    private void ResetAfterFailure()
    {
        _installing = false;
        DownloadProgress.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = SkipButton.IsEnabled = true;
    }

    private void SkipButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_release is { } release) _updates.Skip(release);
        Close();
    }

    private void LaterButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ReleasePageButton_OnClick(object sender, RoutedEventArgs e) => UpdateCoordinator.OpenReleasePage(_release);

    protected override void OnClosing(CancelEventArgs e)
    {
        _lifetime.Cancel();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Turns release-note Markdown into readable plain text for the notes box.</summary>
    public static string CleanNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var result = new StringBuilder();
        var blank = 0;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd().Replace("**", string.Empty, StringComparison.Ordinal);
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) line = trimmed.TrimStart('#').Trim();
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                line = "• " + trimmed[2..];
            if (line.Length == 0)
            {
                if (++blank > 1 || result.Length == 0) continue;
            }
            else
            {
                blank = 0;
            }
            result.AppendLine(line);
        }
        return result.ToString().Trim();
    }
}
