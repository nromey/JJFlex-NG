using System;
using System.IO;
using System.Threading.Tasks;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The check is put back, in order and to completion, before the shell
    /// tears the radio down (#835, fix round 2, Codex blocker 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>EndCheckBeforeTeardown</c> ran only to the first
    /// await: <c>OnSessionEnded</c> was <c>async void</c>, the Loopback
    /// teardown and an off-air take's restore were discarded tasks, and the
    /// disconnect then stopped the command loop with no final drain, so queued
    /// unkey and restore writes could be lost. The first fix round had moved
    /// the call earlier; earlier is not finished.
    /// </para>
    /// <para>
    /// <b>What these run:</b> the real <see cref="AudioCheckTeardown"/> order
    /// with fake stages, the real drain on an unconnected radio, and pins that
    /// the window waits on them. <b>What they cannot see:</b> a real radio
    /// applying the writes, which is the bench step.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioTeardownOrderTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

        [Fact]
        public async Task Every_stage_runs_in_order_and_the_drain_comes_last()
        {
            var order = new System.Collections.Generic.List<string>();
            int transmitting = 3;
            bool pending = true;
            var ending = new TaskCompletionSource();
            var run = AudioCheckTeardown.RunAsync(
                transmitting: () => { if (transmitting-- > 0) return true; order.Add("receiving"); return false; },
                sessionEnding: () => { order.Add("ending-asked"); return ending.Task; },
                restorePending: () => { if (pending) { order.Add("restore-waited"); pending = false; return true; } return false; },
                drain: _ => { order.Add("drain"); return Task.FromResult(true); },
                bound: Bound);
            await Task.Delay(150);
            Assert.DoesNotContain("drain", order);          // waits for the session's own ending
            ending.SetResult();
            var result = await run;
            Assert.True(result.Completed);
            Assert.Equal(new[] { "receiving", "ending-asked", "restore-waited", "drain" }, order);
        }

        [Theory]
        [InlineData("still-transmitting")]
        [InlineData("session-ending")]
        [InlineData("restore")]
        [InlineData("queue")]
        public async Task A_stage_that_cannot_finish_is_named_and_the_close_is_not_held(string stuck)
        {
            var started = DateTime.UtcNow;
            var result = await AudioCheckTeardown.RunAsync(
                transmitting: () => stuck == "still-transmitting",
                sessionEnding: () => stuck == "session-ending" ? new TaskCompletionSource().Task : Task.CompletedTask,
                restorePending: () => stuck == "restore",
                drain: _ => Task.FromResult(stuck != "queue"),
                bound: TimeSpan.FromMilliseconds(300));
            Assert.False(result.Completed);
            Assert.Equal(stuck, result.Stage);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "the bound held");
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
        public void The_window_keeps_the_sessions_ending_and_waits_on_it_before_the_rig_goes()
        {
            string check = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            Assert.DoesNotContain("private async void OnSessionEnded()", check);
            Assert.Contains("private async Task OnSessionEndedAsync()", check);
            Assert.Contains("_owner._sessionEnding = _owner.OnSessionEndedAsync();", Body(check, "private void End(string? reason)"));
            string ended = Body(check, "private async Task OnSessionEndedAsync()");
            Assert.Contains("await FinishIqAsync(", ended);
            Assert.DoesNotContain("_ = FinishIqAsync(", ended);

            string shell = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.cs");
            string teardown = Body(shell, "public static void EndCheckBeforeTeardown()");
            Assert.Contains("AudioCheckTeardown.RunAsync(", teardown);
            Assert.Contains("DrainCommandQueueAsync", teardown);
            Assert.Contains("Dispatcher.PushFrame(", teardown);
            // A timeout or a failure is traced and said, not swallowed.
            Assert.Contains("audio.check.teardown_unconfirmed", teardown);
        }

        [Fact]
        public void An_off_air_take_ending_for_a_teardown_restores_and_does_not_play()
        {
            string offAir = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.OffAir.cs");
            string finish = Body(offAir, "private async Task FinishIqAsync(string reason)");
            Assert.Contains("if (_iqClosing || _tearingDown) { AnnounceClosedRecovery(run.Restored); return; }", finish);
        }
    }
}
