using AppleMusicLyrics.App;
using Xunit;

namespace AppleMusicLyrics.Tests.Updates;

public sealed class CrashExitTests
{
    [Fact]
    public void AJustInstalledVersionExitsSilentlyWhileTheHelperStillWatchesIt()
        => Assert.True(App.App.ExitsSilentlyOnCrash(false, App.App.UpdateOutcomeInstalled, TimeSpan.FromSeconds(3)));

    [Fact]
    public void AJustInstalledVersionReportsACrashAfterItsStartupCheck()
    {
        Assert.False(App.App.ExitsSilentlyOnCrash(false, App.App.UpdateOutcomeInstalled, App.App.UpdatedStartupWindow));
        Assert.False(App.App.ExitsSilentlyOnCrash(false, App.App.UpdateOutcomeInstalled, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void TheStartupWindowOutlastsTheUpdateHelpersCheck()
        => Assert.True(App.App.UpdatedStartupWindow > TimeSpan.FromSeconds(4));

    [Theory]
    [InlineData(null)]
    [InlineData("failed")]
    public void AnOrdinaryOrRolledBackLaunchReportsACrash(string? outcome)
        => Assert.False(App.App.ExitsSilentlyOnCrash(false, outcome, TimeSpan.Zero));

    [Fact]
    public void ASmokeTestAlwaysExitsSilently()
        => Assert.True(App.App.ExitsSilentlyOnCrash(true, null, TimeSpan.FromHours(1)));
}
