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
    /// gesture tracker the hook feeds in order, and the real gate the window
    /// applies, with the window's own events delivered late (the queued
    /// schedule Codex showed in round 2), wired the way the hook and the
    /// window wire them.
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
        /// handled (Ctrl+Enter's Enter is handled by the window itself; a
        /// tracker that missed it would turn the cancel into a transmission).
        /// And while the system-wide hook is installed, only the hook's
        /// verdict may skip, because only the hook sees every edge in order
        /// (Codex round 2). Source rules, because the window and the hook are
        /// never built here.
        /// </summary>
        [Fact]
        public void Check_your_audio_takes_the_hooks_verdict_and_decides_alone_only_without_a_hook()
        {
            string src = Read("JJFlexWpf", "Dialogs", "CheckYourAudioDialog.Countdown.cs");
            Assert.Contains("AddHandler(PreviewKeyDownEvent, new KeyEventHandler(CountdownSkipKeyDown), handledEventsToo: true)", src);
            Assert.Contains("AddHandler(PreviewKeyUpEvent, new KeyEventHandler(CountdownSkipKeyUp), handledEventsToo: true)", src);
            Assert.Contains("SystemWideKeys.BareControlReleased += OnHookBareControl;", src);
            Assert.Contains("SystemWideKeys.BareControlReleased -= OnHookBareControl;", src);
            // Exactly two ways to skip: the hook's verdict through the gate,
            // and the window's own bare release, only when no hook is installed.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(src, @"\.Skip\(\)").Count);
            string hookPath = Body(src, "private void OnHookBareControl");
            Assert.Contains("_skipGate.HookVerdictSkips(verdict, SystemWideKeys.Installed, IsActive,", hookPath);
            Assert.Contains("run.Skip();", hookPath);
            string windowPath = Body(src, "private void CountdownSkipKeyUp");
            int bare = windowPath.IndexOf("if (!bare) return;", System.StringComparison.Ordinal);
            int may = windowPath.IndexOf("CountdownSkipGate.WindowMayDecide(_hookAtControlDown, SystemWideKeys.Installed)", System.StringComparison.Ordinal);
            int skip = windowPath.IndexOf(".Skip()", System.StringComparison.Ordinal);
            Assert.True(bare > 0 && may > bare && skip > may, "the window's skip must sit behind both its own bare test and the no-hook test");

            // The hook feeds EVERY edge, passed or swallowed, to the gesture
            // tracker, inside its lock, right after deciding, before any return.
            string hook = Read("JJFlexWpf", "SystemWideKeys.cs");
            string callback = Body(hook, "private static IntPtr HookCallback");
            int decide = callback.IndexOf("d = _decider.Decide(data.vkCode, isDown, held);", System.StringComparison.Ordinal);
            int observe = callback.IndexOf("bare = _bareGestures.Observe(data.vkCode, isDown, now);", System.StringComparison.Ordinal);
            int swallow = callback.IndexOf("if (d.Swallow)", System.StringComparison.Ordinal);
            Assert.True(decide > 0 && observe > decide && swallow > observe, "every edge must reach the gesture tracker, before the swallow");
            Assert.Contains("BareControlReleased?.Invoke(verdict)", callback);
        }

        // ── The real hook, composed with the gesture tracker and the gate ──

        private const uint VCtl = 0xA2, VShift = 0xA0, VJ = 0x4A, VK = 0x4B, VEsc = 0x1B, VSpace = 0x20, VAlt = 0xA4, VEnter = 0x0D;

        /// <summary>
        /// Replay key events the way the app routes them (Codex round 2's
        /// queued schedule). With the hook installed: every edge goes through
        /// the real decider and, in order, the real gesture tracker on the
        /// "hook thread"; the window's events are QUEUED and delivered only
        /// after the whole gesture, and the hook's verdicts after that, through
        /// the real gate, for a running count in the active window that began
        /// before the gesture. With no hook: nothing is swallowed, and the
        /// window's own recogniser decides from its (equally late) events.
        /// True when the count would be skipped.
        /// </summary>
        private static bool Replay(bool systemWide, params (uint vk, bool down, bool ctrl, bool alt, bool shift)[] keys)
        {
            const long countStarted = 1000;
            long tick = countStarted;
            var passed = new System.Collections.Generic.List<(uint vk, bool down, bool alt, bool shift)>();
            var verdicts = new System.Collections.Generic.List<BareControlRelease>();
            var decider = new SystemWideKeyDecider { Set = systemWide ? SystemWideKeySet.Defaults : SystemWideKeySet.Off };
            var gestures = new BareControlGestures();
            foreach (var k in keys)
            {
                tick += 50;
                var d = decider.Decide(k.vk, k.down, new SystemWideModifiers(k.ctrl, k.alt, k.shift));
                if (systemWide)
                {
                    var v = gestures.Observe(k.vk, k.down, tick);
                    if (v != null) verdicts.Add(v.Value);
                }
                if (!d.Swallow) passed.Add((k.vk, k.down, k.alt, k.shift));
            }

            // The window, late. It may decide alone only with no hook.
            bool skipped = false;
            var press = new BareControlPress();
            bool hookInstalled = systemWide;
            foreach (var k in passed)
            {
                bool control = BareControlGestures.IsControlVirtualKey(k.vk);
                if (k.down) press.KeyDown((int)k.vk, control, false, k.alt || k.shift);
                else if (press.KeyUp((int)k.vk, control) && CountdownSkipGate.WindowMayDecide(hookInstalled, hookInstalled))
                    skipped = true;
            }

            // The hook's verdicts, later still.
            var gate = new CountdownSkipGate();
            foreach (var v in verdicts)
                skipped |= gate.HookVerdictSkips(v, hookInstalled, windowActive: true, countRunning: true, countStartedTick: countStarted);
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
            // with the hook off is a chord, and a real bare Control skips with
            // the hook on (through its verdict) and with it off (through the
            // window's own recogniser).
            Assert.False(Replay(false, ctrlK));
            Assert.True(Replay(true, (VCtl, true, true, false, false), (VCtl, false, false, false, false)));
            Assert.True(Replay(false, (VCtl, true, true, false, false), (VCtl, false, false, false, false)));
            // And the cancel stays a cancel with the hook on.
            Assert.False(Replay(true, (VCtl, true, true, false, false), (VEnter, true, true, false, false),
                (VEnter, false, true, false, false), (VCtl, false, false, false, false)));
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

        // ── The gate: which count, which window, once ──────────────────────

        [Fact]
        public void A_hook_verdict_skips_only_this_windows_running_count_pressed_during_it_and_only_once()
        {
            var during = new BareControlRelease(Gesture: 1, DownTick: 2000, UpTick: 2100);
            Assert.True(new CountdownSkipGate().HookVerdictSkips(during, true, true, true, countStartedTick: 1000));

            // Control went down before the count began (the Ctrl+Enter that started it).
            Assert.False(new CountdownSkipGate().HookVerdictSkips(during with { DownTick = 900 }, true, true, true, 1000));
            // Another program was in front.
            Assert.False(new CountdownSkipGate().HookVerdictSkips(during, true, windowActive: false, true, 1000));
            // No count running.
            Assert.False(new CountdownSkipGate().HookVerdictSkips(during, true, true, countRunning: false, 1000));
            // The hook is gone: a stale verdict is not trusted.
            Assert.False(new CountdownSkipGate().HookVerdictSkips(during, hookInstalled: false, true, true, 1000));

            // One gesture, one skip; an older one never.
            var gate = new CountdownSkipGate();
            Assert.True(gate.HookVerdictSkips(during, true, true, true, 1000));
            Assert.False(gate.HookVerdictSkips(during, true, true, true, 1000));
            Assert.False(gate.HookVerdictSkips(during with { Gesture = 0 }, true, true, true, 1000));
            Assert.True(gate.HookVerdictSkips(during with { Gesture = 2 }, true, true, true, 1000));
        }

        [Fact]
        public void The_window_decides_alone_only_when_no_hook_was_installed_at_either_end()
        {
            Assert.True(CountdownSkipGate.WindowMayDecide(false, false));
            Assert.False(CountdownSkipGate.WindowMayDecide(true, false));
            Assert.False(CountdownSkipGate.WindowMayDecide(false, true));
            Assert.False(CountdownSkipGate.WindowMayDecide(true, true));
        }

        [Fact]
        public void The_hook_tracker_numbers_each_bare_press_and_dates_it_from_Controls_press()
        {
            var g = new BareControlGestures();
            Assert.Null(g.Observe(VCtl, true, 100));
            Assert.Null(g.Observe(VCtl, true, 130));   // a repeat keeps the first press's time
            var first = g.Observe(VCtl, false, 400);
            Assert.Equal(new BareControlRelease(1, 100, 400), first);

            Assert.Null(g.Observe(VCtl, true, 500));
            Assert.Null(g.Observe(VK, true, 520));      // swallowed or not, the hook sees it
            Assert.Null(g.Observe(VK, false, 540));
            Assert.Null(g.Observe(VCtl, false, 600));   // a chord: no verdict

            Assert.Null(g.Observe(VCtl, true, 700));
            Assert.Equal(new BareControlRelease(2, 700, 800), g.Observe(VCtl, false, 800));
        }

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, System.StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find " + signature + "; the rule built on it proves nothing.");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(at, i - at + 1);
            }
            throw new System.InvalidOperationException("Unbalanced braces after " + signature);
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
