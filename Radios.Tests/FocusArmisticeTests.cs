using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #331: a disconnect during connect could put a modal error box
    /// UNDERNEATH a window that grabs focus five times a second.
    ///
    /// <para><b>The path, in order.</b> <c>ShowErrorCallback</c> — a
    /// <c>MessageBox.Show</c> owned by the shell — is wired before
    /// <c>Start()</c> is called. <c>_radioPowerOn</c> goes true inside
    /// <c>Start()</c>, so an SSL or SmartLink drop while it is running
    /// satisfies the disconnect guard and raises that box. The connecting form
    /// is not closed until after <c>Start()</c> and all its retries, and it is
    /// <c>TopMost</c> with a 200 ms focus-reclaim timer whose only stand-down
    /// condition was a flag set by the sign-in windows and nothing else.</para>
    ///
    /// <para><b>Why it is an accessibility defect and not a cosmetic one.</b>
    /// A modal a blind operator cannot reach, under a window stealing focus
    /// repeatedly, is an application that is unusable and unexplainable at the
    /// same time — and the taskkill-class hang this is a miniature of was
    /// supposed to have been fixed.</para>
    ///
    /// <para>The armistice mechanism is testable directly; the wiring that uses
    /// it is pinned by reading the source, because reproducing it needs a live
    /// connect, a real drop, and two message pumps.</para>
    /// </summary>
    public sealed class FocusArmisticeTests
    {
        // ------------------------------------------------------------------
        // The mechanism
        // ------------------------------------------------------------------

        /// <summary>
        /// A modal of ours stands every focus-reclaim loop down, and lets go
        /// again afterwards.
        /// </summary>
        [Fact]
        public void AModalClaimsAndReleasesTheOperatorsAttention()
        {
            Assert.False(WindowFocusForcer.AttentionWindowOpen);

            WindowFocusForcer.PushAttentionWindow();
            try
            {
                Assert.True(WindowFocusForcer.AttentionWindowOpen);
                Assert.True(WindowFocusForcer.FocusReclaimShouldYield);
            }
            finally
            {
                WindowFocusForcer.PopAttentionWindow();
            }

            Assert.False(WindowFocusForcer.AttentionWindowOpen);
            Assert.False(WindowFocusForcer.FocusReclaimShouldYield);
        }

        /// <summary>
        /// A counter, not a flag — dialogs stack, and an inner one closing must
        /// not clear an outer one's claim. That is the same reasoning the
        /// sign-in counter was built on, and the reason this is not a bool.
        /// </summary>
        [Fact]
        public void NestedModalsDoNotClearEachOthersClaim()
        {
            WindowFocusForcer.PushAttentionWindow();
            WindowFocusForcer.PushAttentionWindow();
            try
            {
                WindowFocusForcer.PopAttentionWindow();
                Assert.True(WindowFocusForcer.AttentionWindowOpen);
            }
            finally
            {
                WindowFocusForcer.PopAttentionWindow();
            }

            Assert.False(WindowFocusForcer.AttentionWindowOpen);
        }

        /// <summary>
        /// The two claims are independent. A sign-in window and a modal of ours
        /// are different situations and must not be able to release each other
        /// — which is why #331 added a second counter rather than renaming the
        /// first.
        /// </summary>
        [Fact]
        public void TheSignInClaimAndTheModalClaimAreSeparate()
        {
            WindowFocusForcer.PushSignInWindow();
            try
            {
                Assert.True(WindowFocusForcer.FocusReclaimShouldYield);
                Assert.False(WindowFocusForcer.AttentionWindowOpen);

                WindowFocusForcer.PushAttentionWindow();
                WindowFocusForcer.PopAttentionWindow();

                // The sign-in window is still up.
                Assert.True(WindowFocusForcer.SignInWindowOpen);
                Assert.True(WindowFocusForcer.FocusReclaimShouldYield);
            }
            finally
            {
                WindowFocusForcer.PopSignInWindow();
            }

            Assert.False(WindowFocusForcer.FocusReclaimShouldYield);
        }

        // ------------------------------------------------------------------
        // The wiring
        // ------------------------------------------------------------------

        /// <summary>
        /// The reclaim timer asks the widened question, and gives up TopMost
        /// while it is standing down. Focus alone is not enough: this form is
        /// TopMost, so a shell-owned message box sits under it however the
        /// focus argument comes out, and a sighted helper would see a
        /// connecting box with no explanation beside it.
        /// </summary>
        [Fact]
        public void TheConnectingFormsReclaimTimerYieldsToAnyModal()
        {
            string source = ReadRepoFile("ConnectingForm.vb");

            Assert.Contains("FocusReclaimShouldYield", source, StringComparison.Ordinal);
            Assert.Contains("TopMost = Not standDown", source, StringComparison.Ordinal);

            // Its own escalation prompt is owned by the form specifically so it
            // INHERITS topmost. Dropping topmost for that one would undo the
            // thing that makes it reachable, so it is checked first.
            Assert.Contains("If _escalationActive Then Return", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The mid-connect error box takes the claim, and takes it in a way
        /// that cannot leak — a claim never released leaves every reclaim loop
        /// stood down for the rest of the session.
        /// </summary>
        [Fact]
        public void TheMidConnectErrorBoxClaimsAttentionAndAlwaysReleasesIt()
        {
            string source = ReadRepoFile("globals.vb");

            int wiring = source.IndexOf("WpfMainWindow.ShowErrorCallback", StringComparison.Ordinal);
            Assert.True(wiring >= 0, "ShowErrorCallback is no longer wired in globals.vb — #331's "
                + "whole path moved, and this test must follow it.");

            string region = source.Substring(wiring, Math.Min(900, source.Length - wiring));

            Assert.Contains("PushAttentionWindow", region, StringComparison.Ordinal);
            Assert.Contains("Finally", region, StringComparison.Ordinal);
            Assert.Contains("PopAttentionWindow", region, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every WPF modal in the app takes the claim, because the rule is
        /// stated once at the chokepoint they all go through rather than once
        /// per dialog — which is the form of the rule the next author will
        /// still be obeying without having read it.
        /// </summary>
        [Fact]
        public void EveryWpfModalClaimsAttentionAtTheOneChokepoint()
        {
            string source = ReadRepoFile("JJFlexWpf/JJFlexDialog.cs");

            int method = source.IndexOf("public bool? ShowModalDialog()", StringComparison.Ordinal);
            Assert.True(method >= 0, "JJFlexDialog.ShowModalDialog is gone. If modals now go "
                + "somewhere else, the attention claim has to go with them (#331).");

            string region = source.Substring(method, Math.Min(1400, source.Length - method));

            Assert.Contains("PushAttentionWindow", region, StringComparison.Ordinal);
            Assert.Contains("finally", region, StringComparison.Ordinal);
            Assert.Contains("PopAttentionWindow", region, StringComparison.Ordinal);
        }

        private static string ReadRepoFile(string relative)
            => File.ReadAllText(
                Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
