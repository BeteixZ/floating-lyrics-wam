using AppleMusicLyrics.Infrastructure.Windows.Updates;
using Xunit;

namespace AppleMusicLyrics.Tests.Updates;

public sealed class UpdateHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aml-handoff-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public async Task WaitForHelper_SucceedsOnlyOnceTheHelperSignalsReady()
    {
        var name = UpdateHandoff.NewSignalName();
        using var ready = UpdateHandoff.CreateSignal(name);
        var waiting = UpdateHandoff.WaitForHelperAsync(ready, () => false, TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        Assert.True(UpdateHandoff.SignalReady(name));

        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForHelper_FailsWhenTheHelperExitsWithoutTakingOver()
    {
        // The helper could not get the update lock: the app must keep running.
        using var ready = UpdateHandoff.CreateSignal(UpdateHandoff.NewSignalName());
        var exited = false;
        var waiting = UpdateHandoff.WaitForHelperAsync(ready, () => exited, TimeSpan.FromSeconds(10));
        exited = true;

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForHelper_FailsWhenTheHelperSignalledButThenDied()
    {
        // A helper that crashes after signalling installs nothing; exiting would leave no app.
        var name = UpdateHandoff.NewSignalName();
        using var ready = UpdateHandoff.CreateSignal(name);
        UpdateHandoff.SignalReady(name);

        Assert.False(await UpdateHandoff.WaitForHelperAsync(ready, () => true, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForHelper_CancellationPropagatesSoTheCallerCanStopTheHelper()
    {
        using var ready = UpdateHandoff.CreateSignal(UpdateHandoff.NewSignalName());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateHandoff.WaitForHelperAsync(ready, () => false, TimeSpan.FromSeconds(10), cancellation.Token));
    }

    [Fact]
    public void CanApply_RejectsAMissingPackageBeforeTheAppIsAskedToExit()
    {
        var source = Path.Combine(_root, "staging");
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, UpdateInstaller.ExecutableName), "old");

        // An antivirus quarantined the staged executable.
        Assert.False(UpdateInstaller.CanApply(source, target, out var problem));
        Assert.Contains(source, problem);

        File.WriteAllText(Path.Combine(source, UpdateInstaller.ExecutableName), "new");
        Assert.True(UpdateInstaller.CanApply(source, target, out _));
        Assert.False(UpdateInstaller.CanApply(target, Path.Combine(target, "nested"), out _));
    }

    [Fact]
    public async Task WaitForHelper_FailsWhenTheHelperDoesNotAnswerInTime()
    {
        using var ready = UpdateHandoff.CreateSignal(UpdateHandoff.NewSignalName());
        Assert.False(await UpdateHandoff.WaitForHelperAsync(ready, () => false, TimeSpan.FromMilliseconds(120)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Global\\SomethingElse")]
    [InlineData("AppleMusicLyrics_UpdateReady_not-a-guid")]
    public void SignalReady_IgnoresAMissingOrForeignName(string? name)
    {
        Assert.False(UpdateHandoff.SignalReady(name));
        Assert.False(UpdateHandoff.IsValidSignalName(name));
    }

    [Fact]
    public void ApplyArguments_CarryTheReadySignalAndStayCompatibleWithoutIt()
    {
        var name = UpdateHandoff.NewSignalName();
        var start = UpdateInstaller.CreateApplyStartInfo(@"C:\h\AppleMusicLyrics.App.exe", @"C:\s", @"C:\t", 42, name);

        Assert.True(UpdateInstaller.TryParseApplyArguments(start.ArgumentList.ToArray(), out var source, out var target,
            out var pid, out var signal));
        Assert.Equal((@"C:\s", @"C:\t", 42, name), (source, target, pid, signal));

        // Arguments from a helper started by 0.10 or earlier: no signal.
        var legacy = UpdateInstaller.CreateApplyStartInfo(@"C:\h\AppleMusicLyrics.App.exe", @"C:\s", @"C:\t", 42);
        Assert.True(UpdateInstaller.TryParseApplyArguments(legacy.ArgumentList.ToArray(), out _, out _, out _, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void FindLeftoverBackups_ListsOnlyRollbackFolders()
    {
        var installer = new UpdateInstaller(_root);
        Assert.Empty(installer.FindLeftoverBackups());

        Directory.CreateDirectory(Path.Combine(_root, "rollback-0123"));
        Directory.CreateDirectory(Path.Combine(_root, "staging-v1.0.0"));
        Directory.CreateDirectory(Path.Combine(_root, "helper-abc"));

        var leftover = Assert.Single(installer.FindLeftoverBackups());
        Assert.EndsWith("rollback-0123", leftover);
    }
}
