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

    // Apple Music for Windows, recorded November 2026: a seek from 63 to 147 ("Never Too Far").
    // The seek publishes 147 at once (with a stale 64 in between), and the restarted timer
    // republishes 147 one period later. Forty later reports pin the timeline: playback reached
    // 146.970 at that republish (13.018), a full second after 147 was first published.
    private static readonly (double PublishedAt, double Position)[] RecordedSeekReports =
    [
        (0.000, 52), (1.021, 53), (2.045, 54), (3.056, 55), (4.089, 56), (5.110, 57), (6.122, 58),
        (7.134, 59), (8.164, 60), (9.187, 61), (10.209, 62), (11.227, 63), (11.784, 147), (11.926, 64),
        (11.996, 147), (13.018, 147), (14.038, 148), (15.052, 149), (16.068, 150), (17.080, 151),
        (18.100, 152), (19.112, 153), (20.125, 154), (21.136, 155), (22.155, 156), (23.166, 157),
        (24.178, 158), (25.200, 159), (26.227, 160), (27.241, 161), (28.253, 162), (29.279, 163),
        (30.304, 164), (31.326, 165), (32.339, 166), (33.364, 167), (34.375, 168), (35.399, 169),
        (36.421, 170), (37.447, 171), (38.472, 172),
    ];

    [Fact]
    public void Update_PinsTheTimelineOneTickAfterARecordedSeek()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        static double TruePosition(double wall) => 146.970 + (wall - 13.018);

        var worstBeforeRepublish = 0.0;
        var worstAfter = 0.0;
        foreach (var wall in Poll(0.1, RecordedSeekReports[^1].PublishedAt + 0.9))
        {
            var report = RecordedSeekReports.Last(item => item.PublishedAt <= wall + 1e-9);
            clock.Update(report.Position, playing: true, wall - report.PublishedAt);
            var error = Math.Abs(clock.GetEstimatedPosition() - TruePosition(wall));
            if (wall is > 12.05 and < 13.0) worstBeforeRepublish = Math.Max(worstBeforeRepublish, error);
            if (wall > 13.05) worstAfter = Math.Max(worstAfter, error);
            time.Advance(TimeSpan.FromSeconds(PollInterval));
        }

        // Before: a second off at most, rather than a second and a half ahead. After: pinned at
        // once, where the bounds alone left up to half a second for the next ~45 seconds.
        Assert.True(worstBeforeRepublish < 0.1, $"worst error before the republish was {worstBeforeRepublish:F3}s");
        Assert.True(worstAfter < 0.06, $"worst error after the republish was {worstAfter:F3}s");
    }

    // Apple Music for Windows, recorded November 2026: the first 48 seconds of three tracks, timed
    // from each track's first report. Later reports pin where playback reached 0 (ZeroAt).
    public static TheoryData<string, double, (double PublishedAt, double Position)[]> RecordedTrackStarts => new()
    {
        { "Everything is romantic", 0.992, [
            (0.000, 0), (0.030, 0), (0.242, 0), (0.734, 0), (1.753, 0), (1.981, 1), (2.031, 2), (2.075, 2),
            (2.718, 2), (3.746, 2), (4.770, 3), (5.800, 4), (6.820, 5), (7.849, 6), (8.868, 7), (9.895, 8),
            (10.912, 9), (11.932, 10), (12.957, 11), (13.989, 12), (15.013, 14), (16.035, 15), (17.053, 16),
            (18.075, 17), (19.100, 18), (20.124, 19), (21.149, 20), (22.169, 21), (23.200, 22), (24.220, 23),
            (25.237, 24), (26.259, 25), (27.278, 26), (28.299, 27), (29.322, 28), (30.346, 29), (31.372, 30),
            (32.389, 31), (33.419, 32), (34.447, 33), (35.472, 34), (36.500, 35), (37.531, 36), (38.551, 37),
            (39.566, 38), (40.586, 39), (41.608, 40), (42.633, 41), (43.658, 42), (44.689, 43), (45.709, 44),
            (46.737, 45), (47.755, 46)] },
        { "Mean girls", 0.969, [
            (0.000, 0), (0.032, 0), (0.245, 0), (1.273, 0), (2.390, 1), (2.630, 2), (2.878, 2), (2.914, 2),
            (3.335, 2), (3.755, 3), (3.894, 3), (4.909, 3), (5.928, 4), (6.949, 5), (7.975, 7), (8.998, 8),
            (10.029, 9), (11.046, 10), (12.066, 11), (13.089, 12), (14.107, 13), (15.126, 14), (16.151, 15),
            (17.170, 16), (18.190, 17), (19.218, 18), (20.236, 19), (21.261, 20), (22.282, 21), (23.305, 22),
            (24.334, 23), (25.362, 24), (26.383, 25), (27.412, 26), (28.443, 27), (29.474, 28), (30.495, 29),
            (31.517, 30), (32.538, 31), (33.557, 32), (34.576, 33), (35.600, 34), (36.621, 35), (37.646, 36),
            (38.667, 37), (39.692, 38), (40.711, 39), (41.743, 40), (42.772, 41), (43.796, 42), (44.812, 43),
            (45.840, 44), (46.871, 45), (47.895, 46)] },
        { "Apple", 1.006, [
            (0.000, 0), (0.033, 0), (0.245, 0), (1.270, 0), (1.717, 1), (2.740, 1), (3.105, 2), (3.215, 3),
            (3.584, 3), (3.707, 3), (4.735, 3), (5.763, 4), (6.780, 5), (7.808, 6), (8.835, 7), (9.858, 8),
            (10.880, 9), (11.908, 10), (12.929, 11), (13.955, 12), (14.976, 13), (16.001, 14), (17.027, 16),
            (18.049, 17), (19.072, 18), (20.096, 19), (21.118, 20), (22.146, 21), (23.168, 22), (24.184, 23),
            (25.210, 24), (26.236, 25), (27.260, 26), (28.282, 27), (29.307, 28), (30.333, 29), (31.358, 30),
            (32.382, 31), (33.402, 32), (34.426, 33), (35.448, 34), (36.471, 35), (37.492, 36), (38.521, 37),
            (39.542, 38), (40.561, 39), (41.584, 40), (42.610, 41), (43.632, 42), (44.654, 43), (45.685, 44),
            (46.709, 45), (47.735, 46)] },
    };

    [Theory]
    [MemberData(nameof(RecordedTrackStarts))]
    public void Update_PinsATrackFromItsFirstReport(string track, double zeroAt, (double PublishedAt, double Position)[] reports)
    {
        // Every phase of the 200ms poll relative to the track change.
        foreach (var firstPoll in new[] { 0.005, 0.05, 0.1, 0.15, 0.195 })
        {
            var time = new ManualTimeProvider();
            var clock = new PlaybackClock(time);
            var worst = 0.0;
            foreach (var wall in Poll(firstPoll, reports[^1].PublishedAt + 0.9))
            {
                var report = reports.Last(item => item.PublishedAt <= wall + 1e-9);
                clock.Update(report.Position, playing: true, wall - report.PublishedAt);
                if (wall > zeroAt + 0.1)
                    worst = Math.Max(worst, Math.Abs(clock.GetEstimatedPosition() - (wall - zeroAt)));
                time.Advance(TimeSpan.FromSeconds(PollInterval));
            }

            // Without the anchor the start-up burst pushed the estimate up to a second early, and the
            // bounds then took some 45 seconds to settle.
            Assert.True(worst < 0.07, $"{track}, first poll {firstPoll}s after the change: worst error {worst:F3}s");
        }
    }

    [Fact]
    public void Update_PausedReportOverridesATrackStartAnchorThatRanAhead()
    {
        // Reported by a user: "paused, raw 00:00.000, estimated 00:02.733". The track's first
        // report anchored the start, but playback never got going before the pause.
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(0, playing: true, 0.02);
        foreach (var _ in Poll(0.2, 3.7))
        {
            time.Advance(TimeSpan.FromSeconds(PollInterval));
            clock.Update(0, playing: true, 0.1);
        }
        Assert.True(clock.GetEstimatedPosition() > 2);

        clock.Update(0, playing: false, 0.05);

        Assert.Equal(0, clock.GetEstimatedPosition(), 3);
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Update(0, playing: false, 0.05);
        Assert.Equal(0, clock.GetEstimatedPosition(), 3);
    }

    // Apple Music for Windows, recorded November 2026 on a busy system: each report reached the
    // reader about 0.8s after its own timestamp (Arrival, Lateness = timestamp - arrival, Position).
    private static readonly (double Arrival, double Lateness, double Position)[] LateDeliveredReports =
    [
        (0.000, -0.792, 120), (1.021, -0.788, 122), (2.047, -0.790, 123), (3.072, -0.791, 124), (4.089, -0.780, 125),
        (5.118, -0.783, 126), (6.145, -0.793, 127), (7.176, -0.794, 128), (8.203, -0.793, 129), (9.231, -0.792, 130),
        (10.258, -0.792, 131), (11.284, -0.791, 132), (12.312, -0.792, 133), (13.335, -0.789, 134), (14.362, -0.792, 135),
        (15.369, -0.776, 136), (16.398, -0.778, 137), (17.433, -0.787, 138), (18.465, -0.795, 139), (19.491, -0.794, 140),
        (20.520, -0.794, 141), (21.536, -0.779, 142), (22.570, -0.783, 143), (23.599, -0.780, 144), (24.614, -0.766, 145),
        (25.649, -0.775, 146), (26.674, -0.773, 147), (27.696, -0.776, 148), (28.718, -0.773, 149), (29.750, -0.782, 150),
        (30.780, -0.794, 151), (31.804, -0.790, 152), (32.848, -0.811, 153), (33.874, -0.825, 154), (34.905, -0.824, 155),
        (35.923, -0.813, 156), (36.946, -0.806, 157), (37.970, -0.807, 158), (38.983, -0.792, 159),
    ];

    [Fact]
    public void Update_FollowsTheTimestampsOfReportsDeliveredLate()
    {
        // The timestamps fit one timeline; where it lies is the intersection of their bounds.
        var low = LateDeliveredReports.Max(report => report.Position - (report.Arrival + report.Lateness));
        var high = LateDeliveredReports.Min(report => report.Position + 1 - (report.Arrival + report.Lateness));
        var truthAtZero = (low + high) / 2;

        foreach (var firstPoll in new[] { 0.0, 0.05, 0.1, 0.15 })
        {
            var time = new ManualTimeProvider();
            var clock = new PlaybackClock(time);
            var worst = 0.0;
            foreach (var wall in Poll(firstPoll, LateDeliveredReports[^1].Arrival))
            {
                var report = LateDeliveredReports.Last(item => item.Arrival <= wall + 1e-9);
                clock.Update(report.Position, playing: true, wall - (report.Arrival + report.Lateness));
                if (wall > 15) worst = Math.Max(worst, Math.Abs(clock.GetEstimatedPosition() - (wall + truthAtZero)));
                time.Advance(TimeSpan.FromSeconds(PollInterval));
            }

            // Clamping each timestamp to the previous poll ran about 0.5s late, and judging jumps
            // at arrival mistook ordinary reports for seeks.
            Assert.True(worst < 0.06, $"first poll {firstPoll}s: worst error {worst:F3}s");
        }
    }

    [Fact]
    public void Update_DropsASeekAnchorThatLaterReportsContradict()
    {
        var time = new ManualTimeProvider();
        var clock = new PlaybackClock(time);
        clock.Update(50, playing: true, 0);
        time.Advance(TimeSpan.FromSeconds(0.5));
        clock.Update(120, playing: true, 0);
        time.Advance(TimeSpan.FromSeconds(1.0));
        clock.Update(120, playing: true, 0); // the republish: anchored at 120.0
        Assert.Equal(120, clock.GetEstimatedPosition(), 1);

        // This player's timeline is actually 0.6s later; its next edges must win.
        for (var edge = 1; edge <= 3; edge++)
        {
            time.Advance(TimeSpan.FromSeconds(0.4));
            clock.Update(120 + edge, playing: true, 0);
            time.Advance(TimeSpan.FromSeconds(0.6));
            clock.Update(120 + edge, playing: true, 0.6);
        }
        Assert.InRange(clock.GetEstimatedPosition() - (123.6 + 0.6), -0.5, 0.5);
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
