using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class WindowMessageCoordinatorTests
{
    [Fact]
    public void SizeMove_DefersPersistenceUntilExit()
    {
        var coordinator = new WindowMessageCoordinator();

        coordinator.EnterSizeMove();
        Assert.False(coordinator.RequestCommit(reconcileDisplayTopology: false));
        Assert.True(coordinator.ExitSizeMove());
        Assert.Equal(WindowStateCommitRequest.Persist, coordinator.TakeScheduledCommit());
    }

    [Fact]
    public void DisplayChangeDuringSizeMove_ReconcilesOnceAfterExit()
    {
        var coordinator = new WindowMessageCoordinator();

        coordinator.EnterSizeMove();
        Assert.False(coordinator.RequestCommit(reconcileDisplayTopology: true));
        Assert.True(coordinator.ExitSizeMove());
        Assert.Equal(
            WindowStateCommitRequest.ReconcileDisplayTopologyAndPersist,
            coordinator.TakeScheduledCommit());
    }

    [Fact]
    public void RepeatedRequests_AreCoalescedAndPreserveReconcilePriority()
    {
        var coordinator = new WindowMessageCoordinator();

        Assert.True(coordinator.RequestCommit(reconcileDisplayTopology: false));
        Assert.False(coordinator.RequestCommit(reconcileDisplayTopology: true));
        Assert.False(coordinator.RequestCommit(reconcileDisplayTopology: false));
        Assert.Equal(
            WindowStateCommitRequest.ReconcileDisplayTopologyAndPersist,
            coordinator.TakeScheduledCommit());
    }

    [Fact]
    public void SizeMoveStartingBeforeDispatcherCallback_KeepsPendingCommitForExit()
    {
        var coordinator = new WindowMessageCoordinator();

        Assert.True(coordinator.RequestCommit(reconcileDisplayTopology: true));
        coordinator.EnterSizeMove();
        Assert.Equal(WindowStateCommitRequest.None, coordinator.TakeScheduledCommit());
        Assert.True(coordinator.ExitSizeMove());
        Assert.Equal(
            WindowStateCommitRequest.ReconcileDisplayTopologyAndPersist,
            coordinator.TakeScheduledCommit());
    }
}
