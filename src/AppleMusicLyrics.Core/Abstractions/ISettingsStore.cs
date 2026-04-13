using AppleMusicLyrics.Core.Configuration;

namespace AppleMusicLyrics.Core.Abstractions;

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
