#nullable enable
namespace Radios.Speech
{
    /// <summary>
    /// What an utterance is ABOUT — the identity the arbiter uses to decide
    /// whether a newer announcement has made an unheard older one worthless.
    ///
    /// <para><b>Why a subject and not a timer.</b> Until #503 the ledger
    /// expired a rescued utterance by a bound derived from its own word count:
    /// twice the estimated time to say it. That made lifetime a function of
    /// LENGTH, and length is inversely related to how long a message stays
    /// true. <c>SWR 1.7</c> — seven characters, the answer to a question the
    /// operator asked by keying up — got 1.6 seconds; a 300-character courtesy
    /// paragraph about mic profiles got thirty. Measured across 2026-09-01: 89
    /// drops, 52 of them never re-spoken even once, and the one keypress that
    /// discarded the SWR reading was the operator pressing Tune AGAIN because
    /// they had heard nothing — the retry a silent system invites was the
    /// event that guaranteed the silence.</para>
    ///
    /// <para><b>The discriminator is supersession, not age.</b> Some of those
    /// 89 were dropped correctly: the digits <c>1</c> and <c>5</c> typed into a
    /// field are worthless once <c>Tune Power 15</c> has been said, and a
    /// progress line is worthless the moment the next one exists. What makes
    /// them worthless is not that they are old — it is that something newer
    /// covers the same subject. <c>SWR 1.7</c> is covered by nothing: no later
    /// announcement says what the tune measured, so it stays worth hearing
    /// until the next tune says otherwise.</para>
    ///
    /// <para><b>The emitter declares the subject; the arbiter never infers
    /// it.</b> Pattern-matching message text to guess what it is about is the
    /// description-drift defect this project loses days to — the words change
    /// and the guess silently stops matching. The call site is the only place
    /// that knows what the announcement is about, so the call site says so.
    /// Utterances sharing a subject replace one another: before emission for
    /// <see cref="SpeechIntent.Latest"/> (coalescing, the existing
    /// <c>coalesceKey</c>), and in the salvage ledger for everything else
    /// (supersession). One idea, two stages.</para>
    ///
    /// <para><b>An unkeyed utterance keeps the word-count bound.</b> Most
    /// emitters declare nothing today, and for those the arbiter cannot know
    /// what would supersede them — so it keeps the conservative bound rather
    /// than keeping everything, because a stale "Muted" re-spoken after an
    /// unheard "Unmuted" is a lie, and the old bound at least kills those
    /// quickly. Declaring a subject is the emitter saying "this remains true
    /// until something with the same subject replaces it", and that is a
    /// statement only the emitter can make. The drop trace says "no subject
    /// declared" so each remaining stale drop names its own fix.</para>
    ///
    /// <para><b>The constants here are the vocabulary.</b> A subject is a
    /// plain string so that parameterised identities (a value field by its
    /// label, a slice by its letter) can be expressed, but every spelling
    /// lives in this one class so two emitters cannot invent two names for
    /// one thing. Add a constant here rather than a literal at a call site.</para>
    /// </summary>
    public static class SpeechSubject
    {
        /// <summary>
        /// The narration of a slow operation — "Searching for radios", "Still
        /// searching", "Connected to X. Waiting for slice...". Only the newest
        /// line is ever worth hearing, and none of them once the operation has
        /// ended, which <see cref="ProgressVoice"/> declares through
        /// <see cref="ScreenReaderOutput.Supersede"/> when it stops.
        /// </summary>
        public const string Progress = "progress";

        /// <summary>
        /// Whether radio audio is playing through this computer. "PC audio
        /// on." is true until "PC audio off" or "could not start" replaces it,
        /// and nothing else replaces it — a tune, a band change or a focus
        /// move leaves it exactly as true as it was.
        /// </summary>
        public const string PcAudio = "pc-audio";

        /// <summary>
        /// The SWR measured by the tune that just ended. The answer to a
        /// question the operator asked by keying up; covered only by the next
        /// tune's reading. Pressing Tune again is NOT what covers it — that
        /// press is the retry a lost answer provokes, and it must deliver the
        /// answer rather than destroy it (#503, the 2026-09-01 case).
        /// </summary>
        public const string SwrAfterTune = "swr-after-tune";

        /// <summary>
        /// The reflected-power cut ended the transmission — which rung, how
        /// much came back, and that the operator is no longer on the air
        /// (#224, #571). A safety outcome the operator has no other way of
        /// learning; covered only by the next cut, never by the ordinary
        /// transmit and receive announcements, because "why did that stop"
        /// stays a live question until it has been answered once.
        /// </summary>
        public const string ReflectedPowerCut = "reflected-power-cut";

