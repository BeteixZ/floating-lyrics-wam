using System.IO;
using AppleMusicLyrics.Application.Services;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Configuration;
using AppleMusicLyrics.Core.Parsing;
using AppleMusicLyrics.Core.Sync;
using AppleMusicLyrics.Infrastructure.Windows.Cache;
using AppleMusicLyrics.Infrastructure.Windows.Catalog;
using AppleMusicLyrics.Infrastructure.Windows.Configuration;
using AppleMusicLyrics.Infrastructure.Windows.External;
using AppleMusicLyrics.Infrastructure.Windows.Media;

namespace AppleMusicLyrics.App.Composition;

public sealed record AppRuntimeComposition(
    IniSettingsStore SettingsStore,
    AppSettings Settings,
    GlobalMediaSessionProvider PlayerProvider,
    ITunesCatalogSongResolver? CatalogResolver,
    LrcLibLyricsProvider? ExternalLyricsProvider,
    LyricsRuntimeService RuntimeService);

public static class AppCompositionRoot
{
    public static AppRuntimeComposition Create()
    {
        var settingsStore = new IniSettingsStore(GetWritableDataPath("settings.ini"));
        var settings = settingsStore.Load();
        var scanner = new AppleMusicCacheScanner(new TtmlLyricsParser());
        var playerProvider = new GlobalMediaSessionProvider(settings.AllowNonAppleMediaSessions);
        var catalogResolver = settings.CatalogLookupEnabled
            ? new ITunesCatalogSongResolver(
                settings.CatalogStorefronts,
                GetWritableDataPath("catalog-cache.json"),
                settings.CatalogLookupTimeoutSeconds)
            : null;
        var externalProvider = settings.ExternalLyricsEnabled
            ? new LrcLibLyricsProvider(
                timeoutSeconds: settings.ExternalLyricsTimeoutSeconds,
                persistentCachePath: settings.PersistExternalLyricsCache
                    ? GetWritableDataPath("lrclib-cache.json")
                    : null)
            : null;
        var runtimeService = new LyricsRuntimeService(
            scanner,
            playerProvider,
            new LyricsSynchronizer(),
            new PlaybackClock(),
            settings.LyricsOffsetSeconds,
            catalogResolver)
        {
            ApplyNativeLyricOffset = settings.ApplyNativeLyricOffset,
            AllowLowConfidenceLyrics = settings.AllowLowConfidenceLyrics,
            ExternalLyricsProviders = externalProvider is null
                ? Array.Empty<IExternalLyricsProvider>()
                : [externalProvider],
        };

        return new AppRuntimeComposition(
            settingsStore,
            settings,
            playerProvider,
            catalogResolver,
            externalProvider,
            runtimeService);
    }

    internal static string GetWritableDataPath(string fileName)
    {
        var legacyPath = Path.Combine(AppContext.BaseDirectory, fileName);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return legacyPath;
        }

        var dataDirectory = Path.Combine(localAppData, "AppleMusicLyrics");
        var targetPath = Path.Combine(dataDirectory, fileName);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (!File.Exists(targetPath) && File.Exists(legacyPath))
            {
                File.Copy(legacyPath, targetPath);
            }

            return targetPath;
        }
        catch (IOException)
        {
            return legacyPath;
        }
        catch (UnauthorizedAccessException)
        {
            return legacyPath;
        }
    }
}
