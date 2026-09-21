using System.Collections.Generic;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// Steps five and six, and the no-focus disposition: reserved application
    /// commands beat local shortcuts; editing and ordinary control interaction
    /// stay local; a surface's SEMANTIC ROLE decides, never its class name;
    /// and an unknown surface defaults to local input, never radio commands.
    /// </summary>
    public class KeyOwnershipLocalInputTests
    {
        /// <summary>
        /// The ruled map plus what a dialog test needs: the transmit-status
        /// chord as a reserved across-application command (it is Alt+Shift+S,
        /// Global, in today's registry), a dialog's own Ctrl+S save, and a
        /// reserved command that may NOT run inside a modal. Which existing
        /// commands really are reserved is an open audit, so these live with
        /// the tests and not in the ruled map.
        /// </summary>
        private static readonly GestureDeclarationSet WithDialog = GestureDeclarationSet.Create(
            new List<GestureDeclaration>(RuledHomeKeyMap.Declarations)
            {
                new GestureDeclaration
                {
                    CommandId = "test.speak-tx-status",
                    RegistryCommand = CommandValues.SpeakTxStatus,
                    Chord = new ChordPattern(GestureKey.S, Alt: true, Shift: ShiftRequirement.Either),
                    Reach = InvocationReach.AcrossApplication,
                    Reserved = true,
                    Modal = ModalPolicy.AllowedInModal,
                },
                new GestureDeclaration
                {
                    CommandId = "test.preset-save",
                    Chord = new ChordPattern(GestureKey.S, Control: true),
                    ContextId = "dialog.preset",
                    Reach = InvocationReach.ContextAndDescendants,
                },
                new GestureDeclaration
                {
                    CommandId = "test.focus-home-frequency",
                    Chord = new ChordPattern(GestureKey.F2, Control: true),
                    Reach = InvocationReach.AcrossApplication,
                    Reserved = true,
                    Modal = ModalPolicy.RefusedInModal,
                },
            },
            RuledHomeKeyMap.LayerTargets, RuledHomeKeyMap.ContextParents);

        private static readonly KeyChord AltShiftS = new KeyChord(GestureKey.S, Alt: true, Shift: ShiftSide.Left);

        // ── Text is text ────────────────────────────────────────────────

        public static IEnumerable<object[]> EditingGestures()
        {
            yield return new object[] { new KeyChord(GestureKey.M) };                              // a callsign M
            yield return new object[] { new KeyChord(GestureKey.A, Shift: ShiftSide.Left) };       // uppercase A
            yield return new object[] { new KeyChord(GestureKey.Down) };                           // caret, not tuning
            yield return new object[] { new KeyChord(GestureKey.Up) };
            yield return new object[] { new KeyChord(GestureKey.Left, Shift: ShiftSide.Right) };   // selection
            yield return new object[] { new KeyChord(GestureKey.Home) };
            yield return new object[] { new KeyChord(GestureKey.Back) };
            yield return new object[] { new KeyChord(GestureKey.C, Control: true) };               // clipboard
            yield return new object[] { new KeyChord(GestureKey.V, Control: true) };
            yield return new object[] { new KeyChord(GestureKey.X, Control: true) };
            yield return new object[] { new KeyChord(GestureKey.Z, Control: true) };
            yield return new object[] { new KeyChord(GestureKey.A, Control: true) };
            yield return new object[] { new KeyChord(GestureKey.Right, Control: true, Shift: ShiftSide.Left) };
        }

        [Theory]
        [MemberData(nameof(EditingGestures))]
        public void In_an_editor_typing_caret_selection_and_clipboard_produce_no_radio_action(KeyChord chord)
        {
            var fake = new FakeActions();
            var decision = fake.Press(InDialog(SurfaceRole.TextEditor, chord), WithDialog);

            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.TextEditor, handoff.Owner);
            Assert.Equal(6, handoff.DecidedAtStep);
            Assert.False(handoff.ReportIfUnhandled);
            Assert.True(fake.RadioUntouched);
            Assert.Empty(fake.OtherActions);
        }

        [Fact]
        public void Composition_input_is_never_a_command_however_much_its_modifier_bits_look_like_one()
        {
            // AltGr+S arrives looking like Ctrl+Alt+S. Declare a reserved
            // command on exactly that chord and it still must not fire.
            var set = GestureDeclarationSet.Create(new[]
            {
                new GestureDeclaration
                {
                    CommandId = "test.ctrl-alt-s",
                    Chord = new ChordPattern(GestureKey.S, Control: true, Alt: true),
                    Reach = InvocationReach.AcrossApplication,
                    Reserved = true,
                    Modal = ModalPolicy.AllowedInModal,
                },
            });
            var chord = new KeyChord(GestureKey.S, Control: true, Alt: true);

            // Positive control: as a real chord, it fires.
            Assert.IsType<ExecuteAction>(KeyArbiter.Decide(InDialog(SurfaceRole.TextEditor, chord), set));

            var composed = KeyArbiter.Decide(InDialog(SurfaceRole.TextEditor, chord) with { IsTextComposition = true }, set);
            Assert.Equal(KeyOwnerKind.TextEditor, Assert.IsType<HandToOwner>(composed).Owner);
        }

        // ── Role, not class name ────────────────────────────────────────

        [Fact]
        public void The_same_surface_identity_is_a_command_surface_or_an_editor_by_its_declared_role_alone()
        {
            var asCommandSurface = OnHome("home.frequency", Bare(GestureKey.Down));
            var asEditor = asCommandSurface with { Surface = asCommandSurface.Surface! with { Role = SurfaceRole.TextEditor } };

            Assert.Equal("home.tune-down", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(asCommandSurface, Ruled)).CommandId);
            Assert.Equal(KeyOwnerKind.TextEditor, Assert.IsType<HandToOwner>(KeyArbiter.Decide(asEditor, Ruled)).Owner);
        }

        [Fact]
        public void A_surface_nobody_classified_is_local_input_even_in_the_middle_of_Home()
        {
            Assert.Equal(SurfaceRole.Unknown, new FocusedSurface().Role);   // the default IS the safe value

            var fake = new FakeActions();
            var home = OnHome("home.frequency", Bare(GestureKey.Down));
            var unknown = home with { Surface = new FocusedSurface { WindowId = HomeWindow, ContextPath = home.Surface!.ContextPath } };

            foreach (var chord in new[] { Bare(GestureKey.Down), Bare(GestureKey.PageDown), AltChord(GestureKey.Down), AltShiftS })
            {
                var handoff = Assert.IsType<HandToOwner>(fake.Press(unknown with { Chord = chord }, WithDialog));
                Assert.Equal(KeyOwnerKind.LocalUnclassified, handoff.Owner);
            }
            Assert.True(fake.RadioUntouched);
            Assert.Empty(fake.OtherActions);
            Assert.Empty(fake.Refusals);
        }

        [Fact]
        public void Foreign_embedded_content_keeps_every_key()
        {
            var fake = new FakeActions();
            var handoff = Assert.IsType<HandToOwner>(fake.Press(InDialog(SurfaceRole.ForeignEmbedded, AltShiftS), WithDialog));
            Assert.Equal(KeyOwnerKind.LocalUnclassified, handoff.Owner);
            Assert.Empty(fake.OtherActions);
        }

        [Fact]
        public void A_list_owns_its_Down_arrow()
        {
            var fake = new FakeActions();
            var handoff = Assert.IsType<HandToOwner>(fake.Press(InDialog(SurfaceRole.StandardControl, Bare(GestureKey.Down))));
            Assert.Equal(KeyOwnerKind.StandardControl, handoff.Owner);
            Assert.True(fake.RadioUntouched);
        }

        // ── Reserved commands against local shortcuts ───────────────────

        public static IEnumerable<object[]> PlacesToPressTheStatusChord()
        {
            yield return new object[] { OnHome("home.slice-operations", AltShiftS) };
            yield return new object[] { InDialog(SurfaceRole.StandardControl, AltShiftS, modal: true) };    // a dialog button
            yield return new object[] { InDialog(SurfaceRole.TextEditor, AltShiftS, modal: true) };
            yield return new object[] { InDialog(SurfaceRole.StandardControl, AltShiftS, modal: false) };   // active modeless
            yield return new object[] { InDialog(SurfaceRole.TextEditor, AltShiftS, modal: false) };
        }

        [Theory]
        [MemberData(nameof(PlacesToPressTheStatusChord))]
        public void Alt_Shift_S_is_transmit_status_everywhere_and_never_the_dialogs_Save(InputSnapshot where)
        {
            var fake = new FakeActions();
            var action = Assert.IsType<ExecuteAction>(fake.Press(where, WithDialog));

            Assert.Equal("test.speak-tx-status", action.CommandId);
            Assert.Equal(61, (int)action.RegistryCommand!.Value);   // SpeakTxStatus, its on-disk number unchanged
            Assert.Equal(5, action.DecidedAtStep);
            Assert.Equal(new[] { "test.speak-tx-status" }, fake.OtherActions);   // zero Save calls
            Assert.Empty(fake.Handoffs);                                          // and the mnemonic did not also fire
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void The_dialogs_own_Ctrl_S_is_still_the_dialogs_Save(bool modal)
        {
            var fake = new FakeActions();
            var action = Assert.IsType<ExecuteAction>(fake.Press(InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.S), modal), WithDialog));
            Assert.Equal("test.preset-save", action.CommandId);
            Assert.Equal(6, action.DecidedAtStep);
            Assert.Equal(new[] { "test.preset-save" }, fake.OtherActions);
        }

        [Fact]
        public void An_inactive_modeless_window_cannot_claim_a_key()
        {
            // Home is active with nothing focused; the modeless preset dialog
            // still remembers focus on its editor. Its Save must not run.
            var fake = new FakeActions();
            var snapshot = InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.S), modal: false) with
            {
                ActiveWindow = new WindowContext { Id = HomeWindow, Kind = WindowKind.HomeShell },
            };
            var unbound = Assert.IsType<Unbound>(fake.Press(snapshot, WithDialog));
            Assert.Equal(UnboundContextKind.NoFocusedControl, unbound.ContextKind);
            Assert.Empty(fake.OtherActions);
        }

        [Fact]
        public void An_active_modal_excludes_the_Home_field_behind_it()
        {
            // WPF still remembers the Slice Operations field; the dialog is what is active.
            var fake = new FakeActions();
            var snapshot = OnHome("home.slice-operations", Bare(GestureKey.Down)) with
            {
                ActiveWindow = new WindowContext { Id = DialogWindow, Kind = WindowKind.ModalDialog, OwnerWindowId = HomeWindow },
                ModalBoundaryWindowId = DialogWindow,
            };
            var unbound = Assert.IsType<Unbound>(fake.Press(snapshot));
            Assert.Equal(UnboundContextKind.NoFocusedControl, unbound.ContextKind);
            Assert.Equal(DialogWindow, unbound.ContextId);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_reserved_command_that_cannot_run_in_a_modal_is_refused_there_and_does_not_become_a_local_key()
        {
            var fake = new FakeActions();
            var chord = Ctrl(GestureKey.F2);

            var refusal = Assert.IsType<Refuse>(fake.Press(InDialog(SurfaceRole.TextEditor, chord, modal: true), WithDialog));
            Assert.Equal(RefusalReason.ModalRestriction, refusal.Reason);
            Assert.Equal("test.focus-home-frequency", refusal.CommandId);
            Assert.Empty(fake.Handoffs);
            Assert.Empty(fake.OtherActions);

            // The same command from an active MODELESS dialog runs: modality is what restricts it.
            Assert.IsType<ExecuteAction>(KeyArbiter.Decide(InDialog(SurfaceRole.TextEditor, chord, modal: false), WithDialog));
        }

        [Fact]
        public void A_modal_refusal_and_a_no_radio_refusal_are_different_answers()
        {
            var modal = Assert.IsType<Refuse>(KeyArbiter.Decide(InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.F2)), WithDialog));
            var noRadio = Assert.IsType<Refuse>(KeyArbiter.Decide(
                OnHome("home.frequency", Bare(GestureKey.Down)) with { Radio = RadioFacts.Disconnected }, WithDialog));
            Assert.NotEqual(modal.Reason, noRadio.Reason);
        }

        // ── Default Enter and Escape come last ──────────────────────────

        [Fact]
        public void A_combo_popup_closes_before_its_dialog()
        {
            var combo = InDialog(SurfaceRole.StandardControl, Bare(GestureKey.Escape));
            var open = combo with { Surface = combo.Surface! with { HasOpenPopup = true } };

            Assert.Equal(KeyOwnerKind.StandardControl, Assert.IsType<HandToOwner>(KeyArbiter.Decide(open, Ruled)).Owner);
            Assert.Equal(KeyOwnerKind.WindowDefault, Assert.IsType<HandToOwner>(KeyArbiter.Decide(combo, Ruled)).Owner);
        }

        [Fact]
        public void Enter_is_text_in_a_multi_line_editor_and_the_default_action_in_a_single_line_one()
        {
            var single = InDialog(SurfaceRole.TextEditor, Bare(GestureKey.Enter));
            var multi = single with { Surface = single.Surface! with { AcceptsReturn = true } };

            Assert.Equal(KeyOwnerKind.WindowDefault, Assert.IsType<HandToOwner>(KeyArbiter.Decide(single, Ruled)).Owner);
            Assert.Equal(KeyOwnerKind.TextEditor, Assert.IsType<HandToOwner>(KeyArbiter.Decide(multi, Ruled)).Owner);
        }

        [Fact]
        public void A_modifier_chord_nothing_declares_is_handed_on_with_a_request_to_report_it_if_nobody_takes_it()
        {
            // The engine cannot see a dialog's mnemonics, so it does not call
            // this unbound itself; it asks the adapter to, if the native route
            // also leaves it unhandled.
            var handoff = Assert.IsType<HandToOwner>(KeyArbiter.Decide(InDialog(SurfaceRole.TextEditor, Ctrl(GestureKey.Q)), WithDialog));
            Assert.True(handoff.ReportIfUnhandled);
        }

        // ── No focused control ──────────────────────────────────────────

        [Fact]
        public void With_no_focused_control_there_is_no_invented_Home_field_but_application_commands_still_work()
        {
            var fake = new FakeActions();
            var nowhere = OnHome("home.frequency", Bare(GestureKey.Down)) with { Surface = null };

            var unbound = Assert.IsType<Unbound>(fake.Press(nowhere, WithDialog));
            Assert.Equal(UnboundContextKind.NoFocusedControl, unbound.ContextKind);
            Assert.Equal(HomeWindow, unbound.ContextId);
            Assert.True(fake.RadioUntouched);

            Assert.Equal("jjkey.arm", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(nowhere with { Chord = Ctrl(GestureKey.J) }, WithDialog)).CommandId);
            Assert.Equal("test.speak-tx-status", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(nowhere with { Chord = AltShiftS }, WithDialog)).CommandId);
        }

        [Fact]
        public void Bare_modifiers_and_unowned_releases_are_not_failed_commands()
        {
            var fake = new FakeActions();
            fake.Press(OnHome("home.frequency", new KeyChord(GestureKey.LeftShift, Shift: ShiftSide.Left)));
            fake.Press(OnHome("home.frequency", new KeyChord(GestureKey.Control, Control: true)));
            fake.Press(OnHome("home.frequency", Bare(GestureKey.S)) with { Phase = KeyPhase.Up });
            fake.Press(InDialog(SurfaceRole.TextEditor, Bare(GestureKey.M)) with { Phase = KeyPhase.Up });

            Assert.Equal(4, fake.NoCommands);
            Assert.Equal(0, fake.Unbounds);
            Assert.Empty(fake.Refusals);
        }
    }
}
