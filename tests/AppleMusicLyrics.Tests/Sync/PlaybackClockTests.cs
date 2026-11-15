using AppleMusicLyrics.Core.Sync;
using Xunit;

namespace AppleMusicLyrics.Tests.Sync;

public sealed class PlaybackClockTests
{
    // Apple Music for Windows, recorded November 2026 at 5ms resolution: the instant each whole-
    // second report was published (SMTC LastUpdatedTime, seconds from the first) and its value.
    // The publisher is a free-running ~1.022s timer reporting floor(true position), so the value
    // skips (217, 265) whenever its drifting phase wraps. All 88 reports fit one real-time timeline
    // whose true position at publish time zero lies in [210.832, 210.851].
    private static readonly (double PublishedAt, double Position)[] RecordedReports =
    [
        (0.000, 210), (1.020, 211), (2.046, 212), (3.067, 213), (4.095, 214), (5.114, 215),
        (6.142, 216), (7.170, 218), (8.187, 219), (9.203, 220), (10.221, 221), (11.250, 222),
        (12.267, 223), (13.285, 224), (14.307, 225), (15.334, 226), (16.354, 227), (17.377, 228),
        (18.399, 229), (19.424, 230), (20.444, 231), (21.463, 232), (22.485, 233), (23.508, 234),
        (24.531, 235), (25.548, 236), (26.572, 237), (27.595, 238), (28.614, 239), (29.639, 240),
        (30.658, 241), (31.671, 242), (32.691, 243), (33.714, 244), (34.734, 245), (35.753, 246),
        (36.778, 247), (37.792, 248), (38.820, 249), (39.838, 250), (40.852, 251), (41.870, 252),
        (42.894, 253), (43.912, 254), (44.935, 255), (45.957, 256), (46.986, 257), (48.012, 258),
        (49.040, 259), (50.058, 260), (51.078, 261), (52.105, 262), (53.121, 263), (54.149, 264),
        (55.168, 266), (56.186, 267), (57.204, 268), (58.225, 269), (59.245, 270), (60.278, 271),
        (61.299, 272), (62.325, 273), (63.357, 274), (64.376, 275), (65.394, 276), (66.411, 277),
        (67.427, 278), (68.449, 279), (69.465, 280), (70.492, 281), (71.521, 282), (72.550, 283),
        (73.567, 284), (74.594, 285), (75.617, 286), (76.632, 287), (77.645, 288), (78.674, 289),
        (79.698, 290), (80.719, 291), (81.748, 292), (82.767, 293), (83.781, 294), (84.809, 295),
        (85.839, 296), (86.861, 297), (87.880, 298),
    ];

    private const double TruePositionAtZero = 210.8415;
    private const double PollInterval = 0.2;

    [Theory]
    [InlineData(true, 0.01)]
    [InlineData(false, 0.05)] // Without publish times, edges are only bounded by 200ms polls.
    public void Update_PinsTheRecordedTimelineOnceThePublishPhaseHasSwept(bool withPublishTimes, double tolerance)
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        var worstSettledError = 0.0;
        foreach (var wall in Poll(0.1, RecordedReports[^1].PublishedAt + 0.9))
        {
            var report = RecordedReports.Last(item => item.PublishedAt <= wall + 1e-9);
            clock.Update(report.Position, playing: true, withPublishTimes ? wall - report.PublishedAt : null);
            if (wall > 50)
            {
                worstSettledError = Math.Max(worstSettledError,
                    Math.Abs(clock.GetEstimatedPosition() - (wall + TruePositionAtZero)));
            }
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        Assert.True(worstSettledError < tolerance, $"worst settled error was {worstSettledError:F4}s");
    }

