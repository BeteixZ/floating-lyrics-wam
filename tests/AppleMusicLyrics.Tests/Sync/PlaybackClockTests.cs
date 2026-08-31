using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class PlaybackClockTests
{
    // Exactly what Apple Music for Windows reported over 12 seconds: the position steps by a whole
    // second every time, but the moment it steps wanders between 814ms and 1106ms apart. The true
    // position implied by a least-squares fit of these edges is wall clock + 32.4286.
    private static readonly (double WallSeconds, double Position)[] MeasuredEdges =
    [
        (0.656, 33), (1.480, 34), (2.586, 35), (3.690, 36),
        (4.516, 37), (5.617, 38), (6.447, 39), (7.546, 40),
        (8.646, 41), (9.460, 42), (10.557, 43), (11.656, 44),
    ];

    private const double TruePositionAtZero = 32.4286;
    private const double PollInterval = 0.04;

    [Fact]
    public void Update_RecoversTheSubSecondPhaseFromWholeSecondReports()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        var finalError = double.MaxValue;
        foreach (var wall in Poll(0, 12.0))
        {
            clock.Update(ReportedPositionAt(wall), playing: true);
            finalError = Math.Abs(clock.GetEstimatedPosition() - (wall + TruePositionAtZero));
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        // Against a per-edge scatter of 0.083s, the median over a full window lands far inside the
        // one-second quantum the raw reports are stuck on.
        Assert.True(finalError < 0.05, $"final error was {finalError:F4}s");

        double ReportedPositionAt(double wall)
        {
            var reported = MeasuredEdges[0].Position - 1;
            foreach (var edge in MeasuredEdges)
            {
                if (wall + 1e-9 >= edge.WallSeconds)
                {
                    reported = edge.Position;
                }
            }

            return reported;
        }
    }

    [Fact]
    public void Update_NeverRunsBackwardsDuringSteadyPlayback()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        var previous = double.MinValue;
        foreach (var wall in Poll(0, 12.0))
        {
            var reported = MeasuredEdges.Where(edge => wall + 1e-9 >= edge.WallSeconds)
                .Select(edge => edge.Position)
                .DefaultIfEmpty(MeasuredEdges[0].Position - 1)
                .Last();

            clock.Update(reported, playing: true);
            var estimated = clock.GetEstimatedPosition();

            Assert.True(estimated >= previous - 1e-9, $"went backwards at {wall:F2}s: {previous:F4} -> {estimated:F4}");
            previous = estimated;
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }
    }

    [Fact]
    public void Update_AssumesHalfAStepBeforeAnyEdgeHasBeenSeen()
    {
        // The first reading is the floor of the true position, so the phase is unknown and uniform
        // over the quantum. Half a step is the expected value, not a tuned fudge.
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        clock.Update(12.0, playing: true);

        Assert.InRange(clock.GetEstimatedPosition(), 12.45, 12.55);
    }

    [Fact]
    public void Update_SnapsImmediatelyAfterASeek()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        for (var step = 0; step < 40; step++)
        {
            clock.Update(10 + (step / 25), playing: true);
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        clock.Update(120.0, playing: true);

        // A jump this large is a seek, not jitter: it must land at once rather than being averaged
        // in against the old position.
        Assert.InRange(clock.GetEstimatedPosition(), 120.0, 120.6);
    }

    [Fact]
    public void GetEstimatedPosition_HoldsStillWhilePaused()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        clock.Update(40.0, playing: true);
        time.Advance(TimeSpan.FromSeconds(0.5));
        clock.Update(40.0, playing: false);

        var atPause = clock.GetEstimatedPosition();
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(atPause, clock.GetEstimatedPosition(), 6);
    }

    [Fact]
    public void Reset_ClearsTheEstimate()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        clock.Update(40.0, playing: true);
        clock.Reset();

        Assert.Equal(0, clock.GetEstimatedPosition());
    }

    private static IEnumerable<double> Poll(double from, double to)
    {
        for (var wall = from; wall <= to + 1e-9; wall += PollInterval)
        {
            yield return wall;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000_000;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan amount)
        {
            _timestamp += (long)(amount.TotalSeconds * TimestampFrequency);
        }
    }
}
