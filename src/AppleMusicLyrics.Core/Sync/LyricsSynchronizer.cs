using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Sync;

public sealed class LyricsSynchronizer
{
    public ActiveLyricState Resolve(
        LyricsDocument? document,
        PlayerState? player,
        double offsetSeconds = 0.0)
    {
        if (document is null || player is null || document.Lines.Count == 0)
        {
            return new ActiveLyricState(null, null, null, null);
        }

        var position = Math.Max(0, player.Position + offsetSeconds);
        var lines = document.Lines;

        if (position < lines[0].Begin)
        {
            return new ActiveLyricState(
                CurrentIndex: null,
                PreviousLine: null,
                CurrentLine: null,
                NextLine: lines[0]);
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var current = lines[index];
            if (position >= current.Begin && position < current.End)
            {
                return new ActiveLyricState(
                    CurrentIndex: index,
                    PreviousLine: index > 0 ? lines[index - 1] : null,
                    CurrentLine: current,
                    NextLine: index + 1 < lines.Count ? lines[index + 1] : null);
            }

            if (index + 1 < lines.Count && position >= current.End && position < lines[index + 1].Begin)
            {
                return new ActiveLyricState(
                    CurrentIndex: index,
                    PreviousLine: index > 0 ? lines[index - 1] : null,
                    CurrentLine: current,
                    NextLine: lines[index + 1]);
            }
        }

        return new ActiveLyricState(
            CurrentIndex: lines.Count - 1,
            PreviousLine: lines.Count > 1 ? lines[^2] : null,
            CurrentLine: lines[^1],
            NextLine: null);
    }
}
