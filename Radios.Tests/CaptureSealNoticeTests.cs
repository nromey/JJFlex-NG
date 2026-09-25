using System;
using System.IO;
using System.Linq;
using JJTrace;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The dialog an operator meets after their radio's connection died mid
    /// recording — pinned as ASSEMBLED SENTENCES, not as the fragments that
    /// build them (Sprint 45 Track H, #566's bridge).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why assembled.</b> This project's product copy defaults to fragments
    /// that read fine in a diff and land badly in the ear. These particular
    /// words arrive at the worst moment the application has — the operator's
    /// radio has just gone — so they are read here as a person would hear them.
    /// </para>
    /// <para>
    /// <b>MARKED FOR NOEL'S PROSE REVIEW.</b> Every string below was written by
    /// an agent and has never been read aloud by a person. The line naming Noel
    /// in particular is a deliberate choice for the bridge period and is exactly
    /// the kind of thing he should overrule if he wants to.
    /// </para>
    /// <para>
    /// <b>The window itself is not tested here and cannot be.</b> It is WPF, so
    /// it belongs to <c>JJFlexWpf.Tests</c>, which <c>DeskGuard</c> refuses on a
    /// live desk — correctly. What is tested is everything the window renders
    /// and everything the Copy button puts on the clipboard. Pressing it is a
    /// bench job and the report says so.
    /// </para>
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class CaptureSealNoticeTests
    {
        private readonly ITestOutputHelper _out;
        public CaptureSealNoticeTests(ITestOutputHelper output) { _out = output; }

        private const string Path0 =
            @"C:\Users\nrome\AppData\Roaming\JJFlexRadio\Traces\2026\09\trace-20260922-201500-connection_dropped.zip";

        /// <summary>The ordinary window: a certain tail, both kinds of
        /// reading counted into the file, recording again now. The H7 prose
        /// is asserted against THIS, because since H10 the content promise
        /// is made only when the file's facts support it.</summary>
        private static CaptureSealNotice Full(string name) =>
            new CaptureSealNotice(name, Path0, successorOpened: true, archivedSessionId: Guid.NewGuid(),
                                  tailUncertain: false, sinkFailedBeforeDrop: false, recordingNow: true,
                                  fileFacts: new TraceFileFacts(powerWritten: true, temperatureWritten: true,
                                                                faulted: false, linesUnflushedAtFault: 0,
                                                                readingsLostAtFault: false,
                                                                readingsRefusedAfterFault: false));
        private static CaptureSealNotice Named() => Full("6300inshack");
        private static CaptureSealNotice Unnamed() => Full("");

        /// <summary>
        /// The two-argument constructor carries no facts, so it names no
        /// readings: "It holds everything up to the moment the connection
        /// went." and nothing about what those lines are. Until H10 it made
        /// the full three-reading promise on no evidence (Sol's review of
        /// H9, blocker 2).
        /// </summary>
        [Fact]
        public void A_notice_without_file_facts_names_no_readings()
        {
            var bare = new CaptureSealNotice("6300inshack", Path0);
            Assert.Null(bare.FileFacts);
            Assert.Equal("The recording has been closed and saved. It holds everything up to the moment the connection went.",
                         bare.WhatWasSaved);
            Assert.DoesNotContain("power", bare.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("temperature", bare.Explanation, StringComparison.OrdinalIgnoreCase);
        }

        // ────────────────────────────────────────────────────────────────
        //  The sentences
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_title_says_both_halves_of_the_news()
        {
            // A screen reader reads the title when the window opens, and it is
            // often all an operator hears before they start moving. The bad
            // news and the reassurance belong in it together.
            Assert.Equal("Connection lost, recording saved", Named().Title);
        }

        [Fact]
        public void What_happened_names_the_radio_when_we_know_it()
        {
            Assert.Equal(
                "The connection to 6300inshack was lost while JJ Flexible was recording.",
                Named().WhatHappened);
        }

        [Fact]
        public void What_happened_falls_back_to_the_radio_when_we_do_not()
        {
            // "the radio" is a hedge, and an operator may have more than one.
            // Used only where the nickname genuinely never arrived.
            Assert.Equal(
                "The connection to the radio was lost while JJ Flexible was recording.",
                Unnamed().WhatHappened);
        }

        [Fact]
        public void What_was_saved_says_what_is_in_it()
        {
            Assert.Equal(
                "The recording has been closed and saved. It holds everything up to the "
                + "moment the connection went, including the last readings the radio sent: "
                + "forward power, reflected power and the temperature of the amplifier.",
                Named().WhatWasSaved);
        }

        [Fact]
        public void What_to_do_is_one_errand_and_says_the_log_is_already_running_again()
        {
            Assert.Equal(
                "Send this file to Noel. Copy the path with the button below, then attach "
                + "the file to an email. There is nothing else to do here: JJ Flexible has "
                + "already started recording again, so the next thing that happens is being "
                + "kept too.",
                Named().WhatToDo);
        }

        [Fact]
        public void The_labels_and_buttons_read_as_labels_and_buttons()
        {
            var n = Named();
            Assert.Equal("The recording is saved here:", n.PathLabel);
            Assert.Equal("_Copy path", n.CopyButtonLabel);
            Assert.Equal("_Close", n.CloseButtonLabel);
            Assert.Equal("Path copied to the clipboard.", n.CopyConfirmation);
            Assert.Equal(
                "The path could not be copied to the clipboard. You can still read it above.",
                n.CopyFailed);
        }

        [Fact]
        public void Every_sentence_ends_in_a_full_stop_so_a_reader_does_not_run_them_together()
        {
            foreach (string s in new[] { Named().WhatHappened, Named().WhatWasSaved, Named().WhatToDo })
            {
                _out.WriteLine(s);
                Assert.EndsWith(".", s.TrimEnd(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void No_key_leaked_through_unresolved()
        {
            // Lexicon.Get returns the KEY when a key is missing, on purpose. A
            // window reading "logging.capture.dropped.title" aloud is the
            // failure that fallback is designed to make visible; here it is
            // made visible before anybody ships it.
            var n = Named();
            foreach (string s in new[]
            {
                n.Title, n.WhatHappened, n.WhatWasSaved, n.PathLabel, n.WhatToDo,
                n.CopyButtonLabel, n.CloseButtonLabel, n.CopyConfirmation, n.CopyFailed
            })
            {
                Assert.DoesNotContain("logging.capture.dropped", s, StringComparison.Ordinal);
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  The path, which is the point of the window
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void The_copy_button_puts_the_path_and_nothing_else_on_the_clipboard()
        {
            // Not a paragraph with the path inside it. This gets pasted into a
            // file dialog's name box as often as into a message, and a path
            // wrapped in explanatory text fails there silently.
            Assert.Equal(Path0, Named().ClipboardText);
        }

        [Fact]
        public void The_path_is_shown_whole()
        {
            // A path the operator can only see half of is a path they cannot
            // send. Nothing shortens, elides or prettifies it.
            Assert.Equal(Path0, Named().ArchivePath);
            Assert.Contains(Path0, Named().AsText(), StringComparison.Ordinal);
        }

        [Fact]
        public void The_explanation_block_leaves_the_path_to_its_own_control()
        {
            string explanation = Named().Explanation;
            _out.WriteLine(explanation);
            Assert.DoesNotContain(Path0, explanation, StringComparison.Ordinal);
            Assert.Contains("was lost while JJ Flexible was recording", explanation, StringComparison.Ordinal);
            Assert.Contains("Send this file to Noel", explanation, StringComparison.Ordinal);
        }

        [Fact]
        public void A_notice_with_no_path_is_never_shown()
        {
            // The window's own guard, stated where it can be read: CaptureSeal
            // does not raise without a path, and the dialog refuses one anyway.
            string dialog = File.ReadAllText(System.IO.Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "Dialogs", "CaptureSealedDialog.cs"));
            Assert.Contains("string.IsNullOrEmpty(notice.ArchivePath)", dialog, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisStringIsNotInTheDialogAnywhere", dialog, StringComparison.Ordinal);
        }

        [Fact]
        public void The_window_closes_on_Escape_because_it_is_a_JJFlexDialog()
        {
            // Escape-closable is inherited, not re-implemented — so what this
            // asserts is the inheritance. Pressing the key is still a bench
            // job; this only stops the dialog quietly becoming a plain Window.
            string dialog = File.ReadAllText(System.IO.Path.Combine(
                CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "Dialogs", "CaptureSealedDialog.cs"));
            Assert.Contains("class CaptureSealedDialog : JJFlexDialog", dialog, StringComparison.Ordinal);
        }
    }
}
