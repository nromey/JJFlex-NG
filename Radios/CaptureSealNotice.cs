using System;

namespace Radios
{
    /// <summary>
    /// What the operator is told when a connection drop sealed their capture —
    /// every sentence of it, composed away from any window so it can be read
    /// and tested as prose.
    ///
    /// <para><b>Noel, 2026-09-22 19:34:</b> <i>"if the system has to seal, it
    /// needs a dialog that allows user to read the error and copy the capture
    /// path to clipboard, just make it easy to get to until infra is set things
    /// up to upload."</i> That is the whole specification, and the temporary
    /// clause in it is load-bearing: this dialog exists because there is no
    /// upload yet. When there is, the copy-the-path step is what goes away.</para>
    ///
    /// <para><b>Why a dialog here when #100 removed the failure-moment
    /// dialog.</b> <c>DiagnosticOffer</c> stopped opening windows on failures
    /// for two reasons — a window that appears unbidden can confuse, and a
    /// screen reader flushes its speech queue when a window opens, destroying
    /// the sentence that was explaining the failure. Neither applies as it did
    /// there. Nothing else is mid-sentence: the drop's own announcement is a
    /// different utterance on a different path, and this window carries its own
    /// explanation, so there is nothing for it to destroy that it does not then
    /// say itself. And the operator has something to DO — a path to copy and a
    /// file to send — which is exactly what #100's replacement gave up. The
    /// deeper #100 worry, that a missed notification cannot be asked about
    /// afterwards, is answered by the archive: the file is on disk with the
    /// right outcome on it, findable from Settings, Diagnostics, whether this
    /// window is read or dismissed.</para>
    ///
    /// <para><b>All of it is lexicon.</b> Keys under <c>logging.capture.dropped</c>
    /// in <c>Radios/Lexicon/logging.json</c>. MARKED FOR NOEL'S PROSE REVIEW —
    /// written by an agent, never read aloud by a person, and it is the first
    /// thing an operator meets after their radio has died.</para>
    /// </summary>
    public sealed class CaptureSealNotice
    {
        public CaptureSealNotice(string radioName, string archivePath)
        {
            RadioName = (radioName ?? string.Empty).Trim();
            ArchivePath = archivePath ?? string.Empty;
        }

        /// <summary>The radio's nickname, or empty when we never learned one.</summary>
        public string RadioName { get; }

        /// <summary>The full path of the sealed archive, exactly as it sits on
        /// disk. Shown in full and copied in full — a path the operator can only
        /// see half of is a path they cannot send.</summary>
        public string ArchivePath { get; }

        /// <summary>The window title.</summary>
        public string Title => Lexicon.Get("logging.capture.dropped.title");

        /// <summary>
        /// What happened. Named radio where we have one: "the radio" is a
        /// hedge, and the operator may have more than one.
        /// </summary>
        public string WhatHappened =>
            string.IsNullOrEmpty(RadioName)
                ? Lexicon.Get("logging.capture.dropped.what_plain")
                : Lexicon.Get("logging.capture.dropped.what_named", ("radioName", RadioName));

        /// <summary>That the recording was kept, and what is in it.</summary>
        public string WhatWasSaved => Lexicon.Get("logging.capture.dropped.saved");

        /// <summary>The label above the path.</summary>
        public string PathLabel => Lexicon.Get("logging.capture.dropped.path_label");

        /// <summary>What to do with it.</summary>
        public string WhatToDo => Lexicon.Get("logging.capture.dropped.what_to_do");

        /// <summary>The Copy path button.</summary>
        public string CopyButtonLabel => Lexicon.Get("logging.capture.dropped.copy_button");

        /// <summary>Spoken and shown after a successful copy.</summary>
        public string CopyConfirmation => Lexicon.Get("logging.capture.dropped.copied");

        /// <summary>Spoken and shown when the clipboard refused.</summary>
        public string CopyFailed => Lexicon.Get("logging.capture.dropped.copy_failed");

        /// <summary>The Close button.</summary>
        public string CloseButtonLabel => Lexicon.Get("logging.capture.dropped.close_button");

        /// <summary>
        /// Exactly what the Copy path button puts on the clipboard: the path
        /// and nothing else.
        ///
        /// <para>Deliberately not a paragraph with the path inside it. This gets
        /// pasted into a file dialog's name box as often as into a message, and
        /// a path wrapped in explanatory text fails there silently.</para>
        /// </summary>
        public string ClipboardText => ArchivePath;

        /// <summary>
        /// The three prose paragraphs, in reading order, with the path left out
        /// because the path has a control of its own.
        ///
        /// <para>One block rather than three labels on purpose. A WPF dialog
        /// runs in focus mode, so a screen reader cannot arrow over static
        /// text at all; making each paragraph focusable instead puts three tab
        /// stops in front of the two controls that DO something (#211's
        /// complaint, from the other side). One read-only box holds all of it,
        /// arrow-navigable line by line, and the operator is two Tabs from the
        /// path and three from the button.</para>
        /// </summary>
        public string Explanation =>
            WhatHappened + Environment.NewLine + Environment.NewLine
            + WhatWasSaved + Environment.NewLine + Environment.NewLine
            + WhatToDo;

        /// <summary>
        /// The whole notice as one block, path included — for the trace, and
        /// for anything that wants to render it without a window.
        /// </summary>
        public string AsText() =>
            WhatHappened + Environment.NewLine + Environment.NewLine
            + WhatWasSaved + Environment.NewLine + Environment.NewLine
            + PathLabel + Environment.NewLine + ArchivePath + Environment.NewLine + Environment.NewLine
            + WhatToDo;
    }
}
