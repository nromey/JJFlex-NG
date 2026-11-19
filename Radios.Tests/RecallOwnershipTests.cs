#nullable enable
using Radios;
using Xunit;

namespace Radios.Tests
{
    // ────────────────────────────────────────────────────────────────
    //  Sprint 49 Track N2 repair 2: a memory recall's spoken status belongs to
    //  the operator's action that made the request, not to the deferred landing
    //  (Codex's re-check of e2b069d8, R1 and R2).
    //
    //  The first three tests are Codex's candidate probes
    //  (for-claude/2026-11-19-codex-n2-recheck-results/N2RecheckBoundaryProbeTests.cs),
    //  compiled and run at e2b069d8 where all three failed for their stated
    //  reasons, then adopted. The recall's "before" is now part of the ask, so
    //  the two R1 probes construct it; the third's expected outcome follows the
    //  repair brief (retire the recall, say where the radio is) rather than the
    //  probe's own guess (speak 7.030 and refuse it later). Said in the report.
    // ────────────────────────────────────────────────────────────────
    public sealed class RecallOwnershipTests
    {
        // ── Codex's probes, adopted ──

        [Fact]
        public void A_no_active_slice_claim_does_not_survive_losing_the_census()
        {
            var now = new RadioStatusSnapshot { IsConnected = true, RadioModel = "FLEX-6300", SliceCount = 2 };
            var status = RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(), null);
            Assert.Equal("Connected to FLEX-6300, no active slice", status.Text);
            Assert.True(status.StillValid()); // unchanged positive control
            now.SliceCount = 0;
            Assert.Equal("Connected to FLEX-6300", RadioStatusBuilder.SpokenText(now));
            Assert.False(status.StillValid());
        }

        [Fact]
        public void A_superseded_recall_does_not_replace_the_current_status_at_capture()
        {
            // The recall was made at 14.100 USB; a later mode choice to LSB ran
            // before the landing, so the recall is no longer pending.
            var now = At(14_100_000, "LSB");
            var status = RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(),
                new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => false));
            Assert.Equal(RadioStatusBuilder.SpokenText(now), status.Text);
        }

        [Fact]
        public void An_unrelated_tune_before_the_idle_landing_does_not_become_the_recalls_old_state()
        {
            // Memory action at 14.100 USB requests 7.030 CW; the ask carries that
            // action's reading. A frequency-only input moves to 14.200 before
            // the ApplicationIdle landing runs.
            var ask = new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => true);
            var now = At(14_200_000, "USB");
            var status = RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(), ask);
            Assert.DoesNotContain("7.030", status.Text);
            Assert.Equal(RadioStatusBuilder.SpokenText(now), status.Text);
            Assert.True(status.StillValid(), "the radio's own report, still where it is");
            // The recall's mode arriving later is a change to that sentence, not its way there.
            now = At(14_200_000, "CW");
            Assert.False(status.StillValid());
        }

        // ── The action's before, at capture ──

        [Fact]
        public void A_recall_whose_radio_is_still_on_its_way_names_what_it_asked_for()
        {
            // The positive control for the probes above: nothing intervened, so
            // the landing names the recall, and the radio's two answers are allowed.
            var ask = new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => true);
            foreach (var landing in new[] { At(14_100_000, "USB"), At(7_030_000, "USB"), At(14_100_000, "CW") })
            {
                var now = landing;
                var status = RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(), ask);
                Assert.Equal("Listening on 7.030 megahertz, CW, 40 meter band, slice A", status.Text);
                Assert.True(status.StillValid(), RadioStatusBuilder.StatedPart(landing));
                now = At(7_030_000, "CW");
                Assert.True(status.StillValid(), "fully answered");
            }
        }

        [Fact]
        public void A_part_already_answered_at_the_landing_does_not_go_back()
        {
            // The frequency had arrived when the landing read the radio: 14.100
            // again afterwards is a new change, not the recall's way there.
            var ask = new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => true);
            var now = At(7_030_000, "USB");
            var status = RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(), ask);
            Assert.Contains("7.030", status.Text);
            now = At(14_100_000, "USB");
            Assert.False(status.StillValid());
        }

        [Fact]
        public void A_mode_or_slice_change_before_the_landing_retires_the_recall()
        {
            var ask = new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => true);
            RadioStatusSnapshot[] intervened =
            {
                At(14_100_000, "LSB"),       // a mode neither the action's nor asked (seen in the radio's report)
                At(14_100_000, "USB", "B"),  // another slice
            };
            foreach (var landing in intervened)
            {
                var status = RadioStatusBuilder.CaptureSpokenStatus(() => landing.Copy(), ask);
                Assert.Equal(RadioStatusBuilder.SpokenText(landing), status.Text);
            }
        }

        [Fact]
        public void A_recall_ask_is_asked_whether_it_is_pending_before_the_first_sentence()
        {
            int asked = 0;
            var ask = new StatusAsk(7_030_000, "CW", At(14_100_000, "USB"), () => { asked++; return true; });
            var now = At(14_100_000, "USB");
            RadioStatusBuilder.CaptureSpokenStatus(() => now.Copy(), ask);
            Assert.True(asked > 0, "the producer asks before it builds the first sentence");
        }

        // ── The marks: which later choices retire a pending recall ──

        [Fact]
        public void A_pending_recall_owns_the_radio_only_until_a_later_choice()
        {
            var radio = new object();
            var marks = new RecallMarks(radio, 0, ModeChoices: 4, FrequencyRequests: 9);
            var recall = new PendingRecall(7_030_000, "CW", At(14_100_000, "USB"), at: 1000, marks);

            Assert.True(recall.StillOwns(marks with { }, 1000 + PendingRecall.LifetimeMs), "unchanged, inside its lifetime");
            Assert.False(recall.StillOwns(marks, 1001 + PendingRecall.LifetimeMs), "outlived");
            Assert.False(recall.StillOwns(marks with { FrequencyRequests = 10 }, 1000), "a tune");
            Assert.False(recall.StillOwns(marks with { ModeChoices = 5 }, 1000), "a mode choice");
            Assert.False(recall.StillOwns(marks with { ReceiveSlice = 1 }, 1000), "a slice change");
            Assert.False(recall.StillOwns(marks with { Radio = null }, 1000), "a disconnect");
            Assert.False(recall.StillOwns(marks with { Radio = new object() }, 1000), "another radio");
        }

        [Fact]
        public void A_later_choice_retires_the_ask_before_the_landing_and_its_pending_answer_after()
        {
            var radio = new object();
            var marks = new RecallMarks(radio, 0, 4, 9);
            var current = marks;
            long ticks = 1000;
            var recall = new PendingRecall(7_030_000, "CW", At(14_100_000, "USB"), 1000, marks);

            var ask = recall.AskIfStillOwned(() => current, () => ticks);
            Assert.NotNull(ask);
            Assert.Same(recall.Before, ask!.Before);
            Assert.True(ask.StillPending());
            // A tune that leaves no trace in the radio's reported state still counts.
            current = marks with { FrequencyRequests = 10 };
            Assert.False(ask.StillPending());
            Assert.Null(recall.AskIfStillOwned(() => current, () => ticks));
        }

        private static RadioStatusSnapshot At(ulong hz, string mode, string slice = "A")
        {
            var snap = new RadioStatusSnapshot { IsConnected = true, HasActiveSlice = true,
                RadioModel = "FLEX-6300", SliceCount = 1, SliceLetter = slice, Mode = mode };
            RadioStatusBuilder.ApplyAskedFor(snap, hz, null);
            return snap;
        }
    }
}
