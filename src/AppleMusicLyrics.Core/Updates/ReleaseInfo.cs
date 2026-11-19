namespace AppleMusicLyrics.Core.Updates;

/// <summary>A published release and, when it has one, its Windows package.</summary>
public sealed record ReleaseInfo(
    AppVersion Version,
    string Name,
    string Notes,
    Uri PageUrl,
    ReleaseAsset? Package);

/// <summary>
/// A downloadable release file. <see cref="Sha256"/> is the lowercase hex digest GitHub
/// publishes for the asset, or null when the release predates asset digests.
/// </summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

public static class UpdatePolicy
{
    /// <summary>
    /// True when <paramref name="latest"/> should be offered: it is newer than the running
    /// version and is not the version the user chose to skip. An explicit manual check ignores
    /// the skipped version.
    /// </summary>
    public static bool ShouldOffer(AppVersion latest, AppVersion current, string? skippedVersion, bool manualCheck)
    {
        if (!(latest > current)) return false;
        if (manualCheck || string.IsNullOrWhiteSpace(skippedVersion)) return true;
        return !(AppVersion.TryParse(skippedVersion, out var skipped) && skipped.CompareTo(latest) == 0);
    }
}