    [Theory]
    [InlineData(1.0225, true, 0.03)] // The measured cadence: its phase sweeps the quantum every ~45s.
    [InlineData(0.9900, true, 0.03)]
    [InlineData(1.0225, false, 0.2)]
    [InlineData(0.9900, false, 0.2)]
    public void Update_DoesNotFollowTheDriftingPublishPhase(double publishInterval, bool withPublishTimes, double tolerance)
    {
        // Regression: averaging (reported - elapsed) followed this sawtooth and ran 0.17-0.97s late.
        const double truePositionAtZero = 100.37;
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        var nextPublish = 0.3;
        var published = Math.Floor(truePositionAtZero - 0.7);
        var publishedAt = -0.7;
        // The bounds can only close once the publish phase has drifted across a whole quantum.
        var settledAfter = 15 + 1 / Math.Abs(1 - publishInterval);

        var worstSettledError = 0.0;
        foreach (var wall in Poll(0, 240))
        {
            while (nextPublish <= wall + 1e-9)
            {
                published = Math.Floor(nextPublish + truePositionAtZero);
                publishedAt = nextPublish;
                nextPublish += publishInterval;
            }

            clock.Update(published, playing: true, withPublishTimes ? wall - publishedAt : null);
            if (wall > settledAfter)
            {
                worstSettledError = Math.Max(worstSettledError,
                    Math.Abs(clock.GetEstimatedPosition() - (wall + truePositionAtZero)));
            }
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        Assert.True(worstSettledError < tolerance, $"worst settled error was {worstSettledError:F4}s");
    }

    [Fact]
    public void Update_EvictsBoundsThatContradictANewerReport()
    {
        // A sub-threshold seek moves the timeline by less than the discontinuity limit. Older
        // bounds then contradict the new reports and must not hold the estimate on the old timeline.
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        var offset = 50.4;
        var published = Math.Floor(offset - 0.7);
        var nextPublish = 0.3;
        var last = 0.0;
        foreach (var wall in Poll(0, 120))
        {
            if (Math.Abs(wall - 60) < 1e-9) offset += 0.8;
            while (nextPublish <= wall + 1e-9)
            {
                published = Math.Floor(nextPublish + offset);
                nextPublish += 1.0225;
            }

            clock.Update(published, playing: true);
            last = wall;
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        Assert.InRange(clock.GetEstimatedPosition() - (last + PollInterval + offset), -0.15, 0.15);
    }

    [Fact]
    public void Update_NeverRunsBackwardsDuringSteadyPlayback()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        var previous = double.MinValue;
        foreach (var wall in Poll(0.1, RecordedReports[^1].PublishedAt))
        {
            var report = RecordedReports.Last(item => item.PublishedAt <= wall + 1e-9);
            clock.Update(report.Position, playing: true, wall - report.PublishedAt);
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
    public void Update_UsesThePublishTimeOfTheFirstReport()
    {
        // Published 0.8s ago: the true position is now in [12.8, 13.8), centred on 13.3.
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        clock.Update(12.0, playing: true, rawPositionAgeSeconds: 0.8);

        Assert.InRange(clock.GetEstimatedPosition(), 13.25, 13.35);
    }

    [Fact]
    public void Update_IgnoresAnImplausiblyOldPublishTime()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);

        clock.Update(12.0, playing: true, rawPositionAgeSeconds: 30);

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

    [Theory]
    [InlineData(0.2)] // Target offset is above the current phase.
    [InlineData(1.0)] // Target offset is below it.
    public void Update_RepeatedPausePollsDoNotSlewAnUnsettledPhase(double firstEdgeAt)
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(0, playing: true);
        time.Advance(TimeSpan.FromSeconds(firstEdgeAt));
        clock.Update(1, playing: true);
        time.Advance(TimeSpan.FromSeconds(0.1));
        clock.Update(1, playing: false);
        var atPause = clock.GetEstimatedPosition();

        for (var poll = 0; poll < 50; poll++)
        {
            time.Advance(TimeSpan.FromSeconds(0.2));
            clock.Update(1, playing: false);
            Assert.Equal(atPause, clock.GetEstimatedPosition(), 9);
            Assert.Equal(0, clock.RemainingExtrapolationSeconds);
        }
    }

    [Fact]
    public void GetEstimatedPosition_BoundsExtrapolationWithoutAnotherPoll()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(10, playing: true);
        Assert.Equal(3, clock.RemainingExtrapolationSeconds, 6);
        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(11, clock.GetEstimatedPosition(), 6);
        Assert.Equal(2.5, clock.RemainingExtrapolationSeconds, 6);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(13.5, clock.GetEstimatedPosition(), 6);
        Assert.Equal(0, clock.RemainingExtrapolationSeconds);
        clock.Update(10, playing: true);
        Assert.Equal(13.5, clock.GetEstimatedPosition(), 6);
    }

