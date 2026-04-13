using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class PlaybackClockTests
{
    [Fact]
    public async Task Update_WithQuantizedPosition_AppliesForwardBias()
    {
        var clock = new PlaybackClock();
        clock.Reset();

        await Task.Delay(260);
        clock.Update(12.0, playing: true);

        var estimated = clock.GetEstimatedPosition();

        Assert.True(estimated >= 12.18);
    }
}
