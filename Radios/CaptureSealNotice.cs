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
            : this(radioName, archivePath, successorOpened: true, archivedSessionId: null)
        {
        }

        /// <summary>
        /// The facts the trace boundary can now state, added WITHOUT changing a
        /// sentence of the prose below.
        ///
        /// <para>The dialog's "what to do" text says recording has already
        /// restarted, and that is false in reachable states: a drop that wins
        /// during a teardown seals its own session and deliberately opens no
        /// successor, and a restart can fail on its own. Until Noel rules the
        /// wording, the FACT is at least carried and traceable rather than
        /// assumed — which is the half of the problem a track can fix.</para>
        /// </summary>
        public CaptureSealNotice(string radioName, string archivePath,
                                 bool successorOpened, Guid? archivedSessionId)
            : this(radioName, archivePath, successorOpened, archivedSessionId, recoveryAtRisk: false)
        {
        }

        /// <summary>
        /// The Track H7 shape: the drop result also says whether the sealed
        /// session's recovery is at risk, so the window can carry that
        /// condition rather than a possibly unwritable log being the only
        /// place it is said (Astra's ruling: the capture/drop result reads the
        /// same health state Diagnostics does).
        /// </summary>
        public CaptureSealNotice(string radioName, string archivePath,
                                 bool successorOpened, Guid? archivedSessionId,
                                 bool recoveryAtRisk)
        {
            RadioName = (radioName ?? string.Empty).Trim();
            ArchivePath = archivePath ?? string.Empty;
            SuccessorOpened = successorOpened;
            ArchivedSessionId = archivedSessionId;
            RecoveryAtRisk = recoveryAtRisk;
        }

        /// <summary>The radio's nickname, or empty when we never learned one.</summary>
        public string RadioName { get; }

        /// <summary>
        /// The sealed session's durable recovery record could not be written,
        /// or its last lines may not have reached the disk. When this window
        /// is shown at all an archive path exists, so the archive itself is
        /// committed — but the condition is still reported, because a
        /// committed archive of a file whose tail is uncertain is not a
        /// complete recording, and the operator sending it should know.
        /// </summary>
        public bool RecoveryAtRisk { get; }

        /// <summary>
        /// Whether a fresh recording really opened after the seal. Not yet
        /// spoken anywhere: the sentence that would carry it is Noel's to rule.
        /// </summary>
        public bool SuccessorOpened { get; }

        /// <summary>Which trace session was archived.</summary>
        public Guid? ArchivedSessionId { get; }

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

        /// <summary>
        /// The extra paragraph when <see cref="RecoveryAtRisk"/>: that this
        /// recording's details may not be recovered automatically, and that the
        /// file above is still the one to send. Empty otherwise, so the ordinary
        /// window's prose is untouched. DRAFT — Noel's to rule; listed in the
        /// Track H7 wording file.
        /// </summary>
        public string RecoveryCaveat =>
            RecoveryAtRisk ? Lexicon.Get("logging.capture.dropped.recovery_at_risk") : string.Empty;

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
            + WhatToDo
            + (RecoveryAtRisk ? Environment.NewLine + Environment.NewLine + RecoveryCaveat : string.Empty);

        /// <summary>
        /// The whole notice as one block, path included — for the trace, and
        /// for anything that wants to render it without a window.
        /// </summary>
        public string AsText() =>
            WhatHappened + Environment.NewLine + Environment.NewLine
            + WhatWasSaved + Environment.NewLine + Environment.NewLine
            + PathLabel + Environment.NewLine + ArchivePath + Environment.NewLine + Environment.NewLine
            + WhatToDo
            + (RecoveryAtRisk ? Environment.NewLine + Environment.NewLine + RecoveryCaveat : string.Empty);
    }
}
