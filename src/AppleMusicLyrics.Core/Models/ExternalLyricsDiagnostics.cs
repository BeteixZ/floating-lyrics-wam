namespace AppleMusicLyrics.Core.Models;

public enum ExternalLyricsQueryStage { Fallback, Enrichment }
public enum ExternalLyricsQueryState { NotStarted, Querying, RetryWaiting, Completed, Satisfied, Canceled }
public enum ExternalLyricsQueryOutcome { NotStarted, Querying, Success, NoResult, Failed, Rejected, Skipped, Canceled }

public enum ExternalLyricsDiagnosticReason
{
    None,
    NoDocumentReturned,
    EmptyDocument,
    PlainLyricsOnly,
    NoAdditionalRequestedFeatures,
    WouldLoseWordTimings,
    WouldLoseTranslations,
    WouldLoseWordsAndTranslations,
    AlreadySatisfied,
    Canceled,
}

public enum ExternalLyricsFailureReason { None, Timeout, HttpError, NetworkError, InvalidResponse, Other }

/// <summary>
/// One provider's latest attempt in a lookup stage. NoResult only means that the
/// provider returned no document; it does not establish why or prove no match.
/// Counts describe the returned document even when selection rejected it.
/// Cache origin is intentionally not inferred from a provider name or source URI.
/// </summary>
public sealed record ExternalLyricsDiagnostic(
    string Source,
    ExternalLyricsQueryStage Stage,
    ExternalLyricsQueryOutcome Outcome,
    int Attempt = 0,
    int LineCount = 0,
    int WordLineCount = 0,
    int TranslatedLineCount = 0,
    ExternalLyricsDiagnosticReason Reason = ExternalLyricsDiagnosticReason.None,
    string? ExceptionType = null,
    ExternalLyricsFailureReason FailureReason = ExternalLyricsFailureReason.None,
    int? HttpStatusCode = null,
    string? Detail = null);

/// <summary>Immutable, track-scoped evidence published by the player poll.</summary>
public sealed record ExternalLyricsDiagnostics(
    IReadOnlyList<ExternalLyricsDiagnostic> Providers,
    ExternalLyricsQueryState FallbackState,
    ExternalLyricsQueryState EnrichmentState,
    int FallbackAttempt,
    int EnrichmentAttempt,
    bool MissingWordTimings,
    bool MissingTranslations);