        // ── The other three transmit-safety owners ────────────────────────
        //
        // Added 2026-09-23. Three `PttSafetyController` Urgent calls declared
        // NO subject, and an urgent warning became a protected obligation the
        // day before — so those three could be retired by nothing at all. That
        // is the safe direction and the wrong one: the owner's own later
        // episode is the one lifecycle event allowed to retire an unheard
        // warning, and without a subject the owner cannot reach its own
        // sentence. An accumulating set of unretirable warnings then competes
        // for release slots with the one that matters.
        //
        // **Deliberately three separate owners, not one safety subject.** A
        // broad "transmit safety" subject would let the missing-microphone
        // warning retire the reflected-power cut, which says nothing about it:
        // a replacement must come from the same owner about the same
        // condition, or it is not a replacement at all.

        /// <summary>
        /// The transmit time limit: the warning that the transmission is about
        /// to be ended, and the announcement that it HAS been ended, are one
        /// incident with one owner.
        ///
        /// <para>Sharing the subject is the point and is the only pairing of
        /// this kind here. "You are about to be cut off" is worth nothing once
        /// "you have been cut off" is true, and the outcome explicitly covers
        /// the warning's fact — so if the warning is still unheard when the
        /// stop happens, the stop retires it rather than queueing behind it.
        /// Every other transmit-safety condition keeps its own subject,
        /// because no other pair stands in that relation.</para>
        /// </summary>
        public const string TransmitTimeLimit = "transmit-time-limit";

        /// <summary>
        /// Nothing at all is reaching the radio from the microphone path
        /// (#571). Its own owner: the operator is transmitting into silence,
        /// which is a different fault from the power coming back and is not
        /// covered by it. Retired by a later verdict from the same check.
        /// </summary>
        public const string NoTransmitAudio = "no-transmit-audio";

        /// <summary>
        /// Reflected power is high enough to warn about, whether or not a cut
        /// follows (#224, #571).
        ///
        /// <para><b>Not the same subject as
        /// <see cref="ReflectedPowerCut"/>.</b> A warning that power is coming
        /// back and an announcement that the transmission was ENDED are
        /// different facts about the same meter, and the operator needs the
        /// second whether or not they heard the first. Sharing one subject
        /// would let a later warning retire an unheard cut — a sentence saying
        /// the station is off the air, retired by one saying it might be in
        /// trouble.</para>
        /// </summary>
        public const string ReflectedPowerWarning = "reflected-power-warning";

        /// <summary>
        /// The receipt that a change will not survive disconnect unless the
        /// profile is saved (#442). One reminder outstanding at a time: the
        /// newest change's receipt covers every earlier one.
        /// </summary>
        public const string ProvisionalReceipt = "provisional-receipt";

        /// <summary>
        /// What this application decided to do about the PROFILES on the radio
        /// just connected to — applied the operator's transmit audio, loaded
        /// their whole set, left everything alone, or could not and why.
        ///
        /// <para><b>Exactly one of these is true at a time</b>, which is what
        /// makes them one subject: a later verdict does not merely follow the
        /// earlier one, it REPLACES it. "Your transmit audio is applied" and
        /// "this radio's profiles were left alone" cannot both describe the
        /// same connection, so an unheard one is worthless the moment the
        /// other exists.</para>
        ///
        /// <para><b>Keyed after the Sprint 44 integration pass, not by the
        /// track that wrote them.</b> Track D authored seven of these against
        /// the API as it stood before Track A landed, so they were unkeyed —
        /// and one of them, "This radio's profiles were left alone…", is a
        /// message the 2026-09-01 traces show being DROPPED three times. The
        /// sprint that fixed the channel would otherwise have shipped seven
        /// new emitters that did not use the fix, which is exactly the
        /// confound it existed to remove: an announcement that goes missing
        /// during a guest-radio test must not leave the operator unable to
        /// tell a broken feature from a dropped sentence.</para>
        ///
        /// <para>These fire at connect, in a burst, across window changes —
        /// the worst case the arbiter has.</para>
        /// </summary>
        public const string ProfileGuestOutcome = "profile-guest-outcome";

