using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Core.Sync;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace AppleMusicLyrics.App.Presentation;

/// <summary>
/// Builds timed text only when a displayed line changes. Frame updates change the existing
/// brushes, leaving WPF responsible for wrapping, shaping, and ellipsis.
/// </summary>
public sealed class KaraokeTextController
{
    private readonly TextBlock _target;
    private readonly List<TimedRun> _timedRuns = [];
    private string? _text;
    private LyricsLine? _line;
    private bool _enabled;
    private bool _hasLine;
    private Color _sung = Colors.White;
    private Color _upcoming = Colors.Gray;

    public KaraokeTextController(TextBlock target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    public bool HasTimedWords => _timedRuns.Count > 0;

    /// <summary>The full display text, independent of WPF's plain Text property for rich inlines.</summary>
    public string DisplayText => _text ?? string.Empty;

    public void SetLine(string text, LyricsLine? line, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_hasLine && string.Equals(_text, text, StringComparison.Ordinal)
            && ReferenceEquals(_line, line) && _enabled == enabled)
        {
            return;
        }

        _text = text;
        _line = line;
        _enabled = enabled;
        _hasLine = true;
        _timedRuns.Clear();
        _target.Inlines.Clear();

        if (!enabled || !TryGetSegments(text, line, out var segments)
            || !segments.Any(segment => segment.Word is not null))
        {
            // No local foreground override: line-only and disabled lyrics inherit the ordinary
            // playing/paused color from the TextBlock.
            _target.Text = text;
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Word is not null)
            {
                var brush = CreateWordBrush();
                run.Foreground = brush;
                _timedRuns.Add(new TimedRun(segment.Word, brush));
            }
            _target.Inlines.Add(run);
        }
    }

    public void SetColors(Color sung, Color upcoming)
    {
        if (_sung == sung && _upcoming == upcoming)
        {
            return;
        }

        _sung = sung;
        _upcoming = upcoming;
        foreach (var timed in _timedRuns)
        {
            timed.Brush.GradientStops[0].Color = sung;
            timed.Brush.GradientStops[1].Color = sung;
            timed.Brush.GradientStops[2].Color = upcoming;
            timed.Brush.GradientStops[3].Color = upcoming;
        }
    }

    public void Update(double lyricPosition)
    {
        if (!double.IsFinite(lyricPosition))
        {
            return;
        }

        // Recalculate every fragment, rather than advancing a one-way index. A seek backwards
        // within the same lyric must remove a previously completed highlight immediately.
        foreach (var timed in _timedRuns)
        {
            var progress = LyricsTiming.GetWordProgress(timed.Word, lyricPosition);
            if (progress == timed.Progress)
            {
                continue;
            }
            timed.Brush.GradientStops[1].Offset = progress;
            timed.Brush.GradientStops[2].Offset = progress;
            timed.Progress = progress;
        }
    }

    private LinearGradientBrush CreateWordBrush() => new(
        new GradientStopCollection
        {
            new(_sung, 0),
            new(_sung, 0),
            new(_upcoming, 0),
            new(_upcoming, 1),
        },
        new Point(0, 0.5), new Point(1, 0.5));

    private static bool TryGetSegments(string text, LyricsLine? line, out List<TextSegment> segments)
    {
        segments = [];
        if (line?.Words is not { Count: > 0 } words
            || !string.Equals(text.Trim(), line.Text.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var cursor = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (word is null || string.IsNullOrEmpty(word.Text)
                || !double.IsFinite(word.Begin) || !double.IsFinite(word.End)
                || !double.IsFinite(word.End - word.Begin) || word.End < word.Begin)
            {
                return false;
            }

            var fragment = word.Text;
            var start = text.IndexOf(fragment, cursor, StringComparison.Ordinal);
            if (index == 0 || index == words.Count - 1)
            {
                // Providers trim their display line while retaining exact QRC/YRC fragments.
                // Only trim the outside edges; interior spaces and punctuation are meaningful.
                var trimmed = fragment;
                if (index == 0) trimmed = trimmed.TrimStart();
                if (index == words.Count - 1) trimmed = trimmed.TrimEnd();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                var trimmedStart = text.IndexOf(trimmed, cursor, StringComparison.Ordinal);
                // A leading space from the first fragment might otherwise match a later repeat
                // of that word. Prefer the earlier trimmed occurrence in that case.
                if (trimmedStart >= 0 && (start < 0 || trimmedStart < start))
                {
                    fragment = trimmed;
                    start = trimmedStart;
                }
            }
            if (start < 0)
            {
                return false;
            }
            if (start > cursor)
            {
                segments.Add(new TextSegment(text[cursor..start], null));
            }
            segments.Add(new TextSegment(fragment, word));
            cursor = start + fragment.Length;
        }
        if (cursor < text.Length)
        {
            segments.Add(new TextSegment(text[cursor..], null));
        }
        return true;
    }

    private sealed record TextSegment(string Text, LyricWord? Word);

    private sealed class TimedRun(LyricWord word, LinearGradientBrush brush)
    {
        public LyricWord Word { get; } = word;
        public LinearGradientBrush Brush { get; } = brush;
        public double Progress { get; set; } = double.NaN;
    }
}
