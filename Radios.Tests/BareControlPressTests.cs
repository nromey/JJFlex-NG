using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The bare Control that skips Check your audio's countdown (#835, ruled by
    /// Noel 2026-10-29 01:05): Control pressed and released with no other key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why bare matters.</b> Ctrl+Enter, which CANCELS the count, and
    /// Ctrl+Space both begin with Control down. A skip on Control-down, or on
    /// any Control release, would turn the cancel into a transmission. These
    /// tests hold that every chord that merely starts with Control stays a
    /// chord, including chords whose letter this app's own system-wide hook
    /// swallows before the window ever sees it (Codex round 1, B1).
    /// </para>
    /// <para>
    /// What they cannot see: which keys WPF actually delivers to the window
    /// under each screen reader, and the hook's live thread; that is a bench
    /// step. The composition below runs the REAL hook decider, the real
    /// withheld-key counter and the real recogniser, wired the way the hook
    /// and the window wire them.
    /// </para>
    /// </remarks>
    public sealed class BareControlPressTests
    {
        // Key identities. Any distinct numbers do; these are the virtual keys.
        private const int Ctl = 0xA2, RCtl = 0xA3, Enter = 0x0D, Space = 0x20, Shift = 0xA0, Letter = 0x41;

        [Fact]
        public void Control_pressed_and_released_alone_is_a_bare_press()
        {
            var p = new BareControlPress();
            p.KeyDown(Ctl, isControl: true, isRepeat: false);
            Assert.True(p.KeyUp(Ctl, isControl: true));
        }

        [Fact]
        public void A_held_Control_that_repeats_is_still_bare()
        {
            var p = new BareControlPress();
            p.KeyDown(Ctl, true, false);
            p.KeyDown(Ctl, true, true);
            p.KeyDown(Ctl, true, true);
            Assert.True(p.KeyUp(Ctl, true));
        }

        [Fact]
        public void Ctrl_Enter_the_cancel_is_never_a_bare_press()
        {
            var p = new BareControlPress();
            p.KeyDown(Ctl, true, false);
            p.KeyDown(Enter, false, false);
            Assert.False(p.KeyUp(Enter, false));
            Assert.False(p.KeyUp(Ctl, true)); // releasing Control after Enter must not skip
        }

        [Fact]
        public void Ctrl_Space_and_Control_with_any_other_modifier_are_not_bare()
        {
            var space = new BareControlPress();
            space.KeyDown(Ctl, true, false);
            space.KeyDown(Space, false, false);
            space.KeyDown(Space, false, true);  // Space repeating
            Assert.False(space.KeyUp(Ctl, true));

            var shift = new BareControlPress();
            shift.KeyDown(Shift, false, false); // Shift first
            shift.KeyDown(Ctl, true, false, otherModifierHeld: true);
            Assert.False(shift.KeyUp(Ctl, true));

            // And a chord seen only through the modifier state still counts.
            var alt = new BareControlPress();
            alt.KeyDown(Ctl, true, false);
            alt.KeyDown(Ctl, true, true, otherModifierHeld: true);
            Assert.False(alt.KeyUp(Ctl, true));
        }

        [Fact]
        public void A_key_released_without_its_press_being_seen_is_not_bare()
        {
            // Control went down in another window (or before this one had
            // focus) and comes up here: no press was seen, so nothing fires.
            var p = new BareControlPress();
            Assert.False(p.KeyUp(Ctl, true));
        }

        [Fact]
        public void A_bare_press_fires_once_and_the_next_one_starts_clean()
        {
            var p = new BareControlPress();
            p.KeyDown(Ctl, true, false);
            Assert.True(p.KeyUp(Ctl, true));
            Assert.False(p.KeyUp(Ctl, true));

            // A chord, then a fresh bare press.
            p.KeyDown(Ctl, true, false);
            p.KeyDown(Letter, false, false);
            Assert.False(p.KeyUp(Ctl, true));
            p.KeyUp(Letter, false);
            p.KeyDown(Ctl, true, false);
            Assert.True(p.KeyUp(Ctl, true));
        }

        [Fact]
        public void Losing_focus_forgets_a_press_in_progress()
        {
            var p = new BareControlPress();
            p.KeyDown(Ctl, true, false);
            p.Reset();
            Assert.False(p.KeyUp(Ctl, true));
        }

        [Fact]
        public void Two_Control_keys_held_together_are_still_one_bare_press()
        {
            // Left and Right Control are both Control; holding both and
            // letting go is still nothing but Control. It fires on the first
            // release and not again.
            var p = new BareControlPress();
            p.KeyDown(Ctl, true, false);
            p.KeyDown(RCtl, true, false);
            Assert.True(p.KeyUp(Ctl, true));
            Assert.False(p.KeyUp(RCtl, true));
        }

        /// <summary>
        /// Codex round 1, F1: a key already held when Control goes down makes
        /// a chord, even if it does not repeat before Control comes up.
        /// </summary>
        [Fact]
        public void A_key_already_held_when_Control_goes_down_makes_a_chord()
        {
            var p = new BareControlPress();
            p.KeyDown(Letter, false, false);
            p.KeyDown(Ctl, true, false);
            Assert.False(p.KeyUp(Ctl, true));

            // Once that key is let go, a bare Control is bare again.
            p.KeyUp(Letter, false);
            p.KeyDown(Ctl, true, false);
            Assert.True(p.KeyUp(Ctl, true));
        }

        /// <summary>
        /// The window must see every key, INCLUDING the ones it has already
        /// handled. Ctrl+Enter's Enter is handled by the window's own key
        /// handler (it is the cancel); a tracker that did not see it would read
        /// the Control release that follows as bare, and turn the cancel into a
        /// transmission. And the window must count the keys this app's hooks
        /// swallow before WPF sees them. Source rules, because the window and
        /// the hook are never built here.
        /// </summary>
        [Fact]
        public void Check_your_audio_feeds_the_tracker_every_key_and_the_hooks_report_what_they_swallow()
        {
            string src = Read("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Countdown.cs");
            Assert.Contains("AddHandler(PreviewKeyDownEvent, new KeyEventHandler(CountdownSkipKeyDown), handledEventsToo: true)", src);
            Assert.Contains("AddHandler(PreviewKeyUpEvent, new KeyEventHandler(CountdownSkipKeyUp), handledEventsToo: true)", src);
            Assert.Contains("BareControlPress _bareControl = new(() => WithheldKeyCounter.Shared.Downs);", src);
            // And the skip is reached only from the bare release.
            int up = src.IndexOf("private void CountdownSkipKeyUp", System.StringComparison.Ordinal);
            Assert.True(up > 0);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"\.Skip\(\)").Count);
            Assert.True(src.IndexOf(".Skip()", System.StringComparison.Ordinal) > up);
            Assert.True(src.IndexOf("if (!bare) return;", up, System.StringComparison.Ordinal) > up);

            // Every place a hook swallows a key reports it first.
            string hook = Read("JJFlexWpf", "SystemWideKeys.cs");
            Assert.Matches(@"WithheldKeyCounter\.Shared\.Report\(data\.vkCode, isDown, d\);\s*\r?\n\s*if \(d\.Swallow\)\s*\r?\n\s*return \(IntPtr\)1;", hook);
            string help = Read("JJFlexWpf", "HelpLauncher.cs");
            Assert.Matches(@"WithheldKeyCounter\.Shared\.NoteWithheldDown\(\);\s*\r?\n\s*return \(IntPtr\)1;", help);
        }

        // ── The real hook decider, composed with the recogniser (Codex B1) ──

        private const uint VCtl = 0xA2, VShift = 0xA0, VJ = 0x4A, VK = 0x4B, VEsc = 0x1B, VSpace = 0x20, VAlt = 0xA4;

        /// <summary>
        /// Replay key events through the real system-wide decider and the real
        /// withheld-key counter, handing the recogniser only what the hook lets
        /// through, the way the window receives it. True when any release
        /// reads as a bare Control.
        /// </summary>
        private static bool Replay(bool systemWide, params (uint vk, bool down, bool ctrl, bool alt, bool shift)[] keys)
        {
            var decider = new SystemWideKeyDecider { Set = systemWide ? SystemWideKeySet.Defaults : SystemWideKeySet.Off };
            var counter = new WithheldKeyCounter();
            var press = new BareControlPress(() => counter.Downs);
            bool skipped = false;
            foreach (var k in keys)
            {
                var d = decider.Decide(k.vk, k.down, new SystemWideModifiers(k.ctrl, k.alt, k.shift));
                counter.Report(k.vk, k.down, d);
                if (d.Swallow) continue;
                bool control = k.vk == 0xA2 || k.vk == 0xA3;
                if (k.down) press.KeyDown((int)k.vk, control, false, k.alt || k.shift);
                else skipped |= press.KeyUp((int)k.vk, control);
            }
            return skipped;
        }

        private static readonly (uint, bool, bool, bool, bool)[] JJKey =
        {
            (VCtl, true, true, false, false), (VShift, true, true, false, true), (VJ, true, true, false, true),
            (VJ, false, true, false, true), (VShift, false, true, false, false), (VCtl, false, false, false, false),
        };

        [Fact]
        public void The_system_wide_JJ_key_then_Ctrl_K_is_not_a_bare_Control()
        {
            var ctrlK = new (uint, bool, bool, bool, bool)[]
            {
                (VCtl, true, true, false, false), (VK, true, true, false, false),
                (VK, false, true, false, false), (VCtl, false, false, false, false),
            };
            // The hook swallows K, so the window sees Control alone; it must
            // still not read as bare.
            Assert.False(Replay(true, Concat(JJKey, ctrlK)), "a JJ chord's swallowed second key let Control read as bare");
            // Positive controls, on the same composition: an ordinary Ctrl+K
            // with the hook off is a chord, and a real bare Control with the
            // hook ON still skips.
            Assert.False(Replay(false, ctrlK));
            Assert.True(Replay(true, (VCtl, true, true, false, false), (VCtl, false, false, false, false)));
        }

        [Fact]
        public void The_system_wide_JJ_key_cancelled_with_Control_held_is_not_a_bare_Control()
        {
            // Codex's second case: the hook swallows Escape's press (the JJ
            // key's cancel) and passes its release.
            var ctrlEsc = new (uint, bool, bool, bool, bool)[]
            {
                (VCtl, true, true, false, false), (VEsc, true, true, false, false),
                (VEsc, false, true, false, false), (VCtl, false, false, false, false),
            };
            Assert.False(Replay(true, Concat(JJKey, ctrlEsc)));
        }

        [Fact]
        public void The_system_wide_push_to_talk_chord_is_not_a_bare_Control()
        {
            // Ctrl+Alt+Space by default: Space is swallowed, Alt is a modifier.
            Assert.False(Replay(true,
                (VCtl, true, true, false, false), (VAlt, true, true, true, false), (VSpace, true, true, true, false),
                (VSpace, false, true, true, false), (VAlt, false, true, false, false), (VCtl, false, false, false, false)));
            // And with Alt let go before the Space, leaving only Control and a
            // swallowed key in view, still a chord.
            Assert.False(Replay(true,
                (VCtl, true, true, false, false), (VSpace, true, true, true, false),
                (VSpace, false, true, false, false), (VCtl, false, false, false, false)));
        }

        private static (uint, bool, bool, bool, bool)[] Concat(
            (uint, bool, bool, bool, bool)[] a, (uint, bool, bool, bool, bool)[] b)
        {
            var all = new (uint, bool, bool, bool, bool)[a.Length + b.Length];
            a.CopyTo(all, 0);
            b.CopyTo(all, a.Length);
            return all;
        }

        private static string Read(params string[] parts)
        {
            string path = System.IO.Path.Combine(IntegrationPassTree.Root, System.IO.Path.Combine(parts));
            Assert.True(System.IO.File.Exists(path), path + " has moved; this rule reads nothing.");
            return System.IO.File.ReadAllText(path);
        }
    }
}
