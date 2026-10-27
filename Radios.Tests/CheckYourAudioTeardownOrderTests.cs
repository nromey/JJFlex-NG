using System;
using System.IO;
using System.Threading.Tasks;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The teardown before a disconnect waits, bounded, for the radio's restore
    /// owner and then the command queue, and never calls a failure a completion
    /// (#835, fix rounds 2 and 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round 2 waited on the window's session and a guess at whether there was
    /// work, so a check that had already ended, or whose window had closed, was
    /// not waited for at all. Round 3 waits on the radio's
    /// <see cref="CheckRestorations"/>, which holds every change regardless.
    /// These run the real <see cref="AudioCheckTeardown"/> with fake stages and
    /// pin the window and the shell to it; the owner's own behaviour is
    /// <c>CheckRestorationTests</c>.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioTeardownOrderTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

        [Fact]
        public async Task The_restores_come_first_and_the_drain_last()
        {
            var order = new System.Collections.Generic.List<string>();
            var restored = new TaskCompletionSource<bool>();
            var run = AudioCheckTeardown.RunAsync(
                settled: () => { order.Add("settle-asked"); return restored.Task; },
                withheld: () => false,
                drain: _ => { order.Add("drain"); return Task.FromResult(true); },
                bound: Bound);
            await Task.Delay(150);
            Assert.DoesNotContain("drain", order);
            restored.SetResult(true);
            var result = await run;
            Assert.True(result.Completed);
            Assert.Equal(new[] { "settle-asked", "drain" }, order);
        }

        [Fact]
        public async Task Restores_withheld_by_a_transmitting_radio_are_named_as_withheld_not_restored()
        {
            var result = await AudioCheckTeardown.RunAsync(
                settled: () => new TaskCompletionSource<bool>().Task,
                withheld: () => true,
                drain: _ => Task.FromResult(true),
                bound: TimeSpan.FromMilliseconds(200));
            Assert.False(result.Completed);
            Assert.Equal("still-transmitting", result.Stage);
        }

        [Fact]
        public async Task A_restore_still_running_at_the_bound_is_named_and_the_close_is_not_held()
        {
            var started = DateTime.UtcNow;
            var result = await AudioCheckTeardown.RunAsync(
                settled: () => new TaskCompletionSource<bool>().Task,
                withheld: () => false,
                drain: _ => Task.FromResult(true),
                bound: TimeSpan.FromMilliseconds(300));
            Assert.Equal(new AudioCheckTeardownResult(false, "restore"), result);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task A_faulted_or_cancelled_or_failed_restore_is_unconfirmed_never_completed()
        {
            // Codex round 3, follow-up 5: an ending that faulted was waited on
            // and then reported Completed.
            var faulted = Task.FromException<bool>(new InvalidOperationException("boom"));
            var cancelled = Task.FromCanceled<bool>(new System.Threading.CancellationToken(true));
            foreach (var settle in new[] { faulted, cancelled, Task.FromResult(false) })
            {
                var result = await AudioCheckTeardown.RunAsync(() => settle, () => false, _ => Task.FromResult(true), Bound);
                Assert.Equal(new AudioCheckTeardownResult(false, "faulted"), result);
            }
            var threw = await AudioCheckTeardown.RunAsync(() => throw new InvalidOperationException(), () => false,
                _ => Task.FromResult(true), Bound);
            Assert.Equal("faulted", threw.Stage);
        }

        [Fact]
        public async Task A_queue_that_does_not_drain_is_unconfirmed()
        {
            var result = await AudioCheckTeardown.RunAsync(() => Task.FromResult(true), () => false,
                _ => Task.FromResult(false), Bound);
            Assert.Equal(new AudioCheckTeardownResult(false, "queue"), result);
        }

        [Fact]
        public async Task The_drain_on_a_radio_with_no_command_loop_says_no_at_once()
        {
            using var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });
            var started = DateTime.UtcNow;
            Assert.False(await rig.DrainCommandQueueAsync(TimeSpan.FromSeconds(5)));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        }

        private static string Read(string path) => File.ReadAllText(Path.Combine(IntegrationPassTree.Root, path)).Replace("\r\n", "\n");

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "Could not find " + signature);
            int open = source.IndexOf('{', at), depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("Unbalanced braces after " + signature);
        }

        [Fact]
        public void The_window_shuts_admission_first_and_waits_on_the_radios_owner_not_its_session()
        {
            string shell = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.cs");
            string teardown = Body(shell, "public static void EndCheckBeforeTeardown(FlexBase? rig)");
            int shut = teardown.IndexOf("rig.BeginCheckTeardown();", StringComparison.Ordinal);
            int end = teardown.IndexOf("ForceEnd(", StringComparison.Ordinal);
            int settle = teardown.IndexOf("rig.SettleChecksForTeardownAsync(TeardownBound)", StringComparison.Ordinal);
            int pump = teardown.IndexOf("Dispatcher.PushFrame(", StringComparison.Ordinal);
            int open = teardown.IndexOf("rig.EndCheckTeardown();", StringComparison.Ordinal);
            Assert.True(shut >= 0 && end > shut && settle > end && pump > settle && open > pump,
                "admission shut, then the check ended, then the owner settled, then the wait, then admission reopened");
            // No guess at whether there is work, and no wait on the window's session.
            Assert.DoesNotContain("hadWork", teardown);
            Assert.DoesNotContain("_sessionEnding", teardown);
            Assert.Contains("audio.check.teardown_withheld", teardown);
            Assert.Contains("audio.check.teardown_unconfirmed", teardown);
            // It runs whether or not the window is still open.
            Assert.DoesNotContain("if (inst == null) return;", teardown);
            Assert.Contains("Dialogs.CheckYourAudioDialog.EndCheckBeforeTeardown(RigControl);", Read("JJFlexWpf/MainWindow.xaml.cs"));
        }

        [Fact]
        public void A_new_check_and_app_keying_are_refused_while_a_teardown_holds_admission()
        {
            string check = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            string toggle = Body(check, "public void ToggleAudioCheck()");
            int refuse = toggle.IndexOf("if (RefusedForTeardown(_rig)) return;", StringComparison.Ordinal);
            Assert.True(refuse >= 0 && refuse < toggle.IndexOf("StartOffAirAsync", StringComparison.Ordinal));
            Assert.Contains("|| rig.TeardownInProgress", Body(check, "public async Task<bool> StartAsync(AudioCheckStartGate.Lease start)"));
            string ptt = Read("JJFlexWpf/PttSafetyController.cs");
            Assert.Contains("if (State == PttState.Idle && RefusedForTeardown()) return;", Body(ptt, "public void PttDown()"));
            Assert.Contains("if (State == PttState.Idle && RefusedForTeardown()) return;", Body(ptt, "public void ToggleLock()"));
        }

        [Fact]
        public void An_off_air_take_ending_for_a_teardown_restores_and_does_not_play()
        {
            string offAir = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.OffAir.cs");
            string finish = Body(offAir, "private async Task FinishIqAsync(string reason)");
            Assert.Contains("if (_iqClosing || _tearingDown) { AnnounceClosedRecovery(run.Restored); return; }", finish);
            // And the take is the owner's too, so a closed window's take is still stopped and waited for.
            Assert.Contains("rig.CheckRestorations.Register(\"off-air take\"", offAir);
        }
    }
}
