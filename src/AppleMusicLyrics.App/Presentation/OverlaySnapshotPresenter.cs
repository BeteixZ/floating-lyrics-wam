using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.App.Localization;

namespace AppleMusicLyrics.App.Presentation;

public sealed record PlayerPresentation(string WindowTitle, string Subtitle, string PlayerText);

public sealed record LyricsPresentation(
    string StatusText,
    string PathText,
    string PreviousText,
    string CurrentText,
    string NextText,
    bool IsPlaying,
    bool Animate,
    LyricsLine? DisplayedLine = null,
    string? TranslationText = null,
    string DebugSummaryText = "");

public static class OverlaySnapshotPresenter
{
    public static PlayerPresentation CreatePlayer(RuntimeSnapshot snapshot, LocalizationManager? localization = null)
    {
        var text = localization ?? LocalizationManager.Instance;
        if (snapshot.Player is null)
        {
            return new PlayerPresentation(
                "Apple Music Lyrics",
                text.Text("Player.WaitingSession"),
                text.Text("Player.NoSession"));
        }

        var status = text.Text(snapshot.Player.Playing ? "Player.Playing" : "Player.Paused");
        var artist = snapshot.Player.Artist ?? text.Text("Player.UnknownArtist");
        var title = snapshot.Player.Title ?? text.Text("Player.UnknownTitle");
        var subtitle = snapshot.Player.Album is { Length: > 0 }
            ? $"{artist} | {snapshot.Player.Album}"
            : artist;
        return new PlayerPresentation(
            $"{artist} - {title}",
            subtitle,
            $"{status}: {artist} - {title}");
    }

    public static LyricsPresentation CreateLyrics(RuntimeSnapshot snapshot, LocalizationManager? localization = null, bool showTranslation = true)
    {
        var text = localization ?? LocalizationManager.Instance;
        var resolutionLabel = FormatResolutionLabel(snapshot.Resolution, text);
        if (snapshot.Document is null)
        {
            return new LyricsPresentation(
                resolutionLabel,
                BuildDebugText(snapshot, null, null, 0, 0, showTranslation, text),
                string.Empty,
                GetResolutionPlaceholder(snapshot.Resolution, text),
                snapshot.Resolution.Status == LyricsResolutionStatus.FetchingExternal
                    ? text.Text("Lyrics.CheckingAgain")
                    : text.Text("Lyrics.DebugHint"),
                snapshot.Player?.Playing ?? false,
                Animate: false,
                DebugSummaryText: resolutionLabel);
        }

        var displayedLine = snapshot.ActiveLyric.CurrentLine
            ?? snapshot.ActiveLyric.NextLine
            ?? snapshot.Document.Lines.FirstOrDefault();
        var currentText = displayedLine?.Text
            ?? text.Text("Lyrics.NoDisplayableLines");
        var source = GetSourceLabel(snapshot.Document, snapshot.Resolution, text);
        var wordLines = snapshot.Document.Lines.Count(line => line.Words is { Count: > 0 });
        var translatedLines = snapshot.Document.Lines.Count(HasTranslation);
        var features = wordLines > 0
            ? text.Text(translatedLines > 0 ? "Lyrics.FeatureWordsTranslation" : "Lyrics.FeatureWords")
            : translatedLines > 0 ? text.Text("Lyrics.FeatureLinesTranslation")
            : snapshot.Document.SourceFile.StartsWith("lrclib-plain:", StringComparison.OrdinalIgnoreCase)
                || string.Equals(snapshot.Document.Status, "plain", StringComparison.OrdinalIgnoreCase)
                ? text.Text("Lyrics.FeaturePlain") : text.Text("Lyrics.FeatureLines");
        return new LyricsPresentation(
            $"{source} | {features}",
            BuildDebugText(snapshot, displayedLine, source, wordLines, translatedLines, showTranslation, text),
            snapshot.ActiveLyric.PreviousLine?.Text ?? string.Empty,
            currentText,
            snapshot.ActiveLyric.NextLine?.Text ?? string.Empty,
            snapshot.Player?.Playing ?? false,
            Animate: snapshot.ActiveLyric.CurrentLine is not null,
            DisplayedLine: displayedLine,
            TranslationText: displayedLine?.Translation,
            DebugSummaryText: BuildDebugSummary(snapshot, source, wordLines, translatedLines, showTranslation, text));
    }

