using AppleMusicLyrics.Core.Display;
using Xunit;

namespace AppleMusicLyrics.Tests.Display;

public sealed class VisualFrameStatisticsTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void RecordFrame_PublishesTheObservedCadence(int expectedFramesPerSecond)
    {
        var statistics = new VisualFrameStatistics();
        var published = false;

        for (var frame = 0; frame <= expectedFramesPerSecond; frame++)
        {
            published |= statistics.RecordFrame(TimeSpan.FromSeconds(frame / (double)expectedFramesPerSecond));
        }

        Assert.True(published);
        Assert.NotNull(statistics.FramesPerSecond);
        Assert.InRange(statistics.FramesPerSecond.Value, expectedFramesPerSecond - 0.01, expectedFramesPerSecond + 0.01);
    }

    [Fact]
    public void RecordFrame_DoesNotPublishBeforeAFullWindow()
    {
        var statistics = new VisualFrameStatistics();

        for (var frame = 0; frame < 30; frame++)
        {
            Assert.False(statistics.RecordFrame(TimeSpan.FromSeconds(frame / 60.0)));
        }

        Assert.Null(statistics.FramesPerSecond);
    }

    [Fact]
    public void RecordFrame_RestartsAfterCompositionWasSuspended()
    {
        var statistics = new VisualFrameStatistics();
        _ = statistics.RecordFrame(TimeSpan.Zero);
        _ = statistics.RecordFrame(TimeSpan.FromMilliseconds(16));

        Assert.False(statistics.RecordFrame(TimeSpan.FromSeconds(2)));
        Assert.Null(statistics.FramesPerSecond);
    }

    [Fact]
    public void Reset_DiscardsThePublishedRateAndCurrentWindow()
    {
        var statistics = new VisualFrameStatistics();
        for (var frame = 0; frame <= 60; frame++)
        {
            _ = statistics.RecordFrame(TimeSpan.FromSeconds(frame / 60.0));
        }

        statistics.Reset();

        Assert.Null(statistics.FramesPerSecond);
        Assert.False(statistics.RecordFrame(TimeSpan.FromSeconds(10)));
    }
}
