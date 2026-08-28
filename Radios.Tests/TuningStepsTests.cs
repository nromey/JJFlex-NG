using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The step ladder behind Modern tuning's new sizing keys (#302) — walked,
    /// spoken, and pinned to the three surfaces that must agree about it.
    /// </summary>
    /// <remarks>
    /// Everything here is pure lookup and arithmetic, so no window is ever
    /// constructed. The two source scans at the bottom are the exception and
    /// they earn it: a key binding that compiles is not a key binding that
    /// fires, and both of them guard a route that would ship dead and silent.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class TuningStepsTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(TuningStepsTests));

        public TuningStepsTests()
        {
            Lexicon.Load(Lexicon.Partitions);
        }

        public void Dispose() => _scope.Dispose();

        // ────────────────────────────────────────────────────────────────
        //  The ladders themselves
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Both_ladders_are_short_and_ordered_smallest_first()
        {
            // "The four or five an operator actually uses" is the design, and
            // a ladder that quietly grows to twenty is no longer a ladder —
            // it is the thing the picker exists for.
            Assert.InRange(TuningSteps.Coarse.Count, 4, 6);
            Assert.InRange(TuningSteps.Fine.Count, 4, 6);

            Assert.Equal(TuningSteps.Coarse.Select(c => c.Hz).OrderBy(hz => hz),
                TuningSteps.Coarse.Select(c => c.Hz));
            Assert.Equal(TuningSteps.Fine.Select(c => c.Hz).OrderBy(hz => hz),
                TuningSteps.Fine.Select(c => c.Hz));
        }

        /// <summary>
        /// The three coarse and three fine values the Settings dialog offered
        /// before this ladder existed must all still be reachable. Dropping
        /// one would make an operator's saved step unselectable, and a combo
        /// with no matching item falls back to its first row — so the loss
        /// would arrive as a silent reset, not an error.
        /// </summary>
        [Theory]
        [InlineData(1000)]
        [InlineData(2000)]
        [InlineData(5000)]
        public void Every_coarse_step_settings_used_to_offer_is_still_on_the_ladder(int hz)
            => Assert.Contains(TuningSteps.Coarse, c => c.Hz == hz);

        [Theory]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(100)]
        public void Every_fine_step_settings_used_to_offer_is_still_on_the_ladder(int hz)
            => Assert.Contains(TuningSteps.Fine, c => c.Hz == hz);

        [Fact]
        public void The_shipped_defaults_are_on_their_ladders()
        {
            // FreqOutHandlers starts at coarse 5 kHz and fine 100 Hz. If
            // either fell off its ladder the first press of a sizing key
            // would jump somewhere the operator never chose.
            Assert.Contains(TuningSteps.Coarse, c => c.Hz == 5000);
            Assert.Contains(TuningSteps.Fine, c => c.Hz == 100);
        }

        [Fact]
        public void Every_label_key_resolves_to_real_text()
        {
            foreach (var choice in TuningSteps.Coarse.Concat(TuningSteps.Fine))
            {
                Assert.True(Lexicon.Contains(choice.LabelKey),
                    $"no lexicon text for {choice.LabelKey} — the picker and the Settings "
                    + "combo would show the key itself where a step size belongs");
                Assert.NotEqual(choice.LabelKey, TuningSteps.LabelFor(choice));
            }

            // Positive control: a negative result above also claims Contains
            // would have SEEN a missing key, so prove it reports one.
            Assert.False(Lexicon.Contains("settings.tuning.step_that_does_not_exist"));
        }

        // ────────────────────────────────────────────────────────────────
        //  Walking it
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Up_and_down_walk_one_rung_at_a_time()
        {
            var up = TuningSteps.Step(TuningSteps.Coarse, 1000, 1);
            Assert.Equal(2000, up.Hz);
            Assert.False(up.AtLimit);

            var down = TuningSteps.Step(TuningSteps.Coarse, 5000, -1);
            Assert.Equal(2000, down.Hz);
            Assert.False(down.AtLimit);
        }

        [Fact]
        public void A_full_walk_up_visits_every_rung_in_order_and_stops()
        {
            var expected = TuningSteps.Fine.Select(c => c.Hz).ToList();
            var visited = new System.Collections.Generic.List<int> { expected[0] };

            int current = expected[0];
            for (int guard = 0; guard < 50; guard++)
            {
                var move = TuningSteps.Step(TuningSteps.Fine, current, 1);
                if (move.AtLimit) break;
                current = move.Hz;
                visited.Add(current);
            }

            Assert.Equal(expected, visited);
        }

        /// <summary>
        /// The ladder does NOT wrap, in either direction, and the refusal is
        /// reported rather than silent. Wrapping would take an operator from
        /// the largest coarse step to the smallest on one keystroke while
        /// they were listening to the band rather than to the app; a silent
        /// refusal is indistinguishable from a key that is not bound at all,
        /// which is the exact defect #302 was raised about one level up.
        /// </summary>
        [Fact]
        public void The_top_of_the_ladder_is_a_wall_not_a_wrap()
        {
            int largest = TuningSteps.Coarse.Max(c => c.Hz);
            var move = TuningSteps.Step(TuningSteps.Coarse, largest, 1);

            Assert.Equal(largest, move.Hz);
            Assert.True(move.AtLimit);
        }

        [Fact]
        public void The_bottom_of_the_ladder_is_a_wall_not_a_wrap()
        {
            int smallest = TuningSteps.Fine.Min(c => c.Hz);
            var move = TuningSteps.Step(TuningSteps.Fine, smallest, -1);

            Assert.Equal(smallest, move.Hz);
            Assert.True(move.AtLimit);
        }

        [Fact]
        public void An_off_ladder_value_snaps_into_the_ladder_rather_than_stranding()
        {
            // 3 kHz is on nobody's list. Going up should reach 5 kHz, going
            // down 2 kHz — never "no move", which would leave an operator
            // stuck on a value they cannot get off.
            Assert.Equal(5000, TuningSteps.Step(TuningSteps.Coarse, 3000, 1).Hz);
            Assert.Equal(2000, TuningSteps.Step(TuningSteps.Coarse, 3000, -1).Hz);
            Assert.False(TuningSteps.Step(TuningSteps.Coarse, 3000, 1).AtLimit);
        }

        [Fact]
        public void A_value_past_the_top_can_still_come_back_down()
        {
            int largest = TuningSteps.Coarse.Max(c => c.Hz);
            int beyond = largest * 4;

            Assert.True(TuningSteps.Step(TuningSteps.Coarse, beyond, 1).AtLimit);
            Assert.Equal(largest, TuningSteps.Step(TuningSteps.Coarse, beyond, -1).Hz);
        }

        [Fact]
        public void A_direction_of_zero_moves_nothing()
        {
            var move = TuningSteps.Step(TuningSteps.Coarse, 1000, 0);
            Assert.Equal(1000, move.Hz);
            Assert.True(move.AtLimit);
        }

        // ────────────────────────────────────────────────────────────────
        //  What a picker shows
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void A_picker_shows_the_plain_ladder_when_the_current_value_is_on_it()
        {
            Assert.Same(TuningSteps.Coarse,
                TuningSteps.ChoicesIncluding(TuningSteps.Coarse, 5000));
        }

        [Fact]
        public void A_picker_never_omits_the_value_currently_in_force()
        {
            var widened = TuningSteps.ChoicesIncluding(TuningSteps.Coarse, 3000);

            Assert.Equal(TuningSteps.Coarse.Count + 1, widened.Count);
            Assert.Contains(widened, c => c.Hz == 3000);
            Assert.Equal(widened.Select(c => c.Hz).OrderBy(hz => hz), widened.Select(c => c.Hz));

            // And it must not render as a blank row just because it has no
            // label of its own.
            var stranger = widened.First(c => c.Hz == 3000);
            Assert.False(string.IsNullOrWhiteSpace(TuningSteps.LabelFor(stranger)));
        }

        // ────────────────────────────────────────────────────────────────
        //  Saying it
        // ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1, "1 hertz")]
        [InlineData(500, "500 hertz")]
        [InlineData(1000, "1 kilohertz")]
        [InlineData(10000, "10 kilohertz")]
        [InlineData(1000000, "1 megahertz")]
        public void A_step_is_spoken_in_the_largest_unit_that_divides_it(int hz, string expected)
            => Assert.Equal(expected, TuningSteps.FormatForSpeech(hz));

        /// <summary>
        /// A value that is not an exact multiple is spoken in hertz rather
        /// than truncated into the larger unit. The earlier version divided by
        /// 1000 and dropped the remainder, so 2500 Hz announced itself as
        /// "2 kilohertz" — a wrong number, said confidently. Nothing on the
        /// ladder can produce one; #199's adaptive step is exactly the thing
        /// that would.
        /// </summary>
        [Theory]
        [InlineData(2500, "2500 hertz")]
        [InlineData(1000500, "1000500 hertz")]
        // Still the LARGEST unit that divides it exactly, which is the rule —
        // 1.5 MHz is a whole number of kilohertz even though it is not a whole
        // number of megahertz, and "1500 kilohertz" is true.
        [InlineData(1500000, "1500 kilohertz")]
        public void A_step_that_does_not_divide_cleanly_is_never_rounded_into_a_lie(
            int hz, string expected)
            => Assert.Equal(expected, TuningSteps.FormatForSpeech(hz));

        // ────────────────────────────────────────────────────────────────
        //  What the operator actually hears — the review surface
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The four sentences a sizing keypress can produce, assembled the way
        /// FreqOutHandlers.WalkStep assembles them.
        /// </summary>
        /// <remarks>
        /// <para>These are read aloud on every press of a key an operator may
        /// hold down, so they have to be short and they have to say which of
        /// the two steps moved. A failing diff here is a wording change Noel
        /// has not heard yet.</para>
        /// <para>WalkStep picks its key with a ternary and passes the result
        /// as a variable, so LexiconKeyCoverageTests — which reads first
        /// arguments out of source — cannot verify these six keys. This test
        /// is what verifies them, and the source scan below is what keeps the
        /// two lists the same six.</para>
        /// </remarks>
        [Fact]
        public void The_sizing_keys_say_which_step_moved_and_where_it_stopped()
        {
            Assert.Equal("Coarse step 2 kilohertz",
                Lexicon.Get("settings.tuning.coarse_step_now", ("step", "2 kilohertz")));
            Assert.Equal("Coarse step 10 kilohertz, largest",
                Lexicon.Get("settings.tuning.coarse_step_largest", ("step", "10 kilohertz")));
            Assert.Equal("Coarse step 500 hertz, smallest",
                Lexicon.Get("settings.tuning.coarse_step_smallest", ("step", "500 hertz")));

            Assert.Equal("Fine step 10 hertz",
                Lexicon.Get("settings.tuning.fine_step_now", ("step", "10 hertz")));
            Assert.Equal("Fine step 100 hertz, largest",
                Lexicon.Get("settings.tuning.fine_step_largest", ("step", "100 hertz")));
            Assert.Equal("Fine step 1 hertz, smallest",
                Lexicon.Get("settings.tuning.fine_step_smallest", ("step", "1 hertz")));

            // The picker's own confirmation reuses the sentence Shift+S has
            // always spoken, so setting both and asking what they are come
            // back in the same words.
            Assert.Equal("Coarse 2 kilohertz, fine 10 hertz",
                Lexicon.Get("settings.tuning.steps_coarse_fine",
                    ("coarse", TuningSteps.FormatForSpeech(2000)),
                    ("fine", TuningSteps.FormatForSpeech(10))));
        }

        // ────────────────────────────────────────────────────────────────
        //  The routes that would otherwise ship dead
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shift+Left / Shift+Right reach the Modern frequency handler only
        /// because FrequencyDisplay OFFERS the shifted pair to the field
        /// before navigating with it. Delete that and the chords compile,
        /// document themselves in Ctrl+F1 and the keyboard reference, and do
        /// nothing whatsoever — the operator hears the cursor move and cannot
        /// tell a dead binding from an unsupported modifier. This is the same
        /// shape as the Alt+L binding that shipped completely dead on
        /// 2026-08-13.
        /// </summary>
        [Fact]
        public void The_frequency_display_offers_the_shifted_cursor_pair_to_the_field_first()
        {
            string source = LeaderSourceScan.ReadSource(
                Path.Combine("JJFlexWpf", "Controls", "FrequencyDisplay.xaml.cs"));

            int branch = source.IndexOf("if (e.Key == Key.Left || e.Key == Key.Right)",
                StringComparison.Ordinal);
            Assert.True(branch >= 0, "the Left/Right navigation branch has moved or been renamed");

            // The whole branch, up to the Home branch that follows it.
            int end = source.IndexOf("if (e.Key == Key.Home)", branch, StringComparison.Ordinal);
            Assert.True(end > branch, "could not find the end of the Left/Right branch");
            string body = source.Substring(branch, end - branch);

            Assert.Contains("ModifierKeys.Shift", body, StringComparison.Ordinal);
            Assert.Contains("FieldKeyDown?.Invoke", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// And the handler on the other end has to claim them. AdjustFreqModern
        /// must handle both horizontal keys and route them to the coarse and
        /// fine walks — documented-but-unhandled is how #274's deleted 'C' key
        /// got taught for five sprints.
        /// </summary>
        [Fact]
        public void Modern_tuning_handles_both_sizing_pairs()
        {
            string source = LeaderSourceScan.ReadSource(
                Path.Combine("JJFlexWpf", "FreqOutHandlers.cs"));

            int start = source.IndexOf("public void AdjustFreqModern", StringComparison.Ordinal);
            Assert.True(start >= 0, "AdjustFreqModern has moved or been renamed");
            int end = source.IndexOf("public void SpeakCurrentStepFromMenu", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of AdjustFreqModern");
            string body = source.Substring(start, end - start);

            Assert.Contains("case Key.Left:", body, StringComparison.Ordinal);
            Assert.Contains("case Key.Right:", body, StringComparison.Ordinal);
            Assert.Contains("WalkStep(coarse: true", body, StringComparison.Ordinal);
            Assert.Contains("WalkStep(coarse: false", body, StringComparison.Ordinal);
            Assert.Contains("ShowTuningStepsDialog", body, StringComparison.Ordinal);

            // Positive control: the extractor must be reading the real method
            // body, not an empty string that trivially contains nothing.
            Assert.Contains("TuneFreq", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// WalkStep must speak using exactly the six keys the review-surface
        /// test above proof-reads. It selects them with a ternary and passes
        /// the result as a variable, which is invisible to the static lexicon
        /// sweep — so if a seventh key appeared, or one of these changed, the
        /// only thing standing between it and a key name spoken aloud to an
        /// operator is this pairing.
        /// </summary>
        /// <summary>The body of WalkStep, for the scans below to read.</summary>
        private static string WalkStepBody()
        {
            string source = LeaderSourceScan.ReadSource(
                Path.Combine("JJFlexWpf", "FreqOutHandlers.cs"));

            int start = source.IndexOf("public void WalkStep", StringComparison.Ordinal);
            Assert.True(start >= 0, "WalkStep has moved or been renamed");
            int end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
            Assert.True(end > start, "could not find the end of WalkStep");
            return source.Substring(start, end - start);
        }

        [Fact]
        public void The_step_walk_speaks_with_the_keys_that_have_been_proof_read()
        {
            string body = WalkStepBody();

            foreach (string key in new[]
            {
                "settings.tuning.coarse_step_now",
                "settings.tuning.coarse_step_largest",
                "settings.tuning.coarse_step_smallest",
                "settings.tuning.fine_step_now",
                "settings.tuning.fine_step_largest",
                "settings.tuning.fine_step_smallest",
            })
            {
                Assert.Contains("\"" + key + "\"", body, StringComparison.Ordinal);
            }

            // Every quoted lexicon key in the method must be one of the six.
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(body, "\"(settings\\.[^\"]+)\""))
            {
                Assert.StartsWith("settings.tuning.", m.Groups[1].Value, StringComparison.Ordinal);
                Assert.True(Lexicon.Contains(m.Groups[1].Value),
                    $"WalkStep speaks {m.Groups[1].Value}, which has no lexicon text — an "
                    + "operator would hear the key name read out instead of a step size");
            }
        }

        /// <summary>
        /// A step key is a SWEPT VALUE, not a query, and it has to speak like
        /// one: Latest, with a coalesce key, so holding an arrow says where it
        /// landed instead of every rung on the way.
        /// </summary>
        /// <remarks>
        /// <para>Coarse and fine must not share a coalesce key. One shared key
        /// lets a fine announcement replace a pending coarse one while an
        /// operator alternates between them, and they hear half of what they
        /// changed — a bug with no symptom except silence.</para>
        /// <para>And repeatWhileHeld must stay unset. ValueFieldControl
        /// settled this for every swept value with an end stop: arriving at an
        /// end says so once, in words. Setting it here would also cut across
        /// the anti-clip gap that turns a held key into a readable cadence
        /// rather than a stutter.</para>
        /// </remarks>
        [Fact]
        public void The_step_walk_speaks_as_a_sweep_with_coarse_and_fine_kept_apart()
        {
            string body = WalkStepBody();

            Assert.Contains("SpeechIntent.Latest", body, StringComparison.Ordinal);
            Assert.Contains("coalesceKey:", body, StringComparison.Ordinal);
            Assert.Contains("\"tuning-step:coarse\"", body, StringComparison.Ordinal);
            Assert.Contains("\"tuning-step:fine\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("repeatWhileHeld", body, StringComparison.Ordinal);

            // Positive control: this scan must be reading real code, or every
            // DoesNotContain above passes for free.
            Assert.Contains("TuningSteps.Step(", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The picker's rows must CARRY their step, not be matched to one by
        /// position in a parallel list.
        /// </summary>
        /// <remarks>
        /// Two collections holding one fact, kept in step only by both being
        /// appended in the same order, is the shape behind a whole family of
        /// quiet defects — including the one the CW track found on 2026-08-27,
        /// where a flush emptied a queue without decrementing the count of
        /// what was outstanding. Here the failure would be a step the operator
        /// did not choose being applied without a word said about it: no
        /// exception, no failed build, no test that would notice.
        /// </remarks>
        [Fact]
        public void The_picker_reads_the_chosen_step_off_the_row_not_out_of_a_parallel_list()
        {
            string source = LeaderSourceScan.ReadSource(
                Path.Combine("JJFlexWpf", "Dialogs", "TuningStepsDialog.xaml.cs"));

            Assert.Contains("SelectedItem is StepRow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectedIndex]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[CoarseList.SelectedIndex", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[FineList.SelectedIndex", source, StringComparison.Ordinal);

            // Positive control: the file really was read.
            Assert.Contains("ChoicesIncluding", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The Settings dialog must not keep its own step list. It did until
        /// #302, and the moment a key could size the step past the ends of
        /// that private list the two disagreed with teeth: a combo with no
        /// matching item selects its first row, so opening Settings on a
        /// 10 kHz step and pressing OK would silently reset it to 1 kHz.
        /// </summary>
        [Fact]
        public void The_settings_dialog_reads_the_shared_ladder_instead_of_its_own()
        {
            string source = LeaderSourceScan.ReadSource(
                Path.Combine("JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));

            Assert.Contains("TuningSteps.ChoicesIncluding(TuningSteps.Coarse",
                source, StringComparison.Ordinal);
            Assert.Contains("TuningSteps.ChoicesIncluding(TuningSteps.Fine",
                source, StringComparison.Ordinal);
            Assert.DoesNotContain("CoarseStepOptions =", source, StringComparison.Ordinal);
            Assert.DoesNotContain("FineStepOptions =", source, StringComparison.Ordinal);
        }
    }
}