        /// <summary>
        /// The station-first connect's verdict when it is NOT healthy: the
        /// restore could not be confirmed, or the station came up incomplete
        /// (Sprint 45 Track G; #563, #579, #588). Says what is kept, that
        /// nothing was layered on top, and how to retry. One sentence per
        /// connect; a healthy outcome says nothing under this subject.
        /// Distinct from <see cref="ProfileGuestOutcome"/> because it is about
        /// the operator's OWN station, not a guest's stewardship, and the two
        /// can both be true of one connect.
        /// </summary>
        public const string ProfileStationOutcome = "profile-station-outcome";

        /// <summary>
        /// That the connect added NO slices, and why: the end of the radio's
        /// own initial setup is not yet knowable (bench question D), so no
        /// fresh allocation ran. Its own subject because it is true
        /// independently of the restore verdict, and because it is the one
        /// sentence that turns "every slice key is silent" from a broken
        /// application into a described state. Covered only by the next
        /// connect.
        /// </summary>
        public const string ProfileStationSlices = "profile-station-slices";

        /// <summary>
        /// The owner connected and found another operator on their radio
        /// (ruled 2026-09-22, #590 case 2): the profile was NOT loaded over
        /// them, the owner's frequencies were put on whatever slices were
        /// free (or could not be, and why), and the operator may change the
        /// transmit slice or remove the other client themselves. Its own
        /// subject because it is a different fact from the restore verdict —
        /// nothing failed; a choice was made on the operator's behalf and
        /// must be heard as one. Covered only by the next connect's answer
        /// to the same question.
        /// </summary>
        public const string ProfileStationCompany = "profile-station-company";

        /// <summary>
        /// Another operator arriving on or leaving the radio, as the RADIO'S
        /// OWN status reported it — "{who} connected", "{who} disconnected".
        /// One subject for both directions, because the later of the two is
        /// the current state of that company and the earlier one is
        /// worthless once it is heard: an arrival announced after the same
        /// client's departure would describe a roster that no longer exists.
        /// Emitted only for a report on this connection's command transport;
        /// a client that appears in SmartLink's list or a discovery broadcast
        /// is shown, labelled as reported, and never spoken under this subject
        /// (#634, Track L6).
        /// </summary>
        public const string ClientPresence = "client-presence";

        /// <summary>
        /// The radio the operator is on, as stated by the connect briefing's
        /// lead — "Connected to FLEX-8600, SmartLink, 4 slices." One
        /// connection at a time, so the next connect's lead replaces an
        /// unheard one. Emitted by <see cref="ConnectBriefing"/> at the
        /// settle moment, never by a call site of its own (#510).
        /// </summary>
        public const string ConnectLead = "connect-lead";

        /// <summary>
        /// Where the operator is — the Home arrival ("JJ Flexible Home,
        /// Modern tuning mode") and the Home landing prefix ("JJ Flexible
        /// Home, slice, 14.100.000"). Only the newest is true: an arrival
        /// still unheard when a landing prefix speaks is covered by it.
        /// Dialog titles are deliberately NOT here — see the #503 notes on
        /// why "where focus is" across every window is a design of its own.
        /// </summary>
        public const string WhereYouAre = "where-you-are";

        /// <summary>
        /// The title a dialog speaks for itself as it opens — the one line in
        /// <c>JJFlexDialog</c> that 74 dialogs inherit, kept because NVDA may
        /// read the focused control instead of the window.
        ///
        /// <para><b>Deliberately its own subject, and deliberately not
        /// <see cref="WhereYouAre"/></b>, whose remarks say why: "where focus
        /// is" across every window is a design of its own, and folding a
        /// dialog title into Home's arrival would let either retire the
        /// other. What this subject buys is narrower and real — the line
        /// cannot outlive the window it names. A title still queued when its
        /// dialog closes is taken back rather than spoken over whatever
        /// replaced it, and a newer dialog's title retires an unheard older
        /// one, because only one window is in front of the operator at a
        /// time.</para>
        ///
        /// <para><b>What this is NOT.</b> It does not stop the duplication
        /// Astra found — this line is a third producer of the window's name,
        /// beside the progress voice and NVDA's own narration, and removing it
        /// is the architectural direction. That removal needs every affected
        /// arrival checked for a real named focus destination, at the
        /// keyboard, because a dialog that announces nothing is worse than one
        /// that announces twice (#551, #606).</para>
        /// </summary>
        public const string DialogArrival = "dialog-arrival";

        /// <summary>
        /// The radio's own mic-profile selection at connect — repaired by
        /// loading one, or found empty and warned about. Two verdicts on one
        /// radio cannot both be true, so the newer replaces the older.
        /// </summary>
        public const string MicProfileOnRadio = "mic-profile-on-radio";

