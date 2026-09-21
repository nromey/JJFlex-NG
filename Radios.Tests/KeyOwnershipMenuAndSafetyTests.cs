using System.Collections.Generic;
using Radios.KeyOwnership;
using Xunit;
using static Radios.Tests.KeyOwn;

namespace Radios.Tests
{
    /// <summary>
    /// Steps one to three of the order: the actual destination, an owned
    /// release and the transmit stop, and native-menu ownership. The menu
    /// cases are the shape of #583 — the SAME Home snapshot that tunes, with
    /// menu state set, must reach the menu and touch nothing on the radio.
    /// These pin the decision only; they say nothing about the native event
    /// order, which is unverified and not this track's.
    /// </summary>
    public class KeyOwnershipMenuAndSafetyTests
    {
        public static IEnumerable<object[]> OwningMenuStatesOnEveryField()
        {
            var states = new[]
            {
                NativeMenuState.EntryPending, NativeMenuState.BarSelected, NativeMenuState.PopupOpen,
                NativeMenuState.NestedPopupOpen, NativeMenuState.SystemMenu, NativeMenuState.ContextMenu,
            };
            foreach (var state in states)
                foreach (var field in NamedHomeFields)
                    yield return new object[] { state, field };
        }

        [Theory]
        [MemberData(nameof(OwningMenuStatesOnEveryField))]
        public void Menu_ownership_beats_Home_for_Down(NativeMenuState state, string field)
        {
            var fake = new FakeActions();
            var tuning = OnHome(field, Bare(GestureKey.Down));

            // Positive control: without the menu this exact snapshot tunes.
            Assert.IsType<ExecuteAction>(KeyArbiter.Decide(tuning, Ruled));

            var decision = fake.Press(tuning with { Menu = MenuOnHome(state) });

            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.NativeMenu, handoff.Owner);
            Assert.Equal("window.home", handoff.OwnerId);
            Assert.Equal(3, handoff.DecidedAtStep);
            Assert.True(fake.RadioUntouched);   // frequency, gain, pan and slice all untouched
            Assert.Empty(fake.OtherActions);
            Assert.Empty(fake.Refusals);
        }

        public static IEnumerable<object[]> MenuNavigationKeys()
        {
            yield return new object[] { new KeyChord(GestureKey.Up) };
            yield return new object[] { new KeyChord(GestureKey.Left) };
            yield return new object[] { new KeyChord(GestureKey.Enter) };
            yield return new object[] { new KeyChord(GestureKey.Escape) };
            yield return new object[] { new KeyChord(GestureKey.F) };               // first-letter navigation
            yield return new object[] { new KeyChord(GestureKey.S, Alt: true) };    // a mnemonic
            yield return new object[] { new KeyChord(GestureKey.Down, Alt: true) }; // band-down everywhere else
            yield return new object[] { new KeyChord(GestureKey.PageDown) };        // the relocation answer everywhere else
            yield return new object[] { new KeyChord(GestureKey.Q) };               // a key the menu has no use for
        }

