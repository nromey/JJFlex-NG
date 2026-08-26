using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The register that answers "what is still running" (#253).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assertion here is about a rule that has already been got wrong
    /// once somewhere in this codebase: a cached copy of state drifting from
    /// the state (the retired trace dialog), a warning firing repeatedly until
    /// the operator stops hearing it, a probe throwing and taking the whole
    /// answer down with it.
    /// </para>
    /// <para>
    /// Joined to the RadioConfig statics collection because the register's
    /// sentences come from <see cref="Lexicon"/>, and LexiconTests in that
    /// collection calls <c>Lexicon.Forget()</c>. xUnit runs test CLASSES in
    /// parallel; without this, that class empties the store part-way through a
    /// test here and the failure surfaces a long way from its cause.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RunningCostRegisterTests : IDisposable
    {
        public RunningCostRegisterTests() => RunningCostRegister.Clear();

        public void Dispose() => RunningCostRegister.Clear();

        private static RunningCost Cost(
            string id,
            string name,
            Func<bool>? running = null,
            string? costText = null,
            RunningCostWeight weight = RunningCostWeight.Routine)
        {
            return new RunningCost(id, name)
            {
                IsRunning = running,
                DescribeCost = costText == null ? null : () => costText,
                Weight = weight
            };
        }

        // ── What is running ──────────────────────────────────────────────

        [Fact]
        public void AnEmptyRegisterStillAnswers()
        {
            Assert.Empty(RunningCostRegister.Snapshot());
            // Never silence: silence would read as the key not working.
            Assert.False(string.IsNullOrWhiteSpace(RunningCostRegister.DescribeForSpeech()));
        }

        [Fact]
        public void ARegistrationWithNoPredicateIsRunningWhileRegistered()
        {
            IDisposable token = RunningCostRegister.Register(Cost("tone", "Meter test tone"));
            Assert.Single(RunningCostRegister.Snapshot());

            // The #131 shape: the thing stops, and the register stops naming it
            // without anybody having to remember to say so.
            token.Dispose();
            Assert.Empty(RunningCostRegister.Snapshot());
        }

        [Fact]
        public void ThePredicateIsReReadEveryTimeRatherThanCached()
        {
            bool on = false;
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => on));

            Assert.Empty(RunningCostRegister.Snapshot());
            on = true;
            Assert.Single(RunningCostRegister.Snapshot());
            on = false;
            Assert.Empty(RunningCostRegister.Snapshot());
        }

        [Fact]
        public void NotableThingsAreReadBeforeRoutineOnes()
        {
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true,
                weight: RunningCostWeight.Routine));
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true,
                weight: RunningCostWeight.Notable));

            IReadOnlyList<RunningCostReading> readings = RunningCostRegister.Snapshot();
            Assert.Equal(2, readings.Count);
            Assert.Equal("stream", readings[0].Id);
            Assert.Equal("log", readings[1].Id);
        }

        [Fact]
        public void OnlyNotableThingsCountAsWorthAPrompt()
        {
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true,
                weight: RunningCostWeight.Routine));
            Assert.False(RunningCostRegister.AnyNotable);

            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true,
                weight: RunningCostWeight.Notable));
            Assert.True(RunningCostRegister.AnyNotable);
        }

        [Fact]
        public void ReRegisteringTheSameIdReplacesRatherThanDoubles()
        {
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true));
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true));

            Assert.Single(RunningCostRegister.Snapshot());
        }

        // ── The sentences ────────────────────────────────────────────────

        [Fact]
        public void ASentenceCarriesTheNameTheCostAndWhetherItOutlivesTheSession()
        {
            var c = new RunningCost("stream", "Meter stream recording")
            {
                IsRunning = () => true,
                DescribeCost = () => "218,000 meter lines into the log",
                SurvivesRestart = true,
                Weight = RunningCostWeight.Notable
            };
            RunningCostRegister.Register(c);

            string sentence = RunningCostRegister.Snapshot()[0].Sentence();
            Assert.Contains("Meter stream recording", sentence, StringComparison.Ordinal);
            Assert.Contains("218,000 meter lines into the log", sentence, StringComparison.Ordinal);
            // The persistence clause is the single most useful fact the exit
            // prompt carries, and the exact shape of the 2026-08-25 incident.
            Assert.Contains(Lexicon.Get("logging.running.persists"), sentence, StringComparison.Ordinal);
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
        }

        [Fact]
        public void RoutineThingsDoNotRepeatThePersistenceClause()
        {
            // Read the ASSEMBLED sentence, not the source line: the on-demand
            // read lists the always-on log and the meter tones together, and
            // with the clause on both it says "and it will still be on the next
            // time you start" twice in one breath about two things the operator
            // already knows. The clause is for persistence that SURPRISES.
            RunningCostRegister.Register(new RunningCost("log", "The diagnostic log")
            {
                IsRunning = () => true,
                SurvivesRestart = true,
                Weight = RunningCostWeight.Routine
            });

            RunningCostReading r = RunningCostRegister.Snapshot()[0];
            Assert.True(r.SurvivesRestart);  // the fact stays honestly true
            Assert.DoesNotContain(Lexicon.Get("logging.running.persists"), r.Sentence(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void SomethingWithNoMeasurableCostIsStillNamed()
        {
            RunningCostRegister.Register(Cost("tone", "Meter test tone", () => true));

            RunningCostReading r = RunningCostRegister.Snapshot()[0];
            Assert.Null(r.Cost);
            Assert.Equal("Meter test tone.", r.Sentence());
        }

        [Fact]
        public void TheOnDemandReadNamesEverythingRunning()
        {
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true));
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true));
            RunningCostRegister.Register(Cost("off", "Something switched off", () => false));

            string spoken = RunningCostRegister.DescribeForSpeech();
            Assert.Contains("The diagnostic log", spoken, StringComparison.Ordinal);
            Assert.Contains("Meter stream recording", spoken, StringComparison.Ordinal);
            Assert.DoesNotContain("Something switched off", spoken, StringComparison.Ordinal);
        }

        [Fact]
        public void TheLaunchNoticeStaysSilentWhenOnlyRoutineThingsAreOn()
        {
            // The always-on log and the audible meter tones are on for
            // everybody. A launch notice everybody hears every time is one
            // nobody hears at all.
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true));
            RunningCostRegister.Register(Cost("tones", "Meter tones", () => true));

            Assert.Null(RunningCostRegister.DescribeNotableForSpeech());
        }

        [Fact]
        public void TheLaunchNoticeNamesOnlyTheSilentCostlyOnes()
        {
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true));
            RunningCostRegister.Register(Cost("stream", "Meter stream recording", () => true,
                weight: RunningCostWeight.Notable));

            string? notice = RunningCostRegister.DescribeNotableForSpeech();
            Assert.NotNull(notice);
            Assert.Contains("Meter stream recording", notice!, StringComparison.Ordinal);
            Assert.DoesNotContain("The diagnostic log", notice!, StringComparison.Ordinal);
        }

        // ── Thresholds, never timers ─────────────────────────────────────

        [Fact]
        public void NothingIsAnnouncedUntilABoundIsActuallyCrossed()
        {
            long value = 5;
            RunningCostRegister.Register(new RunningCost("capture", "Detailed diagnostic capture")
            {
                IsRunning = () => true,
                Measure = () => value,
                Thresholds = new long[] { 10, 50 }
            });

            var heard = new List<long>();
            void OnCrossed(object? s, RunningCostThresholdEventArgs e) => heard.Add(e.Threshold);
            RunningCostRegister.ThresholdCrossed += OnCrossed;
            try
            {
                // However many times it looks, a poll that finds nothing crossed
                // says nothing. This is the whole difference between a threshold
                // read and the timer Noel ruled out.
                for (int i = 0; i < 20; i++) RunningCostRegister.Poll();
                Assert.Empty(heard);

                value = 12;
                RunningCostRegister.Poll();
                Assert.Equal(new long[] { 10 }, heard);

                // And once crossed, it does not keep saying so.
                for (int i = 0; i < 20; i++) RunningCostRegister.Poll();
                Assert.Single(heard);

                value = 60;
                RunningCostRegister.Poll();
                Assert.Equal(new long[] { 10, 50 }, heard);
            }
            finally
            {
                RunningCostRegister.ThresholdCrossed -= OnCrossed;
            }
        }

        [Fact]
        public void ABoundSpeaksAgainForTheNextRun()
        {
            bool on = true;
            long value = 100;
            RunningCostRegister.Register(new RunningCost("capture", "Detailed diagnostic capture")
            {
                IsRunning = () => on,
                Measure = () => value,
                Thresholds = new long[] { 10 }
            });

            int heard = 0;
            void OnCrossed(object? s, RunningCostThresholdEventArgs e) => heard++;
            RunningCostRegister.ThresholdCrossed += OnCrossed;
            try
            {
                RunningCostRegister.Poll();
                Assert.Equal(1, heard);

                // Stopping clears the bookkeeping; the next bench session gets
                // its warnings rather than inheriting the last one's silence.
                on = false;
                RunningCostRegister.Poll();
                on = true;
                RunningCostRegister.Poll();
                Assert.Equal(2, heard);
            }
            finally
            {
                RunningCostRegister.ThresholdCrossed -= OnCrossed;
            }
        }

        [Fact]
        public void ACrossingSaysWhatItIsHowBigAndHowToStopIt()
        {
            RunningCostRegister.Register(new RunningCost("capture", "Detailed diagnostic capture")
            {
                IsRunning = () => true,
                Measure = () => 42,
                Thresholds = new long[] { 10 },
                DescribeThreshold = _ => "10 megabytes",
                StopHow = "Control J, then Control D"
            });

            string? sentence = null;
            void OnCrossed(object? s, RunningCostThresholdEventArgs e) => sentence = e.Sentence;
            RunningCostRegister.ThresholdCrossed += OnCrossed;
            try
            {
                RunningCostRegister.Poll();
                Assert.NotNull(sentence);
                Assert.Contains("Detailed diagnostic capture", sentence!, StringComparison.Ordinal);
                Assert.Contains("10 megabytes", sentence!, StringComparison.Ordinal);
                // A warning that names no exit is a warning that costs the
                // operator a hunt through Settings.
                Assert.Contains("Control J, then Control D", sentence!, StringComparison.Ordinal);
            }
            finally
            {
                RunningCostRegister.ThresholdCrossed -= OnCrossed;
            }
        }

        // ── Stopping ─────────────────────────────────────────────────────

        [Fact]
        public void StoppingTouchesOnlyTheNotableOnesAndReportsWhatItDid()
        {
            bool routineStopped = false, notableStopped = false;

            RunningCostRegister.Register(new RunningCost("tones", "Meter tones")
            {
                IsRunning = () => true,
                Weight = RunningCostWeight.Routine,
                Stop = () => routineStopped = true
            });
            RunningCostRegister.Register(new RunningCost("stream", "Meter stream recording")
            {
                IsRunning = () => true,
                Weight = RunningCostWeight.Notable,
                Stop = () => notableStopped = true
            });

            IReadOnlyList<string> stopped = RunningCostRegister.StopAll(notableOnly: true);

            Assert.True(notableStopped);
            Assert.False(routineStopped);
            // Names, not a count: "two things turned off" is not something an
            // operator can check.
            Assert.Equal(new[] { "Meter stream recording" }, stopped);
        }

        [Fact]
        public void SomethingAlreadyStoppedIsNotStoppedAgain()
        {
            int calls = 0;
            RunningCostRegister.Register(new RunningCost("stream", "Meter stream recording")
            {
                IsRunning = () => false,
                Weight = RunningCostWeight.Notable,
                Stop = () => calls++
            });

            Assert.Empty(RunningCostRegister.StopAll(notableOnly: true));
            Assert.Equal(0, calls);
        }

        // ── When a probe is the thing that is broken ─────────────────────

        [Fact]
        public void ABrokenProbeDoesNotTakeTheAnswerDownWithIt()
        {
            RunningCostRegister.Register(new RunningCost("broken", "A broken probe")
            {
                IsRunning = () => throw new InvalidOperationException("probe failed")
            });
            RunningCostRegister.Register(new RunningCost("throws-cost", "Costs unknown")
            {
                IsRunning = () => true,
                DescribeCost = () => throw new InvalidOperationException("cost failed")
            });
            RunningCostRegister.Register(Cost("log", "The diagnostic log", () => true));

            IReadOnlyList<RunningCostReading> readings = RunningCostRegister.Snapshot();

            // The positive control: the healthy registrant is still there, so
            // this test is proving that failures are contained, not that the
            // sweep found nothing.
            Assert.Contains(readings, r => r.Id == "log");
            // A probe that cannot answer "am I running" is treated as not.
            Assert.DoesNotContain(readings, r => r.Id == "broken");
            // A probe that cannot price itself is still named.
            RunningCostReading unpriced = readings.Single(r => r.Id == "throws-cost");
            Assert.Null(unpriced.Cost);
        }

        [Fact]
        public void ASubscriberThatThrowsDoesNotStopTheRemainingRegistrations()
        {
            RunningCostRegister.Register(new RunningCost("first", "First")
            {
                IsRunning = () => true,
                Measure = () => 100,
                Thresholds = new long[] { 10 }
            });
            RunningCostRegister.Register(new RunningCost("second", "Second")
            {
                IsRunning = () => true,
                Measure = () => 100,
                Thresholds = new long[] { 10 }
            });

            var seen = new List<string>();
            void OnCrossed(object? s, RunningCostThresholdEventArgs e)
            {
                seen.Add(e.Reading.Id);
                throw new InvalidOperationException("subscriber failed");
            }

            RunningCostRegister.ThresholdCrossed += OnCrossed;
            try
            {
                RunningCostRegister.Poll();
                Assert.Equal(new[] { "first", "second" }, seen);
            }
            finally
            {
                RunningCostRegister.ThresholdCrossed -= OnCrossed;
            }
        }

        [Fact]
        public void ARegistrationNeedsAnIdAndASpokenName()
        {
            Assert.Throws<ArgumentException>(() => new RunningCost("", "Name"));
            Assert.Throws<ArgumentException>(() => new RunningCost("id", "  "));
        }
    }
}
