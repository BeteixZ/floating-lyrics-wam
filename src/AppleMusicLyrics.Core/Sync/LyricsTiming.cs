using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Sync;

public static class LyricsTiming
{
    public static double GetPosition(LyricsDocument? document, PlayerState? player,
        double offsetSeconds = 0, bool applyNativeOffset = true)
    {
        var nativeOffset = applyNativeOffset ? document?.NativeOffsetSeconds ?? 0 : 0;
        var position = (player?.Position ?? 0) + offsetSeconds - nativeOffset;
        return double.IsFinite(position) ? Math.Max(0, position) : 0;
    }

    public static double GetWordProgress(LyricWord word, double position)
    {
        if (!double.IsFinite(position) || !double.IsFinite(word.Begin) || !double.IsFinite(word.End)) return 0;
        if (position < word.Begin) return 0;
        if (word.End <= word.Begin || position >= word.End) return 1;
        return Math.Clamp((position - word.Begin) / (word.End - word.Begin), 0, 1);
    }
}
