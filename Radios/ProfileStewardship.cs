using System;
using System.Collections.Generic;
using System.Linq;

namespace Radios
{
    /// <summary>
    /// The operator's per-radio answer to "may JJ Flexible load MY profiles on
    /// THIS radio, and put yours back afterwards?" (#450, #451, ruled
    /// 2026-09-01).
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

        /// <summary>"Load my profiles here, and put this radio's own back when
        /// I leave." Consent to the whole two-tier arrangement below,
        /// including the restore point left on the radio.</summary>
        LoadMineAndPutBack = 2,
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
        /// list is not an empty one, and a marker cannot be captured over a
        /// state we cannot read.</summary>
        RadioDidNotReportItsList,

        /// <summary>The radio's current selection for this type could not be
        /// read, so there is no prior value to record or put back.</summary>
        SelectionUnreadable,

        /// <summary>What we want is already loaded. The best outcome: no write
        /// at all, no restore point, nothing to put back.</summary>
        AlreadyLoaded,

        /// <summary>The radio reports UNSAVED changes for this type — its
        /// owner has edits in flight. Capturing a restore point over somebody's
        /// half-finished work is its own harm, so nothing is captured and
        /// nothing is applied.</summary>
        OwnerHasUnsavedWork,

        /// <summary>Another operator is on this radio right now. Loading a
        /// profile would change the station under someone using it — the same
        /// hazard the provisional-slice design refused to take.</summary>
        AnotherOperatorIsConnected,

        /// <summary>The profile we want is not on this radio, and this radio is
        /// not marked as the operator's. Creating it would be inventing state
        /// on somebody else's station.</summary>
        ProfileNotOnThisRadioAndNotOurs,

        /// <summary>A restore point from an earlier session is already sitting
        /// on this radio, so what is loaded now is OUR profile from that
        /// session, not the radio owner's state. Capturing over it would
        /// destroy the only record of what was there.</summary>
        RestorePointAlreadyPresent,

        /// <summary>Nothing was changed for this type this session, so there is
        /// nothing to put back.</summary>
        NothingWasChanged,

