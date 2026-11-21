using System.Runtime.CompilerServices;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Sync;

public sealed class LyricsSynchronizer
{
    private static readonly ActiveLyricState Empty = new(null, null, null, null);
    // Documents are immutable snapshots. Weak keys avoid retaining every track ever played;
    // the table and immutable indexes are safe for concurrent poll and visual-frame readers.
    private readonly ConditionalWeakTable<LyricsDocument, TimelineIndex> _indexes = new();

    /// <param name="offsetSeconds">
    /// The user's calibration for how far SMTC's reported position lags the audible audio.
    /// </param>
    /// <param name="applyNativeOffset">
    /// Whether to honour the <c>lyricOffset</c> Apple ships inside the TTML, which corrects the
    /// lyric timings for the particular master being played. Only a minority of documents carry one.
    /// </param>
    public ActiveLyricState Resolve(
        LyricsDocument? document,
        PlayerState? player,
        double offsetSeconds = 0.0,
        bool applyNativeOffset = true)
    {
        if (document is null || player is null || document.Lines.Count == 0)
            return Empty;

        var position = LyricsTiming.GetPosition(document, player, offsetSeconds, applyNativeOffset);
        return _indexes.GetValue(document, static value => new TimelineIndex(value)).Resolve(position);
    }

    private sealed class TimelineIndex
    {
        private readonly IReadOnlyList<LyricsLine> _lines;
        private readonly ActiveLyricState[] _states;
        // Shown in a long instrumental gap after a line: nothing current, the next line previewed.
        private readonly ActiveLyricState?[] _interludes;
        private readonly List<DuetWindow>?[]? _duets;
        // When each line stops being current. Usually its end, but a line hands over when the next
        // one comes in during its last moment (see HandoffSeconds).
        private readonly double[] _ends;
        private readonly ActiveLyricState _intro;
        private readonly bool _orderedNonOverlapping;
        private int _lastIndex;

        public TimelineIndex(LyricsDocument document)
        {
            var lines = document.Lines;
            _lines = lines;
            _intro = new ActiveLyricState(null, null, null, lines[0]);
            _states = new ActiveLyricState[lines.Count];
            _interludes = new ActiveLyricState?[lines.Count];
            _ends = lines.Select(line => line.End).ToArray();
            var orderedByBegin = true;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                _states[index] = new ActiveLyricState(index,
                    index > 0 ? lines[index - 1] : null, line,
                    index + 1 < lines.Count ? lines[index + 1] : null);
                if (!double.IsFinite(line.Begin) || (index > 0 && lines[index - 1].Begin > line.Begin))
                    orderedByBegin = false;
            }
            if (orderedByBegin) ApplySameVoiceHandoffs();

            _orderedNonOverlapping = true;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                if (!double.IsFinite(line.Begin) || !double.IsFinite(line.End) || line.End <= line.Begin
                    || (index > 0 && (lines[index - 1].Begin >= line.Begin || _ends[index - 1] > line.Begin)))
                    _orderedNonOverlapping = false;
            }
            if (!_orderedNonOverlapping && document.HasMultipleVocalists())
            {
                ApplyHandoffs();
                _duets = BuildDuetWindows(orderedByBegin);
            }

