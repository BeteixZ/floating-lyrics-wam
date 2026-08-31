namespace AppleMusicLyrics.Core.Display;

public enum WindowStateCommitRequest
{
    None,
    Persist,
    ReconcileDisplayTopologyAndPersist,
}

/// <summary>
/// Coalesces native window messages so interactive moves never persist intermediate geometry.
/// </summary>
public sealed class WindowMessageCoordinator
{
    private bool _isInSizeMove;
    private bool _displayTopologyReconcilePending;
    private bool _commitScheduled;

    public void EnterSizeMove()
    {
        _isInSizeMove = true;
    }

    public bool ExitSizeMove()
    {
        _isInSizeMove = false;
        return RequestCommit(reconcileDisplayTopology: false);
    }

    public bool RequestCommit(bool reconcileDisplayTopology)
    {
        _displayTopologyReconcilePending |= reconcileDisplayTopology;
        if (_isInSizeMove || _commitScheduled)
        {
            return false;
        }

        _commitScheduled = true;
        return true;
    }

    public WindowStateCommitRequest TakeScheduledCommit()
    {
        _commitScheduled = false;
        if (_isInSizeMove)
        {
            return WindowStateCommitRequest.None;
        }

        if (_displayTopologyReconcilePending)
        {
            _displayTopologyReconcilePending = false;
            return WindowStateCommitRequest.ReconcileDisplayTopologyAndPersist;
        }

        return WindowStateCommitRequest.Persist;
    }
}