    [Fact]
    public void Update_StalePlayingPollsFreezeElapsedAndUnsettledPhase()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(0, playing: true);
        time.Advance(TimeSpan.FromSeconds(0.2));
        clock.Update(1, playing: true);
        time.Advance(TimeSpan.FromSeconds(3));
        var atHorizon = clock.GetEstimatedPosition();

        for (var poll = 0; poll < 50; poll++)
        {
            clock.Update(1, playing: true);
            Assert.Equal(atHorizon, clock.GetEstimatedPosition(), 9);
            Assert.Equal(0, clock.RemainingExtrapolationSeconds, 9);
            time.Advance(TimeSpan.FromSeconds(0.2));
        }
    }

    [Fact]
    public void Update_ReseedsAfterAStallAndContinuesWithFreshEdges()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(10, playing: true);
        time.Advance(TimeSpan.FromSeconds(20));
        clock.Update(10, playing: true);
        Assert.Equal(13.5, clock.GetEstimatedPosition(), 6);

        // A small raw change after the stall must not be folded into the stale median.
        clock.Update(11, playing: true);
        Assert.Equal(11.5, clock.GetEstimatedPosition(), 6);
        Assert.Equal(3, clock.RemainingExtrapolationSeconds, 6);
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Update(12, playing: true);
        // One-second polls only bound that edge to the second after the reseed.
        Assert.InRange(clock.GetEstimatedPosition(), 12, 13);
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Update(13, playing: true);
        Assert.InRange(clock.GetEstimatedPosition(), 13, 14);
    }

    [Fact]
    public void Update_PausedSeekSnapsEvenWhenSmallAndResumeDoesNotAccumulatePauseTime()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(10, playing: true);
        time.Advance(TimeSpan.FromSeconds(0.2));
        clock.Update(10, playing: false);
        time.Advance(TimeSpan.FromSeconds(30));
        clock.Update(11, playing: false);
        Assert.Equal(11, clock.GetEstimatedPosition(), 6);
        time.Advance(TimeSpan.FromSeconds(30));
        clock.Update(11, playing: false);
        Assert.Equal(11, clock.GetEstimatedPosition(), 6);

        clock.Update(11, playing: true);
        Assert.Equal(11, clock.GetEstimatedPosition(), 6);
        Assert.Equal(3, clock.RemainingExtrapolationSeconds, 6);
        time.Advance(TimeSpan.FromSeconds(1));
        clock.Update(12, playing: true);
        // Thirty paused seconds were not counted; only the edge's sub-second phase is refined.
        Assert.InRange(clock.GetEstimatedPosition(), 12, 12.5);
    }

    [Fact]
    public void Update_LongPauseDoesNotConsumeResumeWatchdogBudget()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(40, playing: true);
        time.Advance(TimeSpan.FromSeconds(0.5));
        clock.Update(40, playing: false);
        var atPause = clock.GetEstimatedPosition();
        time.Advance(TimeSpan.FromMinutes(5));
        clock.Update(40, playing: false);
        Assert.Equal(atPause, clock.GetEstimatedPosition(), 6);

        clock.Update(40, playing: true);
        time.Advance(TimeSpan.FromSeconds(1.2));
        Assert.Equal(atPause + 1.2, clock.GetEstimatedPosition(), 6);
        Assert.Equal(1.8, clock.RemainingExtrapolationSeconds, 6);
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
