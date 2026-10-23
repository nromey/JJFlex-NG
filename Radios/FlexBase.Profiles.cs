#define CWMonitor
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flex.Smoothlake.FlexLib;
using JJTrace;
using Radios.StationConnect;

namespace Radios
{
    /// <summary>
    /// FlexBase, radio profiles, with profile stewardship.
    /// </summary>
    /// <remarks>
    /// Moved unchanged from FlexBase.cs on 2026-10-19 by the Barefoot Balun split
    /// (branch refactor/flexbase-partials): whole regions, no member edited.
    /// </remarks>
    public partial class FlexBase
    {
        // region profile management
        #region profiles
        /// <summary>
        /// Get profiles by type.
        /// </summary>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetProfilesByType(ProfileTypes typ, List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (p.ProfileType == typ)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Get profile by name.
        /// </summary>
        /// <param name="name">name</param>
        /// <param name="typ">ProfileType</param>
        /// <param name="lst">(optional) list of profiles</param>
        public Profile_t GetProfileByName(string name, ProfileTypes typ,
            List<Profile_t> lst = null)
        {
            Profile_t rv = null;
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in GetProfilesByType(typ, lst))
            {
                if (p.Name == name)
                {
                    rv = p;
                    break;
                }
            }
            return rv;
        }

        /// <summary>
        /// Get default profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetDefaultProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            // Any default profile must be in Callouts.Profiles.
            if (lst == null) lst = Callouts.Profiles;

            // #423. A null profile list and an empty one are DIFFERENT STATES,
            // and this used to collapse them by walking straight into the
            // foreach. The answer to "which profiles are marked default" is the
            // same either way — none — so returning the empty list is right;
            // what was wrong was arriving there by way of a
            // NullReferenceException. It fires during Dispose with a radio
            // attached, and on the finalizer thread that is process death with
            // no diagnosis: it took down a whole test host when it was found.
            //
            // Traced rather than swallowed, because "the profile list was never
            // loaded" and "this station has no default profiles" want different
            // answers from whoever is reading the log, and only one of them is
            // ordinary.
            if (lst == null)
            {
                Tracing.TraceLine("GetDefaultProfiles: no profile list — Callouts.Profiles is "
                    + "null, which is not the same as empty. Reporting no default profiles "
                    + "(#423).", TraceLevel.Warning);
                return rv;
            }

            foreach (Profile_t p in lst)
            {
                if (p.Default)
                {
                    rv.Add(p);
                }
            }
            return rv;
        }

#if zero
        /// <summary>
        /// Get current profiles.
        /// </summary>
        /// <param name="lst">(optional) list of profiles</param>
        public List<Profile_t> GetCurrentProfiles(List<Profile_t> lst = null)
        {
            List<Profile_t> rv = new List<Profile_t>();
            if (lst == null) lst = Callouts.Profiles;
            foreach (Profile_t p in lst)
            {
                if (Profile_t.Current(this, p))
                {
                    rv.Add(p);
                }
            }
            return rv;
        }
