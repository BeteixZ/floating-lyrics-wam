using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

    private readonly bool _emphasizeSustainedWords;

    /// <param name="emphasizeSustainedWords">
    /// Let held single words swell and glow while sung (Apple Music style). Such words are hosted
    /// in an <see cref="InlineUIContainer"/> instead of a <see cref="Run"/>.
    /// </param>
    public KaraokeTextController(TextBlock target, bool emphasizeSustainedWords = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _emphasizeSustainedWords = emphasizeSustainedWords;
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

        // A provider that times a whole line as one "word" has no syllable to emphasize.
        var timedCount = segments.Count(segment => segment.Word is not null);
        var emphasize = _emphasizeSustainedWords && timedCount > 1;
        var timedIndex = -1;
        foreach (var segment in segments)
        {
            if (segment.Word is not null)
            {
                timedIndex++;
                var brush = CreateWordBrush();
                var strength = emphasize ? GetEmphasisStrength(segment.Word, timedIndex, timedCount) : 0;
                if (strength > 0 && SplitPadding(segment.Text) is { } parts)
                {
                    // A held syllable is hosted on its own element so it can grow and glow
                    // without reflowing the line; surrounding spaces stay ordinary text.
                    if (parts.Lead.Length > 0) _target.Inlines.Add(new Run(parts.Lead));
                    var emphasis = new WordEmphasis(parts.Core, brush, _sung, strength);
                    _target.Inlines.Add(emphasis.Container);
                    if (parts.Trail.Length > 0) _target.Inlines.Add(new Run(parts.Trail));
                    _timedRuns.Add(new TimedRun(segment.Word, brush) { Emphasis = emphasis });
                    continue;
                }

                _target.Inlines.Add(new Run(segment.Text) { Foreground = brush });
                _timedRuns.Add(new TimedRun(segment.Word, brush));
                continue;
            }
            _target.Inlines.Add(new Run(segment.Text));
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
            timed.Emphasis?.SetGlowColor(sung);
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
            timed.Emphasis?.Update(timed.Word, lyricPosition);
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

    // Like Apple Music, a held syllable swells and glows while it is sung. How long counts as
    // "held" depends on where the word sits: a line often opens on a drawn-out pickup ("Oh",
    // "And") that should not swell, while the held word that ends a phrase is the classic case.
    private const double SustainedAtLineStartSeconds = 1.8;
    private const double SustainedAtLineEndSeconds = 0.8;

    /// <summary>0 for no emphasis; otherwise 0.4–1, growing with how far the hold exceeds the threshold.</summary>
    public static double GetEmphasisStrength(LyricWord word, int index, int count)
    {
        var position = count <= 1 ? 1.0 : index / (double)(count - 1);
        // Ease the threshold down late in the line so mid-line words still need a long hold.
        var threshold = SustainedAtLineStartSeconds
            + (SustainedAtLineEndSeconds - SustainedAtLineStartSeconds) * position * position;
        var duration = word.End - word.Begin;
        if (!double.IsFinite(duration) || duration < threshold) return 0;
        return Math.Clamp(0.4 + (duration - threshold) / 1.2, 0.4, 1.0);
    }

    private const string PunctuationOnly = ".,!?;:'\"()-";

    private static (string Lead, string Core, string Trail)? SplitPadding(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start == end || !text.AsSpan(start, end - start).ContainsAnyExcept(PunctuationOnly)) return null;
        // Only a single word swells; a timed phrase keeps the ordinary sweep.
        if (text.AsSpan(start, end - start).ContainsAny(" \t　")) return null;
        return (text[..start], text[start..end], text[end..]);
    }

    /// <summary>One held syllable: scaled and glowing in proportion to how strongly it is sung.</summary>
    private sealed class WordEmphasis
    {
        private const double MaxGrowth = 0.08;
        private const double RiseSeconds = 0.3;
        private const double FallSeconds = 0.45;
        private readonly TextBlock _text;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _lift = new();
        private readonly DropShadowEffect _glow;
        private double _intensity = -1;
        private double _alignedFontSize = double.NaN;
        private System.Windows.Media.FontFamily? _alignedFamily;

        private readonly double _strength;

        public WordEmphasis(string text, System.Windows.Media.Brush foreground, Color glow, double strength)
        {
            _strength = Math.Clamp(strength, 0, 1);
            _glow = new DropShadowEffect
            {
                ShadowDepth = 0,
                BlurRadius = 20,
                Color = glow,
                Opacity = 0,
                RenderingBias = RenderingBias.Performance,
            };
            var transform = new TransformGroup();
            transform.Children.Add(_scale);
            transform.Children.Add(_lift);
            _text = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                RenderTransformOrigin = new Point(0.5, 0.7),
                RenderTransform = transform,
            };
            Container = new InlineUIContainer(_text) { BaselineAlignment = BaselineAlignment.Baseline };
        }

        public InlineUIContainer Container { get; }

        public void SetGlowColor(Color color) => _glow.Color = color;

        public void Update(LyricWord word, double position)
        {
            AlignBaseline();
            // Rise quickly once the syllable starts, hold while it is sung, then settle back.
            var intensity = position < word.Begin ? 0
                : position < word.End ? SmoothStep(Math.Min(1, (position - word.Begin) / RiseSeconds))
                : Math.Max(0, 1 - (position - word.End) / FallSeconds);
            intensity = SmoothStep(intensity) * _strength;
            if (Math.Abs(intensity - _intensity) < 0.002) return;
            _intensity = intensity;
            _scale.ScaleX = _scale.ScaleY = 1 + MaxGrowth * intensity;
            _lift.Y = -0.06 * _text.FontSize * intensity;
            if (intensity <= 0.001)
            {
                // No effect pass at all while idle.
                _text.Effect = null;
                return;
            }
            _glow.Opacity = 0.9 * intensity;
            _text.Effect ??= _glow;
        }

        // An inline element sits with its bottom on the text baseline. Pull the descent below it
        // so the word's own baseline lines up with the surrounding text.
        private void AlignBaseline()
        {
            var size = _text.FontSize;
            var family = _text.FontFamily;
            if (size == _alignedFontSize && ReferenceEquals(family, _alignedFamily)) return;
            _alignedFontSize = size;
            _alignedFamily = family;
            var descent = Math.Max(0, size * (family.LineSpacing - family.Baseline));
            _text.Margin = new Thickness(0, 0, 0, -descent);
        }

        private static double SmoothStep(double value)
        {
            var t = Math.Clamp(value, 0, 1);
            return t * t * (3 - 2 * t);
        }
    }

    private sealed class TimedRun(LyricWord word, LinearGradientBrush brush)
    {
        public LyricWord Word { get; } = word;
        public LinearGradientBrush Brush { get; } = brush;
        public double Progress { get; set; } = double.NaN;
        public WordEmphasis? Emphasis { get; init; }
    }
}
