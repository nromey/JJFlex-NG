using System.Collections.Generic;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// Step four: an explicit keyboard layer owns its declared keys. Inside a
    /// layer the arrows act on THAT LAYER's value or target (#517), and
    /// Shift plus a slice letter is the slice jump inside the JJ key's tier
    /// and inside every layer (#515, tier two) — never outside one.
    /// </summary>
    public class KeyOwnershipLayerTests
    {
        // ── The audio layer: V, O and P are three different things ──────

        [Fact]
        public void V_then_Down_lowers_the_active_slices_gain_and_nothing_else()
        {
            var fake = new FakeActions();

            var select = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.frequency", Bare(GestureKey.V)) with { Layer = AudioLayer(null) }));
            Assert.Equal("layer.select-target", select.CommandId);
            Assert.Equal("slice-volume", select.Target.LayerTargetId);
            Assert.Equal(LayerEffect.SelectTarget, select.LayerEffect);

            var adjust = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = AudioLayer("slice-volume") }));
            Assert.Equal("layer.value-decrease", adjust.CommandId);
            Assert.Equal(ActionTargetKind.LayerValue, adjust.Target.Kind);
            Assert.Equal("audio", adjust.Target.LayerId);
            Assert.Equal("slice-volume", adjust.Target.LayerTargetId);
            Assert.Equal('A', adjust.Target.SliceLetter);
            Assert.Equal(-1, adjust.Direction);
            Assert.True(adjust.Repeats);
            Assert.Equal(4, adjust.DecidedAtStep);

            Assert.Equal(new[] { ((char?)'A', -1) }, fake.SliceGainChanges);
            Assert.Empty(fake.PcOutputChanges);
            Assert.Empty(fake.PanChanges);
            Assert.Empty(fake.Tunes);               // the Home arrow did NOT also tune
            Assert.Empty(fake.SliceSelections);
        }

        [Fact]
        public void O_then_Down_lowers_PC_output_which_is_not_slice_gain()
        {
            var fake = new FakeActions();

            var select = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.slice-operations", Bare(GestureKey.O)) with { Layer = AudioLayer("slice-volume") }));
            Assert.Equal("pc-output", select.Target.LayerTargetId);

            var adjust = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.slice-operations", Bare(GestureKey.Down)) with { Layer = AudioLayer("pc-output") }));
            Assert.Equal("pc-output", adjust.Target.LayerTargetId);
            Assert.Null(adjust.Target.SliceLetter);     // PC output belongs to no slice

            Assert.Equal(new[] { -1 }, fake.PcOutputChanges);
            Assert.Empty(fake.SliceGainChanges);        // O must never stand in for slice gain
            Assert.Empty(fake.PanChanges);
            Assert.Empty(fake.Tunes);
        }

        [Theory]
        [InlineData(GestureKey.Right, +1)]
        [InlineData(GestureKey.Left, -1)]
        [InlineData(GestureKey.Up, +1)]
        [InlineData(GestureKey.Down, -1)]
        public void P_then_any_arrow_moves_the_active_slices_pan(GestureKey arrow, int direction)
        {
            var fake = new FakeActions();

            var select = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.slice", Bare(GestureKey.P)) with { Layer = AudioLayer(null) }));
            Assert.Equal("pan", select.Target.LayerTargetId);

            fake.Press(OnHome("home.slice", Bare(arrow)) with { Layer = AudioLayer("pan") });
            Assert.Equal(new[] { ((char?)'A', direction) }, fake.PanChanges);
            Assert.Empty(fake.SliceGainChanges);
            Assert.Empty(fake.PcOutputChanges);
            Assert.Empty(fake.Tunes);
        }

        [Fact]
        public void The_wrong_axis_for_a_target_is_the_layers_answer_and_changes_nothing()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice", Bare(GestureKey.Right)) with { Layer = AudioLayer("slice-volume") });
            var unbound = Assert.IsType<Unbound>(decision);
            Assert.True(unbound.WrongAxis);
            Assert.Equal(UnboundContextKind.Layer, unbound.ContextKind);
            Assert.Equal("audio", unbound.ContextId);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void An_arrow_before_any_target_is_chosen_does_not_fall_through_to_tuning()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = AudioLayer(null) });
            Assert.Equal(UnboundContextKind.Layer, Assert.IsType<Unbound>(decision).ContextKind);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_key_the_layer_does_not_declare_gets_the_layers_answer_not_something_beneath_it()
        {
            var fake = new FakeActions();
            // PageDown would be the relocation refusal on Home, and text in an editor.
            var onHome = fake.Press(OnHome("home.slice", Bare(GestureKey.PageDown)) with { Layer = AudioLayer("pan") });
            var inEditor = fake.Press(InDialog(SurfaceRole.TextEditor, Bare(GestureKey.T)) with { Layer = AudioLayer("pan", DialogWindow) });

            foreach (var decision in new[] { onHome, inEditor })
            {
                var unbound = Assert.IsType<Unbound>(decision);
                Assert.Equal(UnboundContextKind.Layer, unbound.ContextKind);
                Assert.Equal("audio", unbound.ContextId);
                Assert.Equal(4, unbound.DecidedAtStep);
            }
            Assert.Empty(fake.Handoffs);    // T was NOT also typed
            Assert.Empty(fake.Refusals);
        }

        // ── Tier two: Shift+letter is the slice jump, inside the JJ key ──

        [Fact]
        public void JJ_key_then_Shift_C_selects_slice_C_from_a_Home_field()
        {
            var fake = new FakeActions();

            var arm = Assert.IsType<ExecuteAction>(fake.Press(OnHome("home.slice", Ctrl(GestureKey.J))));
            Assert.Equal("jjkey.arm", arm.CommandId);
            Assert.Equal(LayerEffect.ArmJJKey, arm.LayerEffect);
            Assert.Equal(5, arm.DecidedAtStep);

            var jump = Assert.IsType<ExecuteAction>(fake.Press(
                OnHome("home.slice", Shifted(GestureKey.C)) with { Layer = JJKeyArmed() }));
            Assert.Equal("slice.jump", jump.CommandId);
            Assert.Equal(ActionTargetKind.NamedSlice, jump.Target.Kind);
            Assert.Equal('C', jump.Target.SliceLetter);

            Assert.Equal(new char?[] { 'C' }, fake.SliceSelections);
            Assert.Empty(fake.Tunes);
            Assert.Empty(fake.SliceGainChanges);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void JJ_key_then_Shift_C_from_a_dialog_editor_selects_slice_C_and_types_nothing(bool modal)
        {
            var fake = new FakeActions();

            var arm = Assert.IsType<ExecuteAction>(fake.Press(InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.J), modal)));
            Assert.Equal("jjkey.arm", arm.CommandId);

            var jump = Assert.IsType<ExecuteAction>(fake.Press(
                InDialog(SurfaceRole.TextEditor, Shifted(GestureKey.C, ShiftSide.Right), modal) with { Layer = JJKeyArmed(DialogWindow) }));
            Assert.Equal("slice.jump", jump.CommandId);
            Assert.Equal('C', jump.Target.SliceLetter);
            Assert.Equal(DialogWindow, jump.Target.WindowId);   // the jump moves no focus out of the dialog

            Assert.Equal(new char?[] { 'C' }, fake.SliceSelections);
            Assert.Empty(fake.Handoffs);    // no C reached the editor
        }

        [Theory]
        [InlineData(SurfaceRole.TextEditor, KeyOwnerKind.TextEditor)]
        [InlineData(SurfaceRole.StandardControl, KeyOwnerKind.StandardControl)]
        public void A_bare_Shift_letter_outside_any_layer_is_an_uppercase_letter(SurfaceRole role, KeyOwnerKind owner)
        {
            var fake = new FakeActions();
            foreach (var letter in new[] { GestureKey.A, GestureKey.C, GestureKey.H })
                Assert.Equal(owner, Assert.IsType<HandToOwner>(fake.Press(InDialog(role, Shifted(letter)))).Owner);
            Assert.Empty(fake.SliceSelections);
        }

        [Fact]
        public void A_bare_Shift_letter_on_a_Home_field_is_not_a_slice_jump_either()
        {
            // "From anywhere" means from inside the JJ key's tier, from anywhere.
            var fake = new FakeActions();
            Assert.IsType<Unbound>(fake.Press(OnHome("home.slice", Shifted(GestureKey.C))));
            Assert.Empty(fake.SliceSelections);
        }

        [Fact]
        public void A_missing_slice_is_refused_and_no_other_slice_is_chosen_in_its_place()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice", Shifted(GestureKey.C)) with
            {
                Layer = JJKeyArmed(),
                Radio = Radio(active: 'A', slices: "AB"),
            });
            var refusal = Assert.IsType<Refuse>(decision);
            Assert.Equal(RefusalReason.MissingSlice, refusal.Reason);
            Assert.Equal("slice.jump", refusal.CommandId);
            Assert.Equal('C', refusal.Target!.SliceLetter);
            Assert.Empty(fake.SliceSelections);
        }

        [Fact]
        public void The_slice_letters_stop_at_H()
        {
            var decision = KeyArbiter.Decide(OnHome("home.slice", Shifted(GestureKey.I)) with { Layer = JJKeyArmed() }, Ruled);
            Assert.IsType<Unbound>(decision);
        }

        [Fact]
        public void In_the_audio_layer_H_Shift_H_Ctrl_H_and_question_mark_are_four_different_things()
        {
            InputSnapshot In(KeyChord chord) => OnHome("home.frequency", chord) with
            {
                Layer = AudioLayer("pan"),
                Radio = Radio(active: 'A', slices: "ABCDEFGH"),
            };

            var help = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(In(Bare(GestureKey.H)), Ruled));
            Assert.Equal("layer.help-list", help.CommandId);

            var jump = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(In(Shifted(GestureKey.H)), Ruled));
            Assert.Equal("slice.jump", jump.CommandId);
            Assert.Equal('H', jump.Target.SliceLetter);

            var headphone = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(In(Ctrl(GestureKey.H)), Ruled));
            Assert.Equal("layer.select-target", headphone.CommandId);
            Assert.Equal("headphone", headphone.Target.LayerTargetId);

            foreach (var question in new[] { Shifted(GestureKey.Slash), Bare(GestureKey.Slash) })
            {
                var explorer = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(In(question), Ruled));
                Assert.Equal("layer.help-explorer", explorer.CommandId);
                Assert.NotEqual("layer.help-list", explorer.CommandId);   // "opened something" is not the assertion
            }
        }

        [Fact]
        public void The_armed_JJ_key_opens_the_audio_layer_on_A_and_answers_H_and_question_mark_itself()
        {
            InputSnapshot Armed(KeyChord chord) => OnHome("home.frequency", chord) with { Layer = JJKeyArmed() };

            var open = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(Armed(Bare(GestureKey.A)), Ruled));
            Assert.Equal("jjkey.open-audio-layer", open.CommandId);
            Assert.Equal(LayerEffect.OpenLayer, open.LayerEffect);
            Assert.Equal("audio", open.OpensLayerId);

            Assert.Equal("layer.help-list", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(Armed(Bare(GestureKey.H)), Ruled)).CommandId);
            Assert.Equal("layer.help-explorer", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(Armed(Shifted(GestureKey.Slash)), Ruled)).CommandId);
        }

        // ── Layer lifetime ──────────────────────────────────────────────

        [Fact]
        public void A_suspended_layer_claims_nothing_and_resumes_only_while_its_target_and_origin_hold()
        {
            var fake = new FakeActions();
            var suspended = AudioLayer("slice-volume") with { Suspended = true };
            var home = OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = suspended };

            fake.Press(home);
            Assert.Single(fake.Tunes);
            Assert.Empty(fake.SliceGainChanges);

            Assert.True(KeyArbiter.CanResume(suspended, home));
            Assert.False(KeyArbiter.CanResume(suspended with { TargetValid = false }, home));
            Assert.False(KeyArbiter.CanResume(suspended, home with { Radio = Radio() with { SessionId = "session-2" } }));
            Assert.False(KeyArbiter.CanResume(suspended, InDialog(SurfaceRole.StandardControl, Bare(GestureKey.Down))));
        }

        [Fact]
        public void While_a_layers_help_list_is_up_the_arrows_belong_to_the_list()
        {
            var fake = new FakeActions();
            var helpList = new KeyLayerState { Kind = KeyLayerKinds.LayerHelpList, LayerId = "audio", OriginWindowId = HomeWindow };
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = helpList });

            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.LayerHelpList, handoff.Owner);
            Assert.Equal("audio", handoff.OwnerId);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_layer_stranded_in_a_window_it_was_not_opened_from_is_dropped_and_owns_nothing()
        {
            var fake = new FakeActions();
            // The audio layer was opened on Home; a dialog editor is now active.
            var decision = fake.Press(InDialog(SurfaceRole.TextEditor, Bare(GestureKey.V)) with { Layer = AudioLayer("pan", HomeWindow) });

            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.TextEditor, handoff.Owner);                  // V is a letter here
            Assert.Equal(LayerDisposition.DropWithoutRestore, handoff.Layers);     // and the coordinator is told to drop the layer
            Assert.Empty(fake.OtherActions);
        }

        [Theory]
        [InlineData(false, "session-1")]
        [InlineData(true, "session-2")]
        public void A_layer_whose_target_has_gone_refuses_as_stale_and_writes_nothing(bool targetValid, string layerSession)
        {
            var fake = new FakeActions();
            var layer = AudioLayer("slice-volume") with { TargetValid = targetValid, RadioSessionId = layerSession };
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = layer });

            Assert.Equal(RefusalReason.StaleTarget, Assert.IsType<Refuse>(decision).Reason);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void Help_armed_owns_only_its_own_keys_and_everything_else_continues_below()
        {
            var fake = new FakeActions();
            var helpArmed = new KeyLayerState { Kind = KeyLayerKinds.HelpArmed, LayerId = "help", OriginWindowId = HomeWindow };
            fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Layer = helpArmed });
            Assert.Single(fake.Tunes);
        }

        [Fact]
        public void A_layer_pass_through_continues_in_the_same_origin_context_not_as_a_Home_key()
        {
            var declarations = new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                new GestureDeclaration
                {
                    CommandId = "test.pass-through",
                    Kind = DeclarationKind.LayerPassThrough,
                    Chord = new ChordPattern(GestureKey.Down, Control: true),
                    LayerKinds = KeyLayerKinds.ValueLayer,
                    LayerId = "audio",
                },
            };
            var set = GestureDeclarationSet.Create(declarations, RuledHomeKeyMap.LayerTargets, RuledHomeKeyMap.ContextParents);

            var fake = new FakeActions();
            var decision = fake.Press(
                InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.Down)) with { Layer = AudioLayer("pan", DialogWindow) }, set);

            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.TextEditor, handoff.Owner);   // it stays the editor's Ctrl+Down
            Assert.True(handoff.ContinuedBelowLayer);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_layer_that_reads_the_side_of_Shift_gets_the_side_and_both_sides_together_match_neither()
        {
            // The filter layer's grammar (#516): Left Shift is the low edge,
            // Right Shift the high edge. Test-local declarations; the filter
            // layer itself is not part of the ruled Home map.
            GestureDeclaration Edge(string id, ShiftRequirement side) => new GestureDeclaration
            {
                CommandId = id,
                Chord = new ChordPattern(GestureKey.Left, Shift: side),
                LayerKinds = KeyLayerKinds.ValueLayer,
                LayerId = "test-filter",
                Modal = ModalPolicy.AllowedInModal,
            };
            var set = GestureDeclarationSet.Create(new[]
            {
                Edge("test.low-edge-down", ShiftRequirement.Left),
                Edge("test.high-edge-down", ShiftRequirement.Right),
            });

            InputSnapshot Press(ShiftSide side) => OnHome("home.frequency", Shifted(GestureKey.Left, side)) with
            {
                Layer = new KeyLayerState { Kind = KeyLayerKinds.ValueLayer, LayerId = "test-filter", OriginWindowId = HomeWindow },
            };

            Assert.Equal("test.low-edge-down", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(Press(ShiftSide.Left), set)).CommandId);
            Assert.Equal("test.high-edge-down", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(Press(ShiftSide.Right), set)).CommandId);
            Assert.IsType<Unbound>(KeyArbiter.Decide(Press(ShiftSide.Both), set));
        }
    }
}
