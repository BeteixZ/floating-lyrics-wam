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
    private bool _hostsWordElements;
    private bool _ownsLineHeight;
    private (double Size, System.Windows.Media.FontFamily Family, FontWeight Weight, System.Windows.FontStyle Style,
        FontStretch Stretch, string? Text, double PixelsPerDip) _lineHeightKey;

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

    private static readonly DependencyProperty ManagesLineHeightProperty = DependencyProperty.RegisterAttached(
        "ManagesLineHeight", typeof(bool), typeof(KaraokeTextController), new PropertyMetadata(false));

    /// <summary>
    /// True while a controller sets <paramref name="text"/>'s line height for the line it shows.
    /// That height fits only the current line, so measuring other text must not reuse it.
    /// </summary>
    public static bool ManagesLineHeight(TextBlock text) => (bool)text.GetValue(ManagesLineHeightProperty);

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
        _hostsWordElements = false;

        if (!enabled || !TryGetSegments(text, line, out var segments)
            || !segments.Any(segment => segment.Word is not null))
        {
            // No local foreground override: line-only and disabled lyrics inherit the ordinary
            // playing/paused color from the TextBlock.
            _target.Text = text;
            SyncLineHeight();
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
                if (emphasize && SplitPadding(segment.Text) is { } parts)
                {
                    // As in Apple Music, each sung word floats up a little with a soft-edged
                    // wipe, and a held syllable also swells and glows letter by letter. Such
                    // words are hosted on their own elements so they can move without
                    // reflowing the line; surrounding spaces stay ordinary text.
                    if (parts.Lead.Length > 0) _target.Inlines.Add(new Run(parts.Lead));
                    var strength = GetEmphasisStrength(segment.Word, timedIndex, timedCount);
                    if (strength > 0)
                    {
                        var emphasis = new WordEmphasis(parts.Core, _sung, _upcoming, strength);
                        _target.Inlines.Add(emphasis.Container);
                        _timedRuns.Add(new TimedRun(segment.Word, brush) { Emphasis = emphasis });
                    }
                    else
                    {
                        var lift = new WordLift(parts.Core, brush);
                        _target.Inlines.Add(lift.Container);
                        _timedRuns.Add(new TimedRun(segment.Word, brush) { Lift = lift, Feather = WipeFeather });
                    }
                    if (parts.Trail.Length > 0) _target.Inlines.Add(new Run(parts.Trail));
                    continue;
                }

                _target.Inlines.Add(new Run(segment.Text) { Foreground = brush });
                _timedRuns.Add(new TimedRun(segment.Word, brush));
                continue;
            }
            _target.Inlines.Add(new Run(segment.Text));
        }
        _hostsWordElements = _timedRuns.Any(timed => timed.Lift is not null || timed.Emphasis is not null);
        SyncLineHeight();
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
            timed.Emphasis?.SetColors(sung, upcoming);
        }
    }

    public void Update(double lyricPosition)
    {
        if (!double.IsFinite(lyricPosition))
        {
            return;
        }

        // The host's font size changes as the overlay fits a line; keep the line box in step.
        SyncLineHeight();

        // Recalculate every fragment, rather than advancing a one-way index. A seek backwards
        // within the same lyric must remove a previously completed highlight immediately.
        foreach (var timed in _timedRuns)
        {
            timed.Emphasis?.Update(timed.Word, lyricPosition);
            timed.Lift?.Update(timed.Word, lyricPosition);
            var progress = LyricsTiming.GetWordProgress(timed.Word, lyricPosition);
            if (progress == timed.Progress)
            {
                continue;
            }
            SetWipe(timed.Brush, progress, timed.Feather);
            timed.Progress = progress;
        }
    }

    /// <summary>
    /// Words hosted on their own elements sit with their descent pulled below the baseline
    /// (<see cref="BaselineAligner"/>), so WPF sizes the line without room for descenders and
    /// cuts off "g", "y" and "p". While such words are shown, the host stacks each line at the
    /// height plain text of the same content would have. A line height the host set itself is
    /// left alone.
    /// </summary>
    private void SyncLineHeight()
    {
        var hasForeignLineHeight = !_ownsLineHeight
            && (_target.ReadLocalValue(TextBlock.LineHeightProperty) != DependencyProperty.UnsetValue
                || _target.ReadLocalValue(TextBlock.LineStackingStrategyProperty) != DependencyProperty.UnsetValue);
        if (!_hostsWordElements || hasForeignLineHeight)
        {
            if (_ownsLineHeight)
            {
                _target.ClearValue(TextBlock.LineHeightProperty);
                _target.ClearValue(TextBlock.LineStackingStrategyProperty);
                _target.ClearValue(ManagesLineHeightProperty);
                _ownsLineHeight = false;
                _lineHeightKey = default;
            }
            return;
        }

        var key = (_target.FontSize, _target.FontFamily, _target.FontWeight, _target.FontStyle,
            _target.FontStretch, _text, VisualTreeHelper.GetDpi(_target).PixelsPerDip);
        if (_ownsLineHeight && key.Equals(_lineHeightKey)) return;
        _lineHeightKey = key;

        var lineHeight = MeasurePlainLineHeight(_target, _text ?? string.Empty);
        if (!(lineHeight > 0) || !double.IsFinite(lineHeight)) return;
        _target.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _target.LineHeight = lineHeight;
        _target.SetValue(ManagesLineHeightProperty, true);
        _ownsLineHeight = true;
    }

    /// <summary>The height of one line of <paramref name="text"/> laid out as plain runs.</summary>
    internal static double MeasurePlainLineHeight(TextBlock target, string text)
    {
        var content = string.IsNullOrWhiteSpace(text) ? " " : text.ReplaceLineEndings(" ");
        var formatted = new FormattedText(content, System.Globalization.CultureInfo.CurrentUICulture,
            target.FlowDirection,
            new Typeface(target.FontFamily, target.FontStyle, target.FontWeight, target.FontStretch),
            target.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(target).PixelsPerDip);
        // Never shorter than the font's own line, so a line of short letters keeps descender room.
        return Math.Max(formatted.Height, target.FontSize * target.FontFamily.LineSpacing);
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

    // How far a sung word floats up, in ems, and how long it takes to get there.
    private const double SungLift = 0.045;
    private const double SungRiseSeconds = 0.45;
    // The soft edge of a word's wipe, as a fraction of the word's width.
    private const double WipeFeather = 0.12;

    /// <summary>
    /// Moves the wipe of <paramref name="brush"/> to <paramref name="progress"/>. With a feather,
    /// a soft edge travels across the text, starting and ending fully outside it.
    /// </summary>
    private static void SetWipe(LinearGradientBrush brush, double progress, double feather)
    {
        var center = progress * (1 + 2 * feather) - feather;
        brush.GradientStops[1].Offset = center - feather;
        brush.GradientStops[2].Offset = center + feather;
    }

    // 0 before the word starts, easing to 1 shortly after; sung words stay up with the line.
    private static double Rise(double position, double start) =>
        position <= start ? 0 : SmoothStep((position - start) / SungRiseSeconds);

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// An inline element sits with its bottom on the text baseline. Pulling its descent below
    /// keeps the word's own baseline on the line's.
    /// </summary>
    private sealed class BaselineAligner
    {
        private double _fontSize = double.NaN;
        private System.Windows.Media.FontFamily? _family;

        public void Apply(FrameworkElement element)
        {
            var size = (double)element.GetValue(TextElement.FontSizeProperty);
            var family = (System.Windows.Media.FontFamily)element.GetValue(TextElement.FontFamilyProperty);
            if (size == _fontSize && ReferenceEquals(family, _family)) return;
            _fontSize = size;
            _family = family;
            element.Margin = new Thickness(0, 0, 0, -Math.Max(0, size * (family.LineSpacing - family.Baseline)));
        }
    }

    /// <summary>An ordinary sung word: a soft-edged wipe, floating up a little as it is sung.</summary>
    private sealed class WordLift
    {
        private readonly TextBlock _text;
        private readonly TranslateTransform _lift = new();
        private readonly BaselineAligner _baseline = new();
        private double _rise = -1;

        public WordLift(string text, System.Windows.Media.Brush foreground)
        {
            _text = new TextBlock { Text = text, Foreground = foreground, RenderTransform = _lift };
            Container = new InlineUIContainer(_text) { BaselineAlignment = BaselineAlignment.Baseline };
        }

        public InlineUIContainer Container { get; }

        public void Update(LyricWord word, double position)
        {
            _baseline.Apply(_text);
            var rise = Rise(position, word.Begin);
            if (Math.Abs(rise - _rise) < 0.002) return;
            _rise = rise;
            _lift.Y = -SungLift * _text.FontSize * rise;
        }
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

    /// <summary>
    /// One held syllable, animated letter by letter as Apple Music does: the colour wipes through
    /// the letters, and each letter rises, grows and glows as the wipe reaches it, so a long
    /// note travels through the word as a wave. Everything settles once the syllable ends.
    /// </summary>
    private sealed class WordEmphasis
    {
        private const double MaxGrowth = 0.1;
        private const double MaxLift = 0.07;
        private const double RiseSeconds = 0.35;
        private const double FallSeconds = 0.45;
        // The soft edge of each letter's wipe, as a fraction of the letter's width.
        private const double Feather = 0.3;
        private readonly Glyph[] _glyphs;
        private readonly StackPanel _panel;
        private readonly double _strength;
        private readonly BaselineAligner _baseline = new();

        public WordEmphasis(string text, Color sung, Color upcoming, double strength)
        {
            _strength = Math.Clamp(strength, 0, 1);
            _panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var letters = new List<Glyph>();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                var glyph = new Glyph((string)elements.Current, sung, upcoming);
                letters.Add(glyph);
                _panel.Children.Add(glyph.Text);
            }
            _glyphs = [.. letters];
            Container = new InlineUIContainer(_panel) { BaselineAlignment = BaselineAlignment.Baseline };
        }

        public InlineUIContainer Container { get; }

        public void SetColors(Color sung, Color upcoming)
        {
            foreach (var glyph in _glyphs) glyph.SetColors(sung, upcoming);
        }

        public void Update(LyricWord word, double position)
        {
            _baseline.Apply(_panel);
            var count = _glyphs.Length;
            var duration = Math.Max(0, word.End - word.Begin);
            var filled = LyricsTiming.GetWordProgress(word, position) * count;
            for (var index = 0; index < count; index++)
            {
                // The wipe reaches each letter in turn; the letter lifts from that moment.
                var reached = word.Begin + duration * index / count;
                var intensity = position < reached ? 0
                    : position < word.End ? SmoothStep((position - reached) / RiseSeconds)
                    : SmoothStep((word.End - reached) / RiseSeconds) * Math.Max(0, 1 - (position - word.End) / FallSeconds);
                // Afterwards the letter settles to the height of the other sung words.
                _glyphs[index].Update(Math.Clamp(filled - index, 0, 1), SmoothStep(intensity) * _strength,
                    Rise(position, reached));
            }
        }

        private sealed class Glyph
        {
            private readonly ScaleTransform _scale = new(1, 1);
            private readonly TranslateTransform _lift = new();
            private readonly DropShadowEffect _glow;
            private readonly LinearGradientBrush _brush;
            private double _fill = -1;
            private double _intensity = -1;
            private double _rise = -1;

            public Glyph(string text, Color sung, Color upcoming)
            {
                _brush = new LinearGradientBrush(new GradientStopCollection
                {
                    new(sung, 0), new(sung, 0), new(upcoming, 0), new(upcoming, 1),
                }, new Point(0, 0.5), new Point(1, 0.5));
                _glow = new DropShadowEffect
                {
                    ShadowDepth = 0,
                    BlurRadius = 16,
                    Color = sung,
                    Opacity = 0,
                    RenderingBias = RenderingBias.Performance,
                };
                var transform = new TransformGroup();
                transform.Children.Add(_scale);
                transform.Children.Add(_lift);
                Text = new TextBlock
                {
                    Text = text,
                    Foreground = _brush,
                    RenderTransformOrigin = new Point(0.5, 0.8),
                    RenderTransform = transform,
                };
                SetFill(0);
            }

            public TextBlock Text { get; }

            public void SetColors(Color sung, Color upcoming)
            {
                _brush.GradientStops[0].Color = sung;
                _brush.GradientStops[1].Color = sung;
                _brush.GradientStops[2].Color = upcoming;
                _brush.GradientStops[3].Color = upcoming;
                _glow.Color = sung;
            }

            public void Update(double fill, double intensity, double rise)
            {
                if (fill != _fill) SetFill(fill);
                if (Math.Abs(intensity - _intensity) < 0.002 && Math.Abs(rise - _rise) < 0.002) return;
                _intensity = intensity;
                _rise = rise;
                _scale.ScaleX = _scale.ScaleY = 1 + MaxGrowth * intensity;
                _lift.Y = -(MaxLift * intensity + SungLift * rise) * Text.FontSize;
                if (intensity <= 0.001)
                {
                    // No effect pass at all while idle.
                    Text.Effect = null;
                    return;
                }
                _glow.Opacity = 0.7 * intensity;
                Text.Effect ??= _glow;
            }

            private void SetFill(double fill)
            {
                _fill = fill;
                SetWipe(_brush, fill, Feather);
            }
        }
    }

    private sealed class TimedRun(LyricWord word, LinearGradientBrush brush)
    {
        public LyricWord Word { get; } = word;
        public LinearGradientBrush Brush { get; } = brush;
        public double Progress { get; set; } = double.NaN;
        public WordEmphasis? Emphasis { get; init; }
        public WordLift? Lift { get; init; }
        public double Feather { get; init; }
    }
}
