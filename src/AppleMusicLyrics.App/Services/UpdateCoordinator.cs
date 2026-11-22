using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Updates;
using AppleMusicLyrics.Infrastructure.Windows.Diagnostics;
using AppleMusicLyrics.Infrastructure.Windows.Updates;

namespace AppleMusicLyrics.App.Services;

public sealed record UpdateCheckResult(ReleaseInfo? Release, bool Offered, bool Failed);

/// <summary>
/// Checks GitHub Releases for a newer version (at startup, then daily, when enabled in settings,
/// plus on demand) and hands a found release to the update window. All members are used on the
/// UI thread.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly AppSettings _settings;
    private readonly Action _persistSettings;
    private readonly GitHubReleaseClient _client;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    public UpdateCoordinator(AppSettings settings, Action persistSettings, Dispatcher dispatcher)
    {
        _settings = settings;
        _persistSettings = persistSettings;
        CurrentVersion = ReadCurrentVersion();
        _client = new GitHubReleaseClient(CurrentVersion);
        Installer = new UpdateInstaller(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicLyrics", "updates"));
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync(manual: false);
        };
    }

    public AppVersion CurrentVersion { get; }

    public UpdateInstaller Installer { get; }

    /// <summary>The newer release found by the last check, until it is installed or skipped.</summary>
    public ReleaseInfo? Available { get; private set; }

    /// <summary>Raised when <see cref="Available"/> changes.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>Raised once per version when an automatic check finds it.</summary>
    public event Action<ReleaseInfo>? NewReleaseFound;

    public string InstallDirectory => AppContext.BaseDirectory;

    public HttpClient HttpClient => _client.HttpClient;

    /// <summary>Starts or stops the automatic schedule to match the setting.</summary>
    public void ApplySchedule()
    {
        if (_settings.CheckForUpdates)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (_checking && !manual) return new UpdateCheckResult(Available, Available is not null, false);
        _checking = true;
        try
        {
            var release = await _client.GetLatestReleaseAsync(cancellationToken);
            if (release is null || !UpdatePolicy.ShouldOffer(release.Version, CurrentVersion, _settings.SkippedUpdateVersion, manual))
            {
                if (release is not null && !(release.Version > CurrentVersion)) SetAvailable(null);
                return new UpdateCheckResult(release, false, false);
            }

            var isNew = Available?.Version.CompareTo(release.Version) != 0;
            SetAvailable(release);
            if (isNew && !manual) NewReleaseFound?.Invoke(release);
            return new UpdateCheckResult(release, true, false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            // Offline, rate-limited, GitHub unavailable, or an unexpected response: try again at
            // the next scheduled check.
            if (error is not TaskCanceledException || !cancellationToken.IsCancellationRequested)
            {
                ErrorLog.Report("Updates", "Update check failed", error);
            }
            return new UpdateCheckResult(null, false, true);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Remembers <paramref name="release"/> as skipped so automatic checks stay quiet about it.</summary>
    public void Skip(ReleaseInfo release)
    {
        _settings.SkippedUpdateVersion = release.Version.ToString();
        _persistSettings();
        SetAvailable(null);
    }

    public bool CanInstallInPlace => UpdateInstaller.CanWriteTo(InstallDirectory);

    /// <summary>
    /// Starts a helper copy of this executable, which installs <paramref name="stagingDirectory"/>
    /// once this process has exited. True once the helper has taken over, and the app should
    /// exit; false when it did not, and the app should keep running.
    /// </summary>
    public async Task<bool> LaunchInstallerAsync(string stagingDirectory, CancellationToken cancellationToken = default)
    {
        var current = Environment.ProcessPath
            ?? Path.Combine(InstallDirectory, UpdateInstaller.ExecutableName);
        var helper = Installer.PrepareHelper(current);
        var signalName = UpdateHandoff.NewSignalName();
        using var ready = UpdateHandoff.CreateSignal(signalName);
        using var process = Process.Start(UpdateInstaller.CreateApplyStartInfo(
            helper, stagingDirectory, InstallDirectory, Environment.ProcessId, signalName))
            ?? throw new InvalidOperationException("The update helper did not start.");
        var handedOver = false;
        try
        {
            var helperReady = await UpdateHandoff.WaitForHelperAsync(ready, () => process.HasExited,
                UpdateHandoff.DefaultTimeout, cancellationToken);
            // "Later" may have been clicked after the helper answered: the user's choice wins,
            // and the finally below stops the helper because nothing was handed over.
            cancellationToken.ThrowIfCancellationRequested();
            handedOver = helperReady;
            if (!handedOver)
            {
                ErrorLog.Report("Updates", process.HasExited
                    ? $"The update helper exited with code {process.ExitCode} before taking over"
                    : "The update helper did not take over in time");
            }
            return handedOver;
        }
        finally
        {
            // Also on cancellation (the update window closed): a helper left behind could
            // still install once this app exits later, without the user having agreed to it.
            if (!handedOver) StopHelper(process);
        }
    }

    private static void StopHelper(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Report("Updates", "Could not stop the update helper", error);
        }
    }

    public static void OpenReleasePage(ReleaseInfo? release)
    {
        using var _ = Process.Start(new ProcessStartInfo((release?.PageUrl ?? GitHubReleaseClient.ReleasesPage).ToString())
        {
            UseShellExecute = true,
        });
    }

    private void SetAvailable(ReleaseInfo? release)
    {
        if (Equals(Available, release)) return;
        Available = release;
        AvailabilityChanged?.Invoke();
    }

    private static AppVersion ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateCoordinator).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (AppVersion.TryParse(informational, out var version)) return version;
        var name = assembly.GetName().Version;
        return name is null ? new AppVersion(0, 0, 0) : new AppVersion(name.Major, name.Minor, Math.Max(0, name.Build));
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
