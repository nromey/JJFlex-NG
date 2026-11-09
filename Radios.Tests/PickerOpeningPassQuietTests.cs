using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #775's remainder: once a connect has started from the radio picker, the
    /// opening pass's "Local connection list loaded, still listening" (and its
    /// nothing-found sibling) is not spoken. Measured 2026-10-19: it landed
    /// 300 ms after the main window came up, over the connect, about a radio
    /// already found and chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Repair one: both orderings, through the real speech arbiter on a fake
    /// clock. A connect before the settle window ends: the line is never
    /// said. A connect after the line was QUEUED but before it was heard
    /// (Codex's blocker): the connect's interrupting sentence makes the
    /// arbiter rescue unheard speech, and the line's own condition now refuses
    /// the replay. The positive controls: an idle picker still says its loaded
    /// state, and the same queued line IS rescued when no connect started.
    /// </para>
    /// <para>
    /// These replace two tests that read source spelling, which Codex showed
    /// both pass with the decision forced either way. One wiring pin remains,
    /// so the behaviour held here cannot sit in a type the picker no longer
    /// uses.
    /// </para>
    /// </remarks>
    public sealed class PickerOpeningPassQuietTests
    {
        private const string Loaded = "Local connection list loaded, still listening.";
        private const string Trying = "Trying FLEX-8600 on the local network.";

        private sealed record Said(string Message, bool Interrupt, bool Salvaged);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<Said> _said = new();
        private readonly SpeechArbiter _arbiter;
        private readonly PickerOpeningPassSpeech _pass;

        public PickerOpeningPassQuietTests()
        {
            _arbiter = new SpeechArbiter(
                _clock,
                () => VerbosityLevel.Chatty,
                (message, interrupt, intent, level, origin, salvaged) =>
                {
                    _said.Add(new Said(message, interrupt, salvaged));
                    return true;
                },
                () => { },
                (message, level, intent, origin) => { });
            // The picker's real route, with the arbiter standing in for the
            // screen reader: queued, Terse, the line's own condition handed on.
            _pass = new PickerOpeningPassSpeech((text, stillValid) =>
                _arbiter.Emit(text, false, null, VerbosityLevel.Terse, "RigSelectorDialog", null,
                    additive: false, stillValid: stillValid));
        }

        /// <summary>The connect's own sentence, which interrupts (CompleteConnect).</summary>
        private void TheConnectSpeaks() =>
            _arbiter.Emit(Trying, true, null, VerbosityLevel.Terse, "RigSelectorDialog");

        private int Rescued(string message) => _said.Count(s => s.Salvaged && s.Message == message);

        [Fact]
        public void An_idle_picker_still_says_its_loaded_state()
        {
            Assert.True(_pass.Say(Loaded));
            Assert.Single(_said, s => s.Message == Loaded && !s.Salvaged);
        }

        [Fact]
        public void A_connect_before_the_settle_window_ends_means_the_line_is_never_said()
        {
            _pass.ConnectStarting();
            TheConnectSpeaks();
            Assert.False(_pass.Say(Loaded));
            _clock.Advance(SpeechArbiter.SalvageSettleMs * 2);
            Assert.DoesNotContain(_said, s => s.Message == Loaded);
        }

        /// <summary>
        /// Codex's blocker: the line queued behind the arrival speech, then the
        /// connect (an Enter, or the startup auto-connect's one-second tick)
        /// interrupts while it is still unheard. Before repair one the arbiter
        /// handed it back after its settle window, over the connect.
        /// </summary>
        [Fact]
        public void A_line_queued_before_the_connect_is_not_rescued_after_it()
        {
            _arbiter.Emit("FLEX-8600 online.", false, null, VerbosityLevel.Terse, "RigSelectorDialog");
            Assert.True(_pass.Say(Loaded));
            _clock.Advance(200);

            _pass.ConnectStarting();
            TheConnectSpeaks();
            _clock.Advance(SpeechArbiter.SalvageSettleMs);
            _clock.Advance(SpeechArbiter.SalvageSettleMs);

            Assert.Equal(0, Rescued(Loaded));
            Assert.Equal(1, Rescued("FLEX-8600 online."));   // the arbiter's rescue itself is untouched
        }

        /// <summary>
        /// The control for the test above: the same queue and the same
        /// interrupt with no connect started, and the arbiter does rescue the
        /// line. Without this, "not rescued" could mean the rescue never ran.
        /// </summary>
        [Fact]
        public void With_no_connect_the_same_queued_line_is_rescued()
        {
            _arbiter.Emit("FLEX-8600 online.", false, null, VerbosityLevel.Terse, "RigSelectorDialog");
            Assert.True(_pass.Say(Loaded));
            _clock.Advance(200);

            _arbiter.Emit("Something else entirely.", true, null, VerbosityLevel.Terse, "probe");
            _clock.Advance(SpeechArbiter.SalvageSettleMs);

            Assert.Equal(1, Rescued(Loaded));
        }

        [Fact]
        public void The_condition_is_true_until_a_connect_starts_and_false_after()
        {
            Assert.False(_pass.ConnectStarted);
            Assert.True(_pass.StillWorthSaying());
            _pass.ConnectStarting();
            Assert.True(_pass.ConnectStarted);
            Assert.False(_pass.StillWorthSaying());
        }

        /// <summary>
        /// Wiring, not behaviour: the picker says its opening-pass lines
        /// through this type, marks the connect at the top of CompleteConnect,
        /// and keeps no second flag of its own.
        /// </summary>
        [Fact]
        public void The_picker_uses_it_for_every_opening_pass_line_and_marks_every_connect()
        {
            string src = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))
                .Replace("\r\n", "\n");
            int at = src.IndexOf("private void CompleteConnect(RadioListItem radio, ConnectPathKind path, bool forced,", StringComparison.Ordinal);
            Assert.True(at > 0, "CompleteConnect has moved");
            int open = src.IndexOf('{', at);
            string firstLine = src.Substring(open + 1, src.IndexOf('\n', open + 2) - open).Trim();
            Assert.StartsWith("_openingPass.ConnectStarting();", firstLine, StringComparison.Ordinal);

            int announce = src.IndexOf("private void AnnounceLoadedState(string terse, string chatty)", StringComparison.Ordinal);
            Assert.True(announce > 0, "AnnounceLoadedState has moved");
            string body = src.Substring(announce, src.IndexOf("\n        }", announce, StringComparison.Ordinal) - announce);
            Assert.Contains("_openingPass.Say(text);", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ScreenReaderOutput.Speak", body, StringComparison.Ordinal);
            Assert.DoesNotContain("_connectStarted", src, StringComparison.Ordinal);
        }
    }
}
