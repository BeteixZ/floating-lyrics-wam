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
        private readonly List<DuetWindow>?[]? _duets;
        private readonly ActiveLyricState _intro;
        private readonly bool _orderedNonOverlapping;
        private int _lastIndex;

        public TimelineIndex(LyricsDocument document)
        {
            var lines = document.Lines;
            _lines = lines;
            _intro = new ActiveLyricState(null, null, null, lines[0]);
            _states = new ActiveLyricState[lines.Count];
            _orderedNonOverlapping = true;
            var orderedByBegin = true;
            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                _states[index] = new ActiveLyricState(index,
                    index > 0 ? lines[index - 1] : null, line,
                    index + 1 < lines.Count ? lines[index + 1] : null);
                if (!double.IsFinite(line.Begin) || !double.IsFinite(line.End) || line.End <= line.Begin
                    || (index > 0 && (lines[index - 1].Begin >= line.Begin || lines[index - 1].End > line.Begin)))
                    _orderedNonOverlapping = false;
                if (!double.IsFinite(line.Begin) || (index > 0 && lines[index - 1].Begin > line.Begin))
                    orderedByBegin = false;
            }
            if (!_orderedNonOverlapping && document.HasMultipleVocalists())
                _duets = BuildDuetWindows(orderedByBegin);
        }

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
                    return _states[current];

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
                return _states[selected];
            }

            // Preserve the original first-active-line precedence for overlapping, malformed,
            // or unsorted provider timelines, where a simple binary search is not equivalent.
            for (var index = 0; index < _lines.Count; index++)
            {
                var line = _lines[index];
                if (position >= line.Begin && position < line.End)
                    return ResolveDuet(index, position);
                if (index + 1 < _lines.Count && position >= line.End && position < _lines[index + 1].Begin)
                    return _states[index];
            }
            return _states[^1];
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
                    if (orderedByBegin && partner.Begin >= lead.End) break;
                    if (string.IsNullOrEmpty(partner.VocalistId) || partner.VocalistId == lead.VocalistId
                        || !(Math.Max(lead.Begin, partner.Begin) < Math.Min(lead.End, partner.End))) continue;

                    var after = _states[index] with { NextLine = _states[other].NextLine };
                    var during = after with { DuetLine = partner };
                    result ??= new List<DuetWindow>?[_lines.Count];
                    (result[index] ??= []).Add(new DuetWindow(partner.Begin, partner.End, during, after));
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
