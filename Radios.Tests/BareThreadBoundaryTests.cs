using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #422: <c>remoteAudioProc</c> runs on a bare <see cref="System.Threading.Thread"/>
    /// with no handler above it, so ANY exception in it terminated the process —
    /// no window, no speech, and nothing said. <b>For a blind operator that is
    /// strictly less evidence than a spoken error, not more.</b>
    ///
    /// <para><b>Why a boundary catch is right here and not a shrug.</b> The
    /// failure domain is cleanly separable: audio dies, and the radio session,
    /// the UI and logging all survive. The masking is bounded to one catch with
    /// no retry loop, and the thread still exits, so a recurring fault recurs
    /// loudly on every start. That separability is the test for whether a
    /// catch-all belongs somewhere, and this passes it.</para>
    ///
    /// <para><b>Why a source scan.</b> The alternative is making the audio
    /// thread throw on demand, which means standing up PortAudio devices and a
    /// radio. The property worth defending is structural — the guard exists,
    /// it says something, and the operator is told — and that is exactly what a
    /// future editor deleting a try block would break.</para>
    /// </summary>
    /// <remarks>
    /// Task #319 is the same defect one file over: <c>knobThreadProc</c> caught
    /// <c>ThreadInterruptedException</c> and nothing else, so anything thrown by
    /// <c>New FlexKnob</c> — and the original BUG-004 fault was a
    /// <c>FileNotFoundException</c> out of a serial-port assembly, exactly that
    /// shape — was unhandled on a thread with nothing above it. That thread
    /// starts whether or not a knob is attached, which is most installs.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class BareThreadBoundaryTests
    {
        private const string FlexBase = "Radios/FlexBase.cs";
        private const string Globals = "globals.vb";

        /// <summary>
        /// The guard is present, catches everything, and does the three things
        /// that make it worth having rather than a silent swallow: record the
        /// stack, tell the operator, and let the existing teardown run.
        /// </summary>
        [Fact]
        public void TheAudioThreadHasABoundaryCatchThatSaysSomething()
        {
            string body = MethodBody("private void remoteAudioProc()");

            Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);

            // The stack, not just the message: this is the only record that
            // will exist of a fault that used to kill the process outright.
            Assert.Contains("TraceLevel.Error", body, StringComparison.Ordinal);

            // Spoken, and at Critical — audio that simply stops is not a
            // diagnosis, and the operator cannot act on what nobody told them.
            Assert.Contains("audio.pc_audio.internal_error", body, StringComparison.Ordinal);
            Assert.Contains("VerbosityLevel.Critical", body, StringComparison.Ordinal);

            // The teardown still runs. It is already defensive and
            // null-guarded, and skipping it leaves PortAudio streams and
            // radio-side streams open with no owner.
            Assert.Contains("remoteDone:", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control. If <see cref="MethodBody"/> silently returned
        /// nothing, every assertion above would be vacuous and the test would
        /// pass while the guard was gone — so make it find something that is
        /// certainly there and could not be there by accident.
        /// </summary>
        [Fact]
        public void TheScannerReallyReadsTheMethod()
        {
            string body = MethodBody("private void remoteAudioProc()");

            Assert.True(body.Length > 2000,
                "The scan produced " + body.Length + " characters. remoteAudioProc is "
                + "hundreds of lines long, so this means the method was not found and every "
                + "other assertion in this file is meaningless.");
            Assert.Contains("remoteAudioProc exiting", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The words exist. A lexicon miss is spoken as the key itself, so
        /// "audio.pc_audio.internal_error" would be read out literally at the
        /// exact moment the operator needs a sentence.
        /// </summary>
        [Fact]
        public void TheSpokenExplanationHasText()
        {
            string text = Radios.Lexicon.Get("audio.pc_audio.internal_error");

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual("audio.pc_audio.internal_error", text);
            // It has to say what to do next. An announcement that only reports
            // a failure leaves the operator with no move.
            Assert.Contains("PC audio", text, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // #319 — the knob thread's construction half
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>knobThreadProc</c> catches broadly, not just the interrupt it
        /// uses for shutdown.
        ///
        /// <para>The narrow catch is the whole bug: the shutdown half of
        /// BUG-004 was properly fixed and the construction half was never
        /// covered, and the audit only found it because somebody checked the
        /// fix rather than assuming it. An unhandled exception on this thread
        /// ends the process at startup with no dialog and no speech.</para>
        /// </summary>
        [Fact]
        public void TheKnobThreadCatchesMoreThanTheInterrupt()
        {
            string body = VbMethodBody("Private Sub knobThreadProc()");

            Assert.Contains("Catch ex As ThreadInterruptedException", body, StringComparison.Ordinal);
            Assert.Contains("Catch ex As Exception", body, StringComparison.Ordinal);

            // Says what happened. A contained failure nobody records is only a
            // quieter version of the same problem.
            Assert.Contains("TraceLevel.Error", body, StringComparison.Ordinal);

            // Leaves the app in the state every operator without a knob is
            // already in, rather than holding a half-built one.
            Assert.Contains("Knob = Nothing", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Positive control for the VB scan, for the same reason as the C# one.
        /// </summary>
        [Fact]
        public void TheVbScannerReallyReadsTheMethod()
        {
            string body = VbMethodBody("Private Sub knobThreadProc()");

            Assert.Contains("New FlexKnob", body, StringComparison.Ordinal);
            Assert.Contains("Timeout.Infinite", body, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Text of a VB Sub, from its signature to its <c>End Sub</c>.
        /// </summary>
        private static string VbMethodBody(string signature)
        {
            string source = File.ReadAllText(
                Path.Combine(RepoRoot(), Globals.Replace('/', Path.DirectorySeparatorChar)));

            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return string.Empty;

            int end = source.IndexOf("\n    End Sub", start, StringComparison.Ordinal);
            if (end < 0) end = source.Length;

            return source.Substring(start, end - start);
        }

        /// <summary>
        /// Text from a method's signature to the start of the next member
        /// declaration at the same indentation. Crude on purpose: it needs to
        /// survive a file that four tracks are editing, so it anchors on the
        /// signature and on "\n        private ", both of which are stable.
        /// </summary>
        private static string MethodBody(string signature)
        {
            string source = File.ReadAllText(
                Path.Combine(RepoRoot(), FlexBase.Replace('/', Path.DirectorySeparatorChar)));

            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return string.Empty;

            int next = source.IndexOf("\n        private ", start + signature.Length,
                                      StringComparison.Ordinal);
            if (next < 0) next = source.Length;

            return source.Substring(start, next - start);
        }

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
