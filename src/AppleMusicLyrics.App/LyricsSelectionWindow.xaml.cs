using System.Windows;
using System.Windows.Controls;
using AppleMusicLyrics.App.Localization;
using AppleMusicLyrics.App.Presentation;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;

namespace AppleMusicLyrics.App;

public partial class LyricsSelectionWindow : Window
{
    private readonly LyricsRuntimeService _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LyricsSynchronizer _previewSynchronizer = new();
    private readonly LocalizationManager _text = LocalizationManager.Instance;
    private bool _trackChanged;
    private bool _closed;

    public int TrackGeneration { get; }

    public LyricsSelectionWindow(LyricsRuntimeService runtime, RuntimeSnapshot target)
    {
        _runtime = runtime;
        TrackGeneration = target.TrackGeneration;
        InitializeComponent();
        TrackTitleText.Text = OverlaySnapshotPresenter.CreatePlayer(target).WindowTitle;
        if (target.Document is { Lines.Count: > 0 } current)
            SetChoices([new(current, target.Resolution.Source)], target);
        Loaded += SearchOnLoad;
    }

    private async void SearchOnLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= SearchOnLoad;
        await SearchAsync(refresh: false);
    }

    public void ObserveTrack(RuntimeSnapshot snapshot)
    {
        if (_trackChanged || _closed) return;
        if (snapshot.Player is null || snapshot.TrackGeneration != TrackGeneration)
        {
            _trackChanged = true;
            _lifetime.Cancel();
            SearchStatusText.Text = _text.Text("Lyrics.Selection.TrackChanged");
            ChoicesList.IsEnabled = RetryButton.IsEnabled = AutomaticButton.IsEnabled = UseButton.IsEnabled = false;
        }
        else
        {
            AutomaticButton.IsEnabled = snapshot.CanManageLyrics && snapshot.IsManualSelection;
            UseButton.IsEnabled = snapshot.CanManageLyrics && ChoicesList.SelectedItem is ChoiceRow;
        }
    }

    private async Task SearchAsync(bool refresh)
    {
        var target = _runtime.GetFrameSnapshot();
        ObserveTrack(target);
        if (_trackChanged || _closed || !target.CanManageLyrics) return;
        RetryButton.IsEnabled = false;
        SearchStatusText.Text = _text.Text("Lyrics.Selection.Searching");
        var token = _lifetime.Token;
        try
        {
            var result = await Task.Run(() => _runtime.SearchLyricsChoicesAsync(target, refresh, token), token);
            if (_closed || _trackChanged) return;
            SetChoices(result.Choices, target);
            SearchStatusText.Text = result.FailedSources > 0
                ? _text.Format("Lyrics.Selection.PartialResults", result.Choices.Count, result.FailedSources)
                : result.Choices.Count == 0 ? _text.Text("Lyrics.Selection.NoResults")
                : _text.Format("Lyrics.Selection.Results", result.Choices.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_closed && !_trackChanged) SearchStatusText.Text = _text.Text("Lyrics.Selection.SearchFailed");
        }
        finally
        {
            if (!_closed && !_trackChanged) RetryButton.IsEnabled = true;
        }
    }

    private void SetChoices(IReadOnlyList<LyricsChoice> choices, RuntimeSnapshot target)
    {
        var previous = (ChoicesList.SelectedItem as ChoiceRow)?.Choice.Document.SourceFile;
        var rows = choices.Select(choice =>
        {
            var document = choice.Document;
            var source = choice.ProviderName ?? OverlaySnapshotPresenter.GetSourceLabel(document, choice.Source, _text);
            if (choice.Source == LyricsResolutionSource.AppleMusicCache && !ReferenceEquals(document, target.Document))
                source = _text.Format("Lyrics.Selection.Unverified", source);
            if (ReferenceEquals(document, target.Document)) source = _text.Format("Lyrics.Selection.Current", source);
            var duration = document.DurationSeconds is > 0 ? TimeParser.FormatTimestamp(document.DurationSeconds.Value) : "—";
            var stats = _text.Format("Debug.Counts", document.Lines.Count, document.Lines.Count(LyricWordTiming.IsUsable),
                document.Lines.Count(line => LyricsTranslationFilter.IsMeaningful(line.Translation)));
            var id = CatalogLyricsId.ForDocument(document);
            var details = string.Join(" · ", new[] { id, duration, stats }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new ChoiceRow(choice, source, details);
        }).ToArray();
        ChoicesList.ItemsSource = rows;
        ChoicesList.SelectedItem = rows.FirstOrDefault(row => row.Choice.Document.SourceFile == previous) ?? rows.FirstOrDefault();
    }

    private void ChoicesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var snapshot = _runtime.GetFrameSnapshot();
        ObserveTrack(snapshot);
        if (_trackChanged || ChoicesList.SelectedItem is not ChoiceRow row || snapshot.Player is not { } player)
        {
            PreviewTextBox.Clear();
            return;
        }

        var document = row.Choice.Document;
        var position = _previewSynchronizer.Resolve(document, player, _runtime.LyricsOffsetSeconds, _runtime.ApplyNativeLyricOffset);
        PreviewTitleText.Text = _text.Format("Lyrics.Selection.PreviewAt", TimeParser.FormatTimestamp(player.Position));
        var index = Math.Max(0, (position.CurrentIndex ?? 0) - 1);
        PreviewTextBox.Text = string.Join(Environment.NewLine + Environment.NewLine, document.Lines.Skip(index).Take(4)
            .Select(line => TimeParser.FormatTimestamp(line.Begin) + "  " + line.Text
                + (line.Translation is { Length: > 0 } translation ? Environment.NewLine + translation : string.Empty)));
        PreviewTextBox.ScrollToHome();
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e) => await SearchAsync(refresh: true);

    private void UseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ChoicesList.SelectedItem is ChoiceRow row) SelectLyrics(row.Choice);
    }

    private void AutomaticButton_OnClick(object sender, RoutedEventArgs e) => SelectLyrics(null);

    private void SelectLyrics(LyricsChoice? choice)
    {
        if (_trackChanged || _closed) return;
        if (_runtime.RequestLyricsSelection(TrackGeneration, choice)) Close();
        else ObserveTrack(_runtime.GetFrameSnapshot());
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private sealed record ChoiceRow(LyricsChoice Choice, string Name, string Details);
}