#endif

        /// <summary>
        /// Get all profiles on the radio not in provided list.
        /// </summary>
        /// <param name="lst">list of profiles )may be null)</param>
        public List<Profile_t> GetRigProfiles(List<Profile_t> lst)
        {
            Tracing.TraceLine("GetRigProfiles", TraceLevel.Info);
            List<Profile_t> rv = new List<Profile_t>();
            // Add any profiles not in the list.
            foreach (string name in theRadio.ProfileDisplayList)
            {
                // if no list or else display profile not in it
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.display, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.display,
                        (theRadio.ProfileDisplaySelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileGlobalList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.global, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.global,
                        (theRadio.ProfileGlobalSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileMICList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.mic, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.mic,
                        (theRadio.ProfileMICSelection == name));
                    rv.Add(p);
                }
            }
            foreach (string name in theRadio.ProfileTXList)
            {
                if ((lst == null) || (GetProfileByName(name, ProfileTypes.tx, lst) == null))
                {
                    Profile_t p = new Profile_t(name, ProfileTypes.tx,
                        (theRadio.ProfileTXSelection == name));
                    rv.Add(p);
                }
            }
            return rv;
        }

        /// <summary>
        /// Select the profile. Returns true only when the command was sent;
        /// false covers BOTH a change-nothing refusal and a genuine failure,
        /// which a caller cannot tell apart — see
        /// <see cref="SelectProfileGuarded"/>, which can, and which every
        /// operator-facing caller should prefer (#486).
        /// </summary>
        /// <param name="prof">the profile</param>
        public bool SelectProfile(Profile_t prof) =>
            SelectProfileGuarded(prof) == GuardedOutcome.Done;

        /// <summary>
        /// Select the profile, telling a refusal apart from a failure (#486).
        /// On 2026-09-01 a change-nothing refusal and a real breakage were the
        /// same <c>false</c>, so the guard's good explanation ("Change nothing
        /// is on … the setting is in Settings") was overwritten by a generic
        /// "Could not select profile", and the operator was routed to a bug
        /// report instead of to Settings. This returns
        /// <see cref="GuardedOutcome.Refused"/> when the guard already spoke —
        /// so the caller stays silent — and <see cref="GuardedOutcome.Failed"/>
        /// only when something genuinely broke.
        /// </summary>
        public GuardedOutcome SelectProfileGuarded(Profile_t prof)
        {
            if (prof == null) return GuardedOutcome.Failed;
            Tracing.TraceLine("SelectProfile:" + prof.ToString(), TraceLevel.Info);
            // Tx and mic CREATE an absent profile below — their only "save"
            // from here — so whose radio it is comes first (#777, ruled
            // 2026-10-19); selecting an existing one is not a profile write.
            if (WouldCreateOnSelect(prof)
                && OwnershipRefusesProfileWrite(prof.Name, ProfileWriteActionKey(prof.ProfileType, delete: false)))
                return GuardedOutcome.Refused;
            // Loading rewrites shared station state (#403). This refusal is
            // for the operator surfaces and it speaks; the caller must not.
            if (GuardRefuses("settings.guard.action.profile_load")) return GuardedOutcome.Refused;
            bool rv = true;
            // Set by the transmit and microphone delegates when they run (#777 round four).
            bool dispatched = false;
            bool refusedAtDispatch = false;
            // select profiles, allowed before main loop.
            string str = "";
            switch (prof.ProfileType)
            {
                case ProfileTypes.display:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileDisplaySelection = prof.Name;
                    }), "ProfileDisplaySelection", true);
                    break;
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.ProfileGlobalSelection = prof.Name;
                    }), "ProfileGlobalSelection", true);
                    break;
                case ProfileTypes.tx:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            try
                            {
                                if (!theRadio.ProfileTXList.Contains(prof.Name))
                                {
                                    // The list is read again at dispatch, so the
                                    // create is decided HERE, and whose radio it
                                    // is gets asked here too (#777 round four).
                                    if (OwnershipRefusesProfileWrite(prof.Name, ProfileWriteActionKey(prof.ProfileType, delete: false)))
                                    { refusedAtDispatch = true; return; }
                                    theRadio.CreateTXProfile(prof.Name);
                                    str += "CreateTXProfile_";
                                }
                                theRadio.ProfileTXSelection = prof.Name;
                                str += "ProfileTXSelection";
                            }
                            finally { dispatched = true; }
                        }), str, true);
                    }
                    break;
                case ProfileTypes.mic:
                    {
                        q.Enqueue((FunctionDel)(() =>
                        {
                            try
                            {
                                if (!theRadio.ProfileMICList.Contains(prof.Name))
                                {
                                    // As for transmit, above (#777 round four).
                                    if (OwnershipRefusesProfileWrite(prof.Name, ProfileWriteActionKey(prof.ProfileType, delete: false)))
                                    { refusedAtDispatch = true; return; }
                                    theRadio.CreateMICProfile(prof.Name);
                                    str += "CreateMICProfile_";
                                }
                                theRadio.ProfileMICSelection = prof.Name;
                                str += "ProfileMICSelection";
                            }
                            finally { dispatched = true; }
                        }), str, true);
                    }
                    break;
                default:
                    Tracing.TraceLine("SelectProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    rv = false;
                    break;
            }
            if (!rv) return GuardedOutcome.Failed;

            // THE DISPATCHED DELEGATE'S ANSWER IS THE ANSWER (#777 round four,
            // Codex B1 of the third re-verify). The check above reads the
            // radio's list before the enqueue; the delegate reads it again
            // when the main loop runs it, and until round four a name that had
            // gone by then was CREATED with nobody asking whose radio it was.
            // A guest's "select an existing profile" became "create one on the
            // owner's radio", and this still returned Done, so the dialog
            // announced a selection. The delegate now asks the same ownership
            // decision at the point it would create, and when that refuses it
            // sends nothing and speaks the refusal. On a radio not declared
            // ours this waits, bounded, for the delegate's answer, so that a
            // refusal leaves the dialog silent instead of saying "selected".
            // Run inline (before the main loop) the answer is already in. An
            // owner never waits, and nothing an owner sees changes.
            if (!dispatched && SelectMayBeRefusedAtDispatch(prof)
                && !await(() => dispatched, SelectDispatchWaitMs))
            {
                // Still queued: reported as queued, Select's contract before
                // round four. If the name has gone when it does run, the
                // delegate still refuses, sends nothing and speaks.
                Tracing.TraceLine("SelectProfile: '" + prof.Name + "' had not run within " + SelectDispatchWaitMs
                    + " ms; reported as queued. Whose radio it is is asked again when it runs (#777)", TraceLevel.Warning);
                return GuardedOutcome.Done;
            }
            return refusedAtDispatch ? GuardedOutcome.Refused : GuardedOutcome.Done;
        }

        /// <summary>How long a guest's transmit or microphone Select waits for
        /// its queued delegate's answer (#777 round four): the bound the delete
        /// funnel gives its own queued readback.</summary>
        private const int SelectDispatchWaitMs = 1000;

        /// <summary>
        /// Whether a queued Select could still be refused when it runs: a
        /// transmit or microphone profile, whose delegate may choose to create,
        /// on a radio the single ownership decision does not answer Mine. Not a
        /// second ownership test: the same <c>StationLayoutSaveGuard.OwnershipRefusal</c>
        /// over <c>RadioConfig.OwnershipOf</c> the delegate asks, read here only
        /// to decide whether to wait for the delegate's answer.
        /// </summary>
        private bool SelectMayBeRefusedAtDispatch(Profile_t prof)
        {
            var radio = theRadio;
            if (radio == null) return false;
            if (prof.ProfileType != ProfileTypes.tx && prof.ProfileType != ProfileTypes.mic) return false;
            return StationLayoutSaveGuard.OwnershipRefusal(RadioConfig.OwnershipOf(radio.Serial))
                != StationLayoutSaveRefusal.None;
        }

        /// <summary>
        /// Whether selecting this profile would first CREATE it on the radio:
        /// a transmit or microphone name the radio's own list does not carry.
        /// Read before the enqueue, from the same lists the queued delegate
        /// consults; a list the radio has not reported yet reads as absent,
        /// which refuses rather than guesses.
        /// </summary>
        private bool WouldCreateOnSelect(Profile_t prof)
        {
            var radio = theRadio;
            if (radio == null) return false;
            switch (prof.ProfileType)
            {
                case ProfileTypes.tx: return radio.ProfileTXList?.Contains(prof.Name) != true;
                case ProfileTypes.mic: return radio.ProfileMICList?.Contains(prof.Name) != true;
                default: return false;
            }
        }

        // ── Mic-profile accessors (Track F, 2026-08-16) ──
        //
        // The radio owns its mic profiles — created, stored and autosaved on
        // the radio itself, shared with every other client. JJ Flexible's
        // microphone profiles REFERENCE these by name rather than copying
        // their contents (see Radios.MicrophoneProfile), so the app needs to
        // ask three small questions: what profiles exist, which is loaded,
        // and "load this one IF it exists". The existing SelectProfile
        // silently CREATES a missing mic profile before selecting it — right
        // for its original caller, wrong for applying a reference, where a
        // missing profile must be reported, never invented on someone's
        // radio.

        /// <summary>
        /// Mic-profile names the radio reports. Empty until the radio has
        /// answered the profile subscription.
        /// </summary>
        public List<string> MicProfileNames => theRadio?.ProfileMICList?.ToList() ?? new List<string>();

        /// <summary>The radio's currently loaded mic profile, or "".</summary>
        public string CurrentMicProfileName => theRadio?.ProfileMICSelection ?? "";

        /// <summary>
        /// Load a mic profile by name ONLY if the radio already has it.
        /// Returns false — and sends nothing — when it does not exist here,
        /// so the caller can say so instead of this class quietly creating a
        /// profile on somebody's radio.
        /// </summary>
        public bool SelectMicProfileIfPresent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var radio = theRadio;
            if (radio == null || !radio.ProfileMICList.Contains(name)) return false;
            // A load of an EXISTING profile is still a change to shared
            // station state, which is exactly what the hold holds. Every
            // caller already handles false by saying what did not happen.
            if (GuardRefuses("settings.guard.action.profile_load")) return false;
            q.Enqueue((FunctionDel)(() =>
            {
                theRadio.ProfileMICSelection = name;
            }), "SelectMicProfileIfPresent", true);
            return true;
        }

        // ── Radio-scoped profile enumeration (#418, 2026-08-30) ──
        //
        // The operator's list (Callouts.Profiles) is one list per human on
        // this computer — a set of REFERENCES, not an inventory. The radio's
        // inventory is ProfileGlobalList / ProfileTXList / ProfileMICList,
        // reported asynchronously after "profile <type> info". The settings
        // exports were counting the operator's references and reporting "0"
        // for a radio full of profiles its owner never named — immediately
        // before a factory reset, in a file whose promise is that a blind
        // spot says so. This reader asks the RADIO, waits (bounded) for the
        // answer, and reports which of three states the reading ended in:
        // the radio reported its list (possibly empty); the radio never
        // reported it; we could not ask at all. FlexLib's list getters
        // collapse "never reported" to an empty collection, so the
        // discriminator is the PropertyChanged event ParseProfilesStatus
        // raises — it fires even for an empty list, which is exactly the
        // positive "the radio answered: none" signal the exports need.

        /// <summary>
        /// One reading of the radio's own profile list for one type: the
        /// names, the radio's current selection, and — load-bearing — which
        /// of three states the reading ended in. Plain data; renderers and
        /// tests construct it freely.
        /// </summary>
        public sealed class RadioProfileList
        {
            public ProfileTypes ProfileType;

            /// <summary>Names the radio reports. Meaningful only when
            /// <see cref="Reported"/> is true.</summary>
            public List<string> Names = new List<string>();

            /// <summary>The radio's current selection for this type: a name,
            /// "" when the radio says none is loaded, null when it could not
            /// be read.</summary>
            public string Selection;

            /// <summary>The radio has answered with its list — possibly an
            /// empty one, which is a real answer ("none stored"), not a
            /// gap.</summary>
            public bool Reported;

            /// <summary>The answer arrived within the bounded wait after our
            /// fresh ask. False with <see cref="Reported"/> true means the
            /// names are from a list the radio sent earlier in the
            /// session.</summary>
            public bool FreshAnswer;

            /// <summary>How long the fresh ask was given, so a renderer can
            /// say "waited N seconds" honestly.</summary>
            public int WaitedMs;

            /// <summary>Non-null when we could not ask at all: no radio
            /// connection, or the reading itself failed.</summary>
            public string Problem;

            public bool CouldNotAsk => Problem != null;

            /// <summary>The contradiction a settings file must state rather
            /// than average away: the list shows no names, yet the radio
            /// names a loaded selection — so at least one profile exists
            /// that the list does not show.</summary>
            public bool SelectionContradictsCount =>
                Problem == null && Names.Count == 0 && !string.IsNullOrEmpty(Selection);
        }

        /// <summary>
        /// Read the RADIO's profile list for one type: subscribe for the
        /// answer, send a fresh "profile ... info", wait (bounded), then
        /// report the list and which state the reading ended in. Read-only —
        /// nothing is loaded and nothing on the radio changes.
        /// </summary>
        public RadioProfileList ReadRadioProfileList(ProfileTypes typ, int timeoutMs = 5000)
        {
            var result = new RadioProfileList { ProfileType = typ, WaitedMs = timeoutMs };

            string listProperty;
            string infoCommand;
            switch (typ)
            {
                case ProfileTypes.global:
                    listProperty = "ProfileGlobalList"; infoCommand = "profile global info"; break;
                case ProfileTypes.tx:
                    listProperty = "ProfileTXList"; infoCommand = "profile tx info"; break;
                case ProfileTypes.mic:
                    listProperty = "ProfileMICList"; infoCommand = "profile mic info"; break;
                case ProfileTypes.display:
                    listProperty = "ProfileDisplayList"; infoCommand = "profile display info"; break;
                default:
                    result.Problem = "unknown profile type " + typ;
                    return result;
            }

            var radio = theRadio;
            if (radio == null)
            {
                result.Problem = "no radio connection";
                Tracing.TraceLine("ReadRadioProfileList(" + typ + "): no radio connection",
                    TraceLevel.Warning);
                return result;
            }

            // Subscribe BEFORE asking, so the answer cannot slip past us.
            int answered = 0;
            PropertyChangedEventHandler onChange = (s, e) =>
            {
                if (e.PropertyName == listProperty) Interlocked.Exchange(ref answered, 1);
            };
            radio.PropertyChanged += onChange;
            // The GLOBAL ask is recorded in the station evidence log BEFORE it
            // goes out, so its answer — which may arrive after this bounded
            // wait has given up and returned the cached names, and so after a
            // global load has been sent on the strength of them — is known to
            // be inventory and not the radio's own post-load list re-report
            // (#639, Codex B1). A send that fails withdraws the record.
            int askGeneration = typ == ProfileTypes.global ? StationAttempt.Generation : 0;
            long askId = 0;
            try
            {
                try
                {
                    if (typ == ProfileTypes.global) askId = ProfileEvidence.GlobalInventoryAsked(askGeneration);
                    // Fire the ask and OBSERVE the reply task so a refusal
                    // cannot surface later as an unobserved exception. The
                    // list itself arrives as a status message, not in the
                    // command reply, so the wait below watches the property.
                    var reply = radio.SendCommandAsync(infoCommand);
                    _ = reply.ContinueWith(
                        t =>
                        {
                            Tracing.TraceLine(
                                "ReadRadioProfileList(" + typ + "): info command refused: "
                                + t.Exception?.GetBaseException().Message, TraceLevel.Warning);
                            if (typ == ProfileTypes.global) ProfileEvidence.GlobalInventoryAskWithdrawn(askGeneration, askId);
                        },
                        TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    // The ask itself failed. Fall through to whatever the
                    // session already holds; Reported says whether that is
                    // anything.
                    if (typ == ProfileTypes.global) ProfileEvidence.GlobalInventoryAskWithdrawn(askGeneration, askId);
                    Tracing.TraceLine("ReadRadioProfileList(" + typ + "): ask failed: "
                        + ex.Message, TraceLevel.Warning);
                }

                result.FreshAnswer = await(() => Volatile.Read(ref answered) != 0, timeoutMs);
            }
            finally
            {
                radio.PropertyChanged -= onChange;
            }

            try
            {
                List<string> names = null;
                string selection = null;
                switch (typ)
                {
                    case ProfileTypes.global:
                        names = radio.ProfileGlobalList?.ToList();
                        selection = radio.ProfileGlobalSelection;
                        break;
                    case ProfileTypes.tx:
                        names = radio.ProfileTXList?.ToList();
                        selection = radio.ProfileTXSelection;
                        break;
                    case ProfileTypes.mic:
                        names = radio.ProfileMICList?.ToList();
                        selection = radio.ProfileMICSelection;
                        break;
                    case ProfileTypes.display:
                        names = radio.ProfileDisplayList?.ToList();
                        selection = radio.ProfileDisplaySelection;
                        break;
                }
                result.Names = names ?? new List<string>();
                result.Selection = selection;
                // A non-empty list is positive evidence the radio reported it
                // at some point: the collection is only ever populated from
                // the radio's own status messages.
                result.Reported = result.FreshAnswer || result.Names.Count > 0;
            }
            catch (Exception ex)
            {
                result.Problem = ex.Message;
                Tracing.TraceLine("ReadRadioProfileList(" + typ + "): read failed: "
                    + ex.Message, TraceLevel.Warning);
            }

            Tracing.TraceLine("ReadRadioProfileList(" + typ + "): reported=" + result.Reported
                + " fresh=" + result.FreshAnswer + " count=" + result.Names.Count
                + " selection='" + (result.Selection ?? "<unreadable>") + "'"
                + (result.Problem != null ? " problem=" + result.Problem : ""),
                TraceLevel.Info);
            return result;
        }

        // ── The silent-transmit check (Sprint 31 Track S, task #99) ──
        //
        // A radio whose mic-profile SELECTION is empty has no transmit-audio
        // DSP chain, so audio sent from this computer modulates nothing:
        // SC_MIC pins at -120 and the operator keys up into silence. It was
        // pcap-diffed against SmartSDR on the same 8600 on 2026-08-10 — every
        // command matched except the mic profile, "Default" there and empty
        // here. SmartSDR never lands in this state because it keeps "Default"
        // selected; JJ Flex could, because loading the JJRadioDefault GLOBAL
        // profile carries no mic profile with it.
        //
        // **This surface ANNOUNCES. It never writes.** ProfileMICSelection is
        // shared radio state — every client connected to the radio sees the
        // change, and an empty selection on somebody else's radio may be their
        // deliberate arrangement. Whether JJ Flex may repair it is the
        // ownership question (RadioConfig.RadioOwnership); saying the failure
        // out loud needs no permission from anyone.
        //
        // Why it stayed invisible for so long: a correctly set-up radio cannot
        // detect it. Noel's own 8600 has a mic profile selected, so his working
        // rig is blind to the whole class of bug — which is precisely the
        // argument for announcing rather than waiting to reproduce it.

        /// <summary>
        /// True when this radio has answered its profile subscription and
        /// reports NO mic profile loaded — the silent-transmit failure.
        /// </summary>
        /// <remarks>
        /// A non-empty <c>ProfileMICList</c> is required, and it is doing real
        /// work: it is positive proof that the radio has answered, so a slow
        /// subscription can never be mistaken for the failure. A radio that
        /// reports no mic profiles at all is a genuinely different (and
        /// unverified) state — traced, never announced.
        /// </remarks>
        public bool MicProfileSelectionEmpty
        {
            get
            {
                var radio = theRadio;
                return radio != null
                    && IsConnected
                    && radio.ProfileMICList != null
                    && radio.ProfileMICList.Count > 0
                    && string.IsNullOrEmpty(radio.ProfileMICSelection);
            }
        }

        /// <summary>
        /// The mic profile this radio would most likely want if the operator
        /// asks for the empty selection to be filled: "Default" when the radio
        /// offers it (what SmartSDR keeps selected), otherwise the first one it
        /// lists. Empty string when there is nothing to pick. Naming the
        /// candidate is READ-ONLY; nothing here selects it.
        /// </summary>
        public string SuggestedMicProfileName
        {
            get
            {
                var radio = theRadio;
                var list = radio?.ProfileMICList;
                if (list == null || list.Count == 0) return "";
                return list.Contains("Default") ? "Default" : list[0];
            }
        }

        /// <summary>
        /// The connect-time spoken form, sized to the operator's verbosity.
        /// Spoken at Critical so it survives "speech off" — an operator who
        /// cannot be heard on the air is the enum's own definition of a
        /// safety message — which is exactly why the off-level form is the
        /// shortest one that still carries the consequence.
        /// </summary>
        public static string SilentTxSpokenWarning(VerbosityLevel verbosity) =>
            // Noel's wording, 2026-08-19: "audio from your computer will [not] be
            // transmitted using your radio" in place of "will not go out". It
            // names both halves of the path — the computer that produced the
            // audio and the radio that is supposed to send it — so an operator
            // hearing this for the first time learns where the break is, not
            // just that there is one. "Your", not "this": the house voice.
            // The three tiers live in the store as a ladder: audio.silent_tx.warning.
            Lexicon.Get("audio.silent_tx.warning", verbosity);

        /// <summary>
        /// The re-readable form, for a status line the operator can arrow
        /// through at their own pace. Null when there is nothing wrong — the
        /// caller shows the line only when this returns text, so a healthy
        /// radio never grows a warning it has to dismiss.
        /// </summary>
        public string? SilentTxMicProfileAdvisory()
        {
            if (!MicProfileSelectionEmpty) return null;
            string pick = SuggestedMicProfileName;
            return Lexicon.Get("audio.silent_tx.advisory")
                 + (string.IsNullOrEmpty(pick)
                        ? ""
                        : Lexicon.Get("audio.silent_tx.advisory_suggestion", ("pick", pick)));
        }

        /// <summary>
        /// Run the check and, when the failure is present, say so once. Called
        /// from <see cref="GetProfileInfo"/>, which is the only moment the app
        /// reliably knows the radio's profile answers have landed.
        /// </summary>
        /// <remarks>
        /// The waits are ordered so that a healthy radio pays almost nothing:
        /// the first returns as soon as the profile list arrives (already true
        /// by this point on every radio observed), and the second returns the
        /// instant a selection is seen. Only the genuine failure case waits out
        /// the full settle window, and on that radio the wait is worth it.
        /// </remarks>
        /// <param name="mayRepair">True only when the post-station phase has
        /// established that an automatic shared write is authorised right now:
        /// station established, declared Mine, opted in, hold off, and the
        /// roster verdict for automatic writes is only-us. The WARNING fires
        /// regardless; the REPAIR needs this (design step 10). Unknown is not
        /// "no microphone selected".</param>
        private void CheckMicProfileForSilentTx(bool mayRepair)
        {
            if (theRadio == null) return;

            if (!await(() => theRadio != null && theRadio.ProfileMICList != null
                             && theRadio.ProfileMICList.Count > 0, 1500))
            {
                // Either the subscription has not answered or this radio truly
                // lists no mic profiles. Both are unverified states and neither
                // is the pcap-confirmed failure, so this is for the trace file
                // and no further.
                Tracing.TraceLine(
                    "SilentTxCheck: no mic profiles reported within the settle window — "
                    + "not announcing (this is not the confirmed empty-selection failure).",
                    TraceLevel.Warning);
                return;
            }

            // OBSERVED, not cached (Track G2; review step 10). Until
            // 2026-09-22 this waited on theRadio.ProfileMICSelection being
            // non-empty and treated the window expiring on "" as the failure
            // — but FlexLib's cached selection reads "" before the radio has
            // said anything at all. Only a RADIO-REPORTED selection this
            // connection counts: a report of "" is the failure; no report is
            // "unreported", which is traced and never announced.
            await(() =>
            {
                var obs = ProfileEvidence.Snapshot().ReportedSelectionOf(ProfileTypes.mic);
                return theRadio == null || (obs != null && obs.Name.Length > 0);
            }, 1500);

            var radio = theRadio;
            if (radio == null) return;

            var reported = ProfileEvidence.Snapshot().ReportedSelectionOf(ProfileTypes.mic);
            var verdict = SilentMicrophoneAssessment.Decide(new SilentMicrophoneFacts
            {
                Connected = IsConnected,
                ProfilesReported = radio.ProfileMICList != null && radio.ProfileMICList.Count > 0,
                ReportedSelection = reported?.Name,
            });
            if (verdict == SilentMicrophoneVerdict.Healthy) return;
            if (verdict != SilentMicrophoneVerdict.SilentEmpty)
            {
                Tracing.TraceLine("SilentTxCheck: " + verdict + " — the radio has not reported a microphone selection this "
                    + "connection (cached value '" + (radio.ProfileMICSelection ?? "") + "' is not an observation); not announcing.",
                    TraceLevel.Warning);
                return;
            }

            // Ownership decides whether we repair it or only report it.
            //
            // Noel ruled 2026-08-19: "yes on silent fix to a known radio." So on
            // a radio the operator has marked as theirs, JJ Flex loads the
            // profile itself rather than making them press a button to fix a
            // failure they did not cause. On any other radio — including one
            // never answered for — it still only speaks, because the selection
            // is SHARED state and an empty one on somebody else's radio may be
            // their deliberate arrangement.
            //
            // SILENT MEANS WITHOUT ASKING, NOT WITHOUT SAYING. The repair is
            // still announced, because the alternative is JJ Flex changing a
            // setting on the operator's radio and never mentioning it — which
            // is the shape of thing this project exists not to do. What the
            // ownership answer buys is the absence of a QUESTION, not the
            // absence of information.
            var ownership = RadioConfig.OwnershipOf(radio.Serial);
            var candidate = SuggestedMicProfileName;

            // The change-nothing hold outranks ownership: your own radio can
            // be the one that must not change today, and that is exactly the
            // factory-reset-test case (#403). The WARNING below still fires —
            // saying the failure out loud needs no permission from anyone.
            if (ownership == RadioOwnership.Mine && ChangeNothingActive)
            {
                Tracing.TraceLine(
                    "SilentTxCheck: radio is marked MINE but change-nothing is armed — "
                    + "warning without repairing.", TraceLevel.Warning);
            }
            else if (ownership == RadioOwnership.Mine && !mayRepair)
            {
                Tracing.TraceLine(
                    "SilentTxCheck: radio is marked MINE but the post-station phase did not authorise an "
                    + "automatic shared write (station not established, not opted in, a step ended uncertain, "
                    + "or the roster verdict is not only-us) — warning without repairing (design step 10).",
                    TraceLevel.Warning);
            }

            if (ownership == RadioOwnership.Mine && !ChangeNothingActive && mayRepair
                && !string.IsNullOrEmpty(candidate))
            {
                // The repair is a checked dispatch: every condition is read
                // again inside the delegate immediately before the write, and
                // "repaired" is said only when the radio REPORTS the candidate
                // selected afterwards. Queueing is not applying.
                bool repaired = RepairSilentMicrophoneChecked(radio, candidate, mayRepair);

                Tracing.TraceLine(
                    "SilentTxCheck: mic profile selection is EMPTY (reported) on a radio marked MINE. "
                    + $"Loading '{candidate}' without asking (Noel's ruling 2026-08-19). "
                    + $"confirmed={repaired}. Radio offers: "
                    + string.Join(", ", radio.ProfileMICList),
                    TraceLevel.Warning);

                if (repaired)
                {
                    if (SuppressSpeech) return;
                    // Handed to the connect briefing rather than spoken here
                    // (#510). At connect this is one clause of the composed
                    // statement — the short form, because a repair already
                    // made needs no explanation to act on — and the full
                    // sentence stays readable in the briefing's reference.
                    // Off a connect it is spoken in full at once, as before.
                    ConnectBriefing.Current.Note(new ConnectFact(
                        ConnectFactKind.MicProfileOnRadio,
                        Lexicon.Get("audio.silent_tx.repaired", ("candidate", candidate)),
                        Lexicon.Get("audio.silent_tx.repaired_brief", ("candidate", candidate)),
                        VerbosityLevel.Critical,
                        Speech.SpeechSubject.MicProfileOnRadio));
                    return;
                }

                // The write was refused, or went out and was not reported
                // back. Fall through and warn — an operator told "I fixed it"
                // when nothing changed is worse off than one simply told it
                // is broken.
                Tracing.TraceLine(
                    "SilentTxCheck: the repair was not confirmed — falling back to the warning.",
                    TraceLevel.Error);
            }
            else
            {
                Tracing.TraceLine(
                    "SilentTxCheck: mic profile selection is EMPTY (reported). Transmit audio from this "
                    + "computer will not modulate. Radio offers: "
                    + string.Join(", ", radio.ProfileMICList)
                    + $". Ownership={ownership} — ANNOUNCING ONLY, nothing is written to the "
                    + "radio. Repair is operator-initiated on any radio not marked mine.",
                    TraceLevel.Warning);
            }

            if (SuppressSpeech) return;

            // Alarm first, then the sentence — the same order DiagnosticOffer
            // uses, and for the same reason: the tone tells the operator that
            // what follows is not routine, so the sentence gets listened to
            // rather than filed with the rest of the connect chatter. Not on
            // the repair branch above: that one reports a fixed condition, and
            // an alarm for something already handled teaches the operator to
            // ignore alarms. The briefing plays the alarm immediately before
            // the sentence, whether that is at the connect's settle or, off a
            // connect, right now — so the coupling survives composition.
            //
            // The warning is the one fact the connect briefing exists to
            // deliver (#510): an operator who cannot be heard on the air is
            // the reason to speak at all. In the composed statement it takes
            // the Critical tier — the shortest form that still carries the
            // consequence — and the operator's own verbosity tier in full
            // stays in the reference and in the off-connect path.
            ConnectBriefing.Current.Note(new ConnectFact(
                ConnectFactKind.MicProfileOnRadio,
                SilentTxSpokenWarning(ScreenReaderOutput.CurrentVerbosity),
                SilentTxSpokenWarning(VerbosityLevel.Critical),
                VerbosityLevel.Critical,
                Speech.SpeechSubject.MicProfileOnRadio,
                alarm: true));
        }

        /// <summary>
        /// The automatic repair as a checked dispatch: the pure
        /// <see cref="SilentMicrophoneAssessment.RepairRefusal"/> is read
        /// inside the delegate immediately before the write, and the result
        /// is true only when the radio REPORTS the candidate selected within
        /// the effect bound (Track G2; review step 10).
        /// </summary>
        private bool RepairSilentMicrophoneChecked(Radio radio, string candidate, bool phasePermits)
        {
            var attempt = StationAttempt;
            var op = attempt.CurrentOperation;
            bool sent = false;
            string refusal = null;
            long seq = ProfileEvidence.Sequence;
            Radios.StationConnect.CommandReply reply = null;
            DispatchStationWork("silent-microphone repair '" + candidate + "'", () =>
            {
                var serial = radio.Serial ?? "";
                refusal = SilentMicrophoneAssessment.RepairRefusal(new SilentMicrophoneRepairFacts
                {
                    OperationLive = attempt.IsLive && (op == null || op.IsLive),
                    Connected = theRadio == radio && IsConnected,
                    HoldArmed = ChangeNothingActive,
                    RadioIsOurs = !string.IsNullOrEmpty(serial) && RadioConfig.OwnershipOf(serial) == RadioOwnership.Mine,
                    Intent = string.IsNullOrEmpty(serial) ? ProfileGuestIntent.NotAnswered : RadioConfig.ProfileIntentOf(serial),
                    StrictRoster = RosterJudgementForAutomaticWrite().Verdict,
                    PhasePermitsRepair = phasePermits,
                    ReportedSelectionNow = ProfileEvidence.Snapshot().ReportedSelectionOf(ProfileTypes.mic)?.Name,
                    Candidate = candidate,
                    CandidateListed = radio.ProfileMICList != null && radio.ProfileMICList.Contains(candidate),
                });
                if (refusal != null) return;
                seq = ProfileEvidence.Sequence;
                // The reply-bearing path, not the setter: the setter's cache
                // pre-assignment makes the vendor skip the confirming status
                // (Track G2 re-review, step 10). The reply is the radio's
                // acknowledgment; the "profile mic current=" status that
                // follows a real change is a genuine report.
                refusal = SendRadioCommandWithReply(radio, ProfileLoadCommand(ProfileTypes.mic, candidate),
                    r => Volatile.Write(ref reply, r));
                if (refusal != null) return;
                sent = true;
            });
            if (!sent && refusal == null)
            {
                await(() => sent || refusal != null, StationDeadlines.Default().TxMicEffectMs);
            }
            if (refusal != null)
            {
                Tracing.TraceLine("SilentTxCheck: the repair was NOT sent — " + refusal, TraceLevel.Warning);
                return false;
            }
            if (!sent) return false;

            bool confirmed = await(() =>
            {
                var r = Volatile.Read(ref reply);
                if (r != null && !r.Acknowledged) return true; // stop waiting; fails below
                var obs = ProfileEvidence.Snapshot().ReportedSelectionOf(ProfileTypes.mic);
                return r != null && r.Acknowledged
                    && obs != null && obs.Sequence > seq && string.Equals(obs.Name, candidate, StringComparison.Ordinal);
            }, StationDeadlines.Default().TxMicEffectMs);
            var finalReply = Volatile.Read(ref reply);
            if (finalReply != null && !finalReply.Acknowledged)
            {
                Tracing.TraceLine("SilentTxCheck: the radio REJECTED the repair — " + finalReply, TraceLevel.Warning);
                return false;
            }
            var reportedNow = ProfileEvidence.Snapshot().ReportedSelectionOf(ProfileTypes.mic);
            confirmed = finalReply != null && finalReply.Acknowledged && reportedNow != null
                && reportedNow.Sequence > seq && string.Equals(reportedNow.Name, candidate, StringComparison.Ordinal);
            if (!confirmed)
            {
                Tracing.TraceLine("SilentTxCheck: the repair went out but " + (finalReply == null
                        ? "the radio did not acknowledge it"
                        : "the radio did not report '" + candidate + "' selected")
                    + " within " + StationDeadlines.Default().TxMicEffectMs + " ms — not claimed repaired.",
                    TraceLevel.Warning);
            }
            return confirmed;
        }

        // ── The radio's own save-on-change concept (Sprint 32 Track H, #117) ──
        //
        // READ ONLY, ON PURPOSE. This is deliberately a getter with no setter.
        //
        // The radio has an autosave concept of its own and REPORTS it: FlexLib
        // parses "radio auto_save=1|0" into Radio.ProfileAutoSave. That answers
        // the first question — whether the radio has the feature at all — from
        // the wire rather than from a guess, and it answers it without writing
        // anything to a radio that may have other clients on it.
        //
        // What it does NOT answer is what the radio actually DOES when the flag
        // is on: which profile is written, at what moment, and whether a second
        // MultiFlex client's state is folded in. Those are radio-side semantics.
        // FlexLib's setter is one line that sends a command, so no amount of
        // reading our source or theirs can tell us; it needs a bench session.
        // Until it has had one, nothing here turns it on.
        //
        // Note also that FlexLib carries TWO ways to send this and they do not
        // agree. Radio.ProfileAutoSave sends `profile autosave on|off`
        // unquoted, and is the half wired to the status parser. The older
        // Radio.AutoSaveProfile(string) sends `profile autosave "<state>"`,
        // quoted, with no status handling and no caller anywhere. Whether the
        // radio accepts the quoted form is itself unverified — so if autosave is
        // ever driven from here, drive it through the property.

        /// <summary>
        /// What the radio reports about its own profile autosave setting, or
        /// null when there is no radio to ask. Never written by this
        /// application.
        /// </summary>
        public bool? RadioProfileAutoSave => theRadio?.ProfileAutoSave;

        /// <summary>
        /// Save a global profile — the bool face of <see cref="SaveProfileGuarded"/>
        /// for the one caller that cannot tell a refusal from a failure (the
        /// WinForms form). With <paramref name="immediately"/> true, true means
        /// the command was handed to a connected transport. With it false, the
        /// default, true means only that the save was QUEUED for the main
        /// loop: whether it later went out is not reported here.
        /// </summary>
        /// <param name="p">the profile</param>
        /// <param name="immediately">(optional)true to wait for the command to go out, default is false</param>
        public bool SaveProfile(Profile_t p, bool immediately = false) =>
            SaveProfileGuarded(p, immediately) == GuardedOutcome.Done;

        /// <summary>
        /// Save a global profile, telling a refusal apart from a failure
        /// (#486) and a failure apart from success (#777 rounds two and
        /// three). With <paramref name="immediately"/> true, Done means the
        /// command was HANDED TO A CONNECTED TRANSPORT — FlexLib's public
        /// reply-bearing send returned a sequence number rather than the 0 it
        /// returns for a transport that has dropped. It does not mean the
        /// bytes reached the radio (the TCP writer swallows write errors and
        /// disconnects asynchronously) and it does not mean the radio stored
        /// anything (no readback exists for a global save). Failed means the
        /// command was not handed over within the bound, or the transport
        /// refused it. With <paramref name="immediately"/> false, Done means
        /// queued for the main loop, which was always that caller's contract.
        /// </summary>
        public GuardedOutcome SaveProfileGuarded(Profile_t p, bool immediately = false)
        {
            if (p == null) return GuardedOutcome.Failed;
            Tracing.TraceLine("SaveProfile:" + p.ToString(), TraceLevel.Info);
            // The funnel for every operator-pressed global save (Profiles
            // dialog, SaveCurrentStationLayout, Profile.vb). Ownership first
            // (#777), then the hold — StationLayoutSaveGuard.Decide's order.
            if (p.ProfileType == ProfileTypes.global
                && OwnershipRefusesProfileWrite(p.Name, ProfileWriteActionKey(ProfileTypes.global, delete: false)))
                return GuardedOutcome.Refused;
            if (GuardRefuses("settings.guard.action.profile_save")) return GuardedOutcome.Refused;
            if (p.ProfileType != ProfileTypes.global)
            {
                // Only global is saved this way; tx and mic are the radio's
                // autosave's business. Was a bare false, read as a failure.
                Tracing.TraceLine("SaveProfile: not global, nothing sent: " + p.ToString(), TraceLevel.Warning);
                return GuardedOutcome.Skipped;
            }
            bool commandDone = false;
            bool commandRefused = false;
            {
                q.Enqueue((FunctionDel)(() =>
                    {
                        long seq = ProfileEvidence.Sequence;
                        string notSent;
                        using (OwnProfileWrite()) notSent = SendGlobalProfileSave(p.Name);
                        if (notSent != null) { commandRefused = true; return; }
                        commandDone = true;
                        // An operator save under the pending name satisfies
                        // the deferred create — once it has actually gone out.
                        // Until Track G2 the intent was cleared at ENQUEUE, so a
                        // save that never ran silently cancelled the automatic
                        // create (review section 8). Readback says whether the
                        // save is confirmed; without it the name is recorded
                        // as an uncertain save, which the deferred create's
                        // fresh inventory ask then protects from overwrite.
                        var pending = _pendingGlobalCreation;
                        if (pending != null && p.Name == pending.Name)
                        {
                            bool confirmed = await(() =>
                            {
                                var inv = ProfileEvidence.Snapshot().GlobalList;
                                return inv != null && inv.Provenance == ObservationProvenance.RadioReported
                                       && inv.Sequence > seq && inv.Contains(p.Name);
                            }, StationDeadlines.Default().DisconnectCreateConfirmMs);
                            if (!confirmed)
                            {
                                _uncertainGlobalCreation = pending;
                                Tracing.TraceLine("SaveProfile: the operator's save of '" + p.Name + "' went out but the radio did not "
                                    + "report it within the bound; recorded as uncertain, and the deferred create for that name is withdrawn.",
                                    TraceLevel.Warning);
                            }
                            _pendingGlobalCreation = null;
                        }
                    }), "save global", true);
            }
            if (!immediately) return GuardedOutcome.Done;

            // THE WAIT'S RESULT IS THE ANSWER (#777 round two, Codex B3).
            // Until then this waited up to 3000 ms for the queued command to
            // go out and returned true either way, so a queue that never
            // drained produced a spoken "saved" and a cleared dirty flag for a
            // save that was still sitting in the queue. An unsent save is a
            // failure, and the caller says so — whether the queue never ran
            // it or the transport refused it when it did (round three).
            await(() => commandDone || commandRefused, 3000);
            if (commandDone) return GuardedOutcome.Done;
            Tracing.TraceLine("SaveProfile: the save of '" + p.Name + "' "
                + (commandRefused ? "was refused by the transport" : "did not go out within 3000 ms")
                + "; reported as a failure, not a success (#777)", TraceLevel.Error);
            return GuardedOutcome.Failed;
        }

        /// <summary>The <c>profile global save</c> command exactly as FlexLib's
        /// own <c>Radio.SaveGlobalProfile</c> would send it.</summary>
        private static string ProfileSaveCommand(string name) => "profile global save \"" + name + "\"";

        /// <summary>
        /// Hand the global save to the radio's command transport. Null when
        /// it was handed to a CONNECTED transport; otherwise why it was not.
        /// </summary>
        /// <remarks>
        /// NOT the vendor's void <c>Radio.SaveGlobalProfile</c> (#777 round
        /// three, Codex B3 residual): that discards <c>SendCommand</c>'s
        /// 0-for-disconnected, so a transport that dropped after the save was
        /// queued and before it was dispatched still read as "sent", the
        /// dialog said "saved" and the layout save cleared its dirty flag.
        /// The public reply-bearing send returns the sequence number, or 0
        /// when the transport is down, and <see cref="SendRadioCommandWithReply"/>
        /// turns that 0 into a refusal. The radio's reply, when it arrives,
        /// is traced and nothing waits for it — so a null here means "handed
        /// to a connected transport" and no more: not that the bytes reached
        /// the radio (the TCP writer swallows write errors asynchronously),
        /// and not that the radio stored anything (no readback exists for a
        /// global save). The spoken receipts built on it, "Profile X saved"
        /// and "Station setup saved to X", claim storage; see the #777 report.
        /// </remarks>
        private string SendGlobalProfileSave(string name)
        {
            string notSent = SendRadioCommandWithReply(theRadio, ProfileSaveCommand(name), reply =>
                Tracing.TraceLine("SaveProfile: the radio replied " + reply,
                    reply.Acknowledged ? TraceLevel.Info : TraceLevel.Warning));
            if (notSent != null)
                Tracing.TraceLine("SaveProfile: '" + name + "' was NOT sent — " + notSent, TraceLevel.Error);
            return notSent;
        }

        // ── The one automatic write that already existed, and its gaps ──
        //
        // Sprint 33 Track K found this while auditing #117: the only path in
        // the application that writes a global profile without anybody
        // pressing anything. Create-on-first-use of a name the radio did not
        // have, at teardown.
        //
        // What it got wrong, fixed 2026-09-21 (#578, design step 11):
        //
        //   - It was armed from GetDefaultProfiles()[0], the OPERATOR default,
        //     and searched the same list here — so a per-radio-only wanted
        //     name was never created. The pending intent now carries the
        //     EFFECTIVE per-radio name from WantedProfilesForThisRadio.
        //   - It was never armed at all when the load had been "sent" for the
        //     absent name, because RunProfileAction returned true. The
        //     coordinator's MissingOwnedGlobal route arms it instead, and only
        //     on a confirmed fresh station.
        //   - It ran only from Dispose. It runs from the clean Disconnect too.
        //   - It trusted the cached OnlyStation and remembered permission from
        //     connect. Every step-11 condition is revalidated at the save.
        //   - SaveProfile(immediately) confirmed only that the command was
        //     ISSUED. The save is confirmed by radio-reported inventory
        //     readback within a bound, else reported unconfirmed.
        //
        // THE GAP IN THE CONTENTS remains and is why the roster condition is
        // load-bearing: the profile is created from whatever the station looks
        // like at teardown, and under MultiFlex that includes another
        // operator's slices. Only-us, from the live roster, or nothing.
        private bool saveNewGlobalProfile()
        {
            Tracing.TraceLine("saveNewGlobalProfile", TraceLevel.Info);

            // The hold first. Silent because this runs at teardown, where
            // speech may already be gone; the coordinator also never arms the
            // create under the hold, so this is the second lock on a door that
            // should already be shut.
            if (GuardSkips("saveNewGlobalProfile at disconnect"))
            {
                _pendingGlobalCreation = null;
                return false;
            }
            if (_pendingGlobalCreation == null) return false;
            return CreatePendingGlobalAtDisconnect();
        }

        #region Profile stewardship — not our radio (#450, #451, #403)

        // ══════════════════════════════════════════════════════════════════
        // The guest half of the profile path. Every decision lives in
        // Radios/ProfileStewardship.cs as a pure function; everything here is
        // the part that genuinely needs a radio — reading its lists, sending
        // the commands, waiting for the answers, and saying what happened.
        //
        // WHY THE SPLIT IS THE DELIVERABLE. The radio this protects is three
        // states away and belongs to somebody else. There is no way to test a
        // decision made inside this class against the case that matters, and
        // the symptom of a wrong answer is that a man's microphone stops
        // working at an unrelated hour weeks later. So the judgement sits
        // where a test can put a radio state in and read an action list out,
        // exactly as TransmitSafety does, and this file keeps only the wire.
        //
        // WHAT IT REPLACED: GetProfileInfo applied the OPERATOR's default
        // global, transmit and microphone profiles to whatever radio
        // connected, CREATING each on the radio when absent, and nothing put
        // anything back at disconnect. The names came from one list per human
        // — no radio in it and no account either, which is one scope COARSER
        // than the account-scoped port write that took a tester's radio off
        // the air for five hours (#397, #403).
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// What this session changed and what it left behind, so the way out
        /// can put it back. Tier one of the ruled design — in this process,
        /// which is precisely why it is not the only tier.
        /// </summary>
        private readonly List<ProfileSessionRecord> _profileSessionRecord =
            new List<ProfileSessionRecord>();

        /// <summary>Guards the record against the disconnect path and the
        /// connect path overlapping.</summary>
        private readonly object _profileRecordLock = new object();

        /// <summary>True once the radio has actually reported its profile
        /// autosave setting this session (see the PropertyChanged handler).
        /// Distinguishes a real "autosave is off" from a default we have not
        /// heard confirmed, which matters only on a radio that is not ours —
        /// where turning autosave off is a write we must not make blind.</summary>
        private volatile bool _radioReportedAutosave;

        /// <summary>True while this session has this radio's profile autosave
        /// turned OFF and has not yet put it back. The disconnect plan reads
        /// it to know whether to turn autosave on again.</summary>
        private bool _autosaveTurnedOffThisSession;

        /// <summary>The live transmit-audio settings this session captured off
        /// the radio before applying ours, held in memory for the clean
        /// disconnect. Also written to disk beside the radio's config so a
        /// session that ends badly can be OFFERED the put-back next time.</summary>
        private AudioChainPreset _liveTxSnapshot;

        /// <summary>
        /// Restore points found on the radio at connect that this session did
        /// not leave: an earlier session ended without putting this radio back.
        /// READ ONLY — the offer is made to a human, and nothing acts on this
        /// without one. Empty on every healthy connect.
        /// </summary>
        public IReadOnlyList<ProfileTypes> StrandedProfileRestorePoints { get; private set; }
            = Array.Empty<ProfileTypes>();

        /// <summary>
        /// True when this radio is carrying restore points from a session that
        /// ended without putting things back. The condition for OFFERING the
        /// restore, and the condition for the menu item existing at all — a
        /// verb that announces its own absence after the operator has navigated
        /// to it and pressed is the stub-verb pattern this project refuses.
        /// </summary>
        public bool HasStrandedProfileRestorePoint => StrandedProfileRestorePoints.Count > 0;

        /// <summary>
        /// The global profile this connect asked the radio to load, or null
        /// when none was asked for — which is the answer on every radio the
        /// operator has not opted in. The connect sequence waits on the load
        /// completing, and it needs the name to know what it is waiting for.
        /// </summary>
        internal string CurrentDesiredGlobalProfileName { get; private set; }

        /// <summary>
        /// The operator's per-radio profile answer for the connected radio.
        /// NotAnswered when there is no radio.
        /// </summary>
        public ProfileGuestIntent ProfileIntent
        {
            get
            {
                var serial = theRadio?.Serial;
                return string.IsNullOrEmpty(serial)
                    ? ProfileGuestIntent.NotAnswered
                    : RadioConfig.ProfileIntentOf(serial);
            }
        }

        /// <summary>
        /// Record the operator's answer for the connected radio. Takes effect
        /// at the next connect: the profiles that would have been applied at
        /// THIS one have already not been applied, and applying them now would
        /// be acting on an answer given after the fact.
        /// </summary>
        public bool RecordProfileIntent(ProfileGuestIntent intent)
        {
            var serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial)) return false;
            return RadioConfig.RecordProfileIntent(serial, intent);
        }

        /// <summary>True when the operator has declared the connected radio
        /// theirs. False for a radio that is someone else's or unanswered.</summary>
        public bool RadioIsMine
        {
            get
            {
                var serial = theRadio?.Serial;
                return !string.IsNullOrEmpty(serial)
                       && RadioConfig.OwnershipOf(serial) == RadioOwnership.Mine;
            }
        }

        /// <summary>
        /// The operator's chosen LOCAL transmit-audio profile for the connected
        /// radio (the one applied live under
        /// <see cref="ProfileGuestIntent.UseMyTransmitAudio"/>), or empty.
        /// </summary>
        public string LocalTransmitAudioProfileChoice
        {
            get
            {
                var serial = theRadio?.Serial;
                return string.IsNullOrEmpty(serial)
                    ? ""
                    : RadioConfig.LocalTransmitAudioChoiceOf(serial);
            }
        }

        /// <summary>
        /// The names in the operator's own transmit-audio profile store, for a
        /// picker. Empty when there is no operator context or none saved.
        /// </summary>
        public List<string> LocalTransmitAudioProfileNames()
        {
            if (Callouts == null) return new List<string>();
            try
            {
                var store = AudioChainPresets.Load(Callouts.ConfigDirectory, Callouts.OperatorName);
                return store?.Presets?.Select(p => p.Name).ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("LocalTransmitAudioProfileNames: " + ex.Message, TraceLevel.Warning);
                return new List<string>();
            }
        }

        /// <summary>
        /// Record which of the operator's transmit-audio profiles to apply
        /// live on the connected radio, and with it the intent to use it.
        /// Takes effect at the next connect.
        /// </summary>
        public bool SetLocalTransmitAudioChoice(string presetName)
        {
            var serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial)) return false;
            return RadioConfig.RecordLocalTransmitAudioChoice(serial, presetName);
        }

        /// <summary>
        /// Read the radio's own profile state into the plain shape the
        /// decisions take. One bounded read per type; nothing is written.
        /// </summary>
        /// <param name="wanted">What the operator wants loaded, by type.
        /// Null for a read that is only asking what is there.</param>
        /// <param name="freshAsk">
        /// True to send a fresh <c>profile ... info</c> and wait for the
        /// answer. Right on the CONNECT path, where the subscription may not
        /// have landed and a few seconds are affordable.
        /// <para><b>False on the DISCONNECT path, and that is not an
        /// optimisation.</b> A fresh ask waits out its timeout whenever the
        /// list has not CHANGED, which is the normal case at teardown — three
        /// types would add up to fifteen seconds of a disconnect the operator
        /// asked for, and a restore that makes leaving feel broken is a
        /// restore that gets switched off. The session already holds the
        /// radio's lists; a non-empty one is positive evidence the radio
        /// reported it, because they are only ever filled from its own status
        /// messages.</para>
        /// </param>
        internal ProfileSituation ReadProfileSituation(
            Dictionary<ProfileTypes, string> wanted, bool freshAsk) =>
            ReadProfileSituation(wanted, freshAsk, freshTypes: null, timeoutMs: 2500);

        /// <summary>
        /// The phase-limited read the station-first connect uses (design
        /// section 4): only <paramref name="freshTypes"/> get a fresh
        /// <c>profile ... info</c> ask; the other types are filled from the
        /// lists the session already holds, so a global decision does not
        /// freeze later transmit and microphone selections, and the
        /// post-station phase does not make a second global decision.
        /// </summary>
        internal ProfileSituation ReadProfileSituation(
            Dictionary<ProfileTypes, string> wanted, bool freshAsk,
            IReadOnlyCollection<ProfileTypes> freshTypes, int timeoutMs)
        {
            var situation = ReadBaseProfileSituation();
            foreach (var type in ProfileStewardship.GovernedTypes)
            {
                bool askThisType = freshAsk && (freshTypes == null || freshTypes.Contains(type));
                situation.Types.Add(ReadProfileTypeState(type, wanted, askThisType, timeoutMs));
            }
            return situation;
        }

        /// <summary>
        /// The connection-level facts of a situation with NO type states:
        /// connected, hold, the roster verdicts, ownership, intent, reported
        /// autosave, the local transmit-audio choice, a stranded snapshot,
        /// and station presence from the own-station evidence. The
        /// post-station orchestrator reads this once and then each type at
        /// its own turn (Track G2).
        /// </summary>
        internal ProfileSituation ReadBaseProfileSituation()
        {
            var radio = theRadio;
            var situation = new ProfileSituation
            {
                Connected = radio != null && IsConnected,
                ChangeNothingArmed = ChangeNothingActive,
                OnlyStation = OnlyStation,
                OnlyStationUnknown = OtherOperatorPresence == RosterVerdict.Unknown,
                Ownership = radio == null
                    ? RadioOwnership.Unset
                    : RadioConfig.OwnershipOf(radio.Serial),
                Intent = radio == null
                    ? ProfileGuestIntent.NotAnswered
                    : RadioConfig.ProfileIntentOf(radio.Serial),
                // Only a REPORTED autosave value is trusted; otherwise null,
                // which the live path treats as "not yet known" rather than
                // "off" (#499).
                RadioAutosave = (radio != null && _radioReportedAutosave)
                    ? (bool?)radio.ProfileAutoSave
                    : null,

                // #563. A station, not a name. The radio keeps the global
                // profile's NAME across a client teardown and drops the slices
                // with it, so on a reconnect the name matches over an empty
                // radio. Our own slice count is the honest test: this client
                // sees what it has, and at connect time it has nothing until
                // the profile load or the allocator puts something there.
                //
                // Fed from the own-station EVIDENCE (the tracker the slice
                // handlers write) rather than the mySlices list, so it is the
                // same fact the coordinator decides on. Still a minimum-
                // presence fact with an event-limited lifetime, never
                // completion — the richer state lives in StationResult.
                StationPresent = radio != null && StationTracker.Snapshot().StationPresent,
            };

            var serialForLocal = radio?.Serial;
            situation.LocalTransmitAudioProfile =
                string.IsNullOrEmpty(serialForLocal)
                    ? ""
                    : RadioConfig.LocalTransmitAudioChoiceOf(serialForLocal);
            situation.LocalTransmitAudioProfileExists =
                !string.IsNullOrWhiteSpace(situation.LocalTransmitAudioProfile)
                && FindLocalTransmitAudioProfile(situation.LocalTransmitAudioProfile) != null;
            situation.StrandedLiveTransmitAudioSnapshot =
                !string.IsNullOrEmpty(serialForLocal)
                && LiveTxSnapshotFileExists(serialForLocal);

            return situation;
        }

        /// <summary>
        /// ONE type's state: a fresh bounded <c>profile ... info</c> when
        /// <paramref name="freshAsk"/>, else the lists the session already
        /// holds. A fresh ask whose radio-reported selection has NOT arrived
        /// since the ask reports the selection as unreadable (null) rather
        /// than repeating the cached value as if it were observed: an
        /// inventory answer is not a selection report (review step 4).
        /// </summary>
        internal ProfileTypeState ReadProfileTypeState(
            ProfileTypes type, Dictionary<ProfileTypes, string> wanted, bool freshAsk, int timeoutMs)
        {
            IReadOnlyList<string> names;
            string selection;
            bool reported;

            if (freshAsk)
            {
                var reading = ReadRadioProfileList(type, Math.Max(0, timeoutMs));
                names = reading.Names;
                reported = reading.Reported && !reading.CouldNotAsk;
                // CouldNotAsk is the only case that is genuinely unreadable.
                // A radio that answers "" is telling us none is loaded, which
                // is a fact we can record and put back — but only if the
                // radio has actually REPORTED a selection for this type this
                // connection. Otherwise the vendor's cached default is not an
                // observation, and the selection is unreadable.
                var reportedSelection = type == ProfileTypes.display ? null : ProfileEvidence.Snapshot().ReportedSelectionOf(type);
                if (reading.CouldNotAsk) selection = null;
                else if (type != ProfileTypes.display && reportedSelection == null) selection = null;
                else selection = reading.Selection ?? "";
            }
            else
            {
                names = ProfileNamesOnRadio(type);
                reported = names.Count > 0;
                selection = SelectionOnRadio(type);
            }

            return new ProfileTypeState
            {
                ProfileType = type,
                Reported = reported,
                Names = names,
                Selection = selection,
                UnsavedChanges = UnsavedProfileChangesFor(type),
                Wanted = wanted != null && wanted.TryGetValue(type, out var w) ? (w ?? "") : "",
            };
        }

        /// <summary>
        /// What the radio reports about unsaved work in progress for one
        /// profile type. The radio itself answers this — <c>unsaved_changes_tx</c>
        /// and <c>unsaved_changes_mic</c> in its profile status — and it is the
        /// one question that must be asked BEFORE anything is captured:
        /// freezing somebody's half-finished microphone edit into a restore
        /// point is its own harm, distinct from and additional to overwriting
        /// their selection.
        ///
        /// <para>Global profiles have no such report, so global reads false.
        /// That is an honest gap, not a claim of safety.</para>
        /// </summary>
        public bool UnsavedProfileChangesFor(ProfileTypes type)
        {
            var radio = theRadio;
            if (radio == null) return false;
            switch (type)
            {
                case ProfileTypes.tx: return radio.UnsavedProfileChangesTX;
                case ProfileTypes.mic: return radio.UnsavedProfileChangesMIC;
                default: return false;
            }
        }

        /// <summary>
        /// What the operator wants loaded on THIS radio, by type: the per-radio
        /// choice when there is one, otherwise their default list (#451).
        ///
        /// <para>The fall-through is deliberate and conservative. One default
        /// for all radios cannot express what an operator with two stations
        /// wants, but removing the fall-through would strand every radio
        /// configured before per-radio choices existed.</para>
        /// </summary>
        internal Dictionary<ProfileTypes, string> WantedProfilesForThisRadio()
        {
            var wanted = new Dictionary<ProfileTypes, string>();
            var serial = theRadio?.Serial;
            var cfg = string.IsNullOrEmpty(serial) ? null : RadioConfig.LoadForRadio(serial);

            foreach (var type in ProfileStewardship.GovernedTypes)
            {
                string choice = cfg?.ProfileChoiceFor(type) ?? "";
                if (string.IsNullOrWhiteSpace(choice))
                {
                    var defaults = GetProfilesByType(type, GetDefaultProfiles());
                    choice = defaults.Count > 0 ? defaults[0].Name : "";
                }
                wanted[type] = choice ?? "";
            }
            return wanted;
        }

        // ApplyProfileStewardshipOnConnect used to live here: one method that
        // reset the session records, migrated, read every profile type,
        // planned everything and executed everything, and returned "global
        // loaded" the moment the command was queued. It is gone (2026-09-21).
        // Initialisation is InitializeStewardshipSession, once per attempt;
        // the global decision is the StationCoordinator's, before station
        // establishment; transmit and microphone are read FRESH and decided
        // in RunPostStationPhase, after it. See FlexBase.StationConnect.cs.

        /// <summary>
        /// #495 migration: pre-answer the profile question for a radio the
        /// operator has declared theirs and connected to before, so a radio we
        /// already know is not greeted as a guest on the first run of a new
        /// build. Returns true when it pre-answered this connect.
        /// </summary>
        private bool MigrateProfileIntentForKnownRadio()
        {
            var serial = theRadio?.Serial;
            if (string.IsNullOrEmpty(serial)) return false;

            var ownership = RadioConfig.OwnershipOf(serial);
            var current = RadioConfig.ProfileIntentOf(serial);
            bool connectedBefore = ConnectionHistory.Load(serial).Count > 0;

            var pre = ProfileStewardship.PreAnswerForKnownRadio(ownership, connectedBefore, current, ChangeNothingActive);
            if (pre == null) return false;

            // Recheck at the moment of persisting, not only at the decision:
            // the hold can be armed between the two (Track G review, step 2).
            if (ChangeNothingActive)
            {
                Tracing.TraceLine("ProfileStewardship: the change-nothing hold was armed before the pre-answer "
                    + "could be recorded; the profile question stays unanswered.", TraceLevel.Info);
                return false;
            }
            RadioConfig.RecordProfileIntent(serial, pre.Value);
            Tracing.TraceLine(
                "ProfileStewardship: this radio is marked yours and has a connection history, "
                + "so its profile question was pre-answered '" + pre.Value + "' rather than asked "
                + "(#495). Change it under Profiles on This Radio in the Radio menu.",
                TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// The spoken tail of the connect stewardship. One sentence at most,
        /// chosen by the strongest thing true: a hazard the operator must hear
        /// before handing the radio back (stranded state) outranks a courtesy
        /// (we applied your audio) which outranks a question (whose radio is
        /// this).
        /// </summary>
        /// <param name="liveAudioApplied">True only when the live
        /// transmit-audio apply actually RAN in this phase. Planning an apply
        /// is not evidence it happened; the deferred apply speaks for itself
        /// when it runs.</param>
        private void AnnounceConnectStewardship(
            ProfileSituation situation, ProfilePlan plan, bool preAnswered, bool liveAudioApplied)
        {
            if (SuppressSpeech) return;
            var serial = theRadio?.Serial;

            // A radio WE left with autosave off, or a live snapshot WE left
            // behind — a session of ours ended without putting things back.
            // The most important thing to say: the radio is not as its owner
            // left it, and here is the one press that fixes it.
            bool autosaveOwed = !string.IsNullOrEmpty(serial)
                                && RadioConfig.AutosaveTurnedOffByUsOn(serial);
            if (autosaveOwed)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: a previous session left this radio's autosave off. "
                    + "Offering the one-press restore.", TraceLevel.Warning);
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.autosave_left_off_by_us"), VerbosityLevel.Critical, volunteered: true);
                return;
            }

            if (situation.StrandedLiveTransmitAudioSnapshot)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: this radio has a transmit-audio snapshot from a session "
                    + "that ended without putting it back. Offering only.", TraceLevel.Warning);
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_stranded"), VerbosityLevel.Critical, volunteered: true);
                return;
            }

            if (plan.StrandedRestorePoints.Count > 0)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: this radio is carrying restore points left by an earlier "
                    + "build's session: "
                    + string.Join(", ", plan.StrandedRestorePoints.Select(ProfileStewardship.Label))
                    + ". OFFERING only.", TraceLevel.Warning);
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.stranded"), VerbosityLevel.Critical, volunteered: true);
                return;
            }

            // A live transmit-audio apply that could not run — the operator
            // asked for their audio and did not get it. Say why, once.
            var liveSkip = plan.Skips.FirstOrDefault(s =>
                s.ProfileType == ProfileTypes.tx && IsLiveAudioSkip(s.Reason));
            if (situation.Intent == ProfileGuestIntent.UseMyTransmitAudio && liveSkip != null)
            {
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_not_applied",
                        ("why", Lexicon.Get(LiveAudioSkipKey(liveSkip.Reason),
                            ("preset", liveSkip.ProfileName)))), VerbosityLevel.Terse, volunteered: true);
                return;
            }

            // A live transmit-audio apply that DID run — the executed fact,
            // not membership in plan.Actions. A deferred apply announces
            // itself from ApplyDeferredGuestTransmitAudio once it has run.
            if (situation.Intent == ProfileGuestIntent.UseMyTransmitAudio && liveAudioApplied)
            {
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_applied",
                        ("preset", situation.LocalTransmitAudioProfile)), VerbosityLevel.Terse, volunteered: true);
                return;
            }

            if (plan.AskWhoseRadioThisIs)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: this radio has no profile answer yet, so NOTHING was "
                    + "loaded on it. Suggestion if asked: " + plan.Suggestion, TraceLevel.Warning);
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.left_alone"), VerbosityLevel.Terse, volunteered: true);
                return;
            }

            if (preAnswered)
            {
                // A radio we already knew: its profiles loaded, as they always
                // have. Chatty — nothing changed for this operator, so it is a
                // reassurance, not news.
                NoteProfileVerdict(Lexicon.Get("settings.profile_guest.pre_answered"), VerbosityLevel.Chatty, volunteered: false);
            }
        }


        /// <summary>
        /// Hand one profile verdict to the connect briefing (#510). Every
        /// verdict but the pre-answered reassurance is volunteered in full:
        /// each is either a hazard on the radio or the answer to something the
        /// operator asked for, and none of them is where the 711 characters
        /// were. The pre-answered line — "your profiles load on this radio, as
        /// they always have" — restates the default outcome; it goes to the
        /// briefing's reference and is not spoken in the connect burst.
        /// </summary>
        private static void NoteProfileVerdict(
            string sentence, VerbosityLevel level, bool volunteered,
            [System.Runtime.CompilerServices.CallerFilePath] string file = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
            [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        {
            ConnectBriefing.Current.Note(new ConnectFact(
                ConnectFactKind.ProfileStewardship, sentence, volunteered ? sentence : null,
                level, Speech.SpeechSubject.ProfileGuestOutcome, alarm: false, file, line, member));
        }

        private static bool IsLiveAudioSkip(ProfileSkipReason r) =>
            r == ProfileSkipReason.NoLocalTransmitAudioChosen
            || r == ProfileSkipReason.LocalTransmitAudioProfileNotFound
            || r == ProfileSkipReason.AnotherOperatorIsConnected
            || r == ProfileSkipReason.RosterUnknown
            || r == ProfileSkipReason.OwnerHasUnsavedWork
            || r == ProfileSkipReason.RadioDidNotReportAutosave
            || r == ProfileSkipReason.AutosaveCouldNotBeTurnedOff;

        private static string LiveAudioSkipKey(ProfileSkipReason r)
        {
            switch (r)
            {
                case ProfileSkipReason.NoLocalTransmitAudioChosen: return "settings.profile_guest.why.no_local_profile";
                case ProfileSkipReason.LocalTransmitAudioProfileNotFound: return "settings.profile_guest.why.local_profile_missing";
                case ProfileSkipReason.AnotherOperatorIsConnected: return "settings.profile_guest.why.another_operator";
                case ProfileSkipReason.RosterUnknown: return "settings.profile_guest.why.roster_unknown";
                case ProfileSkipReason.OwnerHasUnsavedWork: return "settings.profile_guest.why.unsaved_work";
                case ProfileSkipReason.RadioDidNotReportAutosave: return "settings.profile_guest.why.autosave_unknown";
                case ProfileSkipReason.AutosaveCouldNotBeTurnedOff: return "settings.profile_guest.why.autosave_off_failed";
                default: return "settings.profile_guest.why.another_operator";
            }
        }

        /// <summary>
        /// Put this radio's own profiles and live transmit audio back, at
        /// disconnect. Best-effort by construction, and it must stay that way
        /// in the code as well as in the comments: the process that must clean
        /// up is the one that dies, so nothing downstream may assume this ran.
        /// </summary>
        private void PutProfilesBackOnDisconnect()
        {
            List<ProfileSessionRecord> record;
            bool autosaveOff;
            lock (_profileRecordLock)
            {
                autosaveOff = _autosaveTurnedOffThisSession;
                if (_profileSessionRecord.Count == 0 && !autosaveOff) return;
                record = _profileSessionRecord.ToList();
            }

            ProfileSituation situation;
            try
            {
                situation = ReadProfileSituation(null, freshAsk: false);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: could not read the radio on the way out ("
                    + ex.Message + "). Nothing put back; the autosave notice, if owed, stays "
                    + "for the next connect.", TraceLevel.Error);
                return;
            }

            var plan = ProfileStewardship.PlanPutBack(situation, record, autosaveOff);

            foreach (var skip in plan.Skips)
            {
                if (skip.Reason == ProfileSkipReason.NothingWasChanged) continue;
                Tracing.TraceLine(
                    "ProfileStewardship: did NOT put the "
                    + ProfileStewardship.Label(skip.ProfileType) + " "
                    + (skip.ProfileType == ProfileTypes.none ? "" : "profile ")
                    + "back — " + skip.Reason + ".", TraceLevel.Warning);
            }

            foreach (var action in plan.Actions)
            {
                bool ok = RunProfileAction(action);
                if (action.Kind == ProfileActionKind.TurnAutosaveOn && ok)
                {
                    _autosaveTurnedOffThisSession = false;
                }
            }

            // If autosave was owed but PlanPutBack did NOT turn it on — because
            // something it changed could not be put back — say so plainly: the
            // radio is left with autosave off ON PURPOSE, detectable and
            // one-press to fix, which is the safe failure by design (#499).
            if (autosaveOff && !plan.Actions.Any(a => a.Kind == ProfileActionKind.TurnAutosaveOn))
            {
                Tracing.TraceLine(
                    "ProfileStewardship: leaving this radio's autosave OFF — not everything we "
                    + "changed could be put back, so turning autosave on could commit our changes. "
                    + "The record stays; the next connect offers the one-press restore.",
                    TraceLevel.Warning);
            }

            lock (_profileRecordLock)
            {
                _profileSessionRecord.Clear();
            }
        }

        /// <summary>
        /// Give the radio its autosave straight back after a connect-time
        /// abort. Nothing else was changed by the time an abort fires (the
        /// abort is on the safety step itself), so this is unconditional.
        /// </summary>
        private void RestoreRadioAutosaveAfterAbort()
        {
            var serial = theRadio?.Serial;
            // Revalidated at the write (Track G3): a restore needs a live
            // connection on the same attempt. The hold is deliberately NOT a
            // veto here — this reverses OUR OWN write, and leaving the
            // owner's autosave off under a hold would strand them; the
            // roster is traced, not consulted, for the same reason.
            if (theRadio == null || !IsConnected || !StationAttempt.IsLive)
            {
                Tracing.TraceLine("ProfileStewardship: autosave could not be given back — no live connection; the durable notice stays.", TraceLevel.Warning);
                return;
            }
            Tracing.TraceLine("ProfileStewardship: giving autosave back with the roster " + RosterJudgementNow().Verdict, TraceLevel.Info);
            bool confirmed = SetRadioProfileAutosaveInternal(true, "restoring autosave after a connect-time abort");
            if (!confirmed)
            {
                // The durable "we owe it back" notice stays until the radio
                // REPORTS autosave on: clearing it on an unconfirmed restore
                // would strand the owner silently (review step 9).
                Tracing.TraceLine("ProfileStewardship: autosave was not confirmed back on after the abort; the "
                    + "durable notice stays and the next connect offers the one-press restore.", TraceLevel.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(serial))
                RadioConfig.RecordAutosaveTurnedOffByUs(serial, false);
            _autosaveTurnedOffThisSession = false;
        }

        /// <summary>
        /// Run an OFFERED restore of the stranded restore points a previous
        /// session left. The caller has already asked a human — this method
        /// never decides to run.
        /// </summary>
        /// <returns>What happened, in a sentence the caller speaks.</returns>
        public string RestoreStrandedProfiles(IEnumerable<ProfileTypes> accepted)
        {
            var situation = ReadProfileSituation(null, freshAsk: true);
            var plan = ProfileStewardship.PlanOfferedRestore(situation, accepted);

            foreach (var skip in plan.Skips)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: offered restore of the "
                    + ProfileStewardship.Label(skip.ProfileType) + " profile did not run — "
                    + skip.Reason, TraceLevel.Warning);
            }

            var done = new List<ProfileTypes>();
            foreach (var action in plan.Actions)
            {
                if (RunProfileAction(action)) done.Add(action.ProfileType);
            }

            StrandedProfileRestorePoints =
                StrandedProfileRestorePoints.Where(t => !done.Contains(t)).ToArray();

            if (done.Count == 0)
            {
                return Lexicon.Get("settings.profile_guest.restore_none");
            }
            return Lexicon.Get("settings.profile_guest.restore_done",
                ("types", string.Join(", ", done.Select(ProfileStewardship.Label))));
        }

        /// <summary>
        /// Send one planned step and confirm it as far as the radio lets us.
        ///
        /// <para><b>A capture is confirmed by readback and a selection is
        /// not</b>, and that asymmetry is deliberate rather than an oversight.
        /// A restore point that did not actually get created is a safety net
        /// that is not there, so the caller must know; the profile list is the
        /// radio's own report and answers that question. A selection has no
        /// equivalent readback that distinguishes "the radio applied it" from
        /// "the radio acknowledged it" (#164), so this reports the command
        /// going out and claims nothing more.</para>
        /// </summary>
        private bool RunProfileAction(ProfileAction action)
        {
            var radio = theRadio;
            if (radio == null || action == null) return false;

            Tracing.TraceLine(
                "ProfileStewardship: " + action.Kind + " " + ProfileStewardship.Label(action.ProfileType)
                + " '" + action.ProfileName + "' — " + action.Because, TraceLevel.Info);

            switch (action.Kind)
            {
                case ProfileActionKind.LoadOurs:
                case ProfileActionKind.LoadTheirNameBack:
                case ProfileActionKind.LoadRestorePoint:
                    ApplyProfileSelection(
                        radio, action.ProfileType, action.ProfileName, action.MayCreate);
                    return true;

                case ProfileActionKind.RemoveRestorePoint:
                    return RemoveProfileRestorePoint(radio, action.ProfileType, action.ProfileName);

                case ProfileActionKind.TurnAutosaveOff:
                    return SetRadioProfileAutosaveGuest(false);

                case ProfileActionKind.TurnAutosaveOn:
                    return SetRadioProfileAutosaveGuest(true);

                case ProfileActionKind.CaptureLiveTransmitAudio:
                    return CaptureLiveTransmitAudio();

                case ProfileActionKind.ApplyLocalTransmitAudio:
                    return ApplyLocalTransmitAudio(action.ProfileName);

                case ProfileActionKind.RestoreLiveTransmitAudio:
                    return RestoreLiveTransmitAudioNow();
            }
            return false;
        }

        // ── The live transmit-audio path and the radio's autosave (#499) ──
        //
        // NOTHING here creates, selects, saves or deletes a profile ON a radio
        // that is not ours. The whole design (#499) is that our transmit-audio
        // profile is kept on THIS COMPUTER and applied to the radio's LIVE
        // state; the radio's own saved profile is never written, so it is never
        // at risk. The one write this makes on a radio that is not ours is
        // turning its profile autosave off for the visit — a single boolean any
        // client can read and reverse in one press, which is the point.

        /// <summary>The connect-time apply is DEFERRED to after the command
        /// queue's main loop starts, because the audio setters enqueue through
        /// it. Empty when nothing is pending. See
        /// <see cref="ApplyDeferredGuestTransmitAudio"/>.</summary>
        private string _pendingLiveTxApplyPreset;

        /// <summary>
        /// Turn the radio's profile autosave off (or back on) as part of a
        /// guest stewardship plan, recording the "we owe it back" flag so a
        /// crash is detectable. The change-nothing hold is not re-checked here:
        /// the plan that produced this action already refuses under the hold.
        /// </summary>
        private bool SetRadioProfileAutosaveGuest(bool on)
        {
            var serial = theRadio?.Serial;

            // Record BEFORE the command when turning OFF, so a crash between the
            // record and the radio acting still leaves the durable "we owe it"
            // note. The reverse order could turn autosave off and leave no
            // record, which is the one failure that strands the owner silently.
            if (!on && !string.IsNullOrEmpty(serial))
                RadioConfig.RecordAutosaveTurnedOffByUs(serial, true);

            bool confirmed = SetRadioProfileAutosaveInternal(
                on, on ? "guest stewardship: giving the radio its autosave back"
                       : "guest stewardship: autosave off for the visit so nothing lands in the owner's profile");

            if (on && confirmed && !string.IsNullOrEmpty(serial))
                RadioConfig.RecordAutosaveTurnedOffByUs(serial, false);

            return confirmed;
        }

        /// <summary>
        /// The raw autosave write and its confirmation. <c>Radio.ProfileAutoSave</c>
        /// sends its command through FlexLib's own transport (not our queue), so
        /// this works before our main loop is up — which is when connect runs.
        /// The confirmation is the radio echoing the new value back through the
        /// status stream, which fires <c>_radioReportedAutosave</c>.
        /// </summary>
        private bool SetRadioProfileAutosaveInternal(bool on, string why)
        {
            var radio = theRadio;
            if (radio == null) return false;
            Tracing.TraceLine("ProfileStewardship: profile autosave -> " + (on ? "ON" : "OFF")
                + " — " + why, TraceLevel.Warning);

            // Radio-origin confirmation, armed BEFORE the write (design step
            // 9). Until 2026-09-21 this waited for theRadio.ProfileAutoSave ==
            // on — a value OUR OWN setter had just assigned, whose
            // PropertyChanged had just set _radioReportedAutosave. The wait
            // was satisfied by the echo of the command, not by the radio. Now
            // only an observation with RadioReported provenance, at a sequence
            // after this send, with the wanted value, confirms it.
            int gen = AttemptGen;
            var before = ProfileEvidence.Snapshot();
            if (before.RadioReportedAutosave == on)
            {
                Tracing.TraceLine("ProfileStewardship: the radio already reports autosave "
                    + (on ? "on" : "off") + "; nothing to send.", TraceLevel.Info);
                return true;
            }
            long seq = ProfileEvidence.Sequence;

            try
            {
                using (OwnProfileWrite())
                using (AppInitiatedSettingChanges())
                {
                    // The FlexLib setter early-returns when its cached value
                    // already equals the request and sends nothing. The cache
                    // is not the radio; when it agrees but the radio has not
                    // said so, send the command directly so a report can come.
                    if (radio.ProfileAutoSave == on)
                    {
                        var reply = radio.SendCommandAsync("profile autosave " + (on ? "on" : "off"));
                        _ = reply.ContinueWith(
                            t => Tracing.TraceLine("ProfileStewardship: autosave command refused: "
                                + t.Exception?.GetBaseException().Message, TraceLevel.Warning),
                            TaskContinuationOptions.OnlyOnFaulted);
                    }
                    else
                        radio.ProfileAutoSave = on;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: setting autosave threw: " + ex.Message,
                    TraceLevel.Error);
                return false;
            }

            int bound = StationDeadlines.Default().AutosaveAndCaptureMs;
            bool ok = await(() =>
            {
                if (theRadio == null || AttemptGen != gen) return true; // stop waiting; fails below
                var s = ProfileEvidence.Snapshot();
                return s.Autosave != null
                       && s.Autosave.Provenance == ObservationProvenance.RadioReported
                       && s.Autosave.Sequence > seq
                       && s.Autosave.On == on;
            }, bound);
            var after = ProfileEvidence.Snapshot();
            bool confirmed = theRadio != null && AttemptGen == gen
                             && after.Autosave != null
                             && after.Autosave.Provenance == ObservationProvenance.RadioReported
                             && after.Autosave.Sequence > seq
                             && after.Autosave.On == on;
            if (!confirmed)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: the radio did not REPORT autosave " + (on ? "on" : "off")
                    + " within " + bound + " ms. A locally assigned value is not evidence; treating as unconfirmed."
                    + (ok ? "" : " (bench question F: does the radio echo auto_save after the command?)"), TraceLevel.Error);
            }
            return confirmed;
        }

        /// <summary>
        /// Snapshot the radio's live transmit-audio settings, in memory and on
        /// disk beside the radio's config, BEFORE ours are applied. A read of
        /// the radio; nothing on it changes. The on-disk copy is what lets a
        /// session that ends badly be OFFERED the put-back next time.
        /// </summary>
        /// <summary>The transmit-chain generation and attempt the in-memory
        /// snapshot was captured at. The deferred apply refuses when either
        /// has moved: a snapshot of a chain that has since changed is a stale
        /// restore point, and applying over it would put back the wrong
        /// settings.</summary>
        private long _liveTxSnapshotChainGeneration = -1;
        private int _liveTxSnapshotAttempt = -1;
        private int _pendingLiveTxApplyAttempt = -1;

        /// <summary>The operation under which the live-audio apply was
        /// deferred. Its liveness, not the current operation's, is what the
        /// apply and every setter it queues check.</summary>
        private Radios.StationConnect.StationOperation _pendingLiveTxApplyOperation;

        /// <summary>The preset as it was when the apply was deferred. Applied
        /// as held; never re-read by name at apply time.</summary>
        private AudioChainPreset _pendingLiveTxApplyPayload;

        private bool CaptureLiveTransmitAudio()
        {
            var radio = theRadio;
            if (radio == null) return false;
            var serial = radio.Serial;

            // Capturing before the radio has reported its own transmit chain
            // would snapshot defaults, and putting defaults back later is
            // worse than nothing. The proof is the RECEIPT SET: every field
            // the snapshot records, reported by the radio's own status this
            // attempt (LiveAudioCapture.RequiredFields). Until Track G3
            // MicSource alone stood in for the whole chain (Track G2
            // re-review, step 9). Until 2026-09-21 this proceeded anyway on
            // expiry ("best-effort"); the design (step 9) rules that expiry
            // PREVENTS application rather than accepting a snapshot of
            // defaults, so an expired bound is a failed capture.
            int bound = StationDeadlines.Default().AutosaveAndCaptureMs;
            int gen = AttemptGen;
            await(() => theRadio == null || AttemptGen != gen
                || LiveAudioCapture.MissingFields(ProfileEvidence.Snapshot().TxChainFieldsReported).Count == 0, bound);
            var missing = theRadio == null || AttemptGen != gen
                ? LiveAudioCapture.RequiredFields
                : LiveAudioCapture.MissingFields(ProfileEvidence.Snapshot().TxChainFieldsReported);
            if (theRadio == null || AttemptGen != gen || missing.Count > 0)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: the radio had not reported its whole transmit chain within " + bound
                    + " ms (missing: " + string.Join(", ", missing) + "). NOT capturing — a snapshot of defaults is not a restore point.",
                    TraceLevel.Error);
                _liveTxSnapshot = null;
                return false;
            }

            try
            {
                var evidence = ProfileEvidence.Snapshot();
                _liveTxSnapshot = AudioChainPreset.CaptureFrom(
                    this, "live transmit audio " + (serial ?? ""));
                _liveTxSnapshotChainGeneration = evidence.TxChainGeneration;
                _liveTxSnapshotAttempt = AttemptGen;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: live-audio capture threw: " + ex.Message,
                    TraceLevel.Error);
                _liveTxSnapshot = null;
                return false;
            }

            // Durable only when the file landed and reads back. A restore
            // point that is not on disk cannot rescue a session that ends
            // badly, so nothing is applied over it (review section 2).
            if (!SaveLiveTxSnapshot(serial, _liveTxSnapshot))
            {
                Tracing.TraceLine(
                    "ProfileStewardship: the live transmit-audio snapshot could not be persisted beside the radio's "
                    + "config, so it is NOT a restore point and nothing will be applied.", TraceLevel.Error);
                _liveTxSnapshot = null;
                return false;
            }
            Tracing.TraceLine(
                "ProfileStewardship: captured this radio's live transmit audio ("
                + _liveTxSnapshot.FormatForSpeech() + ") and persisted it.", TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Apply the operator's LOCAL transmit-audio profile to the radio's
        /// live state. On connect this is DEFERRED (the audio setters enqueue
        /// through the command loop, which is not up yet); on disconnect and
        /// the offered-restore path the loop is running, so it applies now.
        /// </summary>
        private bool ApplyLocalTransmitAudio(string presetName)
        {
            if (q != null && !q.MainLoop)
            {
                _pendingLiveTxApplyPreset = presetName;
                Tracing.TraceLine(
                    "ProfileStewardship: deferring the live transmit-audio apply until the command "
                    + "loop is running.", TraceLevel.Info);
                return true;
            }
            return ApplyLocalTransmitAudioNow(presetName);
        }

        private bool ApplyLocalTransmitAudioNow(string presetName)
        {
            var preset = FindLocalTransmitAudioProfile(presetName);
            if (preset == null)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: the local transmit-audio profile '" + presetName
                    + "' is gone; nothing applied.", TraceLevel.Error);
                return false;
            }
            try
            {
                using (AppInitiatedSettingChanges())
                    preset.ApplyTo(this);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: applying live transmit audio threw: "
                    + ex.Message, TraceLevel.Error);
                return false;
            }
            Tracing.TraceLine(
                "ProfileStewardship: applied the operator's '" + presetName
                + "' transmit audio to the radio's live state. Nothing was saved on the radio.",
                TraceLevel.Info);
            return true;
        }

        /// <summary>
        /// Called once the command loop is up (see the connect sequence), to
        /// run any live transmit-audio apply that was deferred at connect.
        /// </summary>
        /// <remarks>
        /// <para>The apply is the IMMUTABLE payload captured when it was
        /// deferred, never a fresh lookup by name; the gate is
        /// <see cref="DeferredLiveAudioGate"/>, checked here and again inside
        /// EVERY setter the preset enqueues, through the ambient
        /// <see cref="QueuedWriteGate"/>; and the session record — the
        /// put-back obligation — is written by the continuation queued behind
        /// the setters, only when all of them ran. A refusal here applies
        /// nothing, so it owes nothing: the snapshot file is removed and the
        /// radio's autosave is given back (Track G2; review step 9).</para>
        /// </remarks>
        internal void ApplyDeferredGuestTransmitAudio()
        {
            var batch = TakePendingLiveAudioBatch();
            if (batch == null) return;
            string pending = batch.Preset;

            // Revalidate at the deferred delegate (design step 9): the
            // permission and the snapshot are as old as the plan, and the
            // queue delay is exactly where they go stale.
            string refusal = DeferredLiveAudioRefusal(batch);
            if (refusal != null)
            {
                Tracing.TraceLine("ProfileStewardship: the deferred live transmit-audio apply did NOT run — "
                    + refusal + ". Nothing was applied, so nothing is owed: the snapshot is discarded and "
                    + "autosave is given back.", TraceLevel.Warning);
                AbandonUnappliedLiveAudio();
                if (!SuppressSpeech)
                {
                    NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_not_applied",
                        ("why", Lexicon.Get("settings.profile_guest.why.chain_changed"))),
                        VerbosityLevel.Terse, volunteered: true);
                }
                RunPendingSilentMicAssessment("deferred apply refused");
                return;
            }

            // Every setter the preset enqueues is asked again at its run.
            var gate = QueuedWriteGate.Open(() => DeferredLiveAudioRefusal(batch));
            bool enqueued;
            string receipt = "";
            try
            {
                enqueued = ApplyLocalTransmitAudioPayloadNow(batch.Payload, pending, out receipt);
            }
            finally
            {
                gate.Dispose();
            }
            if (!enqueued)
            {
                AbandonUnappliedLiveAudio();
                RunPendingSilentMicAssessment("deferred apply failed to enqueue");
                return;
            }

            // The continuation runs after the setters (same queue, FIFO). It
            // is the only place the obligation is recorded and the only place
            // "applied" is said.
            q.Enqueue((FunctionDel)(() =>
            {
                if (gate.Complete)
                {
                    lock (_profileRecordLock)
                    {
                        _profileSessionRecord.RemoveAll(r => r.LiveTransmitAudio);
                        _profileSessionRecord.Add(new ProfileSessionRecord
                        {
                            ProfileType = ProfileTypes.tx,
                            LiveTransmitAudio = true,
                            TheirSelection = SelectionOnRadio(ProfileTypes.tx) ?? "",
                            WeLoaded = pending,
                        });
                    }
                    Tracing.TraceLine("ProfileStewardship: applied the operator's '" + pending
                        + "' transmit audio to the radio's live state: " + gate.Ran + " setter(s) ran, none refused. "
                        + "Nothing was saved on the radio; the put-back is owed."
                        + (receipt.Length == 0 ? "" : " " + receipt), TraceLevel.Info);
                    if (!SuppressSpeech)
                    {
                        // The preset's own receipt, appended as its author
                        // intended — "callers append it to their own
                        // announcement". It names what could NOT be applied
                        // faithfully (an EQ the file never captured, a
                        // microphone input the preset was tuned for and the
                        // radio is not on), and until Track G5 this path threw
                        // it away, so a half-applied chain was announced as
                        // applied. FOR NOEL'S PROSE REVIEW: the appended
                        // sentences are AudioChainPreset.ApplyTo's.
                        NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_applied",
                            ("preset", pending)) + (receipt.Length == 0 ? "" : " " + receipt),
                            VerbosityLevel.Terse, volunteered: true);
                    }
                }
                else
                {
                    // Some setters ran and some were refused: the radio is
                    // partly changed. The put-back IS owed (the snapshot holds
                    // the owner's chain) and the operator is told it was not
                    // applied in full.
                    lock (_profileRecordLock)
                    {
                        // A setter that THREW may have reached the radio
                        // before it failed: it owes a put-back as a ran one
                        // does. Only a fully refused apply owes nothing.
                        bool anyMayHaveWritten = gate.Ran > 0 || gate.Threw > 0;
                        if (anyMayHaveWritten) _profileSessionRecord.RemoveAll(r => r.LiveTransmitAudio);
                        if (anyMayHaveWritten)
                        {
                            _profileSessionRecord.Add(new ProfileSessionRecord
                            {
                                ProfileType = ProfileTypes.tx,
                                LiveTransmitAudio = true,
                                TheirSelection = SelectionOnRadio(ProfileTypes.tx) ?? "",
                                WeLoaded = pending,
                            });
                        }
                    }
                    Tracing.TraceLine("ProfileStewardship: the live transmit-audio apply was " + (gate.Ran > 0 ? "PARTIAL" : "refused in full")
                        + ": " + gate.Ran + " setter(s) ran, " + gate.Refused + " refused (" + gate.FirstRefusal + "), "
                        + gate.Threw + " threw (" + gate.FirstThrow + "). "
                        + (gate.Ran > 0 || gate.Threw > 0 ? "The put-back is owed." : "Nothing is owed.")
                        + (receipt.Length == 0 ? "" : " " + receipt), TraceLevel.Warning);
                    if (gate.Ran == 0 && gate.Threw == 0) AbandonUnappliedLiveAudio();
                    if (!SuppressSpeech)
                    {
                        NoteProfileVerdict(Lexicon.Get("settings.profile_guest.live_audio_not_applied",
                            ("why", Lexicon.Get("settings.profile_guest.why.chain_changed"))),
                            VerbosityLevel.Terse, volunteered: true);
                    }
                }
                RunPendingSilentMicAssessment("after the deferred apply's setters");
            }), "live transmit audio continuation");
        }

        /// <summary>
        /// THIS batch never applied anything, so it owes nothing: discard its
        /// snapshot and give autosave back.
        /// </summary>
        /// <remarks>
        /// Scoped to the batch being abandoned (Track G5). It used to delete
        /// the snapshot and EVERY live record and turn autosave back on,
        /// whatever else was outstanding, from all three of its entry paths —
        /// so a later operation's refusal erased an earlier operation's
        /// put-back and committed our applied chain into the owner's profile.
        /// An unapplied batch recorded nothing, which is what makes this
        /// decidable: any live record present belongs to an EARLIER operation
        /// on this connection, and so does the snapshot its put-back will read
        /// from. The autosave half is <see cref="LiveAudioAbortPlan"/>, the
        /// same decision Track G3 gave <see cref="AbortLiveAudio"/>.
        /// </remarks>
        private void AbandonUnappliedLiveAudio()
        {
            bool priorRecord;
            lock (_profileRecordLock) priorRecord = _profileSessionRecord.Any(r => r.LiveTransmitAudio);
            var plan = LiveAudioAbortPlan.Decide(false, _autosaveTurnedOffThisSession, priorRecord);
            if (priorRecord)
            {
                Tracing.TraceLine("ProfileStewardship: this live transmit-audio apply is abandoned, but an earlier "
                    + "operation on this connection applied ours and owes a put-back — its record and the snapshot "
                    + "that put-back reads are kept. " + plan.Reason, TraceLevel.Warning);
            }
            else
            {
                var serial = theRadio?.Serial;
                _liveTxSnapshot = null;
                if (!string.IsNullOrEmpty(serial)) DeleteLiveTxSnapshot(serial);
                lock (_profileRecordLock) _profileSessionRecord.RemoveAll(r => r.LiveTransmitAudio);
                Tracing.TraceLine("ProfileStewardship: nothing was applied and nothing earlier is outstanding, so the "
                    + "snapshot is discarded. " + plan.Reason, TraceLevel.Info);
            }
            if (plan.RestoreAutosave) RestoreRadioAutosaveAfterAbort();
        }

        /// <summary>Apply a held payload to the live state: the setters are
        /// enqueued through the command loop, under whatever gate is ambient.
        /// <paramref name="receipt"/> carries the preset's own note about
        /// anything it could not apply faithfully — "" when the apply was
        /// complete. Until Track G5 this method threw that note away.</summary>
        private bool ApplyLocalTransmitAudioPayloadNow(AudioChainPreset payload, string presetName, out string receipt)
        {
            receipt = "";
            if (payload == null)
            {
                Tracing.TraceLine("ProfileStewardship: no held payload for '" + presetName + "'; nothing applied.", TraceLevel.Error);
                return false;
            }
            try
            {
                using (AppInitiatedSettingChanges())
                    receipt = payload.ApplyTo(this) ?? "";
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: applying live transmit audio threw: " + ex.Message, TraceLevel.Error);
                return false;
            }
            return true;
        }

        /// <summary>The assessment the post-station phase deferred behind the
        /// live-audio apply. Runs once; read-only unless the phase permitted
        /// a repair.</summary>
        private void RunPendingSilentMicAssessment(string when)
        {
            if (!_pendingAssessmentOwed) return;
            _pendingAssessmentOwed = false;
            Tracing.TraceLine("StationConnect: silent-microphone assessment " + when, TraceLevel.Info);
            CheckMicProfileForSilentTx(mayRepair: _pendingAssessmentMayRepair);
        }

        /// <summary>
        /// ONE deferred live-audio apply's identity, captured when the batch
        /// was taken off the pending fields and carried to every gate and
        /// continuation that batch opens.
        /// </summary>
        /// <remarks>
        /// The fields it copies are mutable and a later operation overwrites
        /// them: the post-import entry records a new pending apply while the
        /// first apply's setters are still queued behind it. Reading them at
        /// the setter therefore asked about the NEWER operation, and with the
        /// same preset chosen the gate could not tell the batches apart, so
        /// the old setters ran with the new operation's permission (Track G5;
        /// Astra's review of G3 and G4).
        /// </remarks>
        private sealed class LiveAudioBatch
        {
            public string Preset;
            public AudioChainPreset Payload;
            public Radios.StationConnect.StationOperation Operation;
            public int Attempt;
        }

        /// <summary>Take the pending apply off the fields as one batch, so
        /// nothing a later operation writes there can reach it.</summary>
        private LiveAudioBatch TakePendingLiveAudioBatch()
        {
            string preset = _pendingLiveTxApplyPreset;
            if (string.IsNullOrEmpty(preset)) return null;
            var batch = new LiveAudioBatch
            {
                Preset = preset,
                Payload = _pendingLiveTxApplyPayload,
                Operation = _pendingLiveTxApplyOperation,
                Attempt = _pendingLiveTxApplyAttempt,
            };
            _pendingLiveTxApplyPreset = null;
            _pendingLiveTxApplyPayload = null;
            _pendingLiveTxApplyOperation = null;
            _pendingLiveTxApplyAttempt = -1;
            return batch;
        }

        /// <summary>Why the deferred apply must not run now, or null: the
        /// pure <see cref="DeferredLiveAudioGate"/> over the facts as they
        /// are at this instant, judged against the identity THIS batch was
        /// queued with. Called at the delegate and inside every setter it
        /// queues.</summary>
        private string DeferredLiveAudioRefusal(LiveAudioBatch batch)
        {
            string presetName = batch.Preset;
            var payload = batch.Payload;
            var attempt = StationAttempt;
            // The operation that QUEUED the apply, never the current one: a
            // post-import re-entry or a teardown that began meanwhile ended
            // it, and its work must refuse even though the CURRENT operation
            // is live (Track G2 re-review, step 1). Carried in the batch
            // rather than re-read from the field, because the field is where
            // the newer operation writes (Track G5).
            var op = batch.Operation;
            var radio = theRadio;
            var serial = radio?.Serial ?? "";
            var evidence = ProfileEvidence.Snapshot();
            return DeferredLiveAudioGate.Refusal(new DeferredLiveAudioFacts
            {
                OperationLive = attempt.IsLive && op != null && op.IsLive,
                OperationEndReason = op == null ? "no originating operation is recorded for the deferred apply" : (op.WhyNotLive ?? attempt.CancelReason),
                ApplyAttemptMatches = batch.Attempt == attempt.Generation,
                Connected = radio != null && IsConnected,
                HoldArmed = ChangeNothingActive,
                // The GUEST'S authority, not the owner's ruled one (Track G3).
                StrictRoster = RosterJudgementForGuestSharedWrite().Verdict,
                Intent = string.IsNullOrEmpty(serial) ? ProfileGuestIntent.NotAnswered : RadioConfig.ProfileIntentOf(serial),
                ChosenLocalPreset = string.IsNullOrEmpty(serial) ? "" : RadioConfig.LocalTransmitAudioChoiceOf(serial),
                PendingPreset = presetName,
                PendingPayloadHeld = payload != null,
                SnapshotHeld = _liveTxSnapshot != null,
                SnapshotAttemptMatches = _liveTxSnapshotAttempt == attempt.Generation,
                SnapshotChainGeneration = _liveTxSnapshotChainGeneration,
                ChainGenerationNow = evidence.TxChainGeneration,
                OwnerHasUnsavedWork = UnsavedProfileChangesFor(ProfileTypes.tx) || UnsavedProfileChangesFor(ProfileTypes.mic),
                RadioIsOurs = !string.IsNullOrEmpty(serial) && RadioConfig.OwnershipOf(serial) == RadioOwnership.Mine,
                ReportedAutosave = evidence.RadioReportedAutosave,
            });
        }

        /// <summary>
        /// Put the radio's own live transmit audio back from the snapshot —
        /// the clean-disconnect and offered-restore path. Uses the in-memory
        /// snapshot when this session took it, else the on-disk one an earlier
        /// session left. Deletes the on-disk copy once it is back.
        /// </summary>
        private bool RestoreLiveTransmitAudioNow()
        {
            var radio = theRadio;
            if (radio == null) return false;
            var serial = radio.Serial;

            var snapshot = _liveTxSnapshot ?? LoadLiveTxSnapshot(serial);
            if (snapshot == null)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: no live transmit-audio snapshot to put back.",
                    TraceLevel.Warning);
                return false;
            }

            try
            {
                using (AppInitiatedSettingChanges())
                    // applyEq only when the snapshot actually captured the EQ —
                    // a band the snapshot never read must not be written.
                    snapshot.ApplyTo(this, applyEq: snapshot.TxEqCaptured);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: restoring live transmit audio threw: "
                    + ex.Message, TraceLevel.Error);
                return false;
            }

            DeleteLiveTxSnapshot(serial);
            _liveTxSnapshot = null;
            Tracing.TraceLine(
                "ProfileStewardship: put this radio's own live transmit audio back.",
                TraceLevel.Info);
            return true;
        }

        // ── Operator-facing verbs and the local store ──

        /// <summary>
        /// True when a session of ours left this radio's profile autosave off
        /// and has not put it back — the condition for offering the one-press
        /// restore. Read-only.
        /// </summary>
        public bool RadioProfileAutosaveOwedBackOn
        {
            get
            {
                var serial = theRadio?.Serial;
                return !string.IsNullOrEmpty(serial)
                       && IsConnected
                       && RadioConfig.AutosaveTurnedOffByUsOn(serial);
            }
        }

        /// <summary>
        /// Turn this radio's profile autosave back on at the operator's
        /// request (the one-press restore, #499). Guarded like every operator
        /// write; returns an outcome the caller can tell apart from a
        /// failure (#486).
        /// </summary>
        public GuardedOutcome TurnRadioProfileAutosaveBackOn()
        {
            if (theRadio == null || !IsConnected) return GuardedOutcome.Skipped;
            if (GuardRefuses("settings.guard.action.autosave")) return GuardedOutcome.Refused;
            // "Already on" only when the radio has REPORTED it on. The vendor's
            // cached value is optimistically true after an unconfirmed attempt
            // set it, and a second press that returned Skipped on that value
            // left the durable obligation standing (review section 8). The
            // helper below sends nothing when the radio does report on.
            if (ProfileEvidence.Snapshot().RadioReportedAutosave == true) return GuardedOutcome.Skipped;

            bool ok = SetRadioProfileAutosaveInternal(true, "operator asked to turn autosave back on");
            var serial = theRadio?.Serial;
            if (ok && !string.IsNullOrEmpty(serial))
            {
                RadioConfig.RecordAutosaveTurnedOffByUs(serial, false);
                _autosaveTurnedOffThisSession = false;
            }
            return ok ? GuardedOutcome.Done : GuardedOutcome.Failed;
        }

        /// <summary>
        /// True when this radio has a live transmit-audio snapshot from a
        /// PRIOR session on disk — the condition for offering to put it back.
        /// Not the working snapshot of the current session (which is held in
        /// memory), so this is false while a live-audio session is in flight.
        /// </summary>
        public bool HasStrandedLiveTransmitAudioSnapshot
        {
            get
            {
                var serial = theRadio?.Serial;
                return !string.IsNullOrEmpty(serial)
                       && IsConnected
                       && _liveTxSnapshot == null
                       && LiveTxSnapshotFileExists(serial);
            }
        }

        /// <summary>
        /// Put this radio's own transmit audio back from a snapshot a prior
        /// session left. Offered only; the operator has already said yes.
        /// Returns a sentence the caller speaks.
        /// </summary>
        public string RestoreStrandedLiveTransmitAudio()
        {
            var situation = ReadProfileSituation(null, freshAsk: false);
            var plan = ProfileStewardship.PlanOfferedLiveTransmitAudioRestore(situation);
            bool done = false;
            foreach (var action in plan.Actions)
            {
                if (RunProfileAction(action)) done = true;
            }
            return done
                ? Lexicon.Get("settings.profile_guest.live_audio_put_back")
                : Lexicon.Get("settings.profile_guest.live_audio_put_back_none");
        }

        /// <summary>
        /// The operator's local transmit-audio profile of a given name, from
        /// the per-operator store, or null. Read-only; loads the store fresh so
        /// a profile saved in the Audio Workshop this session is visible.
        /// </summary>
        private AudioChainPreset FindLocalTransmitAudioProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Callouts == null) return null;
            try
            {
                var store = AudioChainPresets.Load(Callouts.ConfigDirectory, Callouts.OperatorName);
                return store?.Presets?.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: reading the local transmit-audio store threw: "
                    + ex.Message, TraceLevel.Warning);
                return null;
            }
        }

        private static string LiveTxSnapshotPath(string serial)
        {
            var dir = RadioConfig.RadioDirectory(serial);
            return dir == null ? null : System.IO.Path.Combine(dir, "live-tx-audio-snapshot.xml");
        }

        private static bool LiveTxSnapshotFileExists(string serial)
        {
            var path = LiveTxSnapshotPath(serial);
            return path != null && System.IO.File.Exists(path);
        }

        private static bool SaveLiveTxSnapshot(string serial, AudioChainPreset snapshot)
        {
            var path = LiveTxSnapshotPath(serial);
            if (path == null || snapshot == null) return false;
            return LiveTxSnapshotStore.Persist(path, snapshot);
        }

        private static AudioChainPreset LoadLiveTxSnapshot(string serial)
        {
            var path = LiveTxSnapshotPath(serial);
            if (path == null) return null;
            return AudioChainPreset.TryLoad(path, out var preset) ? preset : null;
        }

        private static void DeleteLiveTxSnapshot(string serial)
        {
            var path = LiveTxSnapshotPath(serial);
            if (path == null) return;
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception ex)
            {
                Tracing.TraceLine("ProfileStewardship: could not delete the live-audio snapshot: "
                    + ex.Message, TraceLevel.Warning);
            }
        }

        /// <summary>
        /// Delete a restore point, confirming it is gone. Used only after the
        /// radio has been put back on its own profile by name, so the
        /// restore point holds nothing that is not also live.
        /// </summary>
        private bool RemoveProfileRestorePoint(Radio radio, ProfileTypes type, string name)
        {
            if (!ProfileRestorePoints.IsRestorePoint(name))
            {
                Tracing.TraceLine(
                    "ProfileStewardship: refusing to delete '" + name + "' — it is not one "
                    + "of ours. Nothing was deleted.", TraceLevel.Error);
                return false;
            }

            try
            {
                switch (type)
                {
                    case ProfileTypes.global:
                        q.Enqueue((FunctionDel)(() => radio.DeleteGlobalProfile(name)),
                            "remove global restore point", true);
                        break;
                    case ProfileTypes.tx:
                        q.Enqueue((FunctionDel)(() => radio.DeleteTXProfile(name)),
                            "remove transmit restore point", true);
                        break;
                    case ProfileTypes.mic:
                        q.Enqueue((FunctionDel)(() => radio.DeleteMICProfile(name)),
                            "remove microphone restore point", true);
                        break;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: delete of '" + name + "' threw: " + ex.Message,
                    TraceLevel.Warning);
                return false;
            }

            // A SHORT wait, and deliberately shorter than the capture's. This
            // runs on the way out, where every second is a second the operator
            // waits for a disconnect they asked for — and unlike the capture,
            // the answer changes nothing: the radio is already back on its own
            // profile, so a restore point left behind is harmless clutter that
            // the next session will recognise and skip. A restore that makes
            // leaving feel broken is a restore that gets switched off.
            bool gone = await(() => !ProfileNamesOnRadio(type).Contains(name), 1500);
            if (!gone)
            {
                Tracing.TraceLine(
                    "ProfileStewardship: '" + name + "' is still listed. It is harmless "
                    + "clutter on the radio, not a fault — the radio is already back on its "
                    + "own profile.", TraceLevel.Warning);
            }
            return gone;
        }

        /// <summary>
        /// Select a profile, creating it first ONLY when the plan said so.
        ///
        /// <para><b><paramref name="mayCreate"/> is the whole difference
        /// between this and what was here before.</b> The old connect path
        /// created a missing transmit or microphone profile unconditionally,
        /// from a list of names that had no radio in it and no account either
        /// — the write #403 exists for. Here the decision has already been
        /// made, by a function a test can reach, and it is false for every
        /// radio the operator has not declared theirs and for every restore:
        /// a restore that invents a profile is not a restore.</para>
        /// </summary>
        private void ApplyProfileSelection(
            Radio radio, ProfileTypes type, string name, bool mayCreate)
        {
            switch (type)
            {
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() => radio.ProfileGlobalSelection = name),
                        "stewardship global selection", true);
                    break;
                case ProfileTypes.tx:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        if (!radio.ProfileTXList.Contains(name))
                        {
                            if (!mayCreate) return;
                            radio.CreateTXProfile(name);
                        }
                        radio.ProfileTXSelection = name;
                    }), "stewardship transmit selection", true);
                    break;
                case ProfileTypes.mic:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        if (!radio.ProfileMICList.Contains(name))
                        {
                            if (!mayCreate) return;
                            radio.CreateMICProfile(name);
                        }
                        radio.ProfileMICSelection = name;
                    }), "stewardship microphone selection", true);
                    break;
            }
        }

        /// <summary>
        /// The radio's own current selection for one profile type, right now:
        /// a name, "" when the radio says none is loaded, null when there is
        /// no radio to ask.
        /// </summary>
        private string SelectionOnRadio(ProfileTypes type)
        {
            var radio = theRadio;
            if (radio == null) return null;
            switch (type)
            {
                case ProfileTypes.global: return radio.ProfileGlobalSelection ?? "";
                case ProfileTypes.tx: return radio.ProfileTXSelection ?? "";
                case ProfileTypes.mic: return radio.ProfileMICSelection ?? "";
                default: return null;
            }
        }

        /// <summary>The radio's own names for one profile type, right now.</summary>
        private IReadOnlyList<string> ProfileNamesOnRadio(ProfileTypes type)
        {
            var radio = theRadio;
            if (radio == null) return Array.Empty<string>();
            switch (type)
            {
                case ProfileTypes.global: return radio.ProfileGlobalList?.ToList() ?? new List<string>();
                case ProfileTypes.tx: return radio.ProfileTXList?.ToList() ?? new List<string>();
                case ProfileTypes.mic: return radio.ProfileMICList?.ToList() ?? new List<string>();
                default: return Array.Empty<string>();
            }
        }

        #endregion

        // ══ Saving the station layout: the one-step verb (Sprint 33 Track K) ══
        //
        // #117 and #59. Sprint 32 Track H established that the transport works,
        // un-stubbed the dialog's Add and Update verbs, and added the spoken
        // receipt that tells the operator a slice change is provisional. What
        // it did NOT close is the half Noel named himself: "I don't know ...
        // what I need to do in JJ Flexible to get it to stick in the radio."
        //
        // He owns the radio and could not name the procedure, which is the
        // finding. The procedure exists — open Radio ▸ Profiles, find the
        // global entry matching the profile the radio currently has loaded,
        // select it, press Save — and every step of it requires knowing
        // something the receipt does not say. A receipt that names a thing the
        // operator cannot find is a receipt for a dead end.
        //
        // So this is the verb that procedure was missing: save the global
        // profile the radio ALREADY HAS LOADED, under the name it already has,
        // without asking the operator to identify it first. That is what every
        // other radio means by "it saves stuff when you turn it off", minus the
        // automatic part, which is deliberately not here — see below.
        //
        // WHY THE NAME COMES FROM THE RADIO AND NOT FROM THE OPERATOR'S LIST.
        // ProfileGlobalSelection is the radio's own report of which global
        // profile is loaded. The operator's list is a list of names the
        // operator cares about and may be empty, may be stale, and under
        // MultiFlex may not describe what is loaded at all — another client can
        // have selected a different profile since we connected. Saving to the
        // loaded name is the only reading of "save this" that cannot silently
        // write over a profile the operator was not looking at.

        /// <summary>
        /// The global profile the radio currently has loaded, or null when
        /// there is no radio or the radio has not reported a selection.
        /// </summary>
        public string CurrentGlobalProfileName
        {
            get
            {
                string name = theRadio?.ProfileGlobalSelection;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }

        /// <summary>
        /// Set when this application changed radio-persisted station state
        /// during this connection — the slice set (Sprint 33 Track K), and
        /// since Sprint 35 Track I (#225) any other profile-persisted setting
        /// the operator changed through this class (power, TX settings, filter
        /// cuts, DSP, antennas). NOT cleared by the spoken receipt. Distinct
        /// from the receipt's own flags on purpose: receipts fire and clear on
        /// their own cadence, whereas this has to survive until disconnect so
        /// the save offer can ask "did anything actually change?" and stay
        /// silent when the answer is no.
        /// </summary>
        public bool OperatorChangedStationThisSession { get; private set; }

        /// <summary>
        /// Why the station layout cannot be saved right now, or null when it
        /// can. A reason string rather than a bool because every caller here
        /// has to TELL the operator — a save verb that silently does nothing is
        /// the defect this track exists to close, not a pattern to repeat.
        /// </summary>
        public string StationLayoutSaveBlocker()
        {
            // Decision and order: StationLayoutSaveGuard.Decide (#777).
            var radio = theRadio;
            bool connected = radio != null && IsConnected;
            var ownership = RadioConfig.OwnershipOf(radio?.Serial);
            var refusal = StationLayoutSaveGuard.Decide(
                connected: connected,
                ownership: ownership,
                holdArmed: ChangeNothingActive,
                presence: connected ? OtherOperatorPresence : RosterVerdict.Unknown,
                hasGlobalProfile: CurrentGlobalProfileName != null);

            switch (refusal)
            {
                case StationLayoutSaveRefusal.NoRadio:
                    return "There is no radio connected to save to.";

                case StationLayoutSaveRefusal.NotOurRadio:
                case StationLayoutSaveRefusal.OwnershipUnknown:
                    return OwnershipRefusalSentence(refusal, ProfileWriteActionKey(ProfileTypes.global, delete: false));

                case StationLayoutSaveRefusal.ChangeNothing:
                    return Lexicon.Get("settings.guard.layout_save_blocked");

                // The MultiFlex refusal. Saving captures the whole station as
                // it looks at this instant, including another operator's
                // slices; the radio has no "only my part". Unknown is NAMED as
                // uncertainty (design section 5), never announced as a person
                // being connected.
                case StationLayoutSaveRefusal.OthersPresent:
                    return "Another operator is connected to this radio. "
                         + "Saving now would store their setup as well as yours.";
                case StationLayoutSaveRefusal.RosterUnknown:
                    return Lexicon.Get("settings.guard.layout_save_roster_unknown");

                case StationLayoutSaveRefusal.NoGlobalProfile:
                    return "This radio has no global profile loaded, so there is "
                         + "nothing to save into. Use Radio, then Profiles, to make one.";

                default:
                    return null;
            }
        }

        /// <summary>
        /// The sentence for an ownership refusal — the radio is declared
        /// someone else's, or the question was never answered — with the verb
        /// that was refused filled in from a <c>settings.guard.action.*</c>
        /// key, the way the hold's <c>settings.guard.refused</c> does it. One
        /// sentence per ownership state is therefore true whether the press
        /// was Save (which may create or overwrite a profile) or Delete; the
        /// first draft said "would replace its owner's profile", which was
        /// false for a new name and for Delete (#777 round two, Codex B2).
        /// Both name where the answer is given (Settings, under Radios),
        /// because #638 showed an owner can sit at an unanswered question for
        /// weeks without knowing one was asked. Neither promises a local copy:
        /// RecordOwnStationLayout records the station only for a radio
        /// declared Mine, so on these radios nothing is saved anywhere.
        /// </summary>
        internal static string OwnershipRefusalSentence(StationLayoutSaveRefusal refusal, string actionKey)
        {
            // APPROVED BY NOEL 2026-10-19 (#777 Q4, Q5): both sentences as
            // written, with the full application name and the Settings tab
            // called what NVDA reads for it, "Per-radio settings".
            string action = Lexicon.Get(actionKey);
            return refusal == StationLayoutSaveRefusal.NotOurRadio
                ? Lexicon.Get("settings.guard.profile_write_not_our_radio", ("action", action))
                : Lexicon.Get("settings.guard.profile_write_ownership_unknown", ("action", action));
        }

        /// <summary>
        /// The sentence the last ownership refusal spoke, or would have spoken
        /// under <see cref="SuppressSpeech"/>. For tests that drive the real
        /// funnel and need to read what the operator would hear; null until a
        /// refusal has happened on this instance. Named for the global case it
        /// began with; since the 2026-10-19 ruling it records transmit and
        /// microphone refusals too.
        /// </summary>
        internal string LastGlobalProfileWriteRefusal { get; private set; }

        /// <summary>
        /// The <c>settings.guard.action.*</c> key naming the verb and the
        /// profile kind an ownership refusal refers to (Noel, 2026-10-19:
        /// transmit and microphone profiles are refused exactly like global
        /// ones, saving and deleting alike, and the sentence names what it
        /// refused). Global keeps its own fragments, "save the profile" and
        /// "delete the profile" since Noel's 2026-10-21 fold took the "on it"
        /// / "from it" tail off every one, which read true of a global
        /// profile. A transmit or microphone profile reaches
        /// the radio only by being CREATED, so that is its verb, and those
        /// four fragments name the kind.
        /// </summary>
        internal static string ProfileWriteActionKey(ProfileTypes type, bool delete)
        {
            switch (type)
            {
                case ProfileTypes.tx:
                    return delete ? "settings.guard.action.tx_profile_delete" : "settings.guard.action.tx_profile_create";
                case ProfileTypes.mic:
                    return delete ? "settings.guard.action.mic_profile_delete" : "settings.guard.action.mic_profile_create";
                default:
                    return delete ? "settings.guard.action.profile_delete" : "settings.guard.action.profile_save";
            }
        }

        /// <summary>
        /// The ownership gate on the funnels every operator-pressed profile
        /// write passes through — <see cref="SaveProfileGuarded"/>,
        /// <see cref="DeleteProfileGuarded"/> and the create half of
        /// <see cref="SelectProfileGuarded"/>, for global, transmit and
        /// microphone profiles alike (ruled 2026-10-19). True, having traced
        /// and spoken the refusal, when the radio is not declared the
        /// operator's. False when it is — or when there is no radio at all,
        /// which is not this gate's question and is left to the caller's own
        /// no-radio handling exactly as before #777.
        /// </summary>
        /// <remarks>
        /// Needed because <see cref="StationLayoutSaveBlocker"/> is asked only
        /// by the layout save; the Profiles dialog's Save, Delete and Select
        /// buttons and the WinForms form's Save reach the funnels directly
        /// with a NAMED profile, and until #777 nothing on those routes asked
        /// whose radio it was. Speaks here, like <c>GuardRefuses</c>, because
        /// not every caller honours the return value.
        /// </remarks>
        private bool OwnershipRefusesProfileWrite(string profileName, string actionKey)
        {
            var radio = theRadio;
            if (radio == null) return false;
            var refusal = StationLayoutSaveGuard.OwnershipRefusal(RadioConfig.OwnershipOf(radio.Serial));
            if (refusal == StationLayoutSaveRefusal.None) return false;

            string sentence = OwnershipRefusalSentence(refusal, actionKey);
            LastGlobalProfileWriteRefusal = sentence;
            Tracing.TraceLine(
                "Ownership: refused " + Lexicon.Get(actionKey) + " for '" + profileName + "' on radio "
                + radio.Serial + " — " + refusal + " (#777)", TraceLevel.Warning);
            if (!SuppressSpeech)
            {
                ScreenReaderOutput.Speak(
                    sentence,
                    Speech.SpeechIntent.Interrupt,
                    VerbosityLevel.Critical,
                    subject: Speech.SpeechSubject.GlobalProfileWriteRefusal);
            }
            return true;
        }

        /// <summary>
        /// Save the station layout into the global profile the radio currently
        /// has loaded. Returns null on success, or the reason it did not happen.
        /// Writes to the radio, and only ever when something asked it to —
        /// there is no caller on any automatic path.
        /// </summary>
        public string SaveCurrentStationLayout()
        {
            string blocker = StationLayoutSaveBlocker();
            if (blocker != null)
            {
                Tracing.TraceLine(
                    "SaveCurrentStationLayout:refused:" + blocker, TraceLevel.Info);
                return blocker;
            }

            string name = CurrentGlobalProfileName;
            Tracing.TraceLine(
                "SaveCurrentStationLayout:saving global profile " + name, TraceLevel.Info);

            // Default:false — this is the radio's loaded profile, and marking it
            // the operator's default is a separate decision they did not make
            // by pressing Save.
            // HOW MUCH THIS ACTUALLY CONFIRMS — read before trusting the
            // receipt this returns to.
            //
            // SaveProfileGuarded(immediately: true) waits for the queued
            // command to be ISSUED, not for the radio to acknowledge that it
            // stored anything. Done means "the save command went out"; Failed
            // means it did not within the bound (#777 round two — until then
            // the wait's result was ignored and this cleared the dirty flag
            // either way); Refused means the hold or the ownership gate said
            // no and has already spoken, which the blocker above makes a race
            // rather than an ordinary route. So a null return from this method
            // means "the save command went out", and the announcement built on
            // it is making a slightly stronger claim than the evidence
            // supports.
            //
            // Compare DeleteProfile a few hundred lines up, which DOES confirm:
            // it awaits the name disappearing from ProfileGlobalList and
            // reports honestly when it does not. Save has no equivalent
            // readback in FlexLib today — ProfileGlobalList still contains the
            // name whether or not the contents were rewritten, and nothing
            // exposes a modified time.
            //
            // Flagged for the bench rather than guessed at: if the radio does
            // emit something observable on a successful global save, this is
            // where to await it, and the receipt wording should follow whatever
            // that turns out to allow. Until then the failure mode is a save
            // that is announced and did not happen, which is exactly the class
            // of thing this application says it does not do.
            var outcome = SaveProfileGuarded(new Profile_t(name, ProfileTypes.global, false), true);
            if (outcome != GuardedOutcome.Done)
            {
                Tracing.TraceLine("SaveCurrentStationLayout:" + outcome + " for " + name + "; nothing saved, dirty flag kept",
                    TraceLevel.Warning);
                // True for Failed (the command never went out) and for a
                // Refused race alike: the station was not saved.
                return Lexicon.Get("settings.station_layout.save_not_sent", ("profileName", name));
            }

            // The layout on the radio now matches what the operator has, so the
            // offer has nothing left to ask about. Only after Done: a save that
            // did not go out leaves the changes exactly as unsaved as they were.
            OperatorChangedStationThisSession = false;

            // A new unsaved-changes epoch begins (#225): whatever changes come
            // AFTER this save are once again provisional, and the receipt owes
            // the operator one fresh telling about them.
            _settingsReceiptGivenThisEpoch = false;
            return null;
        }

        // ══ The disconnect offer, and why it is OFF by default ══
        //
        // Noel, raising it and doubting it in the same breath: "We could also
        // have a setting that if there's been a change to the radio, it could
        // offer to save the profile. I'm not sure I'd do this, but generally,
        // if you tune the radio or any radio, when you turn it off, it saves
        // stuff."
        //
        // Both halves of that are right, which is why this is a SETTING and why
        // it ships off. The expectation is real — every other radio remembers
        // what you did to it, and an operator who releases a slice and finds it
        // back tomorrow concludes the application is broken. But Sprint 32
        // Track H considered a disconnect prompt and deliberately shipped a
        // spoken notification instead, on reasoning that still holds:
        //
        //   1. Disconnect is not power-off. A networked radio keeps running.
        //   2. Under MultiFlex somebody else may still be on it.
        //   3. A prompt that fires whether or not anything changed gets
        //      dismissed reflexively — and a prompt trained to be dismissed is
        //      worse than none, because it creates the belief that the operator
        //      was asked.
        //
        // This does not overturn that. Track H's notification remains what
        // every operator gets. What this adds is the switch Noel described, for
        // the operator who wants radio-like behaviour and says so — and the
        // gates below answer objections 2 and 3 outright rather than accepting
        // them. It never fires when another operator is connected, and it never
        // fires when nothing changed.
        //
        // OFFER, NEVER OVERWRITE. Nothing in this application overwrites a
        // global profile because a session ended. (One automatic global write
        // does exist at teardown, and this paragraph used to deny it:
        // saveNewGlobalProfile CREATES a wanted profile the radio did not have,
        // on a radio declared Mine with only us on the roster — a create,
        // never an overwrite, decided in DeferredGlobalCreation.Decide.) A
        // global profile is station state shared by everyone who connects, so
        // an automatic overwrite at disconnect would let whoever happened to
        // leave last redefine the station for everybody, silently, having
        // never been asked. The setting controls whether the QUESTION is
        // asked. The answer is always the operator's.

        /// <summary>
        /// Whether the operator has asked to be offered a station-layout save
        /// when they disconnect. App-level, one knob for every radio and every
        /// connection, in the shape of the other cross-layer settings on this
        /// class. Default false: the shipped behaviour stays Sprint 32's spoken
        /// notification until somebody deliberately turns this on.
        /// </summary>
        public static bool OfferStationSaveOnDisconnect { get; set; }

        /// <summary>
        /// Whether to offer a station-layout save right now. Every condition
        /// must hold, and each one is a separate objection being answered.
        /// </summary>
        public bool ShouldOfferStationLayoutSave()
        {
            // The operator asked for the question.
            if (!OfferStationSaveOnDisconnect) return false;

            // Something to ask ABOUT. An offer on a session where the operator
            // never touched the slice set or a radio-persisted setting is the
            // reflexive-dismissal trap, and this is the line that keeps it shut.
            if (!OperatorChangedStationThisSession) return false;

            // Saveable at all: connected, sole operator, a profile loaded to
            // save into. Carries the MultiFlex refusal, so the offer cannot
            // become the trap it exists to avoid.
            if (StationLayoutSaveBlocker() != null) return false;

            // Ownership. Until #777 this was the ONLY ownership gate on the
            // layout save, on the reasoning that a proactive prompt is a write
            // "the operator did not individually request" while the menu item
            // is the request itself. #777 overturned the second half: whose
            // radio it is decides whether the write may happen at all, not
            // whether it was asked for, so the blocker above now refuses a
            // radio not declared Mine and this line cannot be reached with
            // any other answer. Kept as the second lock on a door that should
            // already be shut, with its trace.
            var radio = theRadio;
            if (radio == null) return false;
            if (RadioConfig.OwnershipOf(radio.Serial) != RadioOwnership.Mine)
            {
                Tracing.TraceLine(
                    "ShouldOfferStationLayoutSave:not offering, radio "
                    + radio.Serial + " is not declared as the operator's",
                    TraceLevel.Info);
                return false;
            }

            return true;
        }

        // ── The operator's profile list: add and update (Sprint 32 Track H) ──
        //
        // These are the two verbs the Profiles dialog has been stubbed on since
        // it was written — OnAdd and OnUpdate spoke "not yet available" AFTER
        // the operator had navigated to the button and pressed it. Without them
        // Save can only overwrite the profile you are already on, so there was
        // no way to keep a four-slice layout and build a one-slice layout beside
        // it.
        //
        // WHAT THESE DO NOT DO IS AS IMPORTANT AS WHAT THEY DO. They do not
        // write anything to the radio. In Jim's design — which the WinForms
        // Profile dialog still implements and which this restores rather than
        // replaces — the operator's list is a list of NAMES the operator cares
        // about, with at most one default per type; the radio-side write is
        // <see cref="SaveProfile"/>, reached from the dialog's own Save button.
        // So "add a global profile then save it" is the Save As that was
        // missing, in two deliberate steps, and neither step can surprise
        // somebody else's radio.
        //
        // One default per type is enforced on the way in, matching the WinForms
        // dialog. The alternative — two profiles both claiming default — makes
        // GetProfileInfo's crnt[0] pick silently arbitrary at the next connect.

        /// <summary>
        /// Add a profile to the operator's list, clearing any other default of
        /// the same type when this one is marked default. Writes nothing to the
        /// radio. Returns false when the name is already taken for that type.
        /// </summary>
        public bool AddOperatorProfile(Profile_t p, List<Profile_t> lst = null)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return false;
            if (lst == null) lst = Callouts?.Profiles;
            if (lst == null) return false;
            if (GetProfileByName(p.Name, p.ProfileType, lst) != null) return false;

            Tracing.TraceLine("AddOperatorProfile:" + p.ToString(), TraceLevel.Info);
            if (p.Default) ClearDefaultOfType(p.ProfileType, lst);
            lst.Add(p);
            PersistOperatorProfiles();
            return true;
        }

        /// <summary>
        /// Replace an entry in the operator's list. Writes nothing to the
        /// radio: renaming here renames the operator's REFERENCE, and a profile
        /// that already exists on the radio keeps its own name until something
        /// saves under the new one.
        /// </summary>
        public bool UpdateOperatorProfile(
            Profile_t original, Profile_t replacement, List<Profile_t> lst = null)
        {
            if (original == null || replacement == null) return false;
            if (string.IsNullOrWhiteSpace(replacement.Name)) return false;
            if (lst == null) lst = Callouts?.Profiles;
            if (lst == null) return false;

            // The entry may be a rig profile the operator has never adopted —
            // GetRigProfiles hands back fresh objects that are not in the list.
            // Updating one of those ADOPTS it, which is the useful reading of
            // the verb and matches what the operator just described in the
            // dialog.
            int at = lst.IndexOf(original);
            if (at < 0)
            {
                Profile_t existing = GetProfileByName(
                    original.Name, original.ProfileType, lst);
                at = existing != null ? lst.IndexOf(existing) : -1;
            }

            // Refuse a rename that collides with a different existing entry.
            Profile_t clash = GetProfileByName(
                replacement.Name, replacement.ProfileType, lst);
            if (clash != null && (at < 0 || !ReferenceEquals(clash, lst[at])))
                return false;

            Tracing.TraceLine(
                "UpdateOperatorProfile:" + original.ToString() + " -> " + replacement.ToString(),
                TraceLevel.Info);
            if (replacement.Default) ClearDefaultOfType(replacement.ProfileType, lst);
            if (at >= 0) lst[at] = replacement;
            else lst.Add(replacement);
            PersistOperatorProfiles();
            return true;
        }

        /// <summary>Clear the default flag on every profile of one type.</summary>
        private void ClearDefaultOfType(ProfileTypes typ, List<Profile_t> lst)
        {
            foreach (Profile_t p in GetProfilesByType(typ, lst)) p.Default = false;
        }

        /// <summary>
        /// Ask the application to write the operator's record to disk. A no-op
        /// when nothing wired the callout, so the Radios layer never has to know
        /// how the operator's file is stored.
        /// </summary>
        private void PersistOperatorProfiles()
        {
            try { Callouts?.SaveOperator?.Invoke(); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    "PersistOperatorProfiles:" + ex.Message, TraceLevel.Error);
            }
        }

        /// <remarks>
        /// <b>The gap the #397 write-path audit found here, and it is the
        /// sharpest kind.</b> Under "change nothing on this radio" the operator
        /// could still DELETE a profile off it, while the load and save verbs a
        /// few hundred lines up both refused. A hold that stops two of three
        /// writers is worse than none, because it will be trusted — and delete
        /// is the most destructive of the three: a load is reversible and a
        /// save overwrites one profile, while a delete removes the only copy of
        /// somebody's station settings.
        /// </remarks>
        public bool DeleteProfile(Profile_t prof, List<Profile_t> lst = null) =>
            DeleteProfileGuarded(prof, lst) == GuardedOutcome.Done;

        /// <summary>
        /// Delete the profile, telling a refusal apart from a failure (#486),
        /// so a caller can stay silent when the guard has already spoken
        /// instead of overwriting its explanation with a generic error.
        /// </summary>
        public GuardedOutcome DeleteProfileGuarded(Profile_t prof, List<Profile_t> lst = null)
        {
            if (prof == null) return GuardedOutcome.Failed;
            // Whose radio it is first (#777): a guest could delete the owner's
            // profile with the hold off, its default. Same decision as the
            // save funnel, for all three kinds — Noel, 2026-10-19: transmit
            // and microphone profiles are refused exactly like global ones.
            if (OwnershipRefusesProfileWrite(prof.Name, ProfileWriteActionKey(prof.ProfileType, delete: true)))
                return GuardedOutcome.Refused;
            if (GuardRefuses("settings.guard.action.profile_delete")) return GuardedOutcome.Refused;
            Tracing.TraceLine("DeleteProfile:" + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Info);

            bool rv = false;
            bool profileGone = false;
            if (lst == null) lst = Callouts.Profiles;
            switch (prof.ProfileType)
            {
                case ProfileTypes.global:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteGlobalProfile(prof.Name);
                        // await the deletion.  profileGone is true if deleted.
                        if (!(profileGone = await(() => { return !theRadio.ProfileGlobalList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteGlobalProfile");
                    break;
                case ProfileTypes.tx:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteTXProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileTXList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteTXProfile");
                    break;
                case ProfileTypes.mic:
                    q.Enqueue((FunctionDel)(() =>
                    {
                        theRadio.DeleteMICProfile(prof.Name);
                        // await the deletion.
                        if (!(profileGone = await(() => { return !theRadio.ProfileMICList.Contains(prof.Name); }, 1000)))
                        {
                            Tracing.TraceLine("DeleteProfile:profile not deleted:" + prof.Name, TraceLevel.Error);
                        }
                    }), "DeleteMICProfile");
                    break;
                default:
                    Tracing.TraceLine("DeleteProfile:not valid " + prof.Name + ' ' + prof.ProfileType.ToString(), TraceLevel.Error);
                    break;
            }

            // wait for the queued deletion.
            rv = await(() => { return profileGone; }, 1000);

            Profile_t p = GetProfileByName(prof.Name, prof.ProfileType);
            if (rv & (p != null))
            {
                lst.Remove(p);
                // The WinForms dialog wrote the operator's record after every
                // delete; the WPF one never did, so a deleted profile came back
                // on the next launch. (Sprint 32 Track H.)
                PersistOperatorProfiles();
            }
            return rv ? GuardedOutcome.Done : GuardedOutcome.Failed;
        }
        #endregion
    }
}
