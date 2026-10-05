using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The decision behind the system-wide keyboard hook (#307), replayed
    /// against the streams the 2026-10-05 probe measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repeat stream is the probe's: a 1.2 s hold produced one DOWN,
    /// repeats every ~30 ms, then one UP. The release-with-modifiers-lifted
    /// case is the probe's second run — 110 downs and exactly ONE up, because
    /// it matched the release against live modifier state. A push to talk
    /// built that way keys the radio and never hears the release, so the
    /// test that would have caught it is the first one here.
    /// </para>
    /// <para>
    /// Every "it is swallowed" assertion has a "this is not" beside it,
    /// because a hook that swallows the wrong key breaks the program the
    /// operator is actually typing into, and a sweep that only checks what we
    /// claim would never see that.
    /// </para>
    /// </remarks>
    public class SystemWideKeyDeciderTests
    {
        private const uint VK_SPACE = 0x20;
        private const uint VK_J = 0x4A;
        private const uint VK_A = 0x41;
        private const uint VK_H = 0x48;
        private const uint VK_7 = 0x37;
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_OEM_2 = 0xBF;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_LMENU = 0xA4;

        private static readonly SystemWideModifiers CtrlAlt = new(true, true, false);
        private static readonly SystemWideModifiers CtrlShift = new(true, false, true);
        private static readonly SystemWideModifiers AltShift = new(false, true, true);
        private static readonly SystemWideModifiers AltOnly = new(false, true, false);
        private static readonly SystemWideModifiers Nothing = SystemWideModifiers.None;

        private static SystemWideKeyDecider Live()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
            return d;
        }

        // ── Push to talk: the two rules the probe paid for ──────────────

        [Fact]
        public void A_release_is_matched_by_key_code_alone_after_the_modifiers_are_lifted()
        {
            // The probe's second run, as the operator actually does it: lift
            // Ctrl and Alt, THEN let go of Space. Modifier events pass
            // through; the Space up is ours whatever the modifiers say now.
            var d = Live();
            var down = d.Decide(VK_SPACE, true, CtrlAlt);
            Assert.Equal(SystemWideAction.PttDown, down.Action);
            Assert.True(down.Swallow);
            Assert.True(d.PttHeld);

            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_LCONTROL, false, AltOnly));
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_LMENU, false, Nothing));

            var up = d.Decide(VK_SPACE, false, Nothing);
            Assert.Equal(SystemWideAction.PttUp, up.Action);
            Assert.True(up.Swallow);
            Assert.False(d.PttHeld);
        }

        [Fact]
        public void The_measured_repeat_stream_keys_once_and_unkeys_once_and_every_event_is_swallowed()
        {
            var d = Live();
            var actions = new List<SystemWideAction>();
            int swallowed = 0, events = 0;

            void Feed(uint vk, bool isDown, SystemWideModifiers m)
            {
                var r = d.Decide(vk, isDown, m);
                events++;
                if (r.Swallow) swallowed++;
                if (r.Action != SystemWideAction.None) actions.Add(r.Action);
            }

            // 1.2 s hold at ~30 ms: one down, forty repeats, one up.
            Feed(VK_SPACE, true, CtrlAlt);
            for (int i = 0; i < 40; i++) Feed(VK_SPACE, true, CtrlAlt);
            Feed(VK_SPACE, false, CtrlAlt);

            Assert.Equal(new[] { SystemWideAction.PttDown, SystemWideAction.PttUp }, actions);
            Assert.Equal(events, swallowed);
            Assert.Equal(42, events);
        }

        [Fact]
        public void A_second_hold_after_a_release_keys_again()
        {
            var d = Live();
            d.Decide(VK_SPACE, true, CtrlAlt);
            d.Decide(VK_SPACE, false, Nothing);
            var again = d.Decide(VK_SPACE, true, CtrlAlt);
            Assert.Equal(SystemWideAction.PttDown, again.Action);
        }

        [Fact]
        public void A_forced_release_forgets_the_hold_so_the_stale_up_is_nobodys_business()
        {
            var d = Live();
            d.Decide(VK_SPACE, true, CtrlAlt);
            d.ForceReleasePtt();
            Assert.False(d.PttHeld);
            // The up that eventually arrives belongs to nobody and must not
            // be reported as a second release.
            var up = d.Decide(VK_SPACE, false, Nothing);
            Assert.Equal(SystemWideAction.None, up.Action);
        }

        // ── Nothing of ours passes through ───────────────────────────────

        [Fact]
        public void A_plain_space_bar_is_never_touched()
        {
            // The whole reason swallowing is exact: Don is typing into N3FJP.
            var d = Live();
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, Nothing));
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, false, Nothing));
        }

        [Fact]
        public void Alt_space_alone_is_windows_system_menu_and_is_not_ours()
        {
            var d = Live();
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, AltOnly));
        }

        [Fact]
        public void An_extra_modifier_is_a_different_chord_and_passes_through()
        {
            var d = Live();
            var all = new SystemWideModifiers(true, true, true);
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, all));
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_J, true, all));
        }

        [Fact]
        public void Modifier_keys_themselves_pass_through_in_every_state()
        {
            var d = Live();
            uint[] mods = { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

            foreach (uint vk in mods)
            {
                Assert.Equal(SystemWideDecision.Pass, d.Decide(vk, true, CtrlShift));
                Assert.Equal(SystemWideDecision.Pass, d.Decide(vk, false, CtrlShift));
            }

            // And while the layer is waiting for its second key — the
            // operator reaching for Shift+A must not have Shift read as the key.
            d.Decide(VK_J, true, CtrlShift);
            Assert.True(d.LeaderArmed);
            Assert.Equal(SystemWideDecision.Pass, d.Decide(0xA0, true, new(false, false, true)));
            Assert.True(d.LeaderArmed);
        }

        [Fact]
        public void A_disabled_set_claims_nothing_at_all()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults with { Enabled = false } };
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, CtrlAlt));
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_J, true, CtrlShift));
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, AltShift));
        }

        [Fact]
        public void An_unassigned_role_claims_nothing()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults with { PushToTalk = Keys.None } };
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_SPACE, true, CtrlAlt));
            // The other two still work.
            Assert.Equal(SystemWideAction.LockToggle, d.Decide(VK_SPACE, true, AltShift).Action);
        }

        // ── The leader and its second key ────────────────────────────────

        [Fact]
        public void The_leader_then_a_digit_dispatches_the_digit_and_swallows_both_edges_of_both_keys()
        {
            var d = Live();
            var arm = d.Decide(VK_J, true, CtrlShift);
            Assert.Equal(SystemWideAction.LeaderArmed, arm.Action);
            Assert.True(arm.Swallow);

            // Repeats of J while held: eaten, no second arming.
            Assert.Equal(SystemWideDecision.Eat, d.Decide(VK_J, true, CtrlShift));
            var jUp = d.Decide(VK_J, false, CtrlShift);
            Assert.True(jUp.Swallow);
            Assert.Equal(SystemWideAction.None, jUp.Action);

            var seven = d.Decide(VK_7, true, Nothing);
            Assert.Equal(SystemWideAction.LeaderKey, seven.Action);
            Assert.Equal(Keys.D7, seven.Key);
            Assert.True(seven.Swallow);

            // The probe left this one for the logger. Not any more.
            var sevenUp = d.Decide(VK_7, false, Nothing);
            Assert.True(sevenUp.Swallow);
            Assert.Equal(SystemWideAction.None, sevenUp.Action);

            // And the next 7 is just a 7.
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_7, true, Nothing));
        }

        [Fact]
        public void The_second_key_carries_its_modifiers_so_the_four_tiers_survive()
        {
            // #515: Shift+letter jumps to a slice, Ctrl+letter toggles,
            // Alt+letter is the rest. From outside the window the grammar is
            // the same grammar.
            var d = Live();
            d.Decide(VK_J, true, CtrlShift);
            d.Decide(VK_J, false, Nothing);
            var shiftA = d.Decide(VK_A, true, new(false, false, true));
            Assert.Equal(Keys.A | Keys.Shift, shiftA.Key);
        }

        [Fact]
        public void Escape_while_armed_cancels_and_is_swallowed()
        {
            var d = Live();
            d.Decide(VK_J, true, CtrlShift);
            var esc = d.Decide(VK_ESCAPE, true, Nothing);
            Assert.Equal(SystemWideAction.LeaderCancelled, esc.Action);
            Assert.True(esc.Swallow);
            Assert.False(d.LeaderArmed);
        }

        [Fact]
        public void The_leader_pressed_twice_arms_only_once_per_press()
        {
            var d = Live();
            Assert.Equal(SystemWideAction.LeaderArmed, d.Decide(VK_J, true, CtrlShift).Action);
            d.Decide(VK_J, false, Nothing);
            // Second press while armed: it is the second key now, not a re-arm.
            var again = d.Decide(VK_J, true, CtrlShift);
            Assert.Equal(SystemWideAction.LeaderKey, again.Action);
            Assert.Equal(Keys.J | Keys.Control | Keys.Shift, again.Key);
        }

        // ── Help-armed parity with the in-app layer (#303) ───────────────

        [Fact]
        public void After_an_unknown_key_the_layer_claims_only_H_slash_and_Escape()
        {
            var d = Live();
            d.SetHelpArmed(true);
            var h = d.Decide(VK_H, true, Nothing);
            Assert.Equal(SystemWideAction.LeaderKey, h.Action);
            Assert.Equal(Keys.H, h.Key);
            Assert.True(h.Swallow);

            d.SetHelpArmed(true);
            var slash = d.Decide(VK_OEM_2, true, new(false, false, true));
            Assert.Equal(SystemWideAction.LeaderKey, slash.Action);
            Assert.Equal(Keys.Oem2 | Keys.Shift, slash.Key);

            d.SetHelpArmed(true);
            Assert.Equal(SystemWideAction.LeaderCancelled, d.Decide(VK_ESCAPE, true, Nothing).Action);
        }

        [Fact]
        public void After_an_unknown_key_anything_else_lets_go_and_is_not_swallowed()
        {
            var d = Live();
            d.SetHelpArmed(true);
            var a = d.Decide(VK_A, true, Nothing);
            Assert.False(a.Swallow);
            Assert.True(a.HelpLetGo);
            Assert.Equal(SystemWideAction.None, a.Action);

            // And the let-go key may itself be one of our chords.
            d.SetHelpArmed(true);
            var ptt = d.Decide(VK_SPACE, true, CtrlAlt);
            Assert.True(ptt.HelpLetGo);
            Assert.Equal(SystemWideAction.PttDown, ptt.Action);
            Assert.True(ptt.Swallow);
        }

        // ── Transmit lock ────────────────────────────────────────────────

        [Fact]
        public void The_lock_toggles_once_per_press_however_long_it_is_held()
        {
            var d = Live();
            int toggles = 0;
            var r = d.Decide(VK_SPACE, true, AltShift);
            if (r.Action == SystemWideAction.LockToggle) toggles++;
            for (int i = 0; i < 30; i++)
            {
                r = d.Decide(VK_SPACE, true, AltShift);
                Assert.True(r.Swallow);
                if (r.Action == SystemWideAction.LockToggle) toggles++;
            }
            var up = d.Decide(VK_SPACE, false, Nothing);
            Assert.True(up.Swallow);
            Assert.Equal(SystemWideAction.None, up.Action);
            Assert.Equal(1, toggles);

            Assert.Equal(SystemWideAction.LockToggle, d.Decide(VK_SPACE, true, AltShift).Action);
        }

        // ── The two-modifier rule ────────────────────────────────────────

        [Theory]
        [InlineData(Keys.J | Keys.Control | Keys.Shift, SystemWideChordVerdict.Ok)]
        [InlineData(Keys.Space | Keys.Control | Keys.Alt, SystemWideChordVerdict.Ok)]
        [InlineData(Keys.Space | Keys.Alt | Keys.Shift, SystemWideChordVerdict.Ok)]
        [InlineData(Keys.F9 | Keys.Control | Keys.Alt | Keys.Shift, SystemWideChordVerdict.Ok)]
        [InlineData(Keys.J | Keys.Control, SystemWideChordVerdict.FewerThanTwoModifiers)]
        [InlineData(Keys.Space | Keys.Shift, SystemWideChordVerdict.FewerThanTwoModifiers)]
        [InlineData(Keys.F9, SystemWideChordVerdict.FewerThanTwoModifiers)]
        [InlineData(Keys.None, SystemWideChordVerdict.NoKey)]
        [InlineData(Keys.Control | Keys.Shift, SystemWideChordVerdict.NoKey)]
        [InlineData(Keys.ShiftKey | Keys.Control | Keys.Alt, SystemWideChordVerdict.ModifierOnly)]
        [InlineData(Keys.LWin | Keys.Control | Keys.Alt, SystemWideChordVerdict.ModifierOnly)]
        public void The_two_modifier_rule_judges_as_ruled(Keys chord, SystemWideChordVerdict expected)
        {
            Assert.Equal(expected, SystemWideChord.Judge(chord));
        }

        [Fact]
        public void The_three_defaults_pass_the_rule_they_are_the_reason_for()
        {
            foreach (var role in SystemWideKeySet.AllRoles)
                Assert.Equal(SystemWideChordVerdict.Ok, SystemWideChord.Judge(SystemWideKeySet.Defaults.For(role)));
        }

        // ── The config file cannot smuggle a bad chord in ────────────────

        [Fact]
        public void A_hand_edited_one_modifier_chord_goes_back_to_its_default_and_says_so()
        {
            var cfg = new SystemWideKeysConfig { PushToTalk = (int)(Keys.Space | Keys.Control) };
            cfg.Sanitize();
            Assert.Equal((int)SystemWideKeySet.DefaultPushToTalk, cfg.PushToTalk);
            Assert.Single(cfg.Repairs);
            Assert.Contains("PushToTalk", cfg.Repairs[0]);
        }

        [Fact]
        public void Two_roles_on_one_chord_leave_the_second_unassigned_rather_than_colliding()
        {
            var cfg = new SystemWideKeysConfig
            {
                Leader = (int)(Keys.Space | Keys.Control | Keys.Alt),   // the PTT default
            };
            cfg.Sanitize();
            Assert.Equal((int)(Keys.Space | Keys.Control | Keys.Alt), cfg.Leader);
            Assert.Equal(0, cfg.PushToTalk);
            Assert.Contains(cfg.Repairs, r => r.Contains("push to talk"));
        }

        [Fact]
        public void An_unassigned_role_is_a_legitimate_choice_and_is_not_repaired()
        {
            var cfg = new SystemWideKeysConfig { TransmitLock = 0 };
            cfg.Sanitize();
            Assert.Equal(0, cfg.TransmitLock);
            Assert.Empty(cfg.Repairs);
        }

        [Fact]
        public void A_healthy_file_round_trips_through_the_key_set_unchanged()
        {
            var cfg = new SystemWideKeysConfig();
            cfg.Sanitize();
            Assert.Empty(cfg.Repairs);
            var set = cfg.ToKeySet();
            Assert.Equal(SystemWideKeySet.Defaults, set);

            var moved = set.With(SystemWideRole.Leader, Keys.Oem2 | Keys.Alt | Keys.Shift);
            cfg.FromKeySet(moved with { Enabled = false });
            Assert.False(cfg.Enabled);
            Assert.Equal((int)(Keys.Oem2 | Keys.Alt | Keys.Shift), cfg.Leader);
            Assert.Equal(SystemWideRole.Leader, cfg.ToKeySet().RoleOf(Keys.Oem2 | Keys.Alt | Keys.Shift));
        }

        [Fact]
        public void The_config_survives_a_disk_round_trip()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "jjflex-systemwide-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cfg = new SystemWideKeysConfig { Enabled = false, Leader = (int)(Keys.K | Keys.Control | Keys.Alt) };
                Assert.True(cfg.Save(dir));
                var back = SystemWideKeysConfig.Load(dir);
                Assert.False(back.Enabled);
                Assert.Equal((int)(Keys.K | Keys.Control | Keys.Alt), back.Leader);
                Assert.Equal((int)SystemWideKeySet.DefaultPushToTalk, back.PushToTalk);
            }
            finally
            {
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void A_missing_file_yields_the_defaults()
        {
            var cfg = SystemWideKeysConfig.Load(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "jjflex-nowhere-" + Guid.NewGuid().ToString("N")));
            Assert.True(cfg.Enabled);
            Assert.Equal(SystemWideKeySet.Defaults, cfg.ToKeySet());
        }
    }
}
