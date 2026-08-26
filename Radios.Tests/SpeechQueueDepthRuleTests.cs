using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  #197: the transcript proves an utterance was EMITTED, not HEARD.
    //
    //  The rule is pure over recorded JSONL — no radio, no desk, no audio
    //  device, no statics — so these tests are parallel-safe and belong in
    //  the unit tier, where radiocheck already runs them on every pass.
    // ────────────────────────────────────────────────────────────────
    public class SpeechQueueDepthRuleTests
    {
        private static string FixturePath => Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "transcript-20260822-203451-p32012.jsonl");

        // ── The positive control ──

        [Fact]
        public void PositiveControl_FlagsTheUnheardBenchWarning()
        {
            // 2026-08-22, at the bench: the reflected-power warning fired
            // correctly into an open antenna port, the transcript recorded it
            // perfectly — rendered, Critical, correct text — and Noel missed
            // it entirely, because key-down two seconds earlier had queued the
            // TX narration and the warning landed at the back of that queue.
            //
            // Per the standing rule, a negative result needs a positive
            // control: this rule's passes mean nothing until it is shown to
            // FAIL on the recorded session where the failure really happened.
            // This fixture is permanent; if this test ever goes green-by-
            // finding-nothing, the instrument is broken, not the app fixed.
            var findings = SpeechQueueDepthRule.AnalyzeFile(FixturePath);

            var warning = Assert.Single(findings,
                f => f.MonotonicMs is > 84_000 and < 84_100);
            Assert.StartsWith("80 percent of your power", warning.Text);
            Assert.True(warning.BacklogMs > SpeechQueueDepthRule.ThresholdMs,
                $"backlog was {warning.BacklogMs:F0} ms");
            Assert.Contains("CheckReflectedPower", warning.Origin);

            // The fixture is frozen, so its full analysis is pinned. All three
            // findings are true positives:
            //  - 11,073 ms: the connect announcement, Critical, queued behind
            //    ~6.3 s of startup speech — the #93 connect-cluster shape,
            //    flagged here exactly as the rule is meant to.
            //  - 84,038 ms: the warning above, the one that was never heard.
            //  - 90,658 ms: the SAME warning on the second keying — the keying
            //    Noel only had to make because the first one queued.
            Assert.Equal(3, findings.Count);
            Assert.Single(findings, f => f.MonotonicMs is > 11_000 and < 11_100);
            Assert.Single(findings, f => f.MonotonicMs is > 90_600 and < 90_700
                && f.Text.StartsWith("80 percent of your power", StringComparison.Ordinal));
        }

        // ── Synthetic events ──

        private static string Speech(double t, string text, string? level = null,
            bool interrupt = false, bool gated = false, bool suppressed = false)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = "speech",
                ["monotonicMs"] = t,
                ["text"] = text,
                ["level"] = level,
                ["intent"] = null,
                ["interrupt"] = interrupt,
                ["gated"] = gated,
                ["suppressed"] = suppressed,
                ["rendered"] = false,   // render-off transcripts must analyse identically
                ["origin"] = "test",
            });
        }

        private static string Marker(string ev, double t) =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = ev,
                ["monotonicMs"] = t,
            });

        private static readonly string LongNarration = new string('x', 60); // ~4.8 s estimated

        [Fact]
        public void CriticalQueuedBehindLongBacklog_IsFlagged()
        {
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Speech(100, "Check the antenna.", "Critical"),
            });

            var f = Assert.Single(findings);
            Assert.Equal("Check the antenna.", f.Text);
            Assert.True(f.BacklogMs > SpeechQueueDepthRule.ThresholdMs);
        }

        [Fact]
        public void CriticalBehindShortBacklog_Passes()
        {
            // One short utterance ahead is normal sentence flow, not a hidden
            // warning — the threshold tolerates the utterance currently being
            // spoken by design.
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, "Ok", "Terse"),
                Speech(100, "Check the antenna.", "Critical"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void TheRoutineKeydownPair_IsNotFlagged()
        {
            // Every transmit queues a reminder directly behind "Transmitting,
            // locked". That reminder is heard — it is sentence flow, not a
            // buried warning — and a rule that flags every keydown is a rule
            // everyone learns to ignore. Taken verbatim from the 2026-08-22
            // fixture, where it must pass while the 84,038 ms warning fails.
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(82040.364, "Transmitting, locked", "Critical", interrupt: true),
                Speech(82040.437, "Sending the 440 hertz test tone instead of your microphone.",
                    "Critical"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void CriticalCarryingAnInterrupt_Passes()
        {
            // The rule keys on INTENT, not level: a Critical that cuts ahead
            // is doing exactly what the 2026-08-22 warning failed to do.
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Speech(100, "Check the antenna.", "Critical", interrupt: true),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void NonCriticalBehindLongBacklog_Passes()
        {
            // Chatty context queuing behind speech is the intended behaviour
            // of Queue; only a Critical presumed unheard is a failure.
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Speech(100, "Hint: press H for help.", "Chatty"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void AnInterruptClearsTheModelledBacklog()
        {
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Speech(100, "Cut", "Terse", interrupt: true),
                Speech(1100, "Check the antenna.", "Critical"),
            });

            // The interrupt at 100 flushed the narration; only "Cut"
            // (estimated 800 ms, done by 900) precedes the warning.
            Assert.Empty(findings);
        }

        [Fact]
        public void AnExplicitSilenceClearsTheModelledBacklog()
        {
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Marker("silence", 100),
                Speech(200, "Check the antenna.", "Critical"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void GatedAndSuppressedSpeech_OccupiesNothing()
        {
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical", gated: true),
                Speech(10, LongNarration, "Critical", suppressed: true),
                Speech(100, "Check the antenna.", "Critical"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void SessionStartResetsTheModel()
        {
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                Speech(0, LongNarration, "Critical"),
                Marker("session-start", 0),
                Speech(100, "Check the antenna.", "Critical"),
            });

            Assert.Empty(findings);
        }

        [Fact]
        public void TornOrForeignLines_AreSkippedNotFatal()
        {
            // A transcript's final line can be torn by a crash, and future
            // schema versions may add event types. Neither may kill analysis.
            var findings = SpeechQueueDepthRule.Analyze(new[]
            {
                "{\"event\":\"speech\",\"monotonicMs\":0,\"tex",   // torn
                "not json at all",
                Marker("some-future-event", 5),
                Speech(10, LongNarration, "Critical"),
                Speech(100, "Check the antenna.", "Critical"),
            });

            Assert.Single(findings);
        }
    }
}
