using System.Net.Http;
using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Matching;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

/// <summary>Owns request lifetime and a bounded, memory-only cache for platform adapters.</summary>
public abstract class PlatformLyricsProvider : IExternalLyricsProvider, IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, LyricsDocument> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly object _cacheLock = new();
    private const int MaxCacheEntries = 256;

    protected PlatformLyricsProvider(double timeoutSeconds = 6.0, HttpMessageHandler? messageHandler = null)
    {
        HttpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler);
        HttpClient.Timeout = Timeout.InfiniteTimeSpan;
        HttpClient.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        _timeout = TimeSpan.FromSeconds(double.IsFinite(timeoutSeconds) ? Math.Clamp(timeoutSeconds, 2, 30) : 6);
    }

    protected HttpClient HttpClient { get; }

    public abstract string Name { get; }

    public async Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(player.Title) || string.IsNullOrWhiteSpace(player.Artist))
        {
            return null;
        }

        var key = string.Join("|", MetadataMatching.Normalize(player.Title),
            MetadataMatching.Normalize(player.Artist), MetadataMatching.Normalize(player.Album),
            player.Duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var document = await FetchCoreAsync(player, deadline.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (document is not null && document.Lines.Count > 0)
        {
            lock (_cacheLock)
            {
                if (!_cache.ContainsKey(key))
                {
                    while (_cache.Count >= MaxCacheEntries && _cacheOrder.TryDequeue(out var oldest))
                    {
                        _cache.Remove(oldest);
                    }
                    _cacheOrder.Enqueue(key);
                }
                _cache[key] = document;
            }
        }

        return document;
    }

    protected abstract Task<LyricsDocument?> FetchCoreAsync(PlayerState player, CancellationToken cancellationToken);

    public void Dispose() => HttpClient.Dispose();
}
