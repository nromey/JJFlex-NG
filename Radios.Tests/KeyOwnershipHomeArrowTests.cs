using System.Collections.Generic;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// The ruled Home arrow grammar (#517, ruled 2026-09-20 and 2026-09-21):
    /// bare Up and Down TUNE on every Home field, gain and slice selection
    /// gave the arrows up, a modifier never silently keeps the bare meaning,
    /// and the freed PageUp and PageDown say where pan went.
    /// </summary>
    /// <remarks>
    /// "Down changed something" would pass the original volume defect. Each
    /// test here asserts the exact action, target and signed step, AND zero of
    /// every competing effect.
    /// </remarks>
    public class KeyOwnershipHomeArrowTests
    {
        public static IEnumerable<object[]> NamedFields()
        {
            foreach (var f in NamedHomeFields) yield return new object[] { f };
        }

        private static void AssertTunedOnce(FakeActions fake, KeyDecision decision, string commandId, long stepHz, int direction)
        {
            var action = Assert.IsType<ExecuteAction>(decision);
            Assert.Equal(commandId, action.CommandId);
            Assert.Equal(ActionTargetKind.ActiveSlice, action.Target.Kind);
            Assert.Equal('A', action.Target.SliceLetter);
            Assert.Equal("session-1", action.Target.RadioSessionId);
            Assert.Equal(stepHz, action.SignedStepHz);
            Assert.Equal(direction, action.Direction);
            Assert.True(action.Repeats);
            Assert.Equal(ActionDelivery.Immediate, action.Delivery);
            Assert.Equal(7, action.DecidedAtStep);

            Assert.Equal(new[] { ((char?)'A', (long?)stepHz) }, fake.Tunes);
            Assert.Empty(fake.SliceGainChanges);
            Assert.Empty(fake.SliceSelections);
            Assert.Empty(fake.PanChanges);
            Assert.Empty(fake.PcOutputChanges);
            Assert.Empty(fake.BandChanges);
            Assert.Empty(fake.Handoffs);
            Assert.Empty(fake.Refusals);
        }

        [Theory]
        [MemberData(nameof(NamedFields))]
        public void Bare_Down_tunes_down_one_coarse_step_on_every_named_Home_field(string field)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, Bare(GestureKey.Down)));
            AssertTunedOnce(fake, decision, "home.tune-down", -1000, -1);
        }

        [Theory]
        [MemberData(nameof(NamedFields))]
        public void Bare_Up_is_the_inverse(string field)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, Bare(GestureKey.Up)));
            AssertTunedOnce(fake, decision, "home.tune-up", +1000, +1);
        }

        [Fact]
        public void A_Home_field_nobody_wrote_a_case_for_inherits_tuning()
        {
            // This is what "declared once at Home scope" means, and it is the
            // test that fails if someone quietly writes three per-field bindings.
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.a-field-invented-next-year", Bare(GestureKey.Down)));
            AssertTunedOnce(fake, decision, "home.tune-down", -1000, -1);
        }

        [Fact]
        public void The_Home_root_with_no_fields_constructed_still_tunes()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home", Bare(GestureKey.Up)));
            AssertTunedOnce(fake, decision, "home.tune-up", +1000, +1);
        }

        [Theory]
        [MemberData(nameof(NamedFields))]
        public void Classic_tuning_mode_steps_by_the_selected_digit_whichever_field_has_focus(string field)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, Bare(GestureKey.Down)) with { Mode = OperatingMode.Classic });
            AssertTunedOnce(fake, decision, "home.tune-down", -100, -1);
        }

        [Fact]
        public void The_tuning_step_comes_from_the_tuning_state_and_nothing_else()
        {
            var fake = new FakeActions();
            var s = OnHome("home.slice-operations", Bare(GestureKey.Up)) with
            {
                Tuning = new TuningContext { CoarseStepHz = 5000, FineStepHz = 1, ClassicDigitHz = 10 },
            };
            AssertTunedOnce(fake, fake.Press(s), "home.tune-up", +5000, +1);
        }

        [Fact]
        public void A_held_arrow_repeats_and_two_real_presses_are_two_actions()
        {
            var fake = new FakeActions();
            var down = OnHome("home.slice", Bare(GestureKey.Down));
            fake.Press(down);
            fake.Press(down with { Phase = KeyPhase.Repeat });
            fake.Press(down);   // identical in every field, timestamp included: still a real press
            Assert.Equal(3, fake.Tunes.Count);
        }

        [Fact]
        public void Reader_shaped_down_up_pairs_are_each_a_press_and_the_releases_are_not_commands()
        {
            var fake = new FakeActions();
            var down = OnHome("home.frequency", Bare(GestureKey.Down));
            for (int i = 0; i < 4; i++)
            {
                fake.Press(down);
                fake.Press(down with { Phase = KeyPhase.Up });
            }
            Assert.Equal(4, fake.Tunes.Count);
            Assert.Equal(4, fake.NoCommands);
            Assert.Equal(0, fake.Unbounds);
        }

        // ── A modifier never silently retains the bare binding ──────────

        [Theory]
        [MemberData(nameof(NamedFields))]
        public void Alt_Down_is_band_down_as_it_is_today_and_is_not_tune_down(string field)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, AltChord(GestureKey.Down)));

            var action = Assert.IsType<ExecuteAction>(decision);
            Assert.Equal("band.down", action.CommandId);
            Assert.Equal(CommandValues.BandDown, action.RegistryCommand);
            Assert.Equal(75, (int)action.RegistryCommand!.Value);   // the on-disk identity, unchanged
            Assert.Null(action.SignedStepHz);
            Assert.Equal(new[] { "band.down" }, fake.BandChanges);
            Assert.Empty(fake.Tunes);
            Assert.Empty(fake.SliceGainChanges);
        }

        public static IEnumerable<object[]> ModifiedDowns()
        {
            yield return new object[] { new KeyChord(GestureKey.Down, Control: true) };
            yield return new object[] { new KeyChord(GestureKey.Down, Shift: ShiftSide.Left) };
            yield return new object[] { new KeyChord(GestureKey.Down, Shift: ShiftSide.Right) };
            yield return new object[] { new KeyChord(GestureKey.Down, Shift: ShiftSide.Both) };
            yield return new object[] { new KeyChord(GestureKey.Down, Control: true, Alt: true) };
            yield return new object[] { new KeyChord(GestureKey.Down, Control: true, Shift: ShiftSide.Left) };
            yield return new object[] { new KeyChord(GestureKey.Down, Windows: true) };
            yield return new object[] { new KeyChord(GestureKey.Up, Alt: true, Shift: ShiftSide.Left) };
        }

        [Theory]
        [MemberData(nameof(ModifiedDowns))]
        public void A_modified_arrow_the_ruled_map_does_not_declare_is_unbound_never_a_tune(KeyChord chord)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice-operations", chord));

            var unbound = Assert.IsType<Unbound>(decision);
            Assert.Equal(UnboundContextKind.CommandSurface, unbound.ContextKind);
            Assert.Equal("home.slice-operations", unbound.ContextId);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_fine_step_gesture_declared_at_Home_scope_takes_the_fine_step_and_needs_its_exact_Shift()
        {
            // The design RECOMMENDS Shift+arrows as the fine step at Home scope.
            // That is not one of Noel's rulings, so it is not in the ruled map;
            // this pins that the engine can express it when it is ruled.
            var declarations = new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                new GestureDeclaration
                {
                    CommandId = "test.tune-down-fine",
                    Chord = new ChordPattern(GestureKey.Down, Shift: ShiftRequirement.Either),
                    Scope = KeyScope.Radio,
                    ContextId = "home",
                    Reach = InvocationReach.ContextAndDescendants,
                    Requires = CommandRequirements.Radio | CommandRequirements.ActiveSlice,
                    Target = TargetRule.ActiveSlice,
                    Direction = -1,
                    Step = StepSource.TuningFine,
                    Fine = true,
                    Repeats = true,
                },
            };
            var set = GestureDeclarationSet.Create(declarations, RuledHomeKeyMap.LayerTargets, RuledHomeKeyMap.ContextParents);

            var fine = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.slice", Shifted(GestureKey.Down)), set));
            Assert.Equal("test.tune-down-fine", fine.CommandId);
            Assert.Equal(-10, fine.SignedStepHz);

            var coarse = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(OnHome("home.slice", Bare(GestureKey.Down)), set));
            Assert.Equal("home.tune-down", coarse.CommandId);
            Assert.Equal(-1000, coarse.SignedStepHz);
        }

        // ── The freed paging keys ───────────────────────────────────────

        [Theory]
        [InlineData("home.frequency", GestureKey.PageDown)]
        [InlineData("home.slice", GestureKey.PageDown)]
        [InlineData("home.slice-operations", GestureKey.PageDown)]
        [InlineData("home.slice-operations", GestureKey.PageUp)]
        [InlineData("home.a-field-invented-next-year", GestureKey.PageUp)]
        public void PageUp_and_PageDown_on_Home_say_where_pan_went_and_do_nothing_else(string field, GestureKey key)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, Bare(key)));

            var refusal = Assert.IsType<Refuse>(decision);
            Assert.Equal(RefusalReason.Relocated, refusal.Reason);
            Assert.NotNull(refusal.Relocation);
            Assert.Equal("pan-moved-to-audio-layer", refusal.Relocation!.Id);
            Assert.Equal("audio", refusal.Relocation.LayerId);
            Assert.Equal("pan", refusal.Relocation.LayerTargetId);

            Assert.Empty(fake.PanChanges);          // no pan
            Assert.Empty(fake.OtherActions);        // no focus jump, no anything
            Assert.Empty(fake.Handoffs);            // and not passed on to become one
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void The_relocation_answer_does_not_depend_on_a_radio()
        {
            var decision = KeyArbiter.Decide(
                OnHome("home.slice-operations", Bare(GestureKey.PageDown)) with { Radio = RadioFacts.Disconnected }, Ruled);
            Assert.Equal(RefusalReason.Relocated, Assert.IsType<Refuse>(decision).Reason);
        }

        [Theory]
        [InlineData(SurfaceRole.TextEditor, KeyOwnerKind.TextEditor)]
        [InlineData(SurfaceRole.StandardControl, KeyOwnerKind.StandardControl)]
        public void PageDown_keeps_its_ordinary_meaning_in_an_editor_and_a_paging_control(SurfaceRole role, KeyOwnerKind owner)
        {
            var fake = new FakeActions();
            var decision = fake.Press(InDialog(role, Bare(GestureKey.PageDown)));
            Assert.Equal(owner, Assert.IsType<HandToOwner>(decision).Owner);
            Assert.Empty(fake.Refusals);
        }

        // ── Availability: the declaration stays the owner ───────────────

        [Theory]
        [InlineData("home.frequency")]
        [InlineData("home")]    // disconnected, no field list at all: a positive case, not a skipped one
        public void Without_a_radio_tuning_is_refused_for_that_reason(string field)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome(field, Bare(GestureKey.Down)) with { Radio = RadioFacts.Disconnected });

            var refusal = Assert.IsType<Refuse>(decision);
            Assert.Equal(RefusalReason.NoRadio, refusal.Reason);
            Assert.Equal("home.tune-down", refusal.CommandId);
            Assert.True(fake.RadioUntouched);
            Assert.Equal(0, fake.Unbounds);
        }

        [Fact]
        public void An_unknown_letter_without_a_radio_is_still_just_unbound()
        {
            // Do not claim that connecting would enable an arbitrary key.
            var decision = KeyArbiter.Decide(
                OnHome("home", Bare(GestureKey.S)) with { Radio = RadioFacts.Disconnected }, Ruled);
            Assert.IsType<Unbound>(decision);
        }

        [Fact]
        public void With_no_active_slice_tuning_is_refused_as_a_missing_slice()
        {
            var decision = KeyArbiter.Decide(
                OnHome("home.slice", Bare(GestureKey.Up)) with { Radio = Radio(active: null, slices: "") }, Ruled);
            Assert.Equal(RefusalReason.MissingSlice, Assert.IsType<Refuse>(decision).Reason);
        }

        [Fact]
        public void A_bound_command_with_nothing_wired_to_it_says_so_and_never_says_unbound()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice", Bare(GestureKey.Up)) with
            {
                CommandsWithoutExecutor = new[] { "home.tune-up" },
            });
            var refusal = Assert.IsType<Refuse>(decision);
            Assert.Equal(RefusalReason.NoExecutor, refusal.Reason);
            Assert.Equal("home.tune-up", refusal.CommandId);
            Assert.Equal(0, fake.Unbounds);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void KeyScope_still_governs_mode_so_Logging_mode_does_not_tune()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Mode = OperatingMode.Logging });
            Assert.IsType<Unbound>(decision);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void An_auto_repeat_of_a_command_that_does_not_repeat_is_swallowed_not_re_run()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", AltChord(GestureKey.Down)) with { Phase = KeyPhase.Repeat });
            Assert.True(Assert.IsType<NoCommandEvent>(decision).Consume);
            Assert.Empty(fake.BandChanges);
        }

        [Fact]
        public void An_unknown_Home_letter_is_unbound_in_that_field_and_nothing_else_happens()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice-operations", Bare(GestureKey.S)));
            var unbound = Assert.IsType<Unbound>(decision);
            Assert.Equal(UnboundContextKind.CommandSurface, unbound.ContextKind);
            Assert.Equal("home.slice-operations", unbound.ContextId);
            Assert.Equal(8, unbound.DecidedAtStep);
            Assert.True(fake.RadioUntouched);
            Assert.Empty(fake.OtherActions);
        }
    }
}
