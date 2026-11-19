#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The SmartLink sign-in reaches its UI thread through an invoker the app
    /// hands it, not by casting the parent window to a WinForms control
    /// (#1205, junk box batch 16). Every cast had a silent fallback, so the day
    /// the shell stopped being a form, the browser sign-in would have been
    /// shown on the SmartLink thread with nothing to say so.
    /// </summary>
    /// <remarks>
    /// <b>No test here constructs a Form, a WebView2 control or any WinForms
    /// UI</b>: a check that did put an "Exception" box on the live desktop. The
    /// parent is <see cref="FakeWindow"/>, an <see cref="IWin32Window"/> that is
    /// not a <see cref="Control"/>, and the invoker's delegates only record what
    /// they were handed. The posted focus restore is recorded, never run, so no
    /// window is brought to the foreground.
    /// </remarks>
    public class SmartLinkUiThreadTests
    {
        private sealed class FakeWindow : IWin32Window
        {
            public IntPtr Handle => IntPtr.Zero;
        }

        private sealed class Recorder
        {
            public bool Ready = true;
            public bool OffThread = true;
            public int Invoked;
            public readonly List<Action> Posted = new();

            public UiThreadInvoker Invoker() => new(
                () => Ready,
                () => OffThread,
                work => { Invoked++; work(); },
                work => Posted.Add(work));
        }

        [Fact]
        public void Work_goes_through_the_invoker_when_one_is_set_and_the_caller_is_off_its_thread()
        {
            var rec = new Recorder();
            var parms = new FlexBase.OpenParms { ParentWindow = new FakeWindow(), UiThread = rec.Invoker() };
            Assert.False(parms.ParentWindow is Control);
            bool ran = false;

            bool marshalled = UiThreadInvoker.Run(parms.UiThread, () => ran = true);

            Assert.True(marshalled);
            Assert.True(ran);
            Assert.Equal(1, rec.Invoked);
        }

        [Fact]
        public void Work_runs_here_when_the_caller_is_already_on_the_UI_thread()
        {
            var rec = new Recorder { OffThread = false };
            bool ran = false;
            Assert.False(UiThreadInvoker.Run(rec.Invoker(), () => ran = true));
            Assert.True(ran);
            Assert.Equal(0, rec.Invoked);
        }

        [Fact]
        public void Work_runs_here_with_no_invoker_or_before_the_UI_thread_can_take_it()
        {
            bool ran = false;
            Assert.False(UiThreadInvoker.Run(null, () => ran = true));
            Assert.True(ran);

            var rec = new Recorder { Ready = false };
            ran = false;
            Assert.False(UiThreadInvoker.Run(rec.Invoker(), () => ran = true));
            Assert.True(ran);
            Assert.Equal(0, rec.Invoked);
            Assert.False(rec.Invoker().Post(() => { }));
            Assert.Empty(rec.Posted);
        }

        /// <summary>
        /// The focus restore after a sign-in, with a parent that is not a
        /// Control. Before #1205 the cast gave null and nothing was posted, so
        /// focus stayed wherever the closed dialog left it.
        /// </summary>
        [Fact]
        public void The_focus_restore_is_posted_for_a_parent_that_is_not_a_Control()
        {
            var rec = new Recorder();
            int activated = 0;
            var parms = new FlexBase.OpenParms
            {
                ProgramName = "JJFlexTests",
                ParentWindow = new FakeWindow(),
                UiThread = rec.Invoker(),
                ActivateParentWindow = () => activated++,
            };
            var rig = new FlexBase(parms);
            try
            {
                MethodInfo restore = typeof(FlexBase).GetMethod("RestoreParentFocus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.NotNull(restore);
                restore.Invoke(rig, null);

                Assert.Single(rec.Posted);
                Assert.Equal(0, activated);   // posted, not run here: the UI thread runs it
            }
            finally
            {
                try { rig.Dispose(); } catch { }
            }
        }

        private static string Body(string src, string signature)
        {
            int start = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature + " is missing");
            int brace = src.IndexOf('{', start);
            int depth = 0;
            for (int i = brace; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return src.Substring(start, i - start + 1);
            }
            throw new InvalidOperationException("unbalanced braces after " + signature);
        }

        [Fact]
        public void No_sign_in_site_casts_the_parent_window_and_each_uses_the_invoker()
        {
            string wan = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "FlexBase.Wan.cs"));
            Assert.DoesNotContain("ParentWindow as Control", wan, StringComparison.Ordinal);
            Assert.DoesNotContain("ParentWindow as Form", wan, StringComparison.Ordinal);
            Assert.DoesNotContain("ParentWindow is Control", wan, StringComparison.Ordinal);
            Assert.DoesNotContain("ParentWindow is Form", wan, StringComparison.Ordinal);

            // Positive control: the scan finds the invoker at all three sites,
            // so an absence above is not merely the scan looking in the wrong place.
            foreach (string site in new[]
            {
                "private void RestoreParentFocus(",
                "private string PerformNewLogin(",
                "private void DisposeAuthFormOnUiThread(",
            })
            {
                string body = Body(wan, site);
                Assert.Contains("Callouts?.UiThread", body, StringComparison.Ordinal);
                Assert.DoesNotContain("as Control", body, StringComparison.Ordinal);
            }

            string globals = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "globals.vb"));
            int parent = globals.IndexOf("OpenParms.ParentWindow = AppShellForm", StringComparison.Ordinal);
            int invoker = globals.IndexOf("OpenParms.UiThread = Radios.UiThreadInvoker.ForControl(", StringComparison.Ordinal);
            Assert.True(parent > 0 && invoker > parent && invoker - parent < 1000,
                "the invoker is set beside the parent window");
        }
    }
}