    private static string BuildDebugSummary(RuntimeSnapshot snapshot, string source, int wordLines,
        int translatedLines, bool showTranslation, LocalizationManager text)
    {
        var total = snapshot.Document!.Lines.Count;
        var translation = !showTranslation ? text.Text("Debug.CompactTranslationHidden")
            : translatedLines == 0 && snapshot.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.Querying
                ? text.Text("Debug.CompactTranslationSearching")
            : translatedLines == 0 && snapshot.ExternalDiagnostics?.EnrichmentState == ExternalLyricsQueryState.RetryWaiting
                ? text.Text("Debug.CompactTranslationRetry")
            : text.Format("Debug.CompactTranslations", translatedLines, total);
        return string.Join("  ·  ", source, text.Format("Debug.CompactWords", wordLines, total), translation);
    }

    private static string BuildDebugText(RuntimeSnapshot snapshot, LyricsLine? displayedLine, string? source,
        int wordLines, int translatedLines, bool showTranslation, LocalizationManager text)
    {
        var rows = new List<string>();
        if (snapshot.Document is { } document)
        {
            rows.Add(text.Format("Debug.SourceOverview", source,
                text.Format("Lyrics.LineCount", document.Lines.Count),
                text.Format("Lyrics.WordCoverage", wordLines, document.Lines.Count),
                text.Format("Lyrics.TranslationCoverage", translatedLines, document.Lines.Count)));
        }
        rows.Add(text.Format("Debug.Translation", TranslationStatus(snapshot, displayedLine, translatedLines, showTranslation, text)));

        if (snapshot.ExternalDiagnostics is { } diagnostics)
        {
            rows.Add(text.Format("Debug.QueryStages", text.Text("Debug.State." + diagnostics.FallbackState),
                text.Text("Debug.State." + diagnostics.EnrichmentState)));
            foreach (var provider in diagnostics.Providers)
            {
                var outcome = text.Text("Debug.Outcome." + provider.Outcome);
                var details = new List<string>();
                if (provider.Attempt > 0) details.Add(text.Format("Debug.Attempt", provider.Attempt));
                if (provider.LineCount > 0)
                    details.Add(text.Format("Debug.Counts", provider.LineCount, provider.WordLineCount, provider.TranslatedLineCount));
                if (provider.Reason != ExternalLyricsDiagnosticReason.None)
                    details.Add(text.Text("Debug.Reason." + provider.Reason));
                if (provider.FailureReason != ExternalLyricsFailureReason.None)
                    details.Add(text.Text("Debug.Failure." + provider.FailureReason));
                if (provider.HttpStatusCode is { } status) details.Add("HTTP " + status);
                if (!string.IsNullOrWhiteSpace(provider.ExceptionType)) details.Add(provider.ExceptionType);
                rows.Add(text.Format("Debug.Provider", provider.Source, text.Text("Debug.Stage." + provider.Stage),
                    outcome, details.Count == 0 ? string.Empty : " · " + string.Join(" · ", details)));
            }
        }
        rows.Add(text.Format("Debug.MatchDecision", FormatResolutionLabel(snapshot.Resolution, text), snapshot.Resolution.Summary));
        if (snapshot.Document is { } selected) rows.Add(text.Format("Debug.SourcePath", selected.SourceFile));
        return string.Join(Environment.NewLine, rows);
    }

    private static string TranslationStatus(RuntimeSnapshot snapshot, LyricsLine? displayedLine,
        int translatedLines, bool showTranslation, LocalizationManager text)
    {
        if (!showTranslation) return text.Text("Debug.TranslationDisabled");
        if (displayedLine is not null && HasTranslation(displayedLine)) return text.Text("Debug.TranslationVisible");
        if (translatedLines > 0) return text.Text("Debug.TranslationMissingCurrentLine");
        if (snapshot.Document is null) return text.Text("Debug.TranslationWaitingLyrics");
        if (snapshot.ExternalDiagnostics is not { } diagnostics) return text.Text("Debug.TranslationNotChecked");
        if (diagnostics.Providers.Count == 0) return text.Text("Debug.NoSourcesConfigured");
        if (diagnostics.EnrichmentState == ExternalLyricsQueryState.Querying)
            return text.Text("Debug.TranslationSearching");
        if (diagnostics.EnrichmentState == ExternalLyricsQueryState.RetryWaiting)
            return text.Text("Debug.TranslationRetryWaiting");
        if (diagnostics.Providers.Any(provider => provider.TranslatedLineCount > 0 && provider.Outcome == ExternalLyricsQueryOutcome.Rejected
                && provider.Reason is ExternalLyricsDiagnosticReason.WouldLoseWordTimings or ExternalLyricsDiagnosticReason.WouldLoseWordsAndTranslations))
            return text.Text("Debug.TranslationRejectedWords");
        if (diagnostics.Providers.Any(provider => provider.Outcome == ExternalLyricsQueryOutcome.Failed))
            return text.Text("Debug.TranslationQueryFailed");
        return text.Text(diagnostics.EnrichmentState == ExternalLyricsQueryState.NotStarted
            ? "Debug.TranslationNotChecked" : "Debug.TranslationNotReturned");
    }

