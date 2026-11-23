using System.Net;
using System.Text.Json;
using AppleMusicLyrics.Core.Models;
using AppleMusicLyrics.Infrastructure.Windows.External;
using Xunit;

namespace AppleMusicLyrics.Tests.External;

public sealed class NetEaseArtistAliasTests
{
    private static readonly PlayerState Player = new("Duty", "Ayumi Hamasaki", "Duty", 54, 314.01, true);

    [Fact]
    public async Task FetchAsync_UsesProviderVerifiedAliasAndRetainsTranslations()
    {
        var handler = new AliasHandler();
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        var document = await provider.FetchAsync(Player);
        Assert.Equal("NETEASE_22737996", document?.LyricsId);
        Assert.Equal("译文", Assert.Single(document!.Lines).Translation);
        Assert.Equal(1, handler.ArtistRequests);
        Assert.NotNull(await provider.FetchAsync(Player with { Duration = 314.5 }));
        Assert.Equal(1, handler.ArtistRequests);
    }

    [Theory]
    [InlineData("Ayumi", 16405, "浜崎あゆみ", "Duty", 314010)]
    [InlineData("Another Singer", 16405, "浜崎あゆみ", "Duty", 314010)]
    [InlineData("Ayumi Hamasaki", 999, "浜崎あゆみ", "Duty", 314010)]
    [InlineData("Ayumi Hamasaki", 16405, "Another Singer", "Duty", 314010)]
    [InlineData("Ayumi Hamasaki", 16405, "浜崎あゆみ", "Duty (Live)", 314010)]
    [InlineData("Ayumi Hamasaki", 16405, "浜崎あゆみ", "Duty", 360000)]
    public async Task FetchAsync_DoesNotRelaxArtistIdentityOrRecordingGuards(string playingArtist,
        long detailId, string detailName, string title, int duration)
    {
        var handler = new AliasHandler { DetailId = detailId, DetailName = detailName, Title = title, Duration = duration };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        Assert.Null(await provider.FetchAsync(Player with { Artist = playingArtist }));
        Assert.Equal(0, handler.LyricsRequests);
        if (title.Contains("Live", StringComparison.Ordinal) || duration > 330000)
            Assert.Equal(0, handler.ArtistRequests);
    }

    [Fact]
    public async Task FetchAsync_FailedAliasLookupDoesNotHideLaterExactArtistCandidate()
    {
        var handler = new AliasHandler { FailAlias = true, IncludeExactCandidate = true };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);
        Assert.Equal("NETEASE_2", (await provider.FetchAsync(Player))?.LyricsId);
    }

    [Fact]
    public async Task FetchAsync_FailedAliasLookupRemainsRetryable()
    {
        var handler = new AliasHandler { FailAlias = true };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);
        var error = await Assert.ThrowsAsync<AggregateException>(() => provider.FetchAsync(Player));
        Assert.Equal(503, Assert.IsType<ExternalLyricsServiceException>(Assert.Single(error.InnerExceptions)).ServiceCode);
    }

    [Theory]
    [InlineData("{\"code\":200,\"nolyric\":true}")]
    [InlineData("{\"code\":200,\"pureMusic\":true}")]
    [InlineData("{\"code\":200,\"lrc\":{\"lyric\":\"\"}}")]
    public async Task FetchAsync_PartialAliasFailureRemainsRetryableWhenExactCandidateHasNoLyrics(string emptyLyrics)
    {
        var handler = new AliasHandler { FailAlias = true, IncludeExactCandidate = true, LyricsResponse = emptyLyrics };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);

        var error = await Assert.ThrowsAsync<ExternalLyricsServiceException>(() => provider.FetchAsync(Player));
        Assert.Equal(503, error.ServiceCode);
        Assert.Equal(1, handler.LyricsRequests);

        handler.FailAlias = false;
        handler.LyricsResponse = null;
        Assert.Equal("NETEASE_22737996", (await provider.FetchAsync(Player))?.LyricsId);
        Assert.Equal(2, handler.ArtistRequests);
        Assert.Equal(2, handler.LyricsRequests);
    }

    [Fact]
    public async Task FetchAsync_CancellationDuringAliasVerificationIsNotACompletedMiss()
    {
        var handler = new AliasHandler { BlockAlias = true, IncludeExactCandidate = true };
        using var provider = new NetEaseLyricsProvider(messageHandler: handler);
        using var cancellation = new CancellationTokenSource();
        var fetch = provider.FetchAsync(Player, cancellation.Token);
        await handler.AliasStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(0, handler.LyricsRequests);
    }

    private sealed class AliasHandler : HttpMessageHandler
    {
        public long DetailId { get; init; } = 16405;
        public string DetailName { get; init; } = "浜崎あゆみ";
        public string Title { get; init; } = "Duty";
        public int Duration { get; init; } = 314010;
        public bool FailAlias { get; set; }
        public bool IncludeExactCandidate { get; init; }
        public bool BlockAlias { get; init; }
        public string? LyricsResponse { get; set; }
        public TaskCompletionSource AliasStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ArtistRequests { get; private set; }
        public int LyricsRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/artist/", StringComparison.Ordinal))
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                ArtistRequests++;
                AliasStarted.TrySetResult();
                if (BlockAlias) await Task.Delay(Timeout.Infinite, cancellationToken);
                json = FailAlias ? "{\"code\":503}" : JsonSerializer.Serialize(new
                {
                    code = 200,
                    artist = new { id = DetailId, name = DetailName, alias = new[] { "Ayumi Hamasaki" }, trans = "滨崎步" },
                });
            }
            else if (request.RequestUri.AbsolutePath == "/api/search/pc")
            {
                var songs = new List<object>
                {
                    new { id = 22737996, name = Title, duration = Duration,
                        artists = new[] { new { id = 16405, name = "浜崎あゆみ" } }, album = new { name = "Duty" } },
                };
                if (IncludeExactCandidate)
                    songs.Add(new
                    {
                        id = 2,
                        name = "Duty",
                        duration = 314010,
                        artists = new[] { new { id = 1, name = "Ayumi Hamasaki" } },
                        album = new { name = "Duty" }
                    });
                json = JsonSerializer.Serialize(new { code = 200, result = new { songs } });
            }
            else
            {
                LyricsRequests++;
                json = LyricsResponse ?? JsonSerializer.Serialize(new
                {
                    code = 200,
                    lrc = new { lyric = "[00:01.00]Original" },
                    tlyric = new { lyric = "[00:01.00]译文" }
                });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
