using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// On a disconnect or an app close, a check's restoration runs while the
    /// radio it changed is still reachable (#835, fix round 1, Codex blocker 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect.</b> <c>SetRig(null)</c> assigned <c>_rig = null</c> and only
    /// then ended the session, and the session read its radio from the
    /// window's current <c>_rig</c>. So <c>ForceEnd</c> found no radio and skipped
    /// <c>RestoreChangedState</c>: the dummy load the check engaged, the power
    /// it capped and the monitor it turned on were never put back by the
    /// session. The Workshop had the same order before the move.
    /// </para>
    /// <para>
    /// <b>The repair, in three parts, each pinned here:</b> the session holds the
    /// radio it changed and restores THAT one; the window ends a session before
    /// it lets go of the radio; and the shell ends any check from
    /// <c>UnwireRadioEvents</c>, which <c>CloseTheRadio</c> calls while the rig and
    /// its transport are still up, before <c>Dispose</c>.
    /// </para>
    /// <para>
    /// <b>What these cannot see:</b> whether the radio applies the queued
    /// restore before the teardown that follows. That is a bench step, with
    /// power and monitor read back after reconnecting.
    /// </para>
    /// </remarks>
    public sealed class CheckYourAudioTeardownTests
    {
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
        public void The_session_restores_the_radio_it_changed_not_the_windows_current_one()
        {
            string check = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.Check.cs");
            Assert.Contains("private readonly FlexBase _changedRig;", check);
            string forceEnd = Body(check, "public void ForceEnd(string? reason)");
            Assert.Contains("RestoreWhenReceiving(_changedRig);", forceEnd);
            Assert.DoesNotContain("var rig = Rig;", forceEnd);
        }

        [Fact]
        public void The_window_ends_a_session_before_it_lets_go_of_the_radio()
        {
            string shell = Read("JJFlexWpf/Dialogs/CheckYourAudioDialog.cs");
            string setRig = Body(shell, "public void SetRig(FlexBase? rig)");
            int end = setRig.IndexOf("_session?.ForceEnd(", StringComparison.Ordinal);
            int drop = setRig.IndexOf("_rig = rig;", StringComparison.Ordinal);
            Assert.True(end >= 0 && drop > end, "the session ends before _rig changes");
            // A disconnect is a change too: the guard must not require an arriving radio.
            Assert.DoesNotContain("if (oldRig != null && rig != null)", setRig);
        }

        [Fact]
        public void The_shell_ends_any_check_while_the_rig_is_still_up()
        {
            string main = Read("JJFlexWpf/MainWindow.xaml.cs");
            string unwire = Body(main, "public void UnwireRadioEvents()");
            int ptt = unwire.IndexOf("_pttController?.ReleaseRigForTeardown(", StringComparison.Ordinal);
            int check = unwire.IndexOf("Dialogs.CheckYourAudioDialog.EndCheckBeforeTeardown(", StringComparison.Ordinal);
            Assert.True(ptt >= 0 && check > ptt, "the check ends right after the PTT hold is released, before anything else is torn down");
            string globals = Read("globals.vb");
            string close = globals.Substring(globals.IndexOf("Friend Sub CloseTheRadio()", StringComparison.Ordinal));
            Assert.True(close.IndexOf("WpfMainWindow?.UnwireRadioEvents()", StringComparison.Ordinal)
                        < close.IndexOf("RigControl.Dispose()", StringComparison.Ordinal),
                "CloseTheRadio unwires before it disposes the rig, which is what makes the order above worth anything");
        }
    }
}
