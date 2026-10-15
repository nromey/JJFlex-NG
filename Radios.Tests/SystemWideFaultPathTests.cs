using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #698: the system-wide push to talk's two fault paths — a hook fault and
    /// teardown with a hold in flight — must not switch the watchdog off
    /// before the release they post has been honoured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong.</b> Both paths posted the release to the UI thread
    /// and then reset and stopped <see cref="SystemWidePttWatch"/>. A blocked
    /// UI thread (#402) never processes the post, and the watch that would
    /// have dropped the carrier from the pool thread was already off — so a
    /// hook fault during a held transmit had no backstop until the UI thread
    /// recovered. <c>GlobalHookThreadTests</c> could not see it: it scans for
    /// blocking TEXT patterns, and a method that merely stops a timer passes.
    /// </para>
    /// <para>
    /// <b>What is pinned.</b> The pure half is
    /// <c>SystemWidePttWatchTests</c>: a handed-off hold reaches the
    /// not-honoured kill. This file pins the host wiring in source, because
    /// <c>SystemWideKeys</c> is a static class around a live keyboard hook
    /// and cannot be exercised in a test process: each fault path hands the
    /// hold to the watch and drops the carrier directly, and neither resets
    /// the watch. The direct drop must be the controller's NON-BLOCKING half
    /// — the full kill can sleep for <see cref="TransmitKillSwitch.ConfirmDownMs"/>
    /// inside the hook callback, which stalls every keystroke on the machine.
    /// </para>
    /// </remarks>
    public sealed class SystemWideFaultPathTests
    {
        private static string Keys() => Read("JJFlexWpf", "SystemWideKeys.cs");
        private static string Controller() => Read("JJFlexWpf", "PttSafetyController.cs");

        [Theory]
        [InlineData("private static void Fault(Exception ex)")]
        [InlineData("private static void UnhookOnHookThread()")]
        public void Each_fault_path_hands_the_hold_to_the_watch_and_drops_the_carrier_without_waiting(string signature)
        {
            string body = MethodBody(Keys(), signature);

            Assert.Contains("_watch.HandOffHold(", body);
            Assert.Contains("DropCarrierWithoutWaiting(", body);
            Assert.Contains("PttEdge?.Invoke(false)", body);
            Assert.DoesNotContain("_watch.Reset()", body);
            Assert.DoesNotContain("KillTransmitNow(", body);
        }

        [Fact]
        public void A_fault_path_may_not_call_the_blocking_kill_from_the_hook_thread()
        {
            // The one place the full kill is allowed is the watch tick, which
            // runs on the thread pool. Everything else in the file reaches the
            // carrier through the non-blocking drop.
            string src = Keys();
            string tick = MethodBody(src, "private static void WatchTick(object? _)");
            string outsideTick = src.Replace(tick, string.Empty);
            Assert.Contains("KillTransmitNow(", tick);
            Assert.DoesNotContain("KillTransmitNow(", outsideTick);
            Assert.Contains("DropCarrierNow(", MethodBody(src, "private static void DropCarrierWithoutWaiting(string source)"));
        }

        [Fact]
        public void The_direct_drop_is_the_non_blocking_half_of_the_kill_and_nothing_more()
        {
            string body = MethodBody(Controller(), "public void DropCarrierNow(string source)");
            Assert.Equal(2, CountOf(body, "TransmitKillSwitch.DropCarrier("));
            Assert.DoesNotContain("TransmitKillSwitch.Request(", body);
            Assert.DoesNotContain("FinishOwnedKill", body);
            Assert.DoesNotContain("Thread.Sleep", body);
            Assert.DoesNotContain(".Invoke(", body);   // no dispatcher marshal either way
        }

        [Fact]
        public void The_watch_is_never_reset_anywhere_in_the_host()
        {
            // Reset exists for tests and for a future caller that genuinely
            // has nothing in flight; the host had two callers and both were
            // the defect.
            Assert.DoesNotContain("_watch.Reset()", Keys());
        }

        [Fact]
        public void Positive_control_the_scan_sees_the_hook_callback_noting_a_down()
        {
            // A body extractor that returned the wrong span would pass every
            // DoesNotContain above for free. Prove it finds what is there.
            string callback = MethodBody(Keys(), "private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)");
            Assert.Contains("_watch.NoteDown(now)", callback);
            Assert.Contains("_watch.NoteUp(now)", callback);
            Assert.DoesNotContain("HandOffHold", callback);

            string kill = MethodBody(Controller(), "public void KillTransmitNow(string source, string? spokenOutcome, string? subject)");
            Assert.Contains("TransmitKillSwitch.Request(", kill);
        }

        // ── machinery ───────────────────────────────────────────────────

        private static string Read(params string[] parts)
        {
            string path = Path.Combine(IntegrationPassTree.Root, Path.Combine(parts));
            Assert.True(File.Exists(path), path + " has moved; this rule is scanning nothing.");
            return File.ReadAllText(path);
        }

        /// <summary>The text of one method, from its signature through its closing brace.</summary>
        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find '" + signature + "'. The method has been renamed or removed; "
                + "re-anchor this rule rather than letting it pass on nothing.");
            int open = src.IndexOf('{', at + signature.Length);
            Assert.True(open > at, "No body after '" + signature + "'.");
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

        private static int CountOf(string text, string needle)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }
    }
}
