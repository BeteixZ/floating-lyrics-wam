using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using AppleMusicLyrics.App;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Services;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Updates;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

[Collection("Localization")]
public sealed class SettingsWindowUpdateCheckTests
{
    private static readonly AppVersion Current = new(1, 0, 0, "rc.1");

    [Fact]
    public void CheckNow_UpToDateStaysInTheSettingsWindowWithTheCheckTimes() => OnSta(() =>
    {
        var now = DateTimeOffset.Now;
        var updates = new FakeUpdateChecks(new UpdateCheckResult(new ReleaseInfo(Current, "", "", new Uri("https://example.com"), null), false, false))
        {
            NextAutomaticCheck = now.AddDays(1),
        };
        var shown = new List<ReleaseInfo>();
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US", CheckForUpdates = true },
            updates: updates, showRelease: shown.Add);
        try
        {
            Click(window, "CheckUpdatesButton");

            Assert.Equal(1, updates.ManualChecks);
            Assert.Empty(shown);
            Assert.Equal("No update available. You have 1.0.0-rc.1.", Text(window, "UpdateStatusText"));
            var schedule = Text(window, "UpdateScheduleText");
            Assert.StartsWith("Last checked today at ", schedule, StringComparison.Ordinal);
            Assert.Contains("next automatic check tomorrow at " + now.AddDays(1).ToString("HH:mm",
                System.Globalization.CultureInfo.InvariantCulture), schedule, StringComparison.Ordinal);
            Assert.True(((Button)window.FindName("CheckUpdatesButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CheckNow_OpensTheUpdateWindowOnlyForAnOfferedRelease() => OnSta(() =>
    {
        var release = new ReleaseInfo(new AppVersion(1, 0, 0), "v1.0.0", "Notes", new Uri("https://example.com"), null);
        var updates = new FakeUpdateChecks(new UpdateCheckResult(release, true, false));
        var shown = new List<ReleaseInfo>();
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" }, updates: updates, showRelease: shown.Add);
        try
        {
            Click(window, "CheckUpdatesButton");

            Assert.Same(release, Assert.Single(shown));
            Assert.Equal(LocalizationManager.Instance.Format("Update.Headline.Available", release.Version),
                Text(window, "UpdateStatusText"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CheckNow_ShowsAFailureInPlaceAndHidesTheScheduleWhileAutomaticChecksAreOff() => OnSta(() =>
    {
        var updates = new FakeUpdateChecks(new UpdateCheckResult(null, false, true))
        {
            NextAutomaticCheck = DateTimeOffset.Now.AddHours(3),
        };
        var shown = new List<ReleaseInfo>();
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US", CheckForUpdates = false },
            updates: updates, showRelease: shown.Add);
        try
        {
            Click(window, "CheckUpdatesButton");

            Assert.Empty(shown);
            Assert.Equal("Couldn't check for updates. Check your connection and try again.", Text(window, "UpdateStatusText"));
            Assert.DoesNotContain("next automatic check", Text(window, "UpdateScheduleText"), StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Opening_ShowsTheLastAutomaticCheckBeforeAnyClick() => OnSta(() =>
    {
        var checkedAt = DateTimeOffset.Now.AddDays(-1);
        var updates = new FakeUpdateChecks(new UpdateCheckResult(null, false, false))
        {
            LastCheck = new UpdateCheckStatus(checkedAt, new UpdateCheckResult(null, false, false)),
        };
        var window = new SettingsWindow(new AppSettings { UiLanguage = "en-US" }, updates: updates);
        try
        {
            Assert.Equal(0, updates.ManualChecks);
            Assert.Equal("No update available. You have 1.0.0-rc.1.", Text(window, "UpdateStatusText"));
            Assert.Equal("Last checked yesterday at " + checkedAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                Text(window, "UpdateScheduleText"));
        }
        finally { window.Close(); }
    });

    private static void Click(Window window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string Text(Window window, string name)
    {
        var block = (TextBlock)window.FindName(name);
        return block.Visibility == Visibility.Visible ? block.Text : string.Empty;
    }

    private sealed class FakeUpdateChecks(UpdateCheckResult result) : IUpdateChecks
    {
        public int ManualChecks { get; private set; }

        public AppVersion CurrentVersion => Current;

        public UpdateCheckStatus? LastCheck { get; set; }

        public DateTimeOffset? NextAutomaticCheck { get; set; }

        public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
        {
            if (manual) ManualChecks++;
            LastCheck = new UpdateCheckStatus(DateTimeOffset.Now, result);
            return Task.FromResult(result);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var language = LocalizationManager.Instance.Preference;
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { LocalizationManager.Instance.SetLanguage(language); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("The settings window did not finish on the STA thread.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