        /// <summary>There is no radio to act on.</summary>
        NotConnected,
    }

    /// <summary>What one step of a plan does to the radio.</summary>
    public enum ProfileActionKind
    {
        /// <summary>Create a profile on the radio holding its CURRENT state,
        /// under the predictable restore-point name. Tier two of the design:
        /// the record that survives our process dying.</summary>
        CaptureRestorePoint,

        /// <summary>Select the profile the operator wants for this radio.</summary>
        LoadOurs,

        /// <summary>Select the name the radio was on when we arrived. Tier one,
        /// the fast path — perfect restoration including the name.</summary>
        LoadTheirNameBack,

        /// <summary>Select the restore point. The fallback when the original
        /// name is gone: the restore point holds the state, so the name is not
        /// needed.</summary>
        LoadRestorePoint,

        /// <summary>Delete a restore point that is no longer needed.</summary>
        RemoveRestorePoint,
    }

    /// <summary>One step of a plan. Plain data; tests construct and compare
    /// these freely, and nothing here touches a radio.</summary>
    public sealed class ProfileAction
    {
        public ProfileActionKind Kind;
        public ProfileTypes ProfileType;

        /// <summary>The profile name this step names on the radio.</summary>
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
        /// second vocabulary for the same question).</summary>
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

        public List<ProfileTypeState> Types = new List<ProfileTypeState>();

        public ProfileTypeState Type(ProfileTypes t) =>
            Types.FirstOrDefault(x => x.ProfileType == t);
    }

    /// <summary>
    /// What we recorded at connect so we can put the radio back. Tier one of
    /// the design lives here — in this process, which is exactly why it cannot
    /// be the only tier.
    /// </summary>
    public sealed class ProfileSessionRecord
    {
        public ProfileTypes ProfileType = ProfileTypes.none;

        /// <summary>The name the radio was on when we arrived.</summary>
        public string TheirSelection = "";

        /// <summary>True when we left a restore point on the radio for this
        /// type.</summary>
        public bool RestorePointLeft;

        /// <summary>What we loaded instead.</summary>
        public string WeLoaded = "";
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

        /// <summary>Restore points found on the radio that this session did not
        /// leave — an earlier session ended without putting things back. The
        /// caller OFFERS these; nothing here restores anything.</summary>
        public List<ProfileTypes> StrandedRestorePoints = new List<ProfileTypes>();

        public bool ChangesNothing => Actions.Count == 0;

        public bool Skipped(ProfileTypes t, ProfileSkipReason r) =>
            Skips.Any(s => s.ProfileType == t && s.Reason == r);
    }

    /// <summary>
    /// The predictable names of the restore points JJ Flexible leaves on a
    /// radio, and how any JJ Flexible client recognises one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The name is the protocol.</b> Tier two of the ruled design works
    /// because a client that had nothing to do with the session that crashed
    /// can look at the radio's profile list, see one of these, and know both
    /// that a session ended dirty and exactly which profile holds the owner's
    /// state. There is no client-side record involved, which is the point: the
    /// process that must clean up is the one that died.
    /// </para>
    /// <para>
    /// <b>Characters, and they are not cosmetic.</b> A caret separates entries
    /// in the radio's own profile-list status, and the transmit and microphone
    /// create commands strip an asterisk from the name they are given, so
    /// neither may appear here. Quotes are out because the command wraps the
    /// name in them. Spaces are fine — the profile-list parser splits on caret,
    /// and the status parser deliberately takes only one key/value pair per
    /// message precisely so names may contain spaces.
    /// </para>
    /// <para>
    /// <b>Do not version the name.</b> A newer client must recognise an older
    /// client's restore point, and a restore point outlives the session that
    /// made it by definition. If the shape ever has to change, the old shape
    /// still has to be recognised.
    /// </para>
    /// </remarks>
    public static class ProfileRestorePoints
    {
        /// <summary>
        /// The common prefix. Recognition is a prefix match, so an operator
        /// browsing their profiles in any client sees them grouped together and
        /// reading as what they are.
        /// </summary>
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

        /// <summary>
        /// True when the name is one we would ever write. Guards the one place
        /// this code creates a profile on a radio: a capture whose name came
        /// from anywhere but <see cref="NameFor"/> is a defect, and on a
        /// stranger's radio a defect that leaves litter behind.
        /// </summary>
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
    /// Whether JJ Flexible may touch a radio's profiles, what it must capture
    /// first, and how it puts things back — as pure functions, so a test can
    /// put a radio state in and read an action list out without a radio, a
    /// window or a thread.
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
    /// <b>The ruled design, 2026-09-01, in two tiers.</b> Tier one: record the
    /// selection NAMES client-side, apply ours, restore the names on
    /// disconnect. Tier two, and it is the one that survives a crash: BEFORE
    /// applying ours, save the radio's exact current state on the radio itself
    /// under a predictable name. Putting things back is then just selecting
    /// that restore point — the owner's original profile name is not needed,
    /// because the restore point holds their state. Any JJ Flexible client that
    /// connects later sees it in the list and knows a session ended dirty.
    /// </para>
    /// <para>
    /// <b>The underlying fact no design escapes:</b> a remote client cannot
    /// guarantee cleanup, because the process that must clean up is the one
    /// that died. Every restore here is best-effort, and building it as if it
    /// were reliable is how false confidence ships.
    /// </para>
    /// <para>
    /// <b>The restore is OFFERED, never automatic.</b> That is what prevents
    /// the worst failure: we crash, the owner reconnects, notices his audio is
    /// wrong and fixes it himself, we reconnect and "restore" over the change
    /// he just made. A late restore is not obviously safer than none. So
    /// <see cref="PlanConnect"/> reports stranded restore points and never acts
    /// on them.
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
            // piece of information the operator most needs: an earlier session
            // ended without putting this radio back.
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

            foreach (var type in GovernedTypes)
            {
                PlanOneType(plan, s, s.Type(type), type);
            }

            return plan;
        }

        private static void PlanOneType(
            ProfilePlan plan, ProfileSituation s, ProfileTypeState st, ProfileTypes type)
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
            // inventory we cannot tell whether our profile exists, whether a
            // restore point is already sitting there, or what we would be
            // capturing — so we do nothing at all.
            if (!st.Reported) { Skip(ProfileSkipReason.RadioDidNotReportItsList); return; }

            // No prior value means nothing to put back. Changing the selection
            // now would be a one-way door.
            if (st.Selection == null) { Skip(ProfileSkipReason.SelectionUnreadable); return; }

            if (string.Equals(st.Selection, st.Wanted, StringComparison.Ordinal))
            {
                // The best outcome there is: what the operator wants is what is
                // already loaded, so nothing is written, no restore point is
                // left, and there is nothing to put back.
                Skip(ProfileSkipReason.AlreadyLoaded);
                return;
            }

            // The radio is telling us its owner has edits in flight. Capturing
            // a restore point now would freeze half-finished work into a
            // profile, and applying ours would discard it. Both are harm, so
            // neither happens.
            if (st.UnsavedChanges) { Skip(ProfileSkipReason.OwnerHasUnsavedWork); return; }

            // Somebody else is on the radio. Loading a profile changes the
            // station under them.
            if (!s.OnlyStation) { Skip(ProfileSkipReason.AnotherOperatorIsConnected); return; }

            string restorePoint = ProfileRestorePoints.NameFor(type);

            // A restore point from an earlier session is already here, so what
            // is loaded RIGHT NOW is our profile from that session, not the
            // owner's state. Capturing over it would overwrite the only record
            // of what this radio was actually on. Leave everything alone and
            // let the caller offer the restore.
            if (Contains(st.Names, restorePoint))
            {
                Skip(ProfileSkipReason.RestorePointAlreadyPresent);
                return;
            }

            // Loading a profile the radio already has is one thing; CREATING
            // one it does not have is inventing state on somebody's station.
            // That is the existing ownership question, asked through the
            // existing concept rather than a second one.
            bool mayCreate = s.Ownership == RadioOwnership.Mine;
            if (!Contains(st.Names, st.Wanted) && !mayCreate)
            {
                Skip(ProfileSkipReason.ProfileNotOnThisRadioAndNotOurs);
                return;
            }

            // Tier two FIRST, always: the restore point is captured before the
            // state it records can be overwritten. An ordering defect here
            // would produce a restore point holding OUR settings, which is
            // worse than none because it looks like a rescue.
            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.CaptureRestorePoint,
                ProfileType = type,
                ProfileName = restorePoint,
                Because = "holds this radio's own " + Label(type) + " settings ("
                          + (string.IsNullOrEmpty(st.Selection) ? "no profile was loaded" : st.Selection)
                          + ") so they survive this session ending badly",
            });

            plan.Actions.Add(new ProfileAction
            {
                Kind = ProfileActionKind.LoadOurs,
                ProfileType = type,
                ProfileName = st.Wanted,
                MayCreate = mayCreate && !Contains(st.Names, st.Wanted),
                Because = "the operator chose this " + Label(type) + " profile for this radio",
            });

            // Tier one: the name they were on, recorded here in this process,
            // which is exactly why it cannot be the only record.
            plan.Record.Add(new ProfileSessionRecord
            {
                ProfileType = type,
                TheirSelection = st.Selection,
                RestorePointLeft = true,
                WeLoaded = st.Wanted,
            });
        }

        // ------------------------------------------------------------------
        // Putting it back
        // ------------------------------------------------------------------

        /// <summary>
        /// What to do on the way out, given what was recorded at connect.
        ///
        /// <para>Tier one when the name they were on is still on the radio:
        /// select it, then remove the restore point. Tier two when it is not:
        /// select the restore point, which holds their state, and LEAVE it —
        /// deleting a restore point whose contents are now live is deleting the
        /// only copy.</para>
        ///
        /// <para>Refuses in exactly the cases the connect plan refuses, and for
        /// the same reasons: another operator on the radio, or the radio
        /// reporting unsaved changes. Both leave the restore point in place,
        /// which is what it is for — the next JJ Flexible client sees it and
        /// can offer.</para>
        /// </summary>
        public static ProfilePlan PlanPutBack(
            ProfileSituation s, IEnumerable<ProfileSessionRecord> record)
        {
            var plan = new ProfilePlan();
            if (s == null) return plan;

            var records = (record ?? Enumerable.Empty<ProfileSessionRecord>()).ToList();

            foreach (var type in GovernedTypes)
            {
                var rec = records.FirstOrDefault(r => r.ProfileType == type);
                var st = s.Type(type);

                void Skip(ProfileSkipReason r) => plan.Skips.Add(new ProfileSkip
                {
                    ProfileType = type,
                    Reason = r,
                    ProfileName = rec?.TheirSelection ?? "",
                });

                if (rec == null) { Skip(ProfileSkipReason.NothingWasChanged); continue; }
                if (!s.Connected) { Skip(ProfileSkipReason.NotConnected); continue; }

                // The hold can be armed mid-session from Settings. It governs
                // what JJ Flexible writes, and a restore is a write.
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
                    // Tier two. The name is gone but the state is not: the
                    // restore point holds it, so selecting it is the whole
                    // restore. It is deliberately NOT deleted afterwards —
                    // its contents are now the live state, and deleting it
                    // would leave one copy where there had been two.
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

            return plan;
        }

        /// <summary>
        /// The plan for an OFFERED restore of stranded restore points — the
        /// ones an earlier session left behind. Identical in shape to
        /// <see cref="PlanPutBack"/>'s tier two, and separate from it because
        /// the caller reaching this has already asked a human.
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
        /// Restore points sitting on the radio right now. A read; it commits to
        /// nothing.
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
        /// restores what a profile stores. Everything JJ Flexible can change on
        /// a radio that lives OUTSIDE profile scope is not covered by a restore
        /// point, and no amount of restore-point machinery will make it so.
        ///
        /// <para>This is #225's territory meeting #450's: the provisional-change
        /// receipt tells an operator that a change will not survive disconnect,
        /// and the restore point puts profile state back. Between them there is
        /// a class of setting that is neither — station-global settings the
        /// radio keeps forever and no profile carries. Those are covered by
        /// nothing here, and the only protection they have is the per-radio
        /// change-nothing hold and the writers that consult it.</para>
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

        /// <summary>
        /// The sentence an operator needs before accepting a restore: what it
        /// will and will not put back. Drafted, and flagged for human review
        /// like all user-facing prose.
        /// </summary>
        public static string CoverageSentence()
        {
            int notCovered = RestorePointCoverage().Count(c => !c.CoveredByAProfile);
            return "Putting the profiles back restores what a profile holds: which "
                 + "profiles are loaded, the microphone and transmit settings they "
                 + "carry, and the slice layout. It does not restore anything the "
                 + "radio keeps outside a profile — its ports, its remote power "
                 + "setting, its name, or its network. There are " + notCovered
                 + " such settings, and none of them is changed unless you change it.";
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// What to PRE-SELECT if the opt-in question is asked — never what to
        /// store. A radio the operator has declared theirs is the one case
        /// worth suggesting "yes" for; everything else is left blank, because a
        /// pre-answered question about somebody else's radio is how an operator
        /// learns to press Enter without reading.
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
