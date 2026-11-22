using System.Diagnostics;

namespace AppleMusicLyrics.Infrastructure.Windows.Updates;

/// <summary>
/// Confirms that the update helper has taken over before the running app exits. Without it, a
/// helper that cannot start the installation (another update holds the lock) would leave the
/// user with no running app and no update. The app creates a named event, passes its name with
/// <see cref="UpdateInstaller.ReadyArgument"/>, and exits only after the helper sets it.
/// </summary>
public static class UpdateHandoff
{
    private const string Prefix = "AppleMusicLyrics_UpdateReady_";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static string NewSignalName() => Prefix + Guid.NewGuid().ToString("N");

    public static bool IsValidSignalName(string? name) =>
        name is { Length: > 0 } && name.StartsWith(Prefix, StringComparison.Ordinal)
        && name.Length == Prefix.Length + 32 && name[Prefix.Length..].All(Uri.IsHexDigit);

    public static EventWaitHandle CreateSignal(string name) =>
        new(initialState: false, EventResetMode.ManualReset, name);

    /// <summary>Called by the helper once it holds the update lock. False if nobody is waiting.</summary>
    public static bool SignalReady(string? name)
    {
        if (!IsValidSignalName(name) || !EventWaitHandle.TryOpenExisting(name!, out var signal)) return false;
        using (signal) return signal.Set();
    }

    /// <summary>
    /// Waits until the helper reports it has taken over. False when it exits first or does not
    /// answer within <paramref name="timeout"/>; the caller then keeps running.
    /// </summary>
    public static async Task<bool> WaitForHelperAsync(WaitHandle ready, Func<bool> helperHasExited, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            // A helper that signalled and then died installs nothing: keep running.
            if (ready.WaitOne(0)) return !helperHasExited();
            if (helperHasExited()) return false;
            if (watch.Elapsed >= timeout) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }
}