            // Measured from the latest end so far: a long line sung under shorter ones is no gap.
            var sungUntil = double.NegativeInfinity;
            for (var index = 0; orderedByBegin && index + 1 < lines.Count; index++)
            {
                sungUntil = Math.Max(sungUntil, _ends[index]);
                if (lines[index + 1].Begin - sungUntil >= InterludeSeconds)
                    _interludes[index] = new ActiveLyricState(index, lines[index], null, lines[index + 1]);
            }
        }

        // Apple Music replaces a finished line with its interlude dots in a long instrumental gap
        // ("Mean girls": 13.8 s) but keeps it through short pauses ("Everything is romantic": 3.2 s).
        // Apple's exact threshold is not known; this sits between those observations.
        private const double InterludeSeconds = 4.0;

        // Callers only pass a position before the next line begins.
        private ActiveLyricState StateAt(int index, double position) =>
            position >= _ends[index] && _interludes[index] is { } interlude ? interlude : _states[index];

        public ActiveLyricState Resolve(double position)
        {
            if (position < _lines[0].Begin)
                return _intro;

            if (_orderedNonOverlapping && double.IsFinite(position))
            {
                // A lyric remains selected through the gap before its successor. Most frames
                // stay inside this window; seeks use binary search instead of walking the song.
                var current = Volatile.Read(ref _lastIndex);
                if (position >= _lines[current].Begin
                    && (current + 1 == _lines.Count || position < _lines[current + 1].Begin))
                    return StateAt(current, position);

                var low = 0;
                var high = _lines.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (_lines[middle].Begin <= position)
                        low = middle + 1;
                    else
                        high = middle;
                }
                var selected = Math.Max(0, low - 1);
                Volatile.Write(ref _lastIndex, selected);
                return StateAt(selected, position);
            }

            // Preserve the original first-active-line precedence for overlapping, malformed,
            // or unsorted provider timelines, where a simple binary search is not equivalent.
            for (var index = 0; index < _lines.Count; index++)
            {
                var line = _lines[index];
                if (position >= line.Begin && position < _ends[index])
                    return ResolveDuet(index, position);
                if (index + 1 < _lines.Count && position >= _ends[index] && position < _lines[index + 1].Begin)
                    return StateAt(index, position);
            }
            return _states[^1];
        }

        // The other singer often comes in during the last moment of a line, as its final note
        // fades. Showing both lines for that moment and then one again made the overlay jump at
        // every exchange, so such a line simply hands over. Only a part that comes in earlier, or
        // ends within the line (a reply sung over it), is shown alongside it.
        private const double HandoffSeconds = 1.0;

        // A singer's next line often begins while the previous one's last note still rings
        // ("Everything is romantic": 0.1–0.35 s). Apple Music moves to the new line at its start;
        // holding the old line until its end left the overlay up to 0.35 s behind. Duet partners
        // are handled by ApplyHandoffs, which also keeps a reply sung over a line alongside it.
        private void ApplySameVoiceHandoffs()
        {
            for (var index = 0; index + 1 < _lines.Count; index++)
            {
                var line = _lines[index];
                var next = _lines[index + 1];
                var partners = !string.IsNullOrEmpty(line.VocalistId) && !string.IsNullOrEmpty(next.VocalistId)
                    && line.VocalistId != next.VocalistId;
                if (!partners && next.Begin > line.Begin && next.Begin < line.End
                    && next.Begin >= line.End - HandoffSeconds && next.End > line.End)
                    _ends[index] = next.Begin;
            }
        }

        private void ApplyHandoffs()
        {
            for (var index = 0; index < _lines.Count; index++)
            {
                var lead = _lines[index];
                if (string.IsNullOrEmpty(lead.VocalistId)) continue;
                foreach (var partner in _lines)
                {
                    if (!string.IsNullOrEmpty(partner.VocalistId) && partner.VocalistId != lead.VocalistId
                        && partner.Begin > lead.Begin && partner.Begin < _ends[index]
                        && partner.Begin >= lead.End - HandoffSeconds && partner.End > lead.End)
                        _ends[index] = partner.Begin;
                }
            }
        }

        private List<DuetWindow>?[]? BuildDuetWindows(bool orderedByBegin)
        {
            List<DuetWindow>?[]? result = null;
            for (var index = 0; index < _lines.Count; index++)
            {
                var lead = _lines[index];
                if (string.IsNullOrEmpty(lead.VocalistId)) continue;
                for (var other = index + 1; other < _lines.Count; other++)
                {
                    var partner = _lines[other];
                    if (orderedByBegin && partner.Begin >= _ends[index]) break;
                    if (string.IsNullOrEmpty(partner.VocalistId) || partner.VocalistId == lead.VocalistId
                        || !(Math.Max(lead.Begin, partner.Begin) < Math.Min(_ends[index], _ends[other]))) continue;

                    var after = _states[index] with { NextLine = _states[other].NextLine };
                    var during = after with { DuetLine = partner };
                    result ??= new List<DuetWindow>?[_lines.Count];
                    (result[index] ??= []).Add(new DuetWindow(partner.Begin, _ends[other], during, after));
                }
            }
            return result;
        }

        private ActiveLyricState ResolveDuet(int index, double position)
        {
            var state = _states[index];
            if (_duets?[index] is not { } windows) return state;
            foreach (var window in windows)
            {
                if (position >= window.End) state = window.After;
                else if (position >= window.Begin) return window.During;
            }
            return state;
        }

        private sealed record DuetWindow(double Begin, double End, ActiveLyricState During, ActiveLyricState After);
    }
}
