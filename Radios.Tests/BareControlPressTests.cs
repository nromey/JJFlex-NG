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
            Assert.Contains("_skipGate.HookVerdictSkips(verdict, SystemWideKeys.Installed, IsActive, OwnWindowHandle,", hookPath);
            Assert.Contains("_activeSinceTick, run.IsRunning, _countdownStartedTick", hookPath);
            // Ownership is dated: reactivation restarts it, deactivation ends it.
            Assert.Contains("Activated += (s, e) => _activeSinceTick = Environment.TickCount64;", src);
            Assert.Contains("_activeSinceTick = long.MaxValue;", src);
            Assert.Contains("run.Skip();", hookPath);
            string windowPath = Body(src, "private void CountdownSkipKeyUp");
            int bare = windowPath.IndexOf("if (!bare) return;", System.StringComparison.Ordinal);
            int may = windowPath.IndexOf("CountdownSkipGate.WindowMayDecide(SystemWideKeys.EverSawAKey, SystemWideKeys.LastKeyTime, _controlDownTime,", System.StringComparison.Ordinal);
            Assert.Contains("SystemWideKeys.EverSawControlDown, SystemWideKeys.LastControlDownTime)) return;", windowPath);
            // The press's stamp is the Windows stamp on the Control press itself.
            Assert.Contains("_controlDownTime = e.Timestamp;", src);
            int skip = windowPath.IndexOf(".Skip()", System.StringComparison.Ordinal);
            Assert.True(bare > 0 && may > bare && skip > may, "the window's skip must sit behind both its own bare test and the no-hook test");

            // The hook feeds EVERY edge, passed or swallowed, to the gesture
            // tracker, inside its lock, right after deciding, before any return.
            string hook = Read("JJFlexWpf", "SystemWideKeys.cs");
            string callback = Body(hook, "private static IntPtr HookCallback");
            int decide = callback.IndexOf("d = _decider.Decide(data.vkCode, isDown, held);", System.StringComparison.Ordinal);
            int observe = callback.IndexOf("bare = _bareGestures.Observe(data.vkCode, isDown, now, (long)GetForegroundWindow());", System.StringComparison.Ordinal);
            int swallow = callback.IndexOf("if (d.Swallow)", System.StringComparison.Ordinal);
            Assert.True(decide > 0 && observe > decide && swallow > observe, "every edge must reach the gesture tracker, before the swallow");
            Assert.Contains("BareControlReleased?.Invoke(verdict)", callback);
            // Every key edge is stamped as seen before anything can swallow it.
            int stamp = callback.IndexOf("System.Threading.Volatile.Write(ref _lastKeyTime, unchecked((int)data.time));", System.StringComparison.Ordinal);
            Assert.True(stamp > 0 && stamp < decide, "the hook must note every key it sees before deciding or swallowing it");
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
        private static bool Replay(bool systemWide, params (uint vk, bool down, bool ctrl, bool alt, bool shift)[] keys) =>
            Replay(systemWide, hookDead: false, keys);

        /// <summary>
        /// As above; with <paramref name="hookDead"/>, a hook that installed and
        /// saw keys earlier and has since faulted, been unhooked or been removed
        /// by Windows: it sees, swallows and decides nothing now.
        /// </summary>
        private static bool Replay(bool systemWide, bool hookDead, params (uint vk, bool down, bool ctrl, bool alt, bool shift)[] keys)
        {
            const long countStarted = 1000;
            long tick = countStarted;
            bool hookAlive = systemWide && !hookDead;
            bool hookEverSaw = systemWide;                       // a dead hook saw keys before it died
            uint hookLast = (uint)(countStarted - 500);
            var passed = new System.Collections.Generic.List<(uint vk, bool down, bool alt, bool shift, long at)>();
            var verdicts = new System.Collections.Generic.List<BareControlRelease>();
            var decider = new SystemWideKeyDecider { Set = systemWide ? SystemWideKeySet.Defaults : SystemWideKeySet.Off };
            var gestures = new BareControlGestures();
            foreach (var k in keys)
            {
                tick += 50;
                if (!hookAlive) { passed.Add((k.vk, k.down, k.alt, k.shift, tick)); continue; }
                hookLast = (uint)tick;
                var d = decider.Decide(k.vk, k.down, new SystemWideModifiers(k.ctrl, k.alt, k.shift));
                var v = gestures.Observe(k.vk, k.down, tick, Own);
                if (v != null) verdicts.Add(v.Value);
                if (!d.Swallow) passed.Add((k.vk, k.down, k.alt, k.shift, tick));
            }

            // The window, late. It may decide alone only when the hook saw no
            // key of the press, by the Windows stamp each side reads.
            bool skipped = false;
            var press = new BareControlPress();
            bool hookInstalled = hookAlive;
            int controlDownAt = 0;
            foreach (var k in passed)
            {
                bool control = BareControlGestures.IsControlVirtualKey(k.vk);
                if (k.down)
                {
                    if (control && !press.ControlDown) controlDownAt = (int)k.at;
                    press.KeyDown((int)k.vk, control, false, k.alt || k.shift);
                }
                else if (press.KeyUp((int)k.vk, control) && CountdownSkipGate.WindowMayDecide(hookEverSaw, hookLast, controlDownAt))
                    skipped = true;
            }

            // The hook's verdicts, later still.
            var gate = new CountdownSkipGate();
            foreach (var v in verdicts)
                skipped |= gate.HookVerdictSkips(v, hookInstalled, windowActive: true, ownWindow: Own,
                    activeSinceTick: 0, countRunning: true, countStartedTick: countStarted);
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

        private const long Own = 7, Other = 9;

        /// <summary>The gate with this window active since 500 and a count started at 1000, unless a test says otherwise.</summary>
        private static bool Gate(BareControlRelease v, bool hookInstalled = true, bool windowActive = true,
            long activeSince = 500, bool countRunning = true, long countStarted = 1000, CountdownSkipGate? gate = null) =>
            (gate ?? new CountdownSkipGate()).HookVerdictSkips(v, hookInstalled, windowActive, Own, activeSince,
                countRunning, countStarted);

        [Fact]
        public void A_hook_verdict_skips_only_this_windows_running_count_pressed_during_it_and_only_once()
        {
            var during = new BareControlRelease(Gesture: 1, DownTick: 2000, UpTick: 2100, DownWindow: Own, UpWindow: Own);
            Assert.True(Gate(during));

            // Control went down before the count began (the Ctrl+Enter that started it).
            Assert.False(Gate(during with { DownTick = 900 }));
            // Another program is in front now.
            Assert.False(Gate(during, windowActive: false));
            // No count running.
            Assert.False(Gate(during, countRunning: false));
            // The hook is gone: a stale verdict is not trusted.
            Assert.False(Gate(during, hookInstalled: false));

            // One gesture, one skip; an older one never.
            var gate = new CountdownSkipGate();
            Assert.True(Gate(during, gate: gate));
            Assert.False(Gate(during, gate: gate));
            Assert.False(Gate(during with { Gesture = 0 }, gate: gate));
            Assert.True(Gate(during with { Gesture = 2 }, gate: gate));
        }

        /// <summary>Codex round 3, F1: a press at the count's own start tick is not provably after it.</summary>
        [Fact]
        public void A_press_stamped_at_the_counts_own_start_never_skips()
        {
            var v = new BareControlRelease(1, DownTick: 1000, UpTick: 1100, DownWindow: Own, UpWindow: Own);
            Assert.False(Gate(v, countStarted: 1000));
            Assert.True(Gate(v with { DownTick = 1001 }, countStarted: 1000));
        }

        /// <summary>
        /// Codex round 3, B2: the gesture must belong to THIS window from the
        /// moment Control went down, not merely have this window in front when
        /// the verdict is read.
        /// </summary>
        [Fact]
        public void A_Control_pressed_in_another_window_then_released_here_never_skips()
        {
            // Codex's mouse-activation schedule: Control down at 1100 in another
            // window, a mouse click brings this window in front at 1300 (no
            // keyboard edge), Control released here at 1500. Now this window is
            // active, but it was not when Control went down.
            var mouse = new BareControlRelease(1, DownTick: 1100, UpTick: 1500, DownWindow: Other, UpWindow: Own);
            Assert.False(Gate(mouse, activeSince: 1300));
            // Even if the hook could not name the window, the activation is later than the press.
            Assert.False(Gate(mouse with { DownWindow = Own }, activeSince: 1300));
            // And the window named at the press decides on its own.
            Assert.False(Gate(mouse, activeSince: 500));
        }

        [Fact]
        public void A_verdict_queued_while_another_window_was_in_front_never_skips_once_this_one_returns()
        {
            // Press and release both in another window; the verdict is read
            // only after this window has come back to the front.
            var elsewhere = new BareControlRelease(1, DownTick: 1100, UpTick: 1200, DownWindow: Other, UpWindow: Other);
            Assert.False(Gate(elsewhere, activeSince: 1300));
            // Read while the other window is still in front.
            Assert.False(Gate(elsewhere, windowActive: false));
            // Pressed here, this window went away and came back before the release.
            var leftAndBack = new BareControlRelease(1, DownTick: 1100, UpTick: 1500, DownWindow: Own, UpWindow: Own);
            Assert.False(Gate(leftAndBack, activeSince: 1300));
            // Positive control: pressed and released here, this window in front the whole time.
            Assert.True(Gate(leftAndBack, activeSince: 1000));
            // And an activation stamped at the very tick of the press is not provably before it.
            Assert.False(Gate(leftAndBack, activeSince: 1100));
        }

        [Fact]
        public void The_window_decides_alone_only_when_the_hook_saw_no_key_of_the_press()
        {
            // No hook has seen anything this session: the window decides.
            Assert.True(CountdownSkipGate.WindowMayDecide(false, 0, 5000));
            // The hook saw only keys from before Control went down (it has since
            // died): the window decides.
            Assert.True(CountdownSkipGate.WindowMayDecide(true, 4999, 5000));
            // The hook saw Control's own press (same stamp) or anything after:
            // its verdict decides, the window does not.
            Assert.False(CountdownSkipGate.WindowMayDecide(true, 5000, 5000));
            Assert.False(CountdownSkipGate.WindowMayDecide(true, 5050, 5000));
            // Across the 32-bit wrap the order still holds.
            Assert.False(CountdownSkipGate.WindowMayDecide(true, 10, unchecked((int)0xFFFFFFF0u)));
            Assert.True(CountdownSkipGate.WindowMayDecide(true, 0xFFFFFFE0u, unchecked((int)0xFFFFFFF0u)));
        }

        /// <summary>
        /// Codex round 1030, F1: the hook saw Control (stamp 1000), swallowed a
        /// re-injected key whose SUPPLIED stamp is older (999), then died. By
        /// "last key" alone the hook looks as if it saw nothing of the press and
        /// the window would skip. The hook's own record of THIS Control press,
        /// same event and same stamp, keeps the decision with the hook.
        /// </summary>
        [Fact]
        public void A_swallowed_key_with_an_older_supplied_stamp_does_not_hand_the_decision_to_the_window()
        {
            // The counterexample, by the last-key rule alone: the window decides.
            Assert.True(CountdownSkipGate.WindowMayDecide(true, 999, 1000));
            // With the hook's last Control press at the window's stamp: it does not.
            Assert.False(CountdownSkipGate.WindowMayDecide(true, 999, 1000, hookEverSawControlDown: true, hookLastControlDownTime: 1000));
            // A dead hook whose last Control press was an earlier one: the window decides.
            Assert.True(CountdownSkipGate.WindowMayDecide(true, 900, 1000, hookEverSawControlDown: true, hookLastControlDownTime: 880));
            // The exact half-range is ambiguous and falls to the hook.
            Assert.False(CountdownSkipGate.WindowMayDecide(true, 0x80000000u, 0));
            // The hook notes Control presses, by their own stamp, before anything can swallow them.
            string hook = Read("JJFlexWpf", "SystemWideKeys.cs");
            string callback = Body(hook, "private static IntPtr HookCallback");
            Assert.Contains("System.Threading.Volatile.Write(ref _lastControlDownTime, unchecked((int)data.time));", callback);
        }

        /// <summary>
        /// Codex round 4's follow-up: with "no hook EVER installed" as the
        /// rule, a hook that installed and then died left the Control skip dead
        /// for the session. A dead hook sees nothing now, so the window decides,
        /// and it still tells a chord from a bare Control.
        /// </summary>
        [Fact]
        public void After_the_hook_dies_the_window_skips_on_a_bare_Control_and_not_on_a_chord()
        {
            var bareCtl = new[] { (VCtl, true, true, false, false), (VCtl, false, false, false, false) };
            var ctrlK = new[] { (VCtl, true, true, false, false), (VK, true, true, false, false),
                                (VK, false, true, false, false), (VCtl, false, false, false, false) };
            Assert.True(Replay(true, hookDead: true, bareCtl));
            Assert.False(Replay(true, hookDead: true, ctrlK));
            Assert.False(Replay(true, hookDead: true, (VCtl, true, true, false, false), (VEnter, true, true, false, false),
                (VEnter, false, true, false, false), (VCtl, false, false, false, false)));
            // Positive control: the same bare press with the hook alive skips by the hook's verdict alone.
            Assert.True(Replay(true, hookDead: false, bareCtl));
        }

        [Fact]
        public void The_hook_tracker_numbers_each_bare_press_and_dates_and_places_it_from_Controls_press()
        {
            var g = new BareControlGestures();
            Assert.Null(g.Observe(VCtl, true, 100, Own));
            Assert.Null(g.Observe(VCtl, true, 130, Other));   // a repeat keeps the first press's time and window
            Assert.Equal(new BareControlRelease(1, 100, 400, Own, Other), g.Observe(VCtl, false, 400, Other));

            Assert.Null(g.Observe(VCtl, true, 500, Own));
            Assert.Null(g.Observe(VK, true, 520, Own));       // swallowed or not, the hook sees it
            Assert.Null(g.Observe(VK, false, 540, Own));
            Assert.Null(g.Observe(VCtl, false, 600, Own));    // a chord: no verdict

            Assert.Null(g.Observe(VCtl, true, 700, Own));
            Assert.Equal(new BareControlRelease(2, 700, 800, Own, Own), g.Observe(VCtl, false, 800, Own));
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
