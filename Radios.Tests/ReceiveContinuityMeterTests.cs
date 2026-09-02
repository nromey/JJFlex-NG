using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The receive-continuity meter, and the account of the 852.7 ms
    /// "SmartLink startup gap" it used to report (#473).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The timestamps here are shaped exactly like FlexLib's:
    /// <c>TimestampInt + TimestampFrac / 2^16</c>, where the integer half is UTC
    /// seconds and the fractional half covers only about 0.147 of a second
    /// before the integer rolls. <see cref="Key"/> builds them that way, so the
    /// tests that prove the old meter wrong are run against the real shape of
    /// the data rather than against a convenient one.
    /// </para>
    /// </remarks>
    public class ReceiveContinuityMeterTests
    {
        private const int PacketsPerSecond = 100;

        /// <summary>
        /// The fraction of a second the key's fractional term actually covers,
        /// measured from the 2026-09-01 captures: their reported nominal step
        /// was 1.1 to 1.4 units and their largest step 852.7 to 953.2, and
        /// <c>largest + 99 x nominal ~= 1.000</c> holds on every one of them.
        /// </summary>
        private const double FractionalSpanPerSecond = 0.1465;

        private static double Key(int packetIndex)
        {
            int second = packetIndex / PacketsPerSecond;
            int within = packetIndex % PacketsPerSecond;
            return 1788304800L + second
                + (within * FractionalSpanPerSecond / PacketsPerSecond);
        }

        // ── The account of the 853 ms ───────────────────────────────────────

        [Fact]
        public void TheKeyJumpsAtEverySecondBoundaryAndThatIsWhatUsedToBeReportedAsAGap()
        {
            // The step across a second boundary, in key units, on a stream with
            // no loss whatsoever. This is the "852.7 ms" figure, reproduced from
            // the key's own arithmetic — no packet is missing here.
            double last = Key(PacketsPerSecond - 1);
            double first = Key(PacketsPerSecond);
            double boundaryStep = first - last;

            double withinStep = Key(1) - Key(0);
            Assert.InRange(withinStep * 1000, 1.0, 1.6);      // the "nominal 1.4 ms"
            Assert.InRange(boundaryStep * 1000, 840.0, 870.0); // the "852.7 ms gap"

            // The relation the evening's traces all satisfy, and the reason the
            // figure is structural rather than jitter: a boundary step plus the
            // ninety-nine ordinary steps of that second is exactly one second.
            //
            // Four decimal places, not more, and that is itself worth knowing: a
            // double carrying an epoch second has about 1e-7 of resolution left
            // for the fraction, so the key cannot express better than a
            // microsecond however the radio fills it in.
            Assert.Equal(1.0, boundaryStep + (PacketsPerSecond - 1) * withinStep, 4);
        }

        [Fact]
        public void ThatJumpIsNotCountedAsMissingAudio()
        {
            var meter = new ReceiveContinuityMeter();
            for (int i = 0; i < PacketsPerSecond * 5; i++) meter.Consume(Key(i));

            Assert.Equal(0, meter.MissingPackets);
            Assert.Equal(0.0, meter.MissingMilliseconds, 6);
            Assert.Equal(0, meter.ShortSeconds);
            Assert.Equal(0, meter.SkippedSeconds);
        }

        [Fact]
        public void TheOldMeterWouldHaveFlaggedOnePerSecondOnAPerfectStream()
        {
            // The positive control for the finding. Reproduce the old rule —
            // "any step over 1.5x the smallest step seen is a discontinuity" —
            // against a stream in which nothing at all is missing, and count
            // what it would have said. On the 2026-09-01 captures it said 128
            // for 12,772 packets, 768 for 76,881 and 26 for 2,607: one per
            // second of stream, every time.
            const int seconds = 7;
            double minDelta = double.MaxValue;
            int flagged = 0;
            double prev = Key(0);
            for (int i = 1; i < PacketsPerSecond * seconds; i++)
            {
                double now = Key(i);
                double delta = now - prev;
                bool armed = minDelta < double.MaxValue;
                if (delta < minDelta) minDelta = delta;
                if (armed && delta > minDelta * 1.5) flagged++;
                prev = now;
            }

            Assert.Equal(seconds - 1, flagged);   // one per completed second
        }

        [Fact]
        public void TakenAtFaceValueTheOldReadingClaimsMoreLostAudioThanTheStreamContained()
        {
            // The reductio, using the real numbers from
            // trace-20260901-182038: 12,772 packets consumed — 127.7 seconds at
            // a hundred a second — and 128 "discontinuities" of about 850 ms
            // each. That is 108 of 128 seconds gone, on a stream whose playback
            // queue ran dry six times in the whole session.
            const int packets = 12772;
            const int discontinuities = 128;
            const double stepMs = 850.0;

            double streamSeconds = packets / (double)PacketsPerSecond;
            double claimedMissingSeconds = discontinuities * stepMs / 1000.0;

            Assert.InRange(claimedMissingSeconds / streamSeconds, 0.80, 0.90);
        }

        // ── What the meter measures now ─────────────────────────────────────

        [Fact]
        public void AShortSecondIsCountedAndPricedInRealMilliseconds()
        {
            var meter = new ReceiveContinuityMeter();
            for (int i = 0; i < PacketsPerSecond * 4; i++)
            {
                // Second 2 loses ten packets — a hundred milliseconds of audio.
                int second = i / PacketsPerSecond;
                int within = i % PacketsPerSecond;
                if (second == 2 && within >= 40 && within < 50) continue;
                meter.Consume(Key(i));
            }

            Assert.Equal(1, meter.ShortSeconds);
            Assert.Equal(10, meter.MissingPackets);
            Assert.Equal(100.0, meter.MissingMilliseconds, 3);
            Assert.Equal(PacketsPerSecond, meter.PeakPerSecond);
        }

        [Fact]
        public void TheLeadingPartialSecondIsNotJudged()
        {
            // A stream that starts part way through a second holds fewer packets
            // in that second by construction. Judging it would report loss on
            // every connect, which is precisely the class of false alarm this
            // rewrite exists to end.
            var meter = new ReceiveContinuityMeter();
            for (int i = 60; i < PacketsPerSecond * 4; i++) meter.Consume(Key(i));

            Assert.Equal(0, meter.MissingPackets);
            Assert.Equal(0, meter.ShortSeconds);
        }

        [Fact]
        public void AWholeSecondSteppedOverIsReportedAsSuch()
        {
            var meter = new ReceiveContinuityMeter();
            for (int i = 0; i < PacketsPerSecond * 2; i++) meter.Consume(Key(i));
            for (int i = PacketsPerSecond * 4; i < PacketsPerSecond * 6; i++) meter.Consume(Key(i));

            Assert.Equal(2, meter.SkippedSeconds);
        }

        [Fact]
        public void ThePacketRateIsObservedNotAssumed()
        {
            // A radio sending 20 ms frames would be fifty packets a second, and
            // the meter must price its loss against that rather than against a
            // hardcoded hundred. Nothing in this tree asks the radio for a frame
            // duration, so assuming one would be a guess.
            var meter = new ReceiveContinuityMeter();
            const int rate = 50;
            for (int second = 0; second < 4; second++)
            {
                int inThisSecond = (second == 2) ? rate - 5 : rate;
                for (int within = 0; within < inThisSecond; within++)
                {
                    meter.Consume(1788304800L + second
                        + within * FractionalSpanPerSecond / rate);
                }
            }

            Assert.Equal(rate, meter.PeakPerSecond);
            Assert.Equal(5, meter.MissingPackets);
            Assert.Equal(100.0, meter.MissingMilliseconds, 3);  // 5 of 50 = 100 ms
        }

        [Fact]
        public void AStreamShorterThanASecondSaysSoRatherThanInventingAVerdict()
        {
            var meter = new ReceiveContinuityMeter();
            for (int i = 0; i < 30; i++) meter.Consume(Key(i));

            Assert.Equal(30, meter.PacketCount);
            Assert.Equal(0, meter.SecondsJudged);
            Assert.Equal(0, meter.PeakPerSecond);
            Assert.Equal(0.0, meter.MissingMilliseconds, 6);
        }

        [Fact]
        public void RearmDiscardsThePartialSecondWithoutLosingTheTotals()
        {
            var meter = new ReceiveContinuityMeter();
            for (int i = 0; i < PacketsPerSecond * 3; i++) meter.Consume(Key(i));
            long before = meter.PacketCount;

            meter.Rearm();
            for (int i = PacketsPerSecond * 3 + 40; i < PacketsPerSecond * 6; i++)
            {
                meter.Consume(Key(i));
            }

            Assert.True(meter.PacketCount > before);
            Assert.Equal(0, meter.MissingPackets);   // the partial second was not judged
        }
    }
}