    private static bool HasTranslation(LyricsLine line) => !string.IsNullOrWhiteSpace(line.Translation)
        && MetadataMatching.Normalize(line.Translation) != MetadataMatching.Normalize(line.Text);

    private static string GetSourceLabel(LyricsDocument document, LyricsResolution resolution, LocalizationManager text)
    {
        if (document.SourceFile.StartsWith("qqmusic:", StringComparison.OrdinalIgnoreCase)) return "QQ Music";
        if (document.SourceFile.StartsWith("netease:", StringComparison.OrdinalIgnoreCase)) return "NetEase";
        if (document.SourceFile.StartsWith("lrclib:", StringComparison.OrdinalIgnoreCase)
            || document.SourceFile.StartsWith("lrclib-plain:", StringComparison.OrdinalIgnoreCase)) return "LRCLIB";
        return resolution.Source switch
        {
            LyricsResolutionSource.AppleMusicCache or LyricsResolutionSource.CatalogVerifiedCache => text.Text("Lyrics.SourceAppleCache"),
            LyricsResolutionSource.ExternalProvider => text.Text("Lyrics.SourceExternal"),
            _ => text.Text("Lyrics.SourceLyrics"),
        };
    }

    internal static string FormatResolutionLabel(LyricsResolution resolution, LocalizationManager? localization = null)
    {
        var text = localization ?? LocalizationManager.Instance;
        var status = resolution.Status switch
        {
            LyricsResolutionStatus.NoPlayer => text.Text("Lyrics.StatusNoPlayer"),
            LyricsResolutionStatus.WaitingForMetadata => text.Text("Lyrics.StatusWaitingMetadata"),
            LyricsResolutionStatus.SearchingLocal => text.Text("Lyrics.StatusSearchingLocal"),
            LyricsResolutionStatus.VerifyingCatalog => text.Text("Lyrics.StatusVerifyingCatalog"),
            LyricsResolutionStatus.FetchingExternal => text.Text("Lyrics.StatusFetchingExternal"),
            LyricsResolutionStatus.Resolved => text.Text("Lyrics.StatusResolved"),
            LyricsResolutionStatus.Unavailable => text.Text("Lyrics.StatusUnavailable"),
            LyricsResolutionStatus.Error => text.Text("Lyrics.StatusError"),
            _ => resolution.Status.ToString(),
        };

        return resolution.Confidence == LyricsResolutionConfidence.None
            ? status
            : $"{status} ({text.Text("Lyrics.Confidence" + resolution.Confidence)})";
    }

    internal static string GetResolutionPlaceholder(LyricsResolution resolution, LocalizationManager? localization = null)
    {
        var text = localization ?? LocalizationManager.Instance;
        return resolution.Status switch
        {
            LyricsResolutionStatus.NoPlayer => text.Text("Lyrics.PlaceholderNoPlayer"),
            LyricsResolutionStatus.WaitingForMetadata => text.Text("Lyrics.PlaceholderMetadata"),
            LyricsResolutionStatus.SearchingLocal => text.Text("Lyrics.PlaceholderSearching"),
            LyricsResolutionStatus.VerifyingCatalog => text.Text("Lyrics.PlaceholderVerifying"),
            LyricsResolutionStatus.FetchingExternal => text.Text("Lyrics.PlaceholderFetching"),
            LyricsResolutionStatus.Unavailable => text.Text("Lyrics.PlaceholderUnavailable"),
            LyricsResolutionStatus.Error => text.Text("Lyrics.PlaceholderError"),
            _ => text.Text("Lyrics.PlaceholderWaiting"),
        };
    }
}
