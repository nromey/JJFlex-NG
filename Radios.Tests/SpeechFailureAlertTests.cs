using System;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #321: when <c>prism.dll</c> does not load the application goes
    /// silent, and it reported that only in the trace, on Help &gt; About, and
    /// in crash bundles — all three of which are things you READ. The operator
    /// this fires for is a blind operator whose application has just stopped
    /// speaking.
    ///
    /// <para>These pin the decision, which is the part with a wrong answer. The
    /// two acts of telling — the ungated alarm earcon and the ownerless dialog
    /// — live in the startup path, where a test would have to build audio
    /// devices and put a window on the operator's desktop.</para>
    /// </summary>
    public sealed class SpeechFailureAlertTests
    {
        /// <summary>The condition it exists for: speech is gone on a real run.</summary>
        [Fact]
        public void AlertsWhenTheAppCanRenderButCannotSpeak()
        {
            Assert.True(SpeechFailureAlert.ShouldAlert(renderEnabled: true, speechAvailable: false));
        }

        /// <summary>An ordinary healthy launch says nothing.</summary>
        [Fact]
        public void StaysQuietWhenSpeechIsWorking()
        {
            Assert.False(SpeechFailureAlert.ShouldAlert(renderEnabled: true, speechAvailable: true));
        }

        /// <summary>
        /// The silent verification channel (<c>--render-off</c>) has no speech
        /// on purpose. An alarm there is a FALSE alarm, and a false alarm is
        /// how a real one gets ignored — the same reasoning the speech
        /// verbosity rules apply to spoken output.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NeverAlertsOnADeliberatelySilentRun(bool speechAvailable)
        {
            Assert.False(SpeechFailureAlert.ShouldAlert(renderEnabled: false, speechAvailable));
        }

        // ------------------------------------------------------------------
        // The words. Drafted, pending Noel's review — these pin the properties
        // that make them usable, not the wording itself.
        // ------------------------------------------------------------------

        /// <summary>
        /// Read aloud, start to finish, by whatever reader is running. It has
        /// to say what happened, that the reader itself is fine, and what to do
        /// — and it must not be empty, which is the failure mode a lexicon miss
        /// would produce.
        /// </summary>
        [Fact]
        public void TheDialogSaysWhatHappenedAndWhatToDo()
        {
            string message = SpeechFailureAlert.AlertMessage;

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.False(string.IsNullOrWhiteSpace(SpeechFailureAlert.AlertTitle));

            // Names the file, so the operator can hand a helper something
            // actionable rather than "it does not talk".
            Assert.Contains("prism.dll", message, StringComparison.OrdinalIgnoreCase);

            // Says the screen reader is not the broken part. Without this the
            // obvious conclusion is "my screen reader has failed", and the
            // operator goes and reinstalls the wrong thing.
            Assert.Contains("screen reader", message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// No bullet or table characters. This text is spoken, and the audience
        /// is screen reader users — the same rule that governs everything else
        /// user-facing in this project.
        /// </summary>
        [Fact]
        public void TheDialogHasNoListOrTableMarkup()
        {
            string message = SpeechFailureAlert.AlertMessage;

            Assert.DoesNotContain("|", message, StringComparison.Ordinal);
            Assert.DoesNotContain("\t", message, StringComparison.Ordinal);
            Assert.DoesNotContain("* ", message, StringComparison.Ordinal);
            Assert.DoesNotContain("- ", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The trace line records that the operator was TOLD, which is the fact
        /// #321 is about — distinct from the backend line the factory already
        /// writes, which only records that speech failed.
        /// </summary>
        [Fact]
        public void TheTraceLineCarriesTheFindingNumber()
        {
            Assert.Contains("#321", SpeechFailureAlert.TraceLine, StringComparison.Ordinal);
        }
    }
}
