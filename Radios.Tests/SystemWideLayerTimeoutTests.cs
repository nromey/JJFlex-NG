using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The clock on the system-wide JJ key's bare waiting state (Noel,
    /// 2026-10-05, filed under #689): it times out, every key press resets
    /// it, and an open help surface suspends it entirely.
    /// </summary>
    /// <remarks>
    /// The pure half drives <see cref="SystemWideLayerTimeout"/> and
    /// <see cref="SystemWideKeyDecider"/> from numbers. The host half is read
    /// from <c>SystemWideKeys.cs</c> because the host is a static class around
    /// a live hook: the clock is touched where the layer arms, where any key
    /// lands while it waits, and where the help-armed relay arrives; the tick
    /// polls it with the surface flag and expires the decider on its verdict.
    /// </remarks>
    public sealed class SystemWideLayerTimeoutTests
    {
        private const int T = SystemWideLayerTimeout.TimeoutMs;
        private const uint VK_J = 0x4A;
        private const uint VK_A = 0x41;
        private const uint VK_LSHIFT = 0xA0;
        private static readonly SystemWideModifiers CtrlShift = new(true, false, true);

        // ── the clock ───────────────────────────────────────────────────

        [Fact]
        public void A_bare_waiting_state_expires_after_the_timeout_and_not_before()
        {
            var c = new SystemWideLayerTimeout();
            c.Touch(1000);
            Assert.False(c.Poll(1000 + T - 1, waiting: true, surfaceOpen: false));
            Assert.True(c.Poll(1000 + T, waiting: true, surfaceOpen: false));
        }

        [Fact]
        public void Every_key_press_restarts_the_clock()
        {
            var c = new SystemWideLayerTimeout();
            c.Touch(0);
            c.Touch(T - 100);                      // a key, just before expiry
            Assert.False(c.Poll(T + 100, waiting: true, surfaceOpen: false));
            Assert.True(c.Poll(T - 100 + T, waiting: true, surfaceOpen: false));
        }

        [Fact]
        public void An_open_help_surface_suspends_the_clock_and_it_starts_fresh_when_the_surface_closes()
        {
            // The operator opened the list or the explorer and is reading it
            // at a screen reader's pace. Minutes pass. Nothing expires. When
            // the surface closes a full timeout starts from then.
            var c = new SystemWideLayerTimeout();
            c.Touch(0);
            long lastOpenPoll = 0;
            for (long t = 50; t < 10 * T; t += 50)
            {
                Assert.False(c.Poll(t, waiting: true, surfaceOpen: true), "expired at " + t + " with a surface open");
                lastOpenPoll = t;
            }

            // The clock was held at the last poll that saw the surface open, so
            // a full timeout runs from there once it is closed.
            Assert.False(c.Poll(lastOpenPoll + T - 1, waiting: true, surfaceOpen: false));
            Assert.True(c.Poll(lastOpenPoll + T, waiting: true, surfaceOpen: false));
        }

        [Fact]
        public void Nothing_waiting_never_expires_and_leaves_the_clock_fresh_for_the_next_arm()
        {
            var c = new SystemWideLayerTimeout();
            c.Touch(0);
            Assert.False(c.Poll(5 * T, waiting: false, surfaceOpen: false));
            // The layer arms much later without the host touching: the poll
            // that saw nothing waiting kept the clock at now, so it does not
            // expire on the first tick after arming.
            Assert.False(c.Poll(5 * T + 50, waiting: true, surfaceOpen: false));
        }

        // ── the decider's half ──────────────────────────────────────────

        [Fact]
        public void The_decider_is_waiting_after_the_jj_key_and_not_after_the_second_key()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
            Assert.False(d.Waiting);
            Assert.Equal(SystemWideAction.LeaderArmed, d.Decide(VK_J, true, CtrlShift).Action);
            Assert.True(d.Waiting);
            Assert.Equal(SystemWideAction.LeaderKey, d.Decide(VK_A, true, SystemWideModifiers.None).Action);
            Assert.False(d.Waiting);
        }

        [Fact]
        public void The_help_armed_state_is_also_waiting()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
            d.SetHelpArmed(true);
            Assert.True(d.Waiting);
            d.SetHelpArmed(false);
            Assert.False(d.Waiting);
        }

        [Fact]
        public void After_expiry_the_next_key_goes_to_the_other_program_and_the_jj_keys_own_up_is_still_swallowed()
        {
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
            d.Decide(VK_J, true, CtrlShift);                       // armed, JJ key still physically down
            d.ExpireWaiting();
            Assert.False(d.Waiting);

            var next = d.Decide(VK_A, true, SystemWideModifiers.None);
            Assert.Equal(SystemWideDecision.Pass, next);          // NOT a second key, NOT swallowed

            var up = d.Decide(VK_J, false, SystemWideModifiers.None);
            Assert.True(up.Swallow);                              // the logger never sees a stray key-up
            Assert.Equal(SystemWideAction.None, up.Action);
        }

        [Fact]
        public void A_modifier_pressed_while_waiting_passes_through_and_leaves_the_layer_waiting()
        {
            // So the host can restart the clock on it: the operator reaching
            // for Shift+A has pressed a key, and the layer is still live.
            var d = new SystemWideKeyDecider { Set = SystemWideKeySet.Defaults };
            d.Decide(VK_J, true, CtrlShift);
            Assert.Equal(SystemWideDecision.Pass, d.Decide(VK_LSHIFT, true, CtrlShift));
            Assert.True(d.Waiting);
        }

        // ── the host wiring, read from source ───────────────────────────

        [Fact]
        public void The_host_touches_the_clock_where_the_layer_arms_and_where_the_help_relay_arrives()
        {
            string src = Read("JJFlexWpf", "SystemWideKeys.cs");
            string callback = MethodBody(src, "private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)");
            Assert.Contains("if (isDown && _decider.Waiting)", callback);
            Assert.Contains("_layer.Touch(now)", callback);

            string relay = MethodBody(src, "public static void NoteHelpArmed(bool armed)");
            Assert.Contains("_layer.Touch(", relay);
            Assert.Contains("StartWatchLocked()", relay);
        }

        [Fact]
        public void The_tick_polls_the_clock_with_the_surface_flag_and_expires_the_decider_quietly()
        {
            string src = Read("JJFlexWpf", "SystemWideKeys.cs");
            string tick = MethodBody(src, "private static void WatchTick(object? _)");
            Assert.Contains("KeyHelpSurfaces.IsOpen", tick);
            Assert.Contains("_layer.Poll(now, _decider.Waiting, surfaceOpen)", tick);
            Assert.Contains("_decider.ExpireWaiting()", tick);
            Assert.Contains("LeaderHelpLetGo?.Invoke()", tick);
            Assert.DoesNotContain("ScreenReaderOutput", tick);   // expiry says nothing
            Assert.DoesNotContain("LeaderCancelled?.Invoke()", tick);
            // The poll stays alive while the layer waits, not only while PTT is in flight.
            Assert.Contains("stop = !_watch.Busy && !_decider.Waiting;", tick);
        }

        [Fact]
        public void Positive_control_the_scan_distinguishes_the_tick_from_the_callback()
        {
            string src = Read("JJFlexWpf", "SystemWideKeys.cs");
            Assert.DoesNotContain("_layer.Poll(", MethodBody(src, "private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)"));
            Assert.DoesNotContain("_watch.NoteDown(", MethodBody(src, "private static void WatchTick(object? _)"));
        }

        // ── machinery ───────────────────────────────────────────────────

        private static string Read(params string[] parts)
        {
            string path = Path.Combine(IntegrationPassTree.Root, Path.Combine(parts));
            Assert.True(File.Exists(path), path + " has moved; this rule is scanning nothing.");
            return File.ReadAllText(path);
        }

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find '" + signature + "'; re-anchor this rule rather than letting it pass on nothing.");
            int open = src.IndexOf('{', at + signature.Length);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0)
                    return src.Substring(at, i - at + 1);
            }
            Assert.Fail("Unbalanced braces after '" + signature + "'.");
            return string.Empty;
        }
    }
}
