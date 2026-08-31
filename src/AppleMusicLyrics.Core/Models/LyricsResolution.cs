namespace AppleMusicLyrics.Core.Models;

/// <summary>
/// The current stage of resolving lyrics for the active media track.
/// </summary>
public enum LyricsResolutionStatus
{
    NoPlayer,
    WaitingForMetadata,
    SearchingLocal,
    VerifyingCatalog,
    FetchingExternal,
    Resolved,
    Unavailable,
    Error,
}

/// <summary>
/// How strongly the available evidence identifies the selected lyrics as the active track.
/// </summary>
public enum LyricsResolutionConfidence
{
    None,
    Low,
    Medium,
    High,
}

/// <summary>
/// The source and verification path that produced the selected document.
/// </summary>
public enum LyricsResolutionSource
{
    None,
    AppleMusicCache,
    CatalogVerifiedCache,
    ExternalProvider,
}

/// <summary>
/// Diagnostic evidence for one local cache candidate. This is deliberately display-safe and does
/// not include lyric text, so it can be surfaced in diagnostics without exposing song content.
/// </summary>
public sealed record LyricsMatchEvidence(
    string LyricsId,
    int Score,
    double DurationDelta,
    string Decision);

/// <summary>
/// Explains why the runtime selected, rejected, or is still looking for lyrics for one track.
/// </summary>
public sealed record LyricsResolution(
    LyricsResolutionStatus Status,
    LyricsResolutionConfidence Confidence,
    LyricsResolutionSource Source,
    string Summary,
    string? TrackIdentity = null,
    int CandidateCount = 0,
    IReadOnlyList<LyricsMatchEvidence>? Evidence = null)
{
    public IReadOnlyList<LyricsMatchEvidence> CandidateEvidence { get; } =
        Evidence ?? Array.Empty<LyricsMatchEvidence>();

    public static LyricsResolution NoPlayer { get; } = new(
        LyricsResolutionStatus.NoPlayer,
        LyricsResolutionConfidence.None,
        LyricsResolutionSource.None,
        "No active media session.");
}
