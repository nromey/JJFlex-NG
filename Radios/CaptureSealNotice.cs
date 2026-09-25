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
            : this(radioName, archivePath, successorOpened, archivedSessionId, tailUncertain: false)
        {
        }

        /// <summary>
        /// The drop result also says whether the sealed file's last lines
        /// reached the disk, so the window can carry that fact rather than a
        /// possibly unwritable log being the only place it is said (Astra's
        /// ruling: the capture/drop result reads the same health state
        /// Diagnostics does).
        ///
        /// <para><b>One predicate, on purpose.</b> Track H7 gave this window a
        /// single "recovery at risk" flag that ORed two different facts — the
        /// index file did not write, the tail is uncertain — and one sentence
        /// that was false for one of them and overclaimed for the other (Sol's
        /// review of H7, finding 3). This window opens ONLY with a committed
        /// archive, and a committed archive needs no index file: that
        /// predicate cannot be true here, and it is the Problems list's to
        /// carry when it matters. What CAN be true of a committed archive is
        /// that the bytes inside it stop short, and that is a property of the
        /// sealed file which no later retry changes — so it is read from the
        /// seal result and nowhere else.</para>
        /// </summary>
        public CaptureSealNotice(string radioName, string archivePath,
                                 bool successorOpened, Guid? archivedSessionId,
                                 bool tailUncertain)
            : this(radioName, archivePath, successorOpened, archivedSessionId, tailUncertain,
                   sinkFailedBeforeDrop: false)
        {
        }

        /// <summary>
        /// <b>Every paragraph is chosen so that its claims hold</b> (Sol's
        /// review of H8, blocker 2). Track H8 appended a truthful tail caveat
        /// after a saved-paragraph that still promised "everything up to the
        /// moment the connection went, including the last readings" — and a
        /// later paragraph cannot cancel an earlier absolute claim; the
        /// what-to-do paragraph promised recording had restarted whether or
        /// not a successor opened; and the caveat's "a write failed as the
        /// recording was being closed" was false when the file had died
        /// earlier. So the three facts this window can be given — the tail
        /// is uncertain, a successor opened, the sink had already failed
        /// before the drop — each select a paragraph, and no paragraph claims
        /// what its fact does not support. The ordinary window (tail
        /// certain, successor opened) reads exactly as it did.
        /// </summary>
        /// <param name="sinkFailedBeforeDrop">The file had already stopped
        /// taking writes before the connection went — a live write failed
        /// and closed it — so the tail stops at that earlier fault, not at
        /// the seal. Read only when <paramref name="tailUncertain"/>.</param>
        public CaptureSealNotice(string radioName, string archivePath,
                                 bool successorOpened, Guid? archivedSessionId,
                                 bool tailUncertain, bool sinkFailedBeforeDrop)
            : this(radioName, archivePath, successorOpened, archivedSessionId, tailUncertain,
                   sinkFailedBeforeDrop, recordingNow: successorOpened)
        {
        }

        /// <summary>
        /// <b>Whether anything is recording is read when the notice is built,
        /// not when the seal happened</b> (Sol's review of H9, blocker 1).
        /// The seal's "a successor opened" bit is true the moment the seal
        /// returns, and the notice is shown up to five minutes later, once
        /// the archive has committed. In that gap the operator can turn the
        /// standing log off in Settings, or the successor's own file can
        /// fail — and "JJ Flexible has already started recording again, so
        /// the next thing that happens is being kept too" is then false in
        /// its load-bearing half. So the paragraph that says what is being
        /// kept NOW is chosen by the coordinator's state NOW, and the seal's
        /// bit only tells the two not-recording paragraphs apart: nothing
        /// opened after the seal, or something opened and has since stopped.
        /// </summary>
        /// <param name="recordingNow">The coordinator's recording state at the
        /// moment this notice is composed — a live sink that has not faulted.</param>
        public CaptureSealNotice(string radioName, string archivePath,
                                 bool successorOpened, Guid? archivedSessionId,
                                 bool tailUncertain, bool sinkFailedBeforeDrop,
                                 bool recordingNow)
        {
            RadioName = (radioName ?? string.Empty).Trim();
            ArchivePath = archivePath ?? string.Empty;
            SuccessorOpened = successorOpened;
            ArchivedSessionId = archivedSessionId;
            TailUncertain = tailUncertain;
            SinkFailedBeforeDrop = tailUncertain && sinkFailedBeforeDrop;
            RecordingNow = recordingNow;
        }

        /// <summary>The radio's nickname, or empty when we never learned one.</summary>
        public string RadioName { get; }

        /// <summary>
        /// A terminal record or the close failed as the session was sealed,
        /// so the archived file's last lines may not have reached the disk.
        /// The archive is committed — this window does not open otherwise —
        /// but a committed archive of a file whose tail is uncertain is not a
        /// complete recording, and the operator sending it should know.
        /// </summary>
        public bool TailUncertain { get; }

        /// <summary>
        /// The sealed file had already stopped taking writes before the drop,
        /// so its tail stops at that earlier fault. Always false when
        /// <see cref="TailUncertain"/> is false: it qualifies the tail, and a
        /// certain tail has nothing to qualify.
        /// </summary>
        public bool SinkFailedBeforeDrop { get; }

        /// <summary>
        /// Whether a fresh recording really opened after the seal. A fact of
        /// the seal, frozen then. It does NOT choose the "being kept" promise
        /// — <see cref="RecordingNow"/> does — it only tells the two
        /// not-recording paragraphs apart.
        /// </summary>
        public bool SuccessorOpened { get; }

        /// <summary>
        /// Whether something is recording at the moment this notice was
        /// composed. Chooses the what-to-do paragraph: the ordinary one
        /// promises that what happens next is being kept, and that promise is
        /// made only when a live, unfaulted sink exists NOW.
        /// </summary>
        public bool RecordingNow { get; }

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

        /// <summary>
        /// That the recording was kept, and what is in it. The ordinary
        /// sentence promises everything up to the drop including the last
        /// readings; with an uncertain tail that promise cannot be made, and
        /// the alternative says only that the file may stop short — the
        /// caveat paragraph then says how and what to tell Noel. DRAFT for
        /// the alternative — Noel's to rule; in the recording-health wording
        /// file.
        /// </summary>
        public string WhatWasSaved =>
            TailUncertain
                ? Lexicon.Get("logging.capture.dropped.saved_tail_uncertain")
                : Lexicon.Get("logging.capture.dropped.saved");

        /// <summary>The label above the path.</summary>
        public string PathLabel => Lexicon.Get("logging.capture.dropped.path_label");

        /// <summary>
        /// What to do with it. The ordinary sentence says JJ Flexible has
        /// already started recording again and what happens next is being
        /// kept — said only when something is recording NOW. Otherwise one
        /// of two: nothing opened after the seal, or a fresh log did open and
        /// has since stopped (the operator turned it off during the archive
        /// wait, or its file failed); both say where to read why. DRAFTS for
        /// the alternatives — Noel's to rule; in the recording-health wording
        /// file.
        /// </summary>
        public string WhatToDo =>
            RecordingNow
                ? Lexicon.Get("logging.capture.dropped.what_to_do")
                : SuccessorOpened
                    ? Lexicon.Get("logging.capture.dropped.what_to_do_stopped_since")
                    : Lexicon.Get("logging.capture.dropped.what_to_do_not_recording");

        /// <summary>
        /// The extra paragraph when <see cref="TailUncertain"/>: that the
        /// file's last lines may be missing, WHY, and that it is still the
        /// one to send. Two reasons exist and they are different sentences:
        /// a write failed as the recording was being closed, or the file had
        /// already stopped taking writes before the drop
        /// (<see cref="SinkFailedBeforeDrop"/>). Empty when the tail is
        /// certain, so the ordinary window's prose is untouched. DRAFT —
        /// Noel's to rule; listed in the recording-health wording file.
        /// </summary>
        public string TailCaveat =>
            !TailUncertain ? string.Empty
            : SinkFailedBeforeDrop
                ? Lexicon.Get("logging.capture.dropped.tail_uncertain_earlier")
                : Lexicon.Get("logging.capture.dropped.tail_uncertain");

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
            + (TailUncertain ? Environment.NewLine + Environment.NewLine + TailCaveat : string.Empty);

        /// <summary>
        /// The whole notice as one block, path included — for the trace, and
        /// for anything that wants to render it without a window.
        /// </summary>
        public string AsText() =>
            WhatHappened + Environment.NewLine + Environment.NewLine
            + WhatWasSaved + Environment.NewLine + Environment.NewLine
            + PathLabel + Environment.NewLine + ArchivePath + Environment.NewLine + Environment.NewLine
            + WhatToDo
            + (TailUncertain ? Environment.NewLine + Environment.NewLine + TailCaveat : string.Empty);
    }
}