        [Theory]
        [MemberData(nameof(MenuNavigationKeys))]
        public void Inside_a_menu_every_navigation_mnemonic_and_unknown_key_is_the_menus(KeyChord chord)
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice-operations", chord) with { Menu = MenuOnHome(NativeMenuState.PopupOpen) });
            Assert.Equal(KeyOwnerKind.NativeMenu, Assert.IsType<HandToOwner>(decision).Owner);
            Assert.True(fake.RadioUntouched);
            Assert.Empty(fake.Refusals);
            Assert.Equal(0, fake.Unbounds);
        }

        [Fact]
        public void A_cancelled_or_failed_menu_activation_leaves_no_claim_behind()
        {
            // The adapter clears pending state on cancellation or failure; the
            // cleared report is simply "no menu", and Home tunes again.
            var fake = new FakeActions();
            fake.Press(OnHome("home.slice-operations", Bare(GestureKey.Down)) with { Menu = MenuOnHome(NativeMenuState.None) });
            Assert.Single(fake.Tunes);
        }

        [Fact]
        public void Merely_holding_Alt_is_not_menu_entry_and_is_not_a_failed_command()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice-operations", new KeyChord(GestureKey.Alt, Alt: true)));
            Assert.False(Assert.IsType<NoCommandEvent>(decision).Consume);
            Assert.Equal(0, fake.Unbounds);
        }

        [Fact]
        public void A_menu_report_nobody_reconciled_refuses_the_action_rather_than_guessing()
        {
            // The report says the Home menu is open, but it was last confirmed
            // under an older context generation. The arbiter cannot tell
            // whether the menu still owns input, so nothing is changed.
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.slice-operations", Bare(GestureKey.Down)) with
            {
                Menu = MenuOnHome(NativeMenuState.PopupOpen) with { ObservedAtGeneration = Generation - 1 },
            });
            var refusal = Assert.IsType<Refuse>(decision);
            Assert.Equal(RefusalReason.ContextInTransition, refusal.Reason);
            Assert.Equal("home.tune-down", refusal.CommandId);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void A_stale_Home_menu_report_cannot_take_the_keys_of_a_dialog_that_is_now_active()
        {
            // A missed menu-exit notification must not stop a callsign being typed.
            var fake = new FakeActions();
            var decision = fake.Press(InDialog(SurfaceRole.TextEditor, Bare(GestureKey.M)) with
            {
                Menu = MenuOnHome(NativeMenuState.PopupOpen),
            });
            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.TextEditor, handoff.Owner);
            Assert.True(handoff.StaleMenuReportIgnored);
            Assert.True(fake.RadioUntouched);
        }

        // ── The approved transfer out of a menu ─────────────────────────

        [Fact]
        public void The_JJ_key_may_leave_a_menu_but_only_after_the_menu_is_dismissed()
        {
            var decision = KeyArbiter.Decide(
                OnHome("home.slice", Ctrl(GestureKey.J)) with { Menu = MenuOnHome(NativeMenuState.PopupOpen) }, Ruled);

            var action = Assert.IsType<ExecuteAction>(decision);
            Assert.Equal("jjkey.arm", action.CommandId);
            Assert.Equal(LayerEffect.ArmJJKey, action.LayerEffect);
            Assert.Equal(ActionDelivery.AfterMenuDismissal, action.Delivery);
            Assert.Equal(3, action.DecidedAtStep);
        }

        [Fact]
        public void An_action_waiting_on_menu_dismissal_is_never_re_aimed_at_a_changed_world()
        {
            var inMenu = OnHome("home.slice", Ctrl(GestureKey.J)) with { Menu = MenuOnHome(NativeMenuState.PopupOpen) };
            var waiting = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(inMenu, Ruled));

            // The expected transition: the menu closed, the generation moved on, nothing else changed.
            var dismissed = inMenu with { Menu = NativeMenuObservation.NoMenu, ContextGeneration = Generation + 1 };
            Assert.Same(waiting, KeyArbiter.Revalidate(waiting, dismissed));

            // A different radio session.
            var newSession = dismissed with { Radio = Radio() with { SessionId = "session-2" } };
            Assert.Equal(RefusalReason.StaleTarget, Assert.IsType<Refuse>(KeyArbiter.Revalidate(waiting, newSession)).Reason);

            // A dialog took focus instead.
            var dialogTookOver = InDialog(SurfaceRole.StandardControl, Ctrl(GestureKey.J)) with { ContextGeneration = Generation + 1 };
            Assert.Equal(RefusalReason.StaleTarget, Assert.IsType<Refuse>(KeyArbiter.Revalidate(waiting, dialogTookOver)).Reason);
        }

        [Fact]
        public void A_slice_action_is_stale_if_the_active_slice_changed_or_its_slice_went_away()
        {
            var press = OnHome("home.slice", Bare(GestureKey.Down));
            var tune = Assert.IsType<ExecuteAction>(KeyArbiter.Decide(press, Ruled));

            Assert.Same(tune, KeyArbiter.Revalidate(tune, press));
            Assert.IsType<Refuse>(KeyArbiter.Revalidate(tune, press with { Radio = Radio(active: 'B') }));
            Assert.IsType<Refuse>(KeyArbiter.Revalidate(tune, press with { Radio = Radio(active: 'A', slices: "BC") }));
            Assert.IsType<Refuse>(KeyArbiter.Revalidate(tune, press with { ContextGeneration = Generation + 1 }));
        }

        // ── Step two: the owned release, and Escape as a stop ───────────

        [Fact]
        public void A_key_up_goes_to_the_operation_it_started_even_inside_a_menu_with_the_modifier_gone()
        {
            var fake = new FakeActions();
            var release = OnHome("home.frequency", Bare(GestureKey.Space)) with
            {
                Phase = KeyPhase.Up,                                   // started as Ctrl+Space; Ctrl is already up
                Menu = MenuOnHome(NativeMenuState.PopupOpen),
                OwnedOperations = new[] { new OwnedOperation { OperationId = "ptt", Key = GestureKey.Space } },
            };
            var handoff = Assert.IsType<HandToOwner>(fake.Press(release));
            Assert.Equal(KeyOwnerKind.OperationOwner, handoff.Owner);
            Assert.Equal("ptt", handoff.OwnerId);
            Assert.Equal(2, handoff.DecidedAtStep);
        }

        [Fact]
        public void A_key_up_is_still_delivered_to_its_operation_when_focus_has_moved_to_a_dialog()
        {
            var release = InDialog(SurfaceRole.TextEditor, Bare(GestureKey.Space)) with
            {
                Phase = KeyPhase.Up,
                OwnedOperations = new[] { new OwnedOperation { OperationId = "ptt", Key = GestureKey.Space } },
            };
            Assert.Equal(KeyOwnerKind.OperationOwner, Assert.IsType<HandToOwner>(KeyArbiter.Decide(release, Ruled)).Owner);
        }

        [Theory]
        [InlineData(true, false)]    // our own operation is pending; the radio has not caught up yet
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Escape_during_transmit_is_the_stop_request_first_wherever_it_is_pressed(bool local, bool radio)
        {
            var transmitting = new[]
            {
                OnHome("home.frequency", Bare(GestureKey.Escape)) with { Menu = MenuOnHome(NativeMenuState.PopupOpen) },
                OnHome("home.frequency", Bare(GestureKey.Escape)) with { Layer = AudioLayer("pan") },
                InDialog(SurfaceRole.TextEditor, Bare(GestureKey.Escape)),
            };
            foreach (var where in transmitting)
            {
                var fake = new FakeActions();
                var decision = fake.Press(where with { LocalTransmitActiveOrPending = local, RadioReportsTransmit = radio });

                var action = Assert.IsType<ExecuteAction>(decision);
                Assert.Equal("safety.stop-transmit", action.CommandId);
                Assert.Equal(2, action.DecidedAtStep);
                Assert.Equal(LayerDisposition.DropWithoutRestore, action.Layers);   // layers drop; no restore write
                Assert.Equal(1, fake.TransmitStops);
                Assert.Empty(fake.Handoffs);        // the menu or dialog closes on a LATER press
                Assert.Empty(fake.OtherActions);    // and the layer's own cancel did not also run
            }
        }

        [Fact]
        public void Escape_with_nothing_transmitting_is_an_ordinary_Escape()
        {
            var inMenu = OnHome("home.frequency", Bare(GestureKey.Escape)) with { Menu = MenuOnHome(NativeMenuState.PopupOpen) };
            Assert.Equal(KeyOwnerKind.NativeMenu, Assert.IsType<HandToOwner>(KeyArbiter.Decide(inMenu, Ruled)).Owner);

            var inLayer = OnHome("home.frequency", Bare(GestureKey.Escape)) with { Layer = AudioLayer("pan") };
            Assert.Equal("layer.cancel", Assert.IsType<ExecuteAction>(KeyArbiter.Decide(inLayer, Ruled)).CommandId);
        }

        [Fact]
        public void The_stop_request_grants_no_new_way_to_start_transmitting()
        {
            // Nothing in the ruled map keys the transmitter, from anywhere.
            foreach (var d in RuledHomeKeyMap.Declarations)
                Assert.DoesNotContain("transmit", d.CommandId);
        }

        // ── Step one: the destination ───────────────────────────────────

        [Fact]
        public void A_key_bound_for_another_application_is_not_ours_to_decide()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with
            {
                Destination = InputDestination.OutsideApplication,
            });
            var handoff = Assert.IsType<HandToOwner>(decision);
            Assert.Equal(KeyOwnerKind.OutsideApplication, handoff.Owner);
            Assert.Equal(1, handoff.DecidedAtStep);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void An_unknown_destination_refuses_rather_than_acting_on_a_remembered_target()
        {
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { Destination = InputDestination.Unknown });
            Assert.Equal(RefusalReason.ContextInTransition, Assert.IsType<Refuse>(decision).Reason);
            Assert.True(fake.RadioUntouched);
        }

        [Fact]
        public void An_unknown_destination_still_ends_an_operation_we_started()
        {
            var release = OnHome("home.frequency", Bare(GestureKey.Space)) with
            {
                Destination = InputDestination.Unknown,
                Phase = KeyPhase.Up,
                OwnedOperations = new[] { new OwnedOperation { OperationId = "ptt", Key = GestureKey.Space } },
            };
            Assert.Equal(KeyOwnerKind.OperationOwner, Assert.IsType<HandToOwner>(KeyArbiter.Decide(release, Ruled)).Owner);
        }

        [Fact]
        public void A_modal_boundary_that_disagrees_with_the_active_window_is_a_transition_not_a_licence()
        {
            // Home claims to be active while a dialog holds the modal boundary.
            var fake = new FakeActions();
            var decision = fake.Press(OnHome("home.frequency", Bare(GestureKey.Down)) with { ModalBoundaryWindowId = DialogWindow });
            Assert.Equal(RefusalReason.ContextInTransition, Assert.IsType<Refuse>(decision).Reason);
            Assert.True(fake.RadioUntouched);
        }
    }
}
