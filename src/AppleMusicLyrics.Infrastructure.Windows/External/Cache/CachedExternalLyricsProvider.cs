using AppleMusicLyrics.Core.Abstractions;
using AppleMusicLyrics.Core.Models;

namespace AppleMusicLyrics.Infrastructure.Windows.External;

/// <summary>Persists successful source-specific results without changing the underlying provider's license or behavior.</summary>
public sealed class CachedExternalLyricsProvider : IExternalLyricsProvider, IDisposable
{
    private readonly IExternalLyricsProvider _inner;
    private readonly ExternalLyricsCache _cache;

    public CachedExternalLyricsProvider(IExternalLyricsProvider inner, ExternalLyricsCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
    }

    public string Name => _inner.Name;

    public async Task<LyricsDocument?> FetchAsync(PlayerState player, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = _cache.Generation;
        if (_cache.TryGet(Name, player, out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }

        var document = await _inner.FetchAsync(player, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (document is { Lines.Count: > 0 }) _cache.Store(Name, player, document, generation);
        return document;
    }

    public void Dispose()
    {
        if (_inner is IDisposable disposable) disposable.Dispose();
    }
}
