using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Debugger.Models;

public enum VerdictStatus
{
    Pass,
    PassUnverified,
    Missed,
    Misidentified,
    Instrumental,
    Inconclusive,
    TimedOut
}

public sealed record TrackIdentity(
    string SourceAppId,
    string Title,
    string Artist,
    string Album)
{
    public static readonly TrackIdentity Empty = new(string.Empty, string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist);

    public static TrackIdentity Create(PlayerState? player)
    {
        if (player is null)
        {
            return Empty;
        }

        return new TrackIdentity(
            Normalize(player.SourceAppId),
            Normalize(player.Title),
            Normalize(player.Artist),
            Normalize(player.Album));
    }

    public string ToSongKey() => $"{Title}|{Artist}";

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().ToLowerInvariant();
    }
}

public interface IGroundTruthVerifier
{
    Task<GroundTruthResult> QueryGroundTruthAsync(PlayerState player, CancellationToken cancellationToken = default);
}

public sealed record GroundTruthResult(
    bool HasLyrics,
    string Source,
    string? Title,
    string? Artist,
    string? RawLyrics,
    IReadOnlyList<string> Lines);

public sealed class TrackDiagnosticRecord
{
    public TrackIdentity? Identity { get; init; }
    public required string SongKey { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public double Duration { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    // Software results
    public LyricsResolutionStatus ResolutionStatus { get; set; }
    public string ResolutionReason { get; set; } = string.Empty;
    public string SoftwareConfidence { get; set; } = "None";
    public string SoftwareSource { get; set; } = "None";
    public bool SoftwareHasLyrics => SoftwareDoc != null && SoftwareDoc.Lines.Count > 0;
    public LyricsDocument? SoftwareDoc { get; set; }
    public IReadOnlyList<string> SoftwareLyricSample { get; set; } = Array.Empty<string>();
    public int CandidateCount { get; set; }
    public int TopCandidateScore { get; set; }
    public double TopCandidateDurationDelta { get; set; }

    // Online ground truth results
    public string GroundTruthSource { get; set; } = "None";
    public bool GroundTruthHasLyrics { get; set; }
    public IReadOnlyList<string> GroundTruthLyricSample { get; set; } = Array.Empty<string>();

    // Comparison & Verdict
    public double SimilarityScore { get; set; }
    public VerdictStatus Verdict { get; set; }
    public string VerdictReason { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
}

public sealed class MonitorSummaryStatistics
{
    public int TotalTracksPlayed { get; set; }
    public int TotalWithLyrics { get; set; }
    public int TotalInstrumental { get; set; }
    public int PassCount { get; set; }
    public int PassUnverifiedCount { get; set; }
    public int MissedCount { get; set; }
    public int MisidentifiedCount { get; set; }
    public int InconclusiveCount { get; set; }
    public int LocalCacheHits { get; set; }
    public int ExternalLrcHits { get; set; }

    public double RecognitionRate => TotalWithLyrics > 0
        ? (double)(PassCount + PassUnverifiedCount) / TotalWithLyrics * 100.0
        : 100.0;

    public double MisidentificationRate => (PassCount + PassUnverifiedCount + MisidentifiedCount) > 0
        ? (double)MisidentifiedCount / (PassCount + PassUnverifiedCount + MisidentifiedCount) * 100.0
        : 0.0;
}
