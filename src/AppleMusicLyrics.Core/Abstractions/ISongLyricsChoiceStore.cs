using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Core.Abstractions;

public interface ISongLyricsChoiceStore
{
    LyricsChoice? Load(PlayerState player);

    /// <summary>Saves a recording's choice. A null choice restores automatic selection.</summary>
    void Save(PlayerState player, LyricsChoice? choice);
}
