using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The operator's per-radio answer to "what may JJ Flexible do to THIS
    /// radio's profiles?" (#450, #451, ruled 2026-09-01; granularity ruled in
    /// #499 and driven by #501, 2026-09-02).
    ///
    /// <para><b>The default is the whole point.</b> Until this is answered,
    /// connecting to a radio changes none of its profiles. Before this
    /// existed, every connect applied the operator's default global, transmit
    /// and microphone profiles to whatever radio came up — CREATING them on
    /// the radio when absent — and nothing put anything back. The names came
    /// from one list per human, so a guest on somebody else's station swapped
    /// its transmit audio to their own studio settings, silently, every time.
    /// </para>
    ///
    /// <para><b>The middle is where the real use lives (#501).</b> Until
    /// 2026-09-02 the only unit of change was "load ALL my profiles onto this
    /// radio". An operator borrowing a radio wants their own transmit audio
    /// and nothing else disturbed, and with no such option Noel correctly
    /// declined to rewrite Don's setup — and lost an evening of bench time
    /// because he then could not test at all. <see cref="UseMyTransmitAudio"/>
    /// is that middle.</para>
    ///
    /// <para>Numeric values are stable for saved configs. An absent element
    /// deserialises to <see cref="NotAnswered"/>, which is the safe
    /// answer.</para>
    /// </summary>
    public enum ProfileGuestIntent
    {
        /// <summary>Not answered. The DEFAULT: JJ Flexible loads nothing and
        /// saves nothing on this radio's profiles, and the question may be
        /// raised. The first connect to a radio the operator has never used
        /// changes NOTHING.</summary>
        NotAnswered = 0,

        /// <summary>"Leave this radio's profiles alone." A real answer, not a
        /// deferral: the question stops being raised, and nothing here writes
        /// to this radio again.</summary>
        LeaveAlone = 1,

        /// <summary>"Load my profiles here." On a radio the operator has
        /// declared theirs: load them and leave them, creating an absent one
        /// as ordinary housekeeping. On any other radio: select only profiles
        /// the radio ALREADY HAS, never create one, turn the radio's autosave
        /// off for the visit, and put the names it was on back when leaving.
        /// The whole-profile-set option — the intrusive one on a borrowed
        /// station, kept because it is right on your own.</summary>
        LoadMineAndPutBack = 2,

        /// <summary>"Use my transmit audio here, and nothing else." The
        /// operator's own transmit-audio settings — kept on THIS COMPUTER, not
        /// on the radio — are applied to the radio's LIVE state, setting by
        /// setting, with the radio's autosave turned off for the visit so that
        /// nothing lands in its owner's profile. The live settings that were
        /// there are captured first and put back on the way out. No profile on
        /// the radio is created, selected, saved or deleted. This is the middle
        /// #501 asked for, and #499's ruled design.</summary>
        UseMyTransmitAudio = 3,
    }

    /// <summary>
    /// Why one profile type was left alone. Every skip carries one of these
    /// rather than being silent — an operator who is told "your microphone
    /// profile was not loaded because the owner has unsaved work" can act; one
    /// who hears nothing concludes the feature is broken.
    /// </summary>
    public enum ProfileSkipReason
    {
        /// <summary>The per-radio "Change nothing on this radio" hold is
        /// armed. Outranks everything, including ownership.</summary>
        ChangeNothingArmed,

        /// <summary>The operator has never answered for this radio. The safe
        /// default, and the state in which the question is raised.</summary>
        NotOptedIn,

        /// <summary>The operator answered "leave this radio alone".</summary>
        OperatorSaidLeaveAlone,

        /// <summary>Nothing to load: no per-radio choice and no default.</summary>
        NothingWanted,

        /// <summary>The radio never reported its list for this type, so we do
        /// not know what is on it. An absence is not evidence — an unanswered
        /// list is not an empty one.</summary>
        RadioDidNotReportItsList,

        /// <summary>The radio's current selection for this type could not be
        /// read, so there is no prior value to record or put back.</summary>
        SelectionUnreadable,

        /// <summary>What we want is already loaded. The best outcome: no write
        /// at all, nothing to put back.</summary>
        AlreadyLoaded,

        /// <summary>The radio reports UNSAVED changes for this type — its
        /// owner has edits in flight. Applying ours would discard them, and a
        /// put-back that reloads a profile would discard them too.</summary>
        OwnerHasUnsavedWork,

        /// <summary>Another operator is on this radio right now. Loading a
        /// profile would change the station under someone using it — the same
        /// hazard the provisional-slice design refused to take.</summary>
        AnotherOperatorIsConnected,

        /// <summary>The profile we want is not on this radio, and this radio is
        /// not marked as the operator's. Creating it would be inventing state
        /// on somebody else's station.</summary>
        ProfileNotOnThisRadioAndNotOurs,

        /// <summary>A restore point from an EARLIER BUILD's session is sitting
        /// on this radio, so what is loaded now is that session's profile, not
        /// the radio owner's state. Nothing is loaded over it; the caller
        /// offers the restore.</summary>
        RestorePointAlreadyPresent,

        /// <summary>Nothing was changed for this type this session, so there is
        /// nothing to put back.</summary>
        NothingWasChanged,

        /// <summary>There is no radio to act on.</summary>
        NotConnected,

        /// <summary>The operator chose "use my transmit audio here" but has
        /// not said which of their transmit-audio profiles.</summary>
        NoLocalTransmitAudioChosen,

        /// <summary>The per-radio choice names a transmit-audio profile this
        /// computer no longer has — renamed, deleted, or a different
        /// operator's store.</summary>
        LocalTransmitAudioProfileNotFound,

        /// <summary>The radio has not reported whether its profile autosave is
        /// on. On a radio that is not ours, a live change made while autosave
        /// is secretly on lands in the owner's profile permanently, so nothing
        /// is changed until the radio has said.</summary>
        RadioDidNotReportAutosave,

        /// <summary>Turning the radio's autosave off did not take, so no live
        /// change was made: without autosave off, "change but do not save" may
        /// not be a state that exists.</summary>
        AutosaveCouldNotBeTurnedOff,
    }

    /// <summary>What one step of a plan does to the radio.</summary>
    public enum ProfileActionKind
    {
        /// <summary>Select the profile the operator wants for this radio.</summary>
        LoadOurs,

        /// <summary>Select the name the radio was on when we arrived. The
        /// put-back for a selection we changed.</summary>
        LoadTheirNameBack,

        /// <summary>Select a restore point an EARLIER BUILD left on the radio
        /// (the superseded marker design, #499). Offered only, never planned
        /// automatically; recognition is kept so those radios can be put
        /// right.</summary>
        LoadRestorePoint,

        /// <summary>Delete a restore point that is no longer needed.</summary>
        RemoveRestorePoint,

        /// <summary>Send <c>profile autosave off</c>. Always the FIRST action
        /// of any plan that changes a radio that is not ours, so nothing we
        /// change afterwards can be written into its owner's profile by the
        /// radio itself.</summary>
        TurnAutosaveOff,

        /// <summary>Send <c>profile autosave on</c>: give the owner's setting
        /// back. Always the LAST action of a put-back, and only when
        /// everything before it succeeded — with our changes still live,
        /// turning autosave on could commit them.</summary>
        TurnAutosaveOn,

        /// <summary>Read the radio's live transmit-audio settings into a
        /// snapshot kept on THIS COMPUTER. A read; nothing on the radio
        /// changes. Always before <see cref="ApplyLocalTransmitAudio"/>.</summary>
        CaptureLiveTransmitAudio,

        /// <summary>Apply the operator's local transmit-audio profile to the
        /// radio's live state, setting by setting. No profile on the radio is
        /// touched.</summary>
        ApplyLocalTransmitAudio,

        /// <summary>Put the captured live settings back, setting by setting —
        /// exactly what we changed and nothing else.</summary>
        RestoreLiveTransmitAudio,
    }

    /// <summary>One step of a plan. Plain data; tests construct and compare
    /// these freely, and nothing here touches a radio.</summary>
    public sealed class ProfileAction
    {
        public ProfileActionKind Kind;
        public ProfileTypes ProfileType;

        /// <summary>The profile name this step names — on the radio for a
        /// selection, on this computer for a local transmit-audio profile.</summary>
        public string ProfileName = "";

        /// <summary>Trace text: why this step is in the plan.</summary>
        public string Because = "";

        /// <summary>
        /// True when this step may CREATE the profile on the radio if it is
        /// not already there. Only ever set for a radio the operator has
        /// declared theirs, and never for putting a profile back — a restore
        /// that invents a profile is not a restore.
        /// </summary>
        public bool MayCreate;

        public override string ToString() =>
            Kind + " " + ProfileType + " \"" + ProfileName + "\"";
    }

    /// <summary>One profile type left alone, and why.</summary>
    public sealed class ProfileSkip
    {
        public ProfileTypes ProfileType;
        public ProfileSkipReason Reason;

        /// <summary>The name that was NOT loaded, when there was one.</summary>
        public string ProfileName = "";

        public override string ToString() => ProfileType + ": " + Reason;
    }

    /// <summary>
    /// What the radio reports about one profile type, plus what we want for it.
    /// The three-state reading matters: FlexLib collapses "the radio never
    /// answered" to an empty list, and an empty list is a legitimate answer
    /// ("none stored"), so the two must be distinguishable or a silent
    /// subscription reads as a bare radio.
    /// </summary>
    public sealed class ProfileTypeState
    {
        public ProfileTypes ProfileType = ProfileTypes.none;

        /// <summary>The radio has answered with its list for this type —
        /// possibly an empty one, which is a real answer.</summary>
        public bool Reported;

        /// <summary>Names the radio reports.</summary>
        public IReadOnlyList<string> Names = Array.Empty<string>();

        /// <summary>The radio's current selection: a name, "" when the radio
        /// says none is loaded, null when it could not be read.</summary>
        public string Selection;

        /// <summary>The radio reports unsaved changes for this type. Only the
        /// transmit and microphone types report this; global always reads
        /// false.</summary>
        public bool UnsavedChanges;

        /// <summary>The profile the operator wants loaded on THIS radio for
        /// this type. Empty means no opinion — nothing is loaded, which is a
        /// perfectly good answer.</summary>
        public string Wanted = "";
    }

    /// <summary>
    /// Everything the connect and disconnect decisions need to know, with no
    /// radio, window or thread anywhere in it.
    /// </summary>
    public sealed class ProfileSituation
    {
        public bool Connected = true;

        /// <summary>Whose radio the OPERATOR says this is. Governs whether a
        /// profile may be CREATED here (the existing
        /// <see cref="RadioConfig.MayCreateRadioSideState"/> concept — not a
        /// second vocabulary for the same question), and whether the radio's
        /// autosave is ours to leave alone or a guest's to switch off.</summary>
        public RadioOwnership Ownership = RadioOwnership.Unset;

        /// <summary>The per-radio opt-in.</summary>
        public ProfileGuestIntent Intent = ProfileGuestIntent.NotAnswered;

        /// <summary>The per-radio "Change nothing on this radio" hold.</summary>
        public bool ChangeNothingArmed;

        /// <summary>True when this client is the only station on the radio.
        /// Fail-safe direction matters: the caller's OnlyStation is false until
        /// the client list has been parsed at least once, so an early decision
        /// declines rather than acts.</summary>
        public bool OnlyStation = true;

        /// <summary>
        /// The radio's own profile-autosave setting: true or false as the
        /// radio REPORTED it, null when it has not reported it. Null is a
        /// refusal on a radio that is not ours — see
        /// <see cref="ProfileSkipReason.RadioDidNotReportAutosave"/>.
        /// </summary>
        public bool? RadioAutosave;

        /// <summary>The operator's per-radio choice of LOCAL transmit-audio
        /// profile, by name. Empty when none has been chosen.</summary>
        public string LocalTransmitAudioProfile = "";

        /// <summary>True when this computer's store actually holds the profile
        /// named above. A name with nothing behind it is a skip, not an
        /// apply.</summary>
        public bool LocalTransmitAudioProfileExists;

        /// <summary>
        /// True when a live-audio snapshot from an EARLIER session of this
        /// client is on disk for this radio — that session ended without
        /// putting the radio's own transmit audio back. The local analogue of
        /// a stranded restore point: it is OFFERED, never restored on its own,
        /// because the owner may have already put things right by hand.
        /// </summary>
        public bool StrandedLiveTransmitAudioSnapshot;

        public List<ProfileTypeState> Types = new List<ProfileTypeState>();

        public ProfileTypeState Type(ProfileTypes t) =>
            Types.FirstOrDefault(x => x.ProfileType == t);
    }

    /// <summary>
    /// What we recorded at connect so we can put the radio back. Lives in
    /// this process — which is exactly why the design changes as LITTLE as
    /// possible on a radio that is not ours, and makes the one thing it must
    /// leave changed (autosave) loud and one press to reverse.
    /// </summary>
    public sealed class ProfileSessionRecord
    {
        public ProfileTypes ProfileType = ProfileTypes.none;

        /// <summary>The name the radio was on when we arrived.</summary>
        public string TheirSelection = "";

        /// <summary>Always false since #499: no build after 2026-09-02 leaves
        /// a restore point on a radio. Kept so a record from the superseded
        /// design still reads correctly.</summary>
        public bool RestorePointLeft;

        /// <summary>What we loaded instead — a profile on the radio, or for
        /// <see cref="LiveTransmitAudio"/> the LOCAL profile applied.</summary>
        public string WeLoaded = "";

        /// <summary>
        /// True when this record is the live transmit-audio session: nothing
        /// on the radio was selected, the radio's live settings were changed
        /// and what is put back is the captured snapshot, not a profile name.
        /// </summary>
        public bool LiveTransmitAudio;
    }

    /// <summary>The result of one decision. Plain data.</summary>
    public sealed class ProfilePlan
    {
        public List<ProfileAction> Actions = new List<ProfileAction>();
        public List<ProfileSkip> Skips = new List<ProfileSkip>();

        /// <summary>What to record so the disconnect can put things back.
        /// Empty when nothing is being changed.</summary>
        public List<ProfileSessionRecord> Record = new List<ProfileSessionRecord>();

        /// <summary>True when the operator has never answered for this radio
        /// and the question is worth raising. Raising it is the caller's job;
        /// nothing here asks anything.</summary>
        public bool AskWhoseRadioThisIs;

        /// <summary>What to PRE-SELECT if the question is asked — never what to
        /// store. Same rule as <see cref="RadioConfig.SuggestOwnership"/>: a
        /// suggestion must be presented as one.</summary>
        public ProfileGuestIntent Suggestion = ProfileGuestIntent.NotAnswered;

        /// <summary>Restore points found on the radio that an EARLIER BUILD's
        /// session left behind. The caller OFFERS these; nothing here restores
        /// anything.</summary>
        public List<ProfileTypes> StrandedRestorePoints = new List<ProfileTypes>();

        /// <summary>
        /// True when this plan turns the radio's autosave off, so the caller
        /// records that it did — the one thing left changed if this process
        /// dies, and the thing any client can detect and reverse in one
        /// press.
        /// </summary>
        public bool TurnsAutosaveOff =>
            Actions.Any(a => a.Kind == ProfileActionKind.TurnAutosaveOff);

        public bool ChangesNothing => Actions.Count == 0;

        public bool Skipped(ProfileTypes t, ProfileSkipReason r) =>
            Skips.Any(s => s.ProfileType == t && s.Reason == r);
    }

    /// <summary>
    /// The predictable names of the restore points EARLIER BUILDS left on
    /// radios (the marker design ruled 2026-09-01 and superseded by #499 the
    /// same day), and how any JJ Flexible client recognises one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Recognition is kept; creation is gone.</b> No build after
    /// 2026-09-02 creates one of these — nothing is saved to a radio that is
    /// not ours, full stop. But a radio touched by the 2026-09-01 build may
    /// still be carrying one, and its owner's state is in it, so every client
    /// must still recognise the name and offer the restore.
    /// </para>
    /// <para>
    /// <b>Do not version the name.</b> A newer client must recognise an older
    /// client's restore point, and a restore point outlives the session that
    /// made it by definition.
    /// </para>
    /// </remarks>
    public static class ProfileRestorePoints
    {
        /// <summary>The common prefix. Recognition is a prefix match.</summary>
        public const string Prefix = "JJFlex put back ";

        /// <summary>Characters a restore-point name may never contain: the
        /// profile-list separator, the character the transmit and microphone
        /// create commands strip, and the quote the command wraps names in.
        /// </summary>
        public const string ForbiddenCharacters = "^*\"";

        /// <summary>The restore-point name for one profile type.</summary>
        public static string NameFor(ProfileTypes type)
        {
            switch (type)
            {
                case ProfileTypes.global: return Prefix + "global";
                case ProfileTypes.tx: return Prefix + "transmit";
                case ProfileTypes.mic: return Prefix + "microphone";
                case ProfileTypes.display: return Prefix + "display";
                default: return "";
            }
        }

        /// <summary>True when a name on the radio is one of ours.</summary>
        public static bool IsRestorePoint(string profileName) =>
            !string.IsNullOrEmpty(profileName)
            && profileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the name is exactly one an earlier build would
        /// have written.</summary>
        public static bool IsWellFormed(string profileName) =>
            IsRestorePoint(profileName)
            && profileName.IndexOfAny(ForbiddenCharacters.ToCharArray()) < 0
            && profileName == NameFor(TypeOf(profileName));

        /// <summary>Which type a restore-point name belongs to, or
        /// <see cref="ProfileTypes.none"/> when it is not one of ours.</summary>
        public static ProfileTypes TypeOf(string profileName)
        {
            foreach (var t in new[] { ProfileTypes.global, ProfileTypes.tx,
                                      ProfileTypes.mic, ProfileTypes.display })
            {
                if (string.Equals(profileName, NameFor(t), StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return ProfileTypes.none;
        }
    }

    /// <summary>
    /// Whether JJ Flexible may touch a radio's profiles, what it must do first,
    /// and how it puts things back — as pure functions, so a test can put a
    /// radio state in and read an action list out without a radio, a window
    /// or a thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a private method on FlexBase.</b> The same reason
    /// <see cref="TransmitSafety"/> is not a private method on the PTT
    /// controller. A decision that can only be reached by connecting to a real
    /// radio can only be TESTED by connecting to a real radio — and the radio
    /// this protects belongs to somebody else, three states away, whose only
    /// symptom of a wrong answer is that his microphone stops working at some
    /// unrelated hour. That verification loop does not exist. This one does.
    /// </para>
    /// <para>
    /// <b>The ruled design (#499, 2026-09-01).</b> A Flex profile is a saved
    /// snapshot; the live state is separate. Change settings without saving
    /// and the profile is untouched — the radio merely reports unsaved
    /// changes. So the owner's settings are never at risk: they are sitting
    /// in his profile the whole time. Therefore our profile is kept LOCALLY,
    /// applied to the LIVE state, nothing is saved to the radio, and putting
    /// things back means putting the live settings back — his profile was
    /// never written. No marker profile, no restore point, nothing stranded.
    /// </para>
    /// <para>
    /// <b>The one thing that could break it, and the one write this makes on a
    /// radio that is not ours.</b> With the radio's own autosave ON, "change
    /// but do not save" may not be a state that exists: every adjustment could
    /// land in the owner's profile by itself. So on a radio not marked ours,
    /// any plan that changes anything turns autosave OFF first and gives it
    /// back on the way out. <b>What autosave actually governs is not settled
    /// by measurement</b> — Noel's 2026-09-01 test released a slice, which is
    /// global-profile territory, and FlexLib's own obsolete-attribute text says
    /// only that transmit and microphone profiles "are now saved automatically
    /// with changes". Turning it off is right under either reading: if
    /// autosave writes, we are protected; if it does not, we have changed one
    /// boolean that any client can read and reverse in one press.
    /// </para>
    /// <para>
    /// <b>The crash case.</b> We can never guarantee we restore anything — the
    /// process that must clean up is the one that died. So the answer is not a
    /// better restore; it is changing as little as possible and making the
    /// little that is left wrong LOUD. Autosave is a single boolean any client
    /// can read and fix. A profile set is neither. That is the general
    /// principle, worth keeping beyond this feature: when you must change
    /// somebody else's setting, prefer one whose wrong state is detectable and
    /// trivially reversible over one that needs a faithful restore.
    /// </para>
    /// <para>
    /// <b>Remoteness is not what makes a radio someone else's.</b> A MultiFlex
    /// client on the same network is equally a guest, so nothing here consults
    /// the connection path.
    /// </para>
    /// </remarks>
    public static class ProfileStewardship
    {
        /// <summary>The three types this governs, in the order the connect
        /// sequence applies them. Display profiles are not included: JJ
        /// Flexible never selects one.</summary>
        public static readonly ProfileTypes[] GovernedTypes =
            { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic };

        /// <summary>
        /// The two profile types the live transmit-audio path touches. Mic
        /// gain, boost, bias, the compander and processor live in the
        /// microphone profile; the transmit filter, monitor and equaliser in
        /// the transmit profile. Both are consulted for unsaved work.
        /// </summary>
        public static readonly ProfileTypes[] TransmitAudioTypes =
            { ProfileTypes.tx, ProfileTypes.mic };

        /// <summary>The word for the live transmit-audio path in a
        /// sentence.</summary>
        public const string TransmitAudioLabel = "transmit audio";

        // ------------------------------------------------------------------
        // Connect
        // ------------------------------------------------------------------

        /// <summary>
        /// What to do to this radio's profiles on connect. Never throws, never
        /// touches a radio, and returns an empty plan whenever the answer is
        /// "nothing" — which is the answer for every radio the operator has not
        /// deliberately opted in.
        /// </summary>
        public static ProfilePlan PlanConnect(ProfileSituation s)
        {
            var plan = new ProfilePlan();
            if (s == null) return plan;

            // Stranded restore points are REPORTED whatever else is decided —
            // including under the change-nothing hold and on a radio we are
            // told to leave alone. Finding one is a read, and it is the one
            // piece of information the operator most needs: an earlier
            // session ended without putting this radio back.
            plan.StrandedRestorePoints.AddRange(StrandedRestorePoints(s));

            if (!s.Connected)
            {
                AddSkipForAll(plan, s, ProfileSkipReason.NotConnected);
                return plan;
            }

            // The hold outranks everything, ownership included: your own radio
            // can be the one that must not change today.
            if (s.ChangeNothingArmed)
            {
                AddSkipForAll(plan, s, ProfileSkipReason.ChangeNothingArmed);
                return plan;
            }

            if (s.Intent == ProfileGuestIntent.LeaveAlone)
            {
                AddSkipForAll(plan, s, ProfileSkipReason.OperatorSaidLeaveAlone);
                return plan;
            }

            if (s.Intent == ProfileGuestIntent.NotAnswered)
            {
                // THE DEFAULT, AND THE WHOLE POINT. A radio the operator has
                // never answered for is left exactly as it is, and the question
                // is raised once.
                AddSkipForAll(plan, s, ProfileSkipReason.NotOptedIn);
                plan.AskWhoseRadioThisIs = true;
                plan.Suggestion = Suggest(s);
                return plan;
            }

            if (s.Intent == ProfileGuestIntent.UseMyTransmitAudio)
            {
                PlanLiveTransmitAudio(plan, s);
                return plan;
            }

            // LoadMineAndPutBack. Selecting an EXISTING profile by name is not
            // itself an unsaved change, so this path does not touch the radio's
            // autosave — only the live-transmit-audio path does, because only
            // it edits live settings that autosave could capture.
            bool ours = s.Ownership == RadioOwnership.Mine;
            foreach (var type in GovernedTypes)
            {
                PlanOneType(plan, s, s.Type(type), type, ours);
            }
            return plan;
        }

        private static void PlanOneType(
            ProfilePlan plan, ProfileSituation s, ProfileTypeState st, ProfileTypes type, bool ours)
        {
            if (st == null || string.IsNullOrWhiteSpace(st.Wanted))
            {
                plan.Skips.Add(new ProfileSkip
                { ProfileType = type, Reason = ProfileSkipReason.NothingWanted });
                return;
            }

            void Skip(ProfileSkipReason r) => plan.Skips.Add(new ProfileSkip
            { ProfileType = type, Reason = r, ProfileName = st.Wanted });

            // An unanswered list is not an empty one. Without the radio's own
            // inventory we cannot tell whether our profile exists or whether an
            // earlier build's restore point is sitting there.
            if (!st.Reported) { Skip(ProfileSkipReason.RadioDidNotReportItsList); return; }

            // No prior value means nothing to put back. Changing the selection
            // now would be a one-way door.
            if (st.Selection == null) { Skip(ProfileSkipReason.SelectionUnreadable); return; }

            if (string.Equals(st.Selection, st.Wanted, StringComparison.Ordinal))
            {
                // The best outcome there is: what the operator wants is what is
                // already loaded, so nothing is written and there is nothing to
                // put back.
                Skip(ProfileSkipReason.AlreadyLoaded);
                return;
            }

            // The radio is telling us its owner has edits in flight. Loading
            // ours would discard them.
            if (st.UnsavedChanges) { Skip(ProfileSkipReason.OwnerHasUnsavedWork); return; }

            // Somebody else is on the radio. Loading a profile changes the
            // station under them.
            if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); return; }

            // An earlier build's restore point is here, so what is loaded RIGHT
            // NOW is that session's profile, not the owner's state. Leave
            // everything alone and let the caller offer the restore.
            if (Contains(st.Names, ProfileRestorePoints.NameFor(type)))
            {
                Skip(ProfileSkipReason.RestorePointAlreadyPresent);
                return;
            }

            // Loading a profile the radio already has is one thing; CREATING
            // one it does not have is inventing state on somebody's station.
            // That is the existing ownership question, asked through the
            // existing concept rather than a second one.
            if (!Contains(st.Names, st.Wanted) && !ours)
            {
                Skip(ProfileSkipReason.ProfileNotOnThisRadioAndNotOurs);
                return;
            }

            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.LoadOurs,
                ProfileType = type,
                ProfileName = st.Wanted,
                MayCreate = ours && !Contains(st.Names, st.Wanted),
                Because = "the operator chose this " + Label(type) + " profile for this radio",
            });

            // Nothing is put back on a radio the operator has declared theirs:
            // loading your profiles on your own radio is a standing
            // arrangement, and there is nobody to put it back for. That is the
            // behaviour every operator's own radio had before the opt-in
            // existed, and what #495 asked to have back.
            if (ours) return;

            plan.Record.Add(new ProfileSessionRecord
            {
                ProfileType = type,
                TheirSelection = st.Selection,
                RestorePointLeft = false,
                WeLoaded = st.Wanted,
            });
        }

        /// <summary>
        /// The middle (#501): the operator's own transmit audio, applied live,
        /// nothing else disturbed, nothing saved.
        /// </summary>
        private static void PlanLiveTransmitAudio(ProfilePlan plan, ProfileSituation s)
        {
            void Skip(ProfileSkipReason r) => plan.Skips.Add(new ProfileSkip
            {
                ProfileType = ProfileTypes.tx,
                Reason = r,
                ProfileName = s.LocalTransmitAudioProfile ?? "",
            });

            if (string.IsNullOrWhiteSpace(s.LocalTransmitAudioProfile))
            {
                Skip(ProfileSkipReason.NoLocalTransmitAudioChosen);
                return;
            }
            if (!s.LocalTransmitAudioProfileExists)
            {
                Skip(ProfileSkipReason.LocalTransmitAudioProfileNotFound);
                return;
            }

            // Somebody else is on the radio. Changing its transmit chain
            // changes the station under them.
            if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); return; }

            // The owner has edits in flight in the transmit or microphone
            // profile. The snapshot would put them back faithfully — but they
            // exist ONLY live, so a session that ends badly loses them
            // outright. Strictly more at risk than the saved case; refuse.
            foreach (var type in TransmitAudioTypes)
            {
                var st = s.Type(type);
                if (st != null && st.UnsavedChanges)
                {
                    Skip(ProfileSkipReason.OwnerHasUnsavedWork);
                    return;
                }
            }

            bool ours = s.Ownership == RadioOwnership.Mine;

            // On a radio that is not ours, a live change made while the radio's
            // autosave is on may be written into its owner's profile by the
            // radio itself. So autosave must be KNOWN, and if on, turned off
            // before the first setting changes. On our own radio the setting
            // is the operator's own and is left exactly as they keep it.
            if (!ours)
            {
                if (s.RadioAutosave == null)
                {
                    Skip(ProfileSkipReason.RadioDidNotReportAutosave);
                    return;
                }
                if (s.RadioAutosave == true)
                {
                    plan.Actions.Add(AutosaveOff(
                        "the operator's transmit audio is about to be applied live to a radio that is not ours"));
                }
            }

            // The snapshot FIRST, always: it is taken before the state it
            // records can be overwritten. An ordering defect here would
            // produce a snapshot holding OUR settings, which is worse than
            // none because it looks like a rescue.
            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.CaptureLiveTransmitAudio,
                ProfileType = ProfileTypes.tx,
                ProfileName = s.LocalTransmitAudioProfile,
                Because = "holds this radio's own live transmit audio so it can be put back exactly",
            });

            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.ApplyLocalTransmitAudio,
                ProfileType = ProfileTypes.tx,
                ProfileName = s.LocalTransmitAudioProfile,
                Because = "the operator chose this transmit-audio profile, kept on this computer, for this radio",
            });

            plan.Record.Add(new ProfileSessionRecord
            {
                ProfileType = ProfileTypes.tx,
                LiveTransmitAudio = true,
                TheirSelection = s.Type(ProfileTypes.tx)?.Selection ?? "",
                WeLoaded = s.LocalTransmitAudioProfile,
            });
        }

        // ------------------------------------------------------------------
        // Putting it back
        // ------------------------------------------------------------------

        /// <summary>
        /// What to do on the way out, given what was recorded at connect.
        ///
        /// <para>A selection we changed is put back by name. Live transmit
        /// audio is put back from the snapshot. And if this session turned the
        /// radio's autosave off, it is turned back on LAST, and only when
        /// every put-back before it is in the plan — with any of our changes
        /// still live, turning autosave on could commit them to the owner's
        /// profile, which is the one outcome this whole design exists to
        /// prevent. Leaving it off is the safe failure: detectable by any
        /// client and one press to reverse.</para>
        ///
        /// <para>A profile RESELECT refuses when another operator is on the
        /// radio or the radio reports unsaved work for that type, exactly as
        /// the connect plan refuses. The LIVE put-back does not refuse on
        /// unsaved work, because by then the unsaved work is ours — we turned
        /// autosave off and changed things — and it does not refuse under the
        /// hold, because the snapshot IS the radio's own setup: the hold
        /// exists to protect that, not to keep our changes on it.</para>
        /// </summary>
        /// <param name="autosaveWeTurnedOff">True when this session turned the
        /// radio's autosave off at connect.</param>
        public static ProfilePlan PlanPutBack(
            ProfileSituation s, IEnumerable<ProfileSessionRecord> record,
            bool autosaveWeTurnedOff = false)
        {
            var plan = new ProfilePlan();
            if (s == null) return plan;

            var records = (record ?? Enumerable.Empty<ProfileSessionRecord>()).ToList();
            bool everythingPutBack = true;

            foreach (var type in GovernedTypes)
            {
                var rec = records.FirstOrDefault(r => r.ProfileType == type && !r.LiveTransmitAudio);
                var st = s.Type(type);

                void Skip(ProfileSkipReason r)
                {
                    plan.Skips.Add(new ProfileSkip
                    {
                        ProfileType = type,
                        Reason = r,
                        ProfileName = rec?.TheirSelection ?? "",
                    });
                    if (rec != null) everythingPutBack = false;
                }

                if (rec == null)
                {
                    plan.Skips.Add(new ProfileSkip
                    { ProfileType = type, Reason = ProfileSkipReason.NothingWasChanged });
                    continue;
                }
                if (!s.Connected) { Skip(ProfileSkipReason.NotConnected); continue; }

                // The hold can be armed mid-session from Settings. It governs
                // what JJ Flexible writes, and a profile load is a write.
                if (s.ChangeNothingArmed) { Skip(ProfileSkipReason.ChangeNothingArmed); continue; }

                if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); continue; }

                if (st != null && st.UnsavedChanges)
                {
                    Skip(ProfileSkipReason.OwnerHasUnsavedWork);
                    continue;
                }

                if (st != null && !st.Reported)
                {
                    Skip(ProfileSkipReason.RadioDidNotReportItsList);
                    continue;
                }

                string restorePoint = ProfileRestorePoints.NameFor(type);
                bool theirNameStillThere =
                    !string.IsNullOrEmpty(rec.TheirSelection)
                    && st != null && Contains(st.Names, rec.TheirSelection);

                if (theirNameStillThere)
                {
                    plan.Actions.Add(new ProfileAction
                    {
                        Kind = ProfileActionKind.LoadTheirNameBack,
                        ProfileType = type,
                        ProfileName = rec.TheirSelection,
                        Because = "this radio was on this " + Label(type)
                                  + " profile when we arrived",
                    });

                    if (rec.RestorePointLeft && st != null && Contains(st.Names, restorePoint))
                    {
                        plan.Actions.Add(new ProfileAction
                        {
                            Kind = ProfileActionKind.RemoveRestorePoint,
                            ProfileType = type,
                            ProfileName = restorePoint,
                            Because = "the radio is back on its own " + Label(type)
                                      + " profile, so the restore point has nothing left to hold",
                        });
                    }
                    continue;
                }

                if (rec.RestorePointLeft && st != null && Contains(st.Names, restorePoint))
                {
                    // A record from the superseded design: the name is gone but
                    // the restore point holds the state. Deliberately NOT
                    // deleted afterwards — its contents are now the live
                    // state, and deleting it would leave one copy where there
                    // had been two.
                    plan.Actions.Add(new ProfileAction
                    {
                        Kind = ProfileActionKind.LoadRestorePoint,
                        ProfileType = type,
                        ProfileName = restorePoint,
                        Because = "the " + Label(type) + " profile this radio was on ("
                                  + rec.TheirSelection + ") is no longer here, but the "
                                  + "restore point still holds its settings",
                    });
                    continue;
                }

                Skip(ProfileSkipReason.SelectionUnreadable);
            }

            var live = records.FirstOrDefault(r => r.LiveTransmitAudio);
            if (live != null)
            {
                if (!s.Connected)
                {
                    plan.Skips.Add(new ProfileSkip
                    { ProfileType = ProfileTypes.tx, Reason = ProfileSkipReason.NotConnected });
                    everythingPutBack = false;
                }
                else if (!s.OnlyStation)
                {
                    // Another operator arrived mid-session. Putting the
                    // transmit chain back changes the station under them —
                    // and they may be transmitting through it.
                    plan.Skips.Add(new ProfileSkip
                    { ProfileType = ProfileTypes.tx, Reason = ProfileSkipReason.AnotherOperatorIsConnected });
                    everythingPutBack = false;
                }
                else
                {
                    plan.Actions.Add(new ProfileAction
                    {
                        Kind = ProfileActionKind.RestoreLiveTransmitAudio,
                        ProfileType = ProfileTypes.tx,
                        ProfileName = live.WeLoaded,
                        Because = "this radio's own live transmit audio, captured before ours was applied",
                    });
                }
            }

            if (autosaveWeTurnedOff && s.Connected && everythingPutBack)
            {
                plan.Actions.Add(new ProfileAction
                {
                    Kind = ProfileActionKind.TurnAutosaveOn,
                    ProfileType = ProfileTypes.none,
                    Because = "this session turned the radio's autosave off, and everything it changed is being put back first",
                });
            }

            return plan;
        }

        /// <summary>
        /// The plan for an OFFERED restore of stranded restore points — the
        /// ones an EARLIER BUILD's session left behind. Separate from
        /// <see cref="PlanPutBack"/> because the caller reaching this has
        /// already asked a human.
        ///
        /// <para>Nothing here decides to run: the operator does. That is the
        /// rule that prevents the clobber — we crash, the owner fixes his own
        /// audio, we reconnect, and an automatic restore undoes the repair he
        /// just made.</para>
        /// </summary>
        public static ProfilePlan PlanOfferedRestore(
            ProfileSituation s, IEnumerable<ProfileTypes> accepted)
        {
            var plan = new ProfilePlan();
            if (s == null) return plan;

            var wanted = (accepted ?? Enumerable.Empty<ProfileTypes>()).ToList();

            foreach (var type in GovernedTypes)
            {
                if (!wanted.Contains(type)) continue;
                var st = s.Type(type);

                void Skip(ProfileSkipReason r) => plan.Skips.Add(new ProfileSkip
                { ProfileType = type, Reason = r, ProfileName = ProfileRestorePoints.NameFor(type) });

                if (!s.Connected) { Skip(ProfileSkipReason.NotConnected); continue; }
                if (s.ChangeNothingArmed) { Skip(ProfileSkipReason.ChangeNothingArmed); continue; }
                if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); continue; }
                if (st == null || !st.Reported) { Skip(ProfileSkipReason.RadioDidNotReportItsList); continue; }
                if (st.UnsavedChanges) { Skip(ProfileSkipReason.OwnerHasUnsavedWork); continue; }

                string restorePoint = ProfileRestorePoints.NameFor(type);
                if (!Contains(st.Names, restorePoint))
                {
                    Skip(ProfileSkipReason.NothingWasChanged);
                    continue;
                }

                plan.Actions.Add(new ProfileAction
                {
                    Kind = ProfileActionKind.LoadRestorePoint,
                    ProfileType = type,
                    ProfileName = restorePoint,
                    Because = "the operator accepted the offer to put this radio's own "
                              + Label(type) + " settings back",
                });
            }

            return plan;
        }

        /// <summary>
        /// The plan for an OFFERED put-back of a live transmit-audio snapshot
        /// an earlier session of this client left on disk — that session ended
        /// without restoring the radio. Same rule as the restore-point offer:
        /// the operator decides, because the owner may have already put things
        /// right by hand and a late restore would undo the repair.
        /// </summary>
        public static ProfilePlan PlanOfferedLiveTransmitAudioRestore(ProfileSituation s)
        {
            var plan = new ProfilePlan();
            if (s == null) return plan;

            void Skip(ProfileSkipReason r) => plan.Skips.Add(new ProfileSkip
            { ProfileType = ProfileTypes.tx, Reason = r });

            if (!s.StrandedLiveTransmitAudioSnapshot) { Skip(ProfileSkipReason.NothingWasChanged); return plan; }
            if (!s.Connected) { Skip(ProfileSkipReason.NotConnected); return plan; }
            if (s.ChangeNothingArmed) { Skip(ProfileSkipReason.ChangeNothingArmed); return plan; }
            if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); return plan; }

            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.RestoreLiveTransmitAudio,
                ProfileType = ProfileTypes.tx,
                Because = "the operator accepted the offer to put this radio's own transmit audio back "
                          + "from the snapshot an earlier session left",
            });
            return plan;
        }

        /// <summary>
        /// Restore points sitting on the radio right now, left by an earlier
        /// build. A read; it commits to nothing.
        /// </summary>
        public static List<ProfileTypes> StrandedRestorePoints(ProfileSituation s)
        {
            var found = new List<ProfileTypes>();
            if (s == null) return found;
            foreach (var type in GovernedTypes)
            {
                var st = s.Type(type);
                if (st == null || !st.Reported) continue;
                if (Contains(st.Names, ProfileRestorePoints.NameFor(type))) found.Add(type);
            }
            return found;
        }

        // ------------------------------------------------------------------
        // Migration: a radio we already know is not a stranger (#495)
        // ------------------------------------------------------------------

        /// <summary>
        /// The answer to give, WITHOUT asking, for a radio the operator has
        /// already declared theirs and has connected to before — or null when
        /// the question must still be asked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The change-nothing-until-answered default is exactly right for a
        /// radio we have never seen. On 2026-09-01 it was applied to every
        /// radio, including Noel's own 8600 — a radio with a serial-keyed
        /// config directory, months of connections, an ownership declaration
        /// of "mine", and profiles that had loaded on every connect since long
        /// before that day. His first words on connecting to it were that it
        /// said his profiles had been left alone. Treating a radio like that as
        /// an unknown guest asks a question the record already answers.
        /// </para>
        /// <para>
        /// <b>Only "mine" is pre-answered, and only to what that radio always
        /// did.</b> Ownership cannot be inferred and the enum says so; a radio
        /// marked someone else's is NOT pre-answered to anything, because the
        /// record there says "not mine" and nothing about what its owner wants
        /// loaded — and the whole reason #501 exists is that the right answer
        /// on a borrowed radio is usually the middle, which only the operator
        /// can choose. An unset radio with a long history is still unset:
        /// history is evidence of use, not of ownership.
        /// </para>
        /// </remarks>
        public static ProfileGuestIntent? PreAnswerForKnownRadio(
            RadioOwnership ownership, bool hasConnectedBefore, ProfileGuestIntent current)
        {
            if (current != ProfileGuestIntent.NotAnswered) return null;
            if (ownership != RadioOwnership.Mine) return null;
            if (!hasConnectedBefore) return null;
            return ProfileGuestIntent.LoadMineAndPutBack;
        }

        // ------------------------------------------------------------------
        // What a restore point does and does not cover (#225)
        // ------------------------------------------------------------------

        /// <summary>
        /// One class of radio-persistent setting, and whether putting a profile
        /// back restores it.
        /// </summary>
        public sealed class CoverageEntry
        {
            public string What = "";
            public bool CoveredByAProfile;

            /// <summary>Where it is stored instead, when a profile does not
            /// hold it.</summary>
            public string Note = "";
        }

        /// <summary>
        /// <b>The honest limitation, stated rather than hidden.</b> A profile
        /// restores what a profile stores, and the live transmit-audio snapshot
        /// restores exactly the settings it changed. Everything JJ Flexible can
        /// change on a radio that lives OUTSIDE profile scope is covered by
        /// neither, and no amount of restore machinery will make it so.
        ///
        /// <para>Kept as data rather than prose so the settings export, the
        /// restore offer and the help can render one list instead of drifting
        /// into three.</para>
        /// </summary>
        public static IReadOnlyList<CoverageEntry> RestorePointCoverage() => new[]
        {
            new CoverageEntry { What = "Which global, transmit and microphone profile is loaded", CoveredByAProfile = true },
            new CoverageEntry { What = "Microphone source, gain, processor and equaliser", CoveredByAProfile = true, Note = "carried by the microphone profile" },
            new CoverageEntry { What = "Transmit filter cuts, speech processor and monitor levels", CoveredByAProfile = true, Note = "carried by the transmit profile" },
            new CoverageEntry { What = "Slice layout, frequencies, modes and receive filters", CoveredByAProfile = true, Note = "carried by the global profile" },

            new CoverageEntry { What = "The radio's SmartLink port forwarding", CoveredByAProfile = false, Note = "radio-persistent, held in the radio's own settings; no profile carries it" },
            new CoverageEntry { What = "The radio's remote power (REM ON) setting", CoveredByAProfile = false, Note = "radio-persistent; no profile carries it" },
            new CoverageEntry { What = "The radio's name, callsign and front panel display", CoveredByAProfile = false, Note = "radio-persistent; no profile carries it" },
            new CoverageEntry { What = "The radio's network address and which connections it accepts", CoveredByAProfile = false, Note = "radio-persistent; no profile carries it" },
            new CoverageEntry { What = "The radio's frequency reference and oscillator calibration", CoveredByAProfile = false, Note = "radio-persistent; no profile carries it" },
            new CoverageEntry { What = "Tracking notch filters being enabled", CoveredByAProfile = false, Note = "station-global; no profile carries it" },
            new CoverageEntry { What = "Firmware", CoveredByAProfile = false, Note = "nothing here can put firmware back" },
        };

        // A SPOKEN form of the coverage list deliberately does not exist here.
        // Eleven caveats is not a sentence, it is a page, and a page read
        // aloud at the moment somebody presses a button is noise — which is
        // how a caveat gets ignored. The list belongs in the profile report,
        // where an operator arrows through it at their own pace, and that is
        // its one consumer.

        // ------------------------------------------------------------------

        /// <summary>
        /// What to PRE-SELECT if the opt-in question is asked — never what to
        /// store. A radio the operator has declared theirs is the one case
        /// worth suggesting "load mine" for; a radio declared someone else's
        /// suggests leaving it alone; everything else is left blank, because a
        /// pre-answered question about somebody else's radio is how an
        /// operator learns to press Enter without reading.
        /// </summary>
        public static ProfileGuestIntent Suggest(ProfileSituation s)
        {
            if (s == null) return ProfileGuestIntent.NotAnswered;
            if (s.Ownership == RadioOwnership.Mine) return ProfileGuestIntent.LoadMineAndPutBack;
            if (s.Ownership == RadioOwnership.SomeoneElses) return ProfileGuestIntent.LeaveAlone;
            return ProfileGuestIntent.NotAnswered;
        }

        /// <summary>The word for a profile type in a sentence.</summary>
        public static string Label(ProfileTypes type)
        {
            switch (type)
            {
                case ProfileTypes.global: return "global";
                case ProfileTypes.tx: return "transmit";
                case ProfileTypes.mic: return "microphone";
                case ProfileTypes.display: return "display";
                default: return "";
            }
        }

        private static ProfileAction AutosaveOff(string because) => new ProfileAction
        {
            Kind = ProfileActionKind.TurnAutosaveOff,
            ProfileType = ProfileTypes.none,
            Because = because + " — with the radio's own autosave on, a live change could be written "
                      + "into its owner's profile by the radio itself",
        };

        private static void AddSkipForAll(
            ProfilePlan plan, ProfileSituation s, ProfileSkipReason reason)
        {
            foreach (var type in GovernedTypes)
            {
                var st = s.Type(type);
                plan.Skips.Add(new ProfileSkip
                {
                    ProfileType = type,
                    Reason = reason,
                    ProfileName = st?.Wanted ?? "",
                });
            }
        }

        private static bool Contains(IReadOnlyList<string> names, string name)
        {
            if (names == null || string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