        /// <summary>
        /// Instrumentation running that the operator cannot see — "Recording
        /// is on." (#194 by way of #253). Superseded by the next notice about
        /// what is running.
        /// </summary>
        public const string RunningInstrumentation = "running-instrumentation";

        /// <summary>
        /// The foreground watchdog's explanation after it takes the keyboard
        /// back from another program (#529): "Microsoft Teams had taken the
        /// keyboard. You are back in Select Radio." Only the newest is worth
        /// hearing — a second reclaim's sentence covers the first — and
        /// nothing else covers it, because nothing else tells the operator
        /// why their dialog just announced itself again. Spoken a beat after
        /// the grab, deliberately, so the reader's own window announcement
        /// cannot flush it (see <see cref="StrandedFocusSentinel.ReclaimAnnounceDelayMs"/>).
        /// </summary>
        public const string KeyboardReclaimed = "keyboard-reclaimed";

        /// What the JJ key offers from here — the command list behind
        /// <c>JJ key H</c>, the explorer behind <c>JJ key slash</c>, and the
        /// layer's own answers to a key it did not know (the near miss, the
        /// unknown-key sentence). One subject because they answer one
        /// question, "what can I press?", and only the newest answer is worth
        /// hearing: an operator who presses H twice wants the list from the
        /// top, not the tail of the first reading and then the second, and an
        /// unknown-key sentence still queued when the list starts has done
        /// its job. Nothing else supersedes it — a toggle, a slice jump or a
        /// tune leaves the map exactly as true as it was.
        /// </summary>
        public const string JjKeyHelp = "jj-key-help";

        /// <summary>
        /// What the message key just did (Sprint 48 Track A, #151) — "Sending
        /// CQ", "CQ sent", "CQ stopped", "CQ not sent" and why, or "CQ has no
        /// recording, and the radio is in USB". One subject because every one
        /// of them answers the same question, "did my message go out?", and
        /// only the newest answer is true: an unheard "Sending CQ" is
        /// worthless once "CQ sent" exists, and a refusal must replace a
        /// stale success rather than queue behind it. Nothing outside the
        /// message keys covers it — a tune, a slice jump or a mode change
        /// leaves the last send exactly as sent as it was.
        ///
        /// <para>The message keys are a contest surface: pressed every few
        /// seconds, from a logger, by an operator who cannot see the radio.
        /// A send that went out and was never confirmed is the retry a lost
        /// answer provokes, which here costs RF.</para>
        /// </summary>
        public const string MessageKey = "message-key";

        /// <summary>
        /// What just happened to the tracking notch list — a notch placed, a
        /// notch removed, or the radio declining to do either (#482). One
        /// subject because they are successive answers to one question, "is
        /// the notch there or not?", and only the newest is true: an unheard
        /// "Notch added at 14.235" is worthless once "Notch removed" is the
        /// state of the radio.
        ///
        /// <para>The refusals share it deliberately. A tracking notch is
        /// created by the RADIO, not by us, so Add can succeed, fail, or fail
        /// slowly — and without sight those three are the same silence. The
        /// refusal sentence is the only thing that distinguishes them, so it
        /// must be as durable as the success it replaces, not expired by being
        /// a few words longer.</para>
        ///
        /// <para>Nothing outside the notch list covers it: a band change, a
        /// tune or a slice jump leaves a placed notch exactly as placed as it
        /// was, and a permanent notch survives a power cycle.</para>
        /// </summary>
        public const string TrackingNotch = "tracking-notch";

        /// <summary>
        /// One operator alarm's current warning (#566) — its firing, its
        /// reminders, a further step in the bad direction, and the loss of its
        /// reading. One subject per alarm because each of these restates the
        /// same question, "what is this meter doing and what should I do", and
        /// only the newest answer is true: an unheard "61 degrees" is worthless
        /// once "63 degrees" exists. Nothing outside the alarm covers it — a
        /// tune, a slice jump or a focus change leaves the PA exactly as hot as
        /// it was. Deliberately NOT one subject for all alarms: a supply
        /// warning must not retire a temperature warning it says nothing about.
        /// </summary>
        public static string OperatorAlarm(string alarmId) => "operator-alarm:" + alarmId;

        /// <summary>
        /// The state of the alarms as a whole, volunteered — a clearance, or a
        /// requested status or summary. Only the newest statement of state is
        /// worth hearing, and a warning never covers it (it is not on this
        /// subject), because "cleared" must not be able to retire "63 degrees".
        /// </summary>
        public const string OperatorAlarmStatus = "operator-alarm-status";

        /// <summary>
        /// Where the diagnostic recording went after a connection drop archived
        /// it — the confirmation that its path is now on the clipboard (Sprint
        /// 45 Track H, #566's bridge). One subject because the only utterances
        /// on it are successive answers to "have I got the path?", and only the
        /// newest is true: an unheard "Path copied" is worthless once a second
        /// press has copied it again, and a failure sentence must replace a
        /// success rather than queue behind it.
        ///
        /// <para>Nothing outside this window covers it. The radio reconnecting
        /// does not un-copy a path, and the operator is mid-errand — they
        /// pressed a button to get something they intend to paste somewhere
        /// else.</para>
        /// </summary>
        public const string CaptureArchivedPath = "capture-archived-path";

        /// <summary>
        /// The value of one field, named by its label — the committed value
        /// and the swept value share it, so a committed value still queued
        /// when the operator starts sweeping is covered by the sweep. This is
        /// also the field's Latest coalesce key, on purpose: they were always
        /// the same identity.
        /// </summary>
        public static string ValueField(string label) => "value-field:" + label;

        /// <summary>
        /// The keystrokes of a value being typed into one field — the digit
        /// echoes, the point, the sign, a delete. Emitted ADDITIVE, because a
        /// digit must not supersede the digit before it: interrupted
        /// mid-entry, the operator needs "1, 5" again, not a lone "5" over a
        /// field that reads 15. Deliberately not <see cref="ValueField"/>
        /// either, so retiring the entry cannot retire a committed value that
        /// is still true. The echoes are retired together, by the field
        /// calling <see cref="ScreenReaderOutput.Supersede"/> when the entry
        /// ends — in a value, a rejection or a cancel — which is what makes
        /// "1" and "5" worthless once "Tune Power 15" is said.
        /// </summary>
        public static string ValueEntry(string label) => "value-entry:" + label;

        /// <summary>
        /// One target inside a value sub-layer (<see cref="ValueSubLayer"/>),
        /// named by the layer and the target — the audio layer's pan, the
        /// filter layer's low edge. Every nudge, every selection announcement
        /// and every spoken answer about that target share it, so a held
        /// arrow settles to the tail value and the answer to "S" is covered
        /// by the next move. This is also the target's Latest coalesce key,
        /// for the same reason <see cref="ValueField"/> is: they were always
        /// one identity. A single-value layer passes an empty target and
        /// gets the layer's own name.
        /// </summary>
        public static string ValueLayer(string layerId, string targetId = "")
            => string.IsNullOrEmpty(targetId)
                ? "value-layer:" + layerId
                : "value-layer:" + layerId + ":" + targetId;

        /// <summary>
        /// The state of a value sub-layer as a whole — entered, which group
        /// is active, the in-layer help, closed, restored. One of these is
        /// true at a time: "Audio layer closed" makes an unheard entry
        /// sentence worthless, and the help re-states everything the entry
        /// said. Kept apart from <see cref="ValueLayer"/> so closing the layer
        /// cannot retire a value announcement that is still true.
        /// </summary>
        public static string ValueLayerStatus(string layerId) => "value-layer-status:" + layerId;

        // ── The system-wide keys (#307, Sprint 48 Track B) ───────────────

        /// <summary>
        /// What the system-wide keys dialog last said about a chord — the
        /// assignment, the four-layer conflict report, a refusal. Each report
        /// is about the key the operator just pressed and is covered by the
        /// next one; an unheard report about a chord they have since changed
        /// is worthless.
        /// </summary>
        public const string SystemWideKeyPicker = "system-wide-key-picker";

        /// <summary>
        /// The system-wide JJ key was asked to open a layer from another
        /// program, and the layer needs this window. One sentence, true until
        /// the operator presses the JJ key again; the next JJ key press
        /// covers it.
        /// </summary>
        public const string SystemWideLeaderRefusal = "system-wide-leader-refusal";

        /// <summary>
        /// The fail-safe behind the system-wide push to talk ended a transmit
        /// on its own — the release was missed, or posted and never honoured,
        /// or the hold ran past the ceiling. A safety outcome the operator has
        /// no other way of learning, and the only thing that covers it is the
        /// next such outcome. Kept apart from the in-window transmit subjects
        /// for the reason those are kept apart from each other: a replacement
        /// must come from the same owner about the same condition.
        /// </summary>
        public const string SystemWidePttWatchdog = "system-wide-ptt-watchdog";
    }
}
