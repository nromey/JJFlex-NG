using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// Generates a report of all radio profiles, capturing key properties for each.
    /// Sprint 20: Enhanced with profile comparison (load-snapshot-restore) and meter enumeration.
    /// </summary>
    public class ProfileReporter
    {
        /// <summary>
        /// Snapshot of radio state captured while a profile is loaded.
        /// </summary>
        public class ProfileSnapshot
        {
            public string ProfileName { get; set; } = "";
            public string ProfileType { get; set; } = "";

            // Frequency & Mode
            public string Frequency { get; set; } = "";
            public string Mode { get; set; } = "";
            public int FilterLow { get; set; }
            public int FilterHigh { get; set; }

            // DSP
            public string NeuralNR { get; set; } = "";
            public string SpectralNR { get; set; } = "";
            public string LegacyNR { get; set; } = "";
            public string NoiseBlanker { get; set; } = "";
            public string WidebandNB { get; set; } = "";
            public string AutoNotchFFT { get; set; } = "";
            public string AutoNotchLegacy { get; set; } = "";
            public string APF { get; set; } = "";

            // TX
            public int RFPower { get; set; }
            public int TunePower { get; set; }

            // CW
            public int SidetonePitch { get; set; }
            public int KeyerSpeed { get; set; }

            // AGC
            public string AGCMode { get; set; } = "";
            public int AGCThreshold { get; set; }

            // Audio
            public int AudioGain { get; set; }
            public int AudioPan { get; set; }
            public int HeadphoneGain { get; set; }
            public int LineoutGain { get; set; }
            public bool Muted { get; set; }

            // Receiver
            public int RFGain { get; set; }
            public string Squelch { get; set; } = "";
            public int SquelchLevel { get; set; }

            // VOX
            public string VOX { get; set; } = "";

            /// <summary>
            /// Returns a dictionary of property name → display value for diff comparison.
            /// </summary>
            public Dictionary<string, string> ToDictionary()
            {
                return new Dictionary<string, string>
                {
                    ["Frequency"] = Frequency,
                    ["Mode"] = Mode,
                    ["Filter Low"] = FilterLow.ToString(),
                    ["Filter High"] = FilterHigh.ToString(),
                    ["Neural NR"] = NeuralNR,
                    ["Spectral NR"] = SpectralNR,
                    ["Legacy NR"] = LegacyNR,
                    ["Noise Blanker"] = NoiseBlanker,
                    ["Wideband NB"] = WidebandNB,
                    ["Auto-Notch FFT"] = AutoNotchFFT,
                    ["Auto-Notch Legacy"] = AutoNotchLegacy,
                    ["APF"] = APF,
                    ["RF Power"] = RFPower.ToString(),
                    ["Tune Power"] = TunePower.ToString(),
                    ["Sidetone Pitch"] = SidetonePitch.ToString(),
                    ["Keyer Speed"] = KeyerSpeed.ToString(),
                    ["AGC Mode"] = AGCMode,
                    ["AGC Threshold"] = AGCThreshold.ToString(),
                    ["Audio Gain"] = AudioGain.ToString(),
                    ["Audio Pan"] = AudioPan.ToString(),
                    ["Headphone Gain"] = HeadphoneGain.ToString(),
                    ["Line Out Gain"] = LineoutGain.ToString(),
                    ["Muted"] = Muted ? "Yes" : "No",
                    ["RF Gain"] = RFGain.ToString(),
                    ["Squelch"] = Squelch,
                    ["Squelch Level"] = SquelchLevel.ToString(),
                    ["VOX"] = VOX,
                };
            }
        }

        /// <summary>
        /// Captures the current radio state as a ProfileSnapshot.
        /// Call this while a profile is actively loaded.
        /// </summary>
        public static ProfileSnapshot CaptureCurrentState(FlexBase rig, string profileName, string profileType)
        {
            var snap = new ProfileSnapshot
            {
                ProfileName = profileName,
                ProfileType = profileType
            };

            try
            {
                snap.Frequency = RadioStatusBuilder.FormatFreqDisplay(rig.Frequency);
                snap.Mode = rig.Mode ?? "";
                snap.FilterLow = rig.FilterLow;
                snap.FilterHigh = rig.FilterHigh;

                // DSP
                snap.NeuralNR = rig.NeuralNoiseReduction.ToString();
                snap.SpectralNR = rig.SpectralNoiseReduction.ToString();
                snap.LegacyNR = rig.NoiseReductionLegacy.ToString();
                snap.NoiseBlanker = rig.NoiseBlanker.ToString();
                snap.WidebandNB = rig.WidebandNoiseBlanker.ToString();
                snap.AutoNotchFFT = rig.AutoNotchFFT.ToString();
                snap.AutoNotchLegacy = rig.AutoNotchLegacy.ToString();
                snap.APF = rig.APF.ToString();

                // TX
                snap.RFPower = rig.XmitPower;
                snap.TunePower = rig.TunePower;

                // CW
                snap.SidetonePitch = rig.SidetonePitch;
                snap.KeyerSpeed = rig.KeyerSpeed;

                // AGC
                snap.AGCMode = rig.AGCSpeed.ToString();
                snap.AGCThreshold = rig.AGCThreshold;

                // Audio
                snap.AudioGain = rig.AudioGain;
                snap.AudioPan = rig.AudioPan;
                snap.HeadphoneGain = rig.HeadphoneGain;
                snap.LineoutGain = rig.LineoutGain;
                snap.Muted = rig.SliceMute;

                // Receiver
                snap.RFGain = rig.RFGain;
                snap.Squelch = rig.Squelch.ToString();
                snap.SquelchLevel = rig.SquelchLevel;

                // VOX
                snap.VOX = rig.Vox.ToString();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"ProfileReporter.CaptureCurrentState error: {ex.Message}",
                    TraceLevel.Warning);
            }

            return snap;
        }

        /// <summary>
        /// Spoken-word label for a profile type. ToString() gives "tx", which
        /// a screen reader spells out one letter at a time mid-sentence;
        /// this is the form progress speech and the export file both use.
        /// </summary>
        internal static string TypeLabel(ProfileTypes type) => type switch
        {
            ProfileTypes.global => "global",
            ProfileTypes.tx => "TX",
            ProfileTypes.mic => "mic",
            _ => type.ToString(),
        };

        /// <summary>What one profile load came to, as far as this app can know.
        /// QUEUED, never "sent": SelectProfileGuarded's Done means the command
        /// was queued, and FlexLib's setters can skip the send when their own
        /// cache already holds the name (#469 round six). Used only to decide
        /// whether a walk captured a profile and to word why it did not.</summary>
        internal enum ProfileLoadResult
        {
            /// <summary>Not queued: refused before dispatch (change nothing,
            /// ownership) or failed.</summary>
            NotQueued,
            /// <summary>Queued; FlexLib's field did not read it back in time.</summary>
            QueuedNotSettled,
            /// <summary>Queued; the field read it back and the state settled.
            /// Still not the radio's own report.</summary>
            QueuedAndSettled,
        }

        /// <summary>
        /// Loads a profile by name and waits for the radio to settle. Says
        /// whether the load was QUEUED, which until #469 round five was
        /// discarded: a load refused before dispatch then waited on a field
        /// that never changed and was indistinguishable from a slow radio.
        /// Queued is not sent, and neither is the radio's answer.
        /// </summary>
        /// <remarks>
        /// The confirmation await below is weaker than it looks: FlexLib's
        /// profile-selection setters are optimistic — they store the name and
        /// THEN send the load command — so the await confirms our own write
        /// reached the FlexLib object, not that the radio finished loading.
        /// The real wait is <see cref="WaitForSettle"/>, which polls radio
        /// state until it stops changing. Until 2026-08-30 this was a blind
        /// 500 ms sleep, and a global profile load (which tears down and
        /// rebuilds slices) can still be mid-flight at 500 ms — a snapshot
        /// taken then reads transition state and files it under the profile's
        /// name.
        /// </remarks>
        private static ProfileLoadResult LoadProfileAndWait(FlexBase rig, ProfileTypes profileType, string name, int timeoutMs = 3000)
        {
            Tracing.TraceLine($"ProfileReporter: Loading {profileType} profile '{name}'", TraceLevel.Info);

            var prof = new Profile_t(name, profileType, false);
            var dispatched = rig.SelectProfileGuarded(prof);
            if (dispatched != GuardedOutcome.Done)
            {
                Tracing.TraceLine($"ProfileReporter: {profileType} profile '{name}' was not queued ({dispatched})", TraceLevel.Warning);
                return ProfileLoadResult.NotQueued;
            }

            // Wait for the selection to be accepted (see remarks: this is the
            // request landing, not the load finishing).
            bool settled = FlexBase.await(() =>
            {
                switch (profileType)
                {
                    case ProfileTypes.global:
                        return rig.theRadio.ProfileGlobalSelection == name;
                    case ProfileTypes.tx:
                        return rig.theRadio.ProfileTXSelection == name;
                    case ProfileTypes.mic:
                        return rig.theRadio.ProfileMICSelection == name;
                    default:
                        return true;
                }
            }, timeoutMs);

            if (!settled)
            {
                Tracing.TraceLine($"ProfileReporter: Timed out waiting for {profileType} profile '{name}' to settle", TraceLevel.Warning);
                return ProfileLoadResult.QueuedNotSettled;
            }

            WaitForSettle(rig);
            return ProfileLoadResult.QueuedAndSettled;
        }

        /// <summary>
        /// Wait until the radio's reported state stops changing: poll a
        /// fingerprint of the settings a profile load rewrites, and return
        /// once two consecutive reads agree. Costs the same 500 ms as the old
        /// blind sleep when the radio is already quiet, and keeps waiting —
        /// up to <paramref name="capMs"/> — while a load is still landing.
        /// </summary>
        private static void WaitForSettle(FlexBase rig, int capMs = 4000, int intervalMs = 250)
        {
            string prev = null;
            long deadline = Environment.TickCount64 + capMs;
            while (true)
            {
                Thread.Sleep(intervalMs);
                string fp = StateFingerprint(rig);
                if (fp == prev) return;
                if (Environment.TickCount64 >= deadline)
                {
                    Tracing.TraceLine("ProfileReporter: state still changing at the settle cap — capturing anyway", TraceLevel.Warning);
                    return;
                }
                prev = fp;
            }
        }

        /// <summary>
        /// A cheap digest of the state a profile load rewrites. Any exception
        /// collapses to a constant, so a broken read settles immediately
        /// rather than wedging the walk.
        /// </summary>
        private static string StateFingerprint(FlexBase rig)
        {
            try
            {
                var radio = rig.theRadio;
                if (radio == null) return "";
                var sb = new StringBuilder();
                sb.Append(radio.ProfileTXSelection).Append('|')
                  .Append(radio.ProfileMICSelection).Append('|')
                  .Append(radio.RFPower).Append('|')
                  .Append(radio.MicLevel).Append('|')
                  .Append(radio.TXFilterLow).Append('|')
                  .Append(radio.TXFilterHigh);
                var slices = radio.SliceList;
                if (slices != null)
                {
                    foreach (var s in slices.Where(x => x != null)
                        .OrderBy(x => x.Letter, StringComparer.Ordinal))
                    {
                        sb.Append('|').Append(s.Letter).Append(':')
                          .Append(s.Freq).Append(':').Append(s.DemodMode)
                          .Append(':').Append(s.FilterLow).Append(':').Append(s.FilterHigh);
                    }
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// What a profile walk did to the radio: which profiles were captured,
        /// which could not be read, and — the part <c>CaptureAllProfiles</c>
        /// used to throw away — whether a put-back of the starting selection
        /// was requested. Whether it reached the radio is not known and is not
        /// recorded (#469 round six). Until 2026-08-30 nothing recorded even
        /// the request.
        /// </summary>
        public sealed class ProfileWalkOutcome
        {
            public ProfileTypes ProfileType;

            /// <summary>The reading of the radio's own profile list this walk
            /// ran from (#418), carrying which of three states it ended in:
            /// reported (possibly empty), never reported, or could not ask.
            /// The walk walks NOTHING unless the radio reported names, and
            /// callers render the difference rather than collapsing all three
            /// to an empty walk.</summary>
            public FlexBase.RadioProfileList RadioList;

            /// <summary>The profile loaded when the walk began. Empty when
            /// none was selected; null when the selection could not be read.</summary>
            public string OriginalSelection;

            public List<string> Captured = new List<string>();

            /// <summary>Profiles the walk could not read: name and why.</summary>
            public List<(string Name, string Problem)> Unreadable = new List<(string, string)>();

            /// <summary>The last profile the walk actually loaded — where the
            /// radio is left if the restore was skipped or failed.</summary>
            public string LastLoaded;

            /// <summary>How many load requests actually went out. Zero means
            /// the radio was never asked to move — "unreadable" without a
            /// single request is a different fact from a load that hung.</summary>
            public int LoadAttempts;

            /// <summary>A put-back of the starting selection was REQUESTED.
            /// Whether it reached the radio is not known, and nothing says it
            /// did (#469 round six).</summary>
            public bool RestoreAttempted;


            /// <summary>#469: the radio was keyed, so the walk stopped and did
            /// NOT put the original profile back — never automatically, ruled
            /// by Noel 2026-10-20 ("I'd say what profile radio's on now"). Set
            /// whether the key-up came before a load (see
            /// <see cref="StoppedBefore"/>) or after the last one, and the
            /// caller must not start a walk of a later type.</summary>
            public bool StoppedForTransmit;

            /// <summary>The first profile NOT loaded because the radio was
            /// keyed; null when the walk did not stop, or when it stopped after
            /// its last profile, at the put-back.</summary>
            public string StoppedBefore;

            /// <summary>The last profile the walk REQUESTED a load of, or null.
            /// Evidence for the saved files only, labelled there as "requested":
            /// the request can be refused before it is dispatched (the
            /// change-nothing and ownership gates in SelectProfileGuarded), so
            /// it never means "sent" and is never spoken (#469 round four).</summary>
            public string LastRequested;
        }

        // ── #418: the three-state profile list, and its renderings ──
        //
        // Every surface in this file that names or counts profiles reads the
        // RADIO's list through FlexBase.ReadRadioProfileList, and renders
        // which of three states the reading ended in: the radio reports none;
        // the radio never reported its list; we could not ask. Until
        // 2026-08-30 the counts came from the OPERATOR's profile references
        // and rendered "0" for all three states at once — in the file whose
        // one promise is that a blind spot says so, taken immediately before
        // the factory reset that destroys the originals.

        /// <summary>
        /// Read the radio's profile list for one type, never throwing: a
        /// failure becomes a reading that says it could not ask.
        /// </summary>
        private static FlexBase.RadioProfileList ReadListSafely(FlexBase rig, ProfileTypes type)
        {
            try { return rig.ReadRadioProfileList(type); }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"ProfileReporter: could not read the {TypeLabel(type)} profile list: {ex.Message}",
                    TraceLevel.Warning);
                return new FlexBase.RadioProfileList
                {
                    ProfileType = type,
                    Problem = "reading the profile list failed: " + ex.Message,
                };
            }
        }

        private static string WaitedSecondsPhrase(FlexBase.RadioProfileList list)
            => "waited " + Math.Max(1, list.WaitedMs / 1000) + " seconds";

        /// <summary>
        /// The "N profiles stored" line for the restore-grade capture. Three
        /// states, three visibly different renderings — never a bare "0" that
        /// could mean "none", "not yet", or "never asked" at once.
        /// </summary>
        internal static SettingLine ProfileCountLine(FlexBase.RadioProfileList list)
        {
            string label = TypeLabel(list.ProfileType);
            string key = label + " profiles stored";
            if (list.CouldNotAsk)
                return new SettingLine(key, null, list.Problem);
            if (!list.Reported)
                return new SettingLine(key, null,
                    "the radio never reported its " + label + " profile list ("
                    + WaitedSecondsPhrase(list) + ")");
            string value = list.Names.Count.ToString();
            if (!list.FreshAnswer)
                value += " (the radio did not refresh this list within "
                    + Math.Max(1, list.WaitedMs / 1000)
                    + " seconds; this count is from the list it sent earlier in this session)";
            return new SettingLine(key, value, null);
        }

        /// <summary>
        /// The cross-check the capture must state rather than average away: a
        /// list showing no profiles while the radio names a loaded one is a
        /// contradiction, and at least one profile exists that the capture
        /// cannot see. Null when there is nothing to report.
        /// </summary>
        internal static SettingLine ProfileCrossCheckLine(FlexBase.RadioProfileList list)
        {
            if (!list.SelectionContradictsCount) return null;
            string label = TypeLabel(list.ProfileType);
            return new SettingLine(label + " profiles cross-check",
                "the radio names '" + list.Selection + "' as the loaded " + label
                + " profile, yet its list shows no " + label
                + " profiles; at least one exists that this capture cannot see", null);
        }

        /// <summary>
        /// The restore-grade capture's section for a profile type the walk
        /// could not walk: no radio, no answer, or a genuinely empty list.
        /// The gap gets a section for the same reason an unreadable profile
        /// does — after the reset, "these existed and were not captured" is
        /// the fact that matters most.
        /// </summary>
        internal static void AppendUnwalkedTypeSection(StringBuilder sb, FlexBase.RadioProfileList list)
        {
            string label = TypeLabel(list.ProfileType);
            sb.AppendLine($"[{label} profiles not walked]");
            string reason;
            if (list.CouldNotAsk)
                reason = list.Problem + "; any " + label
                    + " profiles the radio holds are not captured in this file";
            else if (!list.Reported)
                reason = "the radio never reported its " + label + " profile list ("
                    + WaitedSecondsPhrase(list) + "); any " + label
                    + " profiles it holds are not captured in this file";
            else
                reason = "the radio reports no stored " + label + " profiles";
            sb.AppendLine("reason = " + reason);
            var crossCheck = ProfileCrossCheckLine(list);
            if (crossCheck != null)
                sb.AppendLine("contradiction = " + crossCheck.Value);
            sb.AppendLine();
        }

        /// <summary>
        /// THE profile walker — the only one. Loads every profile of the given
        /// type in turn, calls <paramref name="captureWhileLoaded"/> while each
        /// one is on the radio, then requests the starting selection again.
        /// Whether that reached the radio is not known, and nothing here
        /// claims it did. Both reports ride this: the comparison
        /// report captures snapshots, the restore-grade export writes keyed
        /// sections.
        /// </summary>
        public static ProfileWalkOutcome WalkProfiles(
            FlexBase rig, ProfileTypes profileType,
            Action<string> captureWhileLoaded,
            Action<string> progressCallback = null,
            Action<string, string> unreadableCallback = null,
            FlexBase.RadioProfileList radioList = null)
        {
            var outcome = new ProfileWalkOutcome { ProfileType = profileType };
            string label = TypeLabel(profileType);

            // #418: the walk enumerates from the RADIO, never from the
            // operator's profile references — a radio full of profiles its
            // owner never named must still be captured, and this walk exists
            // precisely for the radio about to be wiped. The reading also
            // says which of three states it ended in, and the outcome
            // carries it so every caller can render the difference between
            // "the radio has none" and "we never asked".
            if (radioList == null) radioList = ReadListSafely(rig, profileType);
            outcome.RadioList = radioList;
            if (!radioList.Reported || radioList.Names.Count == 0) return outcome;
            List<string> profiles = radioList.Names;

            // With no radio object there is nothing to load from: every
            // profile is unreadable for the same plain reason, and no load
            // request goes out — so the radio, wherever it is, is not moved.
            bool haveRadio;
            try { haveRadio = rig.theRadio != null; }
            catch { haveRadio = false; }
            if (!haveRadio)
            {
                foreach (var name in profiles)
                {
                    outcome.Unreadable.Add((name, "no radio connection"));
                    unreadableCallback?.Invoke(name, "no radio connection");
                }
                Tracing.TraceLine($"ProfileReporter: {label} walk skipped — no radio connection", TraceLevel.Warning);
                return outcome;
            }

            // Record the currently selected profile so we can restore it.
            //
            // #469 round three: null stays null. FlexLib leaves these fields
            // null until the radio's "current" status arrives, and stores ""
            // only when that status carries an empty value (ParseProfilesStatus
            // splits "current=" into an empty value and UpdateProfileListSelection
            // stores it). Folding null into "" used to turn "not yet reported"
            // into "none was selected", which a stopped walk then said aloud.
            try
            {
                outcome.OriginalSelection = StartingSelection(rig.theRadio, profileType);
            }
            catch (Exception ex)
            {
                outcome.OriginalSelection = null;
                Tracing.TraceLine($"ProfileReporter: could not read the current {label} selection: {ex.Message}", TraceLevel.Warning);
            }

            Tracing.TraceLine($"ProfileReporter: Capturing {profiles.Count} {label} profiles (current: '{outcome.OriginalSelection}')", TraceLevel.Info);

            RunWalk(outcome, profiles,
                name => LoadProfileAndWait(rig, profileType, name),
                () => rig.Transmit,
                captureWhileLoaded, progressCallback, unreadableCallback);

            return outcome;
        }

        /// <summary>
        /// The selection a walk starts from, exactly as FlexLib holds it: null
        /// when the radio has not reported one, "" only when it reported an
        /// empty one, otherwise the name. Never folds null into "" (#469 round
        /// three). Read before any load of this walk is requested, so it is
        /// not this walk's own request echoed back.
        /// </summary>
        internal static string StartingSelection(Radio radio, ProfileTypes type)
        {
            switch (type)
            {
                case ProfileTypes.global: return radio.ProfileGlobalSelection;
                case ProfileTypes.tx: return radio.ProfileTXSelection;
                case ProfileTypes.mic: return radio.ProfileMICSelection;
                default: return null;
            }
        }

        /// <summary>
        /// The walk itself, reaching the radio only through the two
        /// functions passed in, so a test can drive every path — a load that
        /// fails, a key-up before a load, a key-up after the last one, a
        /// starting profile nobody could read — without a radio.
        /// <see cref="WalkProfiles"/> is the only production caller and passes
        /// the real ones.
        /// </summary>
        /// <remarks>
        /// <para><b>#469: transmit is asked before EVERY load, and once more
        /// before the put-back.</b> Both reports used to ask only when they
        /// started, so a walk begun unkeyed kept sending profile loads after
        /// the operator, or another client on the radio, keyed up — and a TX
        /// profile carries power and the rest of the transmit setup.</para>
        /// <para><b>Keyed, the walk stops and puts NOTHING back.</b> Putting the
        /// starting profile back is itself a profile load, so it is never done
        /// while keyed, and never later behind the operator's back either
        /// (round one's wait-for-unkey was removed on Noel's ruling). The walk
        /// speaks one fixed sentence that names no profile and claims nothing
        /// about where the radio was left (round four: nothing here can prove
        /// it), and the callers start no later type.</para>
        /// <para><b>Unkeyed, a put-back of the starting selection is
        /// requested,</b> and nothing records or says that it reached the
        /// radio (round six): queued is not sent, FlexLib's setter can skip
        /// the send, and its selection field is written by that same setter.</para>
        /// </remarks>
        internal static void RunWalk(
            ProfileWalkOutcome outcome, IReadOnlyList<string> profiles,
            Func<string, ProfileLoadResult> load, Func<bool> keyed,
            Action<string> captureWhileLoaded, Action<string> progressCallback,
            Action<string, string> unreadableCallback)
        {
            string label = TypeLabel(outcome.ProfileType);
            int count = 0;
            foreach (var name in profiles)
            {
                if (keyed())
                {
                    outcome.StoppedForTransmit = true;
                    outcome.StoppedBefore = name;
                    Tracing.TraceLine($"ProfileReporter: {label} walk stopped before '{name}' — the radio is transmitting", TraceLevel.Warning);
                    break;
                }

                count++;
                string progressMsg = $"Loading {label} profile {count} of {profiles.Count}: {name}";
                progressCallback?.Invoke(progressMsg);
                Tracing.TraceLine($"ProfileReporter: {progressMsg}", TraceLevel.Info);

                string problem = null;
                try
                {
                    outcome.LoadAttempts++;
                    outcome.LastRequested = name;
                    var result = load(name);
                    if (result == ProfileLoadResult.QueuedAndSettled)
                    {
                        outcome.LastLoaded = name;
                        captureWhileLoaded?.Invoke(name);
                        outcome.Captured.Add(name);
                    }
                    else
                    {
                        problem = result == ProfileLoadResult.NotQueued
                            ? "the load was not queued; it was refused or failed before it reached the radio"
                            : "the radio did not confirm loading this profile in time";
                    }
                }
                catch (Exception ex)
                {
                    // A null reference mid-walk means the radio object went
                    // away under us; the exception's own words are developer
                    // plumbing and this file gets read aloud.
                    problem = ex is NullReferenceException
                        ? "the radio connection went away during the load"
                        : "reading failed: " + ex.Message;
                }

                if (problem != null)
                {
                    Tracing.TraceLine($"ProfileReporter: {label} profile '{name}' not captured — {problem}", TraceLevel.Warning);
                    outcome.Unreadable.Add((name, problem));
                    unreadableCallback?.Invoke(name, problem);
                }
            }

            // Keyed after the last profile is the same case as keyed before
            // one: the put-back is a load, so it is not sent (Codex's round-one
            // blocker 1, where this path once left the radio moved and said
            // nothing). A walk that never loaded did not move the radio.
            if (!outcome.StoppedForTransmit && outcome.LoadAttempts > 0 && keyed())
            {
                outcome.StoppedForTransmit = true;
                Tracing.TraceLine($"ProfileReporter: {label} walk keyed after its last profile — the original is not put back", TraceLevel.Warning);
            }

            if (outcome.StoppedForTransmit)
            {
                Tracing.TraceLine($"ProfileReporter: {label} walk stopped for transmit — last load requested '{outcome.LastRequested ?? "(none)"}', app's last recorded selection before the walk '{outcome.OriginalSelection ?? "(none recorded)"}', nothing put back", TraceLevel.Error);
                progressCallback?.Invoke(TransmitStopSentence());
                return;
            }

            // Request the starting selection again. Whether it reached the
            // radio is not known and is never claimed (#469 round six); the
            // result goes to the trace only.
            if (!string.IsNullOrEmpty(outcome.OriginalSelection))
            {
                outcome.RestoreAttempted = true;
                progressCallback?.Invoke($"Asking the radio to load the {label} profile recorded before the walk: {outcome.OriginalSelection}");
                try
                {
                    var result = load(outcome.OriginalSelection);
                    Tracing.TraceLine($"ProfileReporter: put-back of {label} profile '{outcome.OriginalSelection}' requested — {result}", TraceLevel.Info);
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"ProfileReporter: put-back of {label} profile '{outcome.OriginalSelection}' threw: {ex.Message}", TraceLevel.Warning);
                }
            }
        }

        /// <summary>
        /// What a saved file may say about one kind's starting profile after
        /// an ordinary (unkeyed) walk that requested loads: the app's recorded
        /// selection, labelled for what it is, and whether a put-back was
        /// requested, never whether it reached the radio, because nothing here
        /// can know that (#469 round six). Null when the walk requested no load.
        /// </summary>
        internal static string PutBackNote(ProfileWalkOutcome o)
        {
            if (o.LoadAttempts == 0) return null;
            string kind = TypeLabel(o.ProfileType);
            if (o.OriginalSelection == null)
                return "No " + kind + " selection was recorded before the walk, so no put-back was requested.";
            if (o.OriginalSelection.Length == 0)
                return "The " + kind + " selection recorded before the walk was empty, so no put-back was requested.";
            return "A put-back of " + o.OriginalSelection + ", the app's last recorded " + kind
                + " selection before the walk (it may be the app's own earlier request), was requested;"
                + " whether it reached the radio is not known.";
        }

        /// <summary>
        /// The one sentence spoken when a walk stops because the radio was
        /// keyed. It names no profile and claims nothing about where the radio
        /// was left, because nothing here can prove it: FlexLib's selection
        /// getter is written locally by its own setter before the command goes
        /// out, and a load request can be refused before it is dispatched.
        /// Ruled by Noel, 2026-10-20, after three rounds of trying to say more:
        /// "I'm not sure that this is a hugely necessary step for a case that
        /// will happen so rarely" (#469 round four). The finished sentences end
        /// with the same advice.
        /// </summary>
        public static string TransmitStopSentence()
            => Lexicon.Get("settings.profile_walk.stopped_keyed");

        /// <summary>
        /// The labelled evidence a saved file may carry for a stopped walk:
        /// what the walk requested (never "sent"), and the app's last recorded
        /// selection before it began (never "original": the getter may hold
        /// the app's own earlier request). Lines in "key = value" form.
        /// </summary>
        internal static List<(string Key, string Value)> StoppedWalkEvidence(ProfileWalkOutcome o)
        {
            string kind = TypeLabel(o.ProfileType);
            return new List<(string, string)>
            {
                ("last " + kind + " profile the walk requested",
                    o.LastRequested == null
                        ? "none — no load was requested"
                        : o.LastRequested + " (requested only; it may have been refused before it reached the radio, or not applied)"),
                ("app's last recorded " + kind + " selection before the walk",
                    o.OriginalSelection == null
                        ? "none recorded"
                        : o.OriginalSelection.Length == 0
                            ? "recorded as empty"
                            : o.OriginalSelection + " (may be the app's own earlier request, not the radio's state)"),
            };
        }

        /// <summary>
        /// Captures snapshots for all profiles of the given type by loading each one,
        /// snapshotting the radio state, then restoring the original profile.
        /// <paramref name="walk"/> reports what happened — including whether
        /// the restore was confirmed, which callers must surface.
        /// </summary>
        public static List<ProfileSnapshot> CaptureAllProfiles(
            FlexBase rig, ProfileTypes profileType, out ProfileWalkOutcome walk,
            Action<string> progressCallback = null,
            FlexBase.RadioProfileList radioList = null)
        {
            var snapshots = new List<ProfileSnapshot>();
            walk = WalkProfiles(rig, profileType,
                name => snapshots.Add(CaptureCurrentState(rig, name, profileType.ToString())),
                progressCallback,
                radioList: radioList);
            return snapshots;
        }

        /// <summary>
        /// Why a walking entry point declined to run. The reporter only says
        /// which refusal it was; the sentence comes from
        /// <see cref="ComparisonRefusedSentence"/> or the restore capture's
        /// caller.
        /// </summary>
        public enum WalkRefusal
        {
            /// <summary>It ran.</summary>
            None,
            /// <summary>Another profile walk holds the in-flight flag — the
            /// restore capture, or another comparison report.</summary>
            WalkAlreadyRunning,
            /// <summary>The radio is keyed and THIS run would load profiles,
            /// which change power, filters and antennas. A run that would load
            /// nothing is read-only and is never refused for transmit.</summary>
            Transmitting,
        }

        // ══ #469 repair round one: reserving the walk at the press ══
        //
        // The flag used to be taken only when the reporter ran, which is AFTER
        // the menu's four-second settle. Two presses inside that window both
        // passed the menu's advisory WalkInProgress check, both spoke a start
        // sentence, and then one of them said "busy" — a start announced for
        // a walk that never ran. A press now RESERVES the walk: the flag is
        // taken at once, the second press hears busy and nothing else, and
        // the reservation is handed to the reporter, which runs under it
        // rather than taking the flag a second time. The holder releases it
        // with Dispose on every path, a refusal after the settle included.

        /// <summary>
        /// A held profile-walk flag. Taken by <see cref="TryReserveWalk"/>,
        /// released exactly once by <see cref="Dispose"/>; releasing twice is
        /// harmless and never clears a flag some later walk has taken.
        /// </summary>
        public sealed class WalkReservation : IDisposable
        {
            private int _released;

            internal WalkReservation() { }

            /// <summary>True until <see cref="Dispose"/> has run.</summary>
            public bool IsHeld => Volatile.Read(ref _released) == 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    Interlocked.Exchange(ref _walkInFlight, 0);
            }
        }

        /// <summary>
        /// Take the profile-walk flag now, or return null when another walk
        /// holds it. The caller owns the result and must dispose it on every
        /// path.
        /// </summary>
        public static WalkReservation TryReserveWalk()
        {
            if (Interlocked.CompareExchange(ref _walkInFlight, 1, 0) != 0)
            {
                Tracing.TraceLine("ProfileReporter: walk reservation refused — a profile walk is already running", TraceLevel.Warning);
                return null;
            }
            return new WalkReservation();
        }

        private static void RequireHeld(WalkReservation reservation)
        {
            if (reservation == null || !reservation.IsHeld)
                throw new ArgumentException(
                    "a profile walk must run under a reservation that is still held", nameof(reservation));
        }

        // ══ #469 repair round one: one decider for what the comparison loads ══
        //
        // The start sentence promised "each stored profile is loaded" from a
        // policy check alone, while the report loads only global and TX
        // profiles, only a type with two or more stored, and never mic. The
        // plan below is the single answer: the radio's lists are read ONCE,
        // the policy is asked ONCE, and the start sentence, the transmit
        // refusal and the walk itself all read the same plan, so none of them
        // can say something the others do not do.

        /// <summary>
        /// What one profile walk — the comparison report's or the restore
        /// capture's — will do on this radio, decided before anything is said
        /// or loaded. Each walk has its own maker, because they load different
        /// sets: <see cref="PlanComparison"/> and <see cref="PlanRestoreCapture"/>.
        /// </summary>
        public sealed class ProfileWalkPlan
        {
            /// <summary>Policy: the change-nothing hold is off and the operator
            /// opted this radio in to profile loading (#414, #450). False means
            /// the report leaves the comparison out and says why.</summary>
            public bool ProfilesMayBeLoaded { get; internal set; }

            /// <summary>The radio's own lists, global, TX and mic, read once.
            /// The report prints these, so it cannot disagree with the
            /// plan.</summary>
            public IReadOnlyDictionary<ProfileTypes, FlexBase.RadioProfileList> Lists { get; internal set; }

            /// <summary>The types the walk will actually load, in order. Empty
            /// means nothing on the radio moves.</summary>
            public IReadOnlyList<ProfileTypes> TypesToLoad { get; internal set; }

            public bool WillLoad => TypesToLoad.Count > 0;
        }

        /// <summary>
        /// The policy half: whether this radio permits the comparison to load
        /// profiles at all. Not under the change-nothing hold (#414), and only
        /// on a radio the operator opted in to profile loading (#450). It does
        /// not read the lists; <see cref="PlanComparison"/> does.
        /// </summary>
        public static bool ComparisonMayLoadProfiles(FlexBase rig)
            => !rig.ChangeNothingActive
               && rig.ProfileIntent == ProfileGuestIntent.LoadMineAndPutBack;

        /// <summary>
        /// The types a comparison loads, given the policy answer and the
        /// radio's lists: global, then TX, each only when the radio reported
        /// two or more of it — one profile has nothing to be compared with.
        /// Mic profiles are listed by the report and never loaded by it.
        /// Pure, so a test can hold every case.
        /// </summary>
        internal static IReadOnlyList<ProfileTypes> TypesTheComparisonLoads(
            bool profilesMayBeLoaded,
            IReadOnlyDictionary<ProfileTypes, FlexBase.RadioProfileList> lists)
        {
            var result = new List<ProfileTypes>();
            if (!profilesMayBeLoaded || lists == null) return result;
            foreach (var t in new[] { ProfileTypes.global, ProfileTypes.tx })
            {
                if (lists.TryGetValue(t, out var l) && l != null && l.Reported && l.Names.Count >= 2)
                    result.Add(t);
            }
            return result;
        }

        internal static ProfileWalkPlan MakeComparisonPlan(
            bool profilesMayBeLoaded,
            IReadOnlyDictionary<ProfileTypes, FlexBase.RadioProfileList> lists)
            => new ProfileWalkPlan
            {
                ProfilesMayBeLoaded = profilesMayBeLoaded,
                Lists = lists,
                TypesToLoad = TypesTheComparisonLoads(profilesMayBeLoaded, lists),
            };

        /// <summary>
        /// Read the radio's lists and decide what the comparison will do.
        /// Asks the radio (a bounded wait per list), so never on the UI
        /// thread. Read-only: nothing on the radio changes.
        /// </summary>
        public static ProfileWalkPlan PlanComparison(FlexBase rig)
        {
            var lists = new Dictionary<ProfileTypes, FlexBase.RadioProfileList>();
            foreach (var t in new[] { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic })
                lists[t] = ReadListSafely(rig, t);
            var plan = MakeComparisonPlan(ComparisonMayLoadProfiles(rig), lists);
            Tracing.TraceLine("ProfileReporter: comparison plan — "
                + (plan.WillLoad ? "loads " + string.Join(" then ", plan.TypesToLoad.Select(TypeLabel))
                    : plan.ProfilesMayBeLoaded ? "nothing to compare, loads nothing"
                    : "profile loading not permitted here, loads nothing"), TraceLevel.Info);
            return plan;
        }

        /// <summary>The kinds a plan loads, as the start sentences and the
        /// transmit refusal name them: "global", "global and TX", "global, TX
        /// and mic". The words come from the lexicon; only the list
        /// punctuation is here.</summary>
        internal static string KindsPhrase(IReadOnlyList<ProfileTypes> types)
        {
            var words = types.Select(t => Lexicon.Get(t == ProfileTypes.global
                    ? "settings.profile.report_kinds.global"
                    : t == ProfileTypes.tx
                        ? "settings.profile.report_kinds.tx"
                        : "settings.profile.report_kinds.mic")).ToList();
            if (words.Count <= 1) return words.FirstOrDefault() ?? "";
            string and = Lexicon.Get("settings.profile.report_kinds.and");
            return string.Join(", ", words.Take(words.Count - 1)) + " " + and + " " + words[words.Count - 1];
        }

        /// <summary>
        /// The sentence spoken when the comparison report starts, true to the
        /// plan: it names the kinds that will be loaded, or, when nothing will
        /// be, makes no promise of loading.
        /// </summary>
        public static string ComparisonStartSentence(ProfileWalkPlan plan)
        {
            if (plan.WillLoad)
                return Lexicon.Get("settings.profile.report_start",
                    ("kinds", KindsPhrase(plan.TypesToLoad)));
            if (!plan.ProfilesMayBeLoaded)
                return Lexicon.Get("settings.profile.report_start_no_walk");
            // #469 round two (Codex's blocker 2): a list the radio never
            // reported, or that could not be asked for, is NOT an empty one.
            // "Nothing to compare" is said only when both lists arrived and
            // neither holds two; otherwise the sentence says which list did
            // not arrive.
            var missing = new[] { ProfileTypes.global, ProfileTypes.tx }
                .Where(t => plan.Lists == null || !plan.Lists.TryGetValue(t, out var l) || l == null || !l.Reported)
                .ToList();
            if (missing.Count == 0)
                return Lexicon.Get("settings.profile.report_start_nothing_to_compare");
            return Lexicon.Get(missing.Count == 1
                    ? "settings.profile.report_start_list_missing"
                    : "settings.profile.report_start_lists_missing",
                ("kinds", KindsPhrase(missing)));
        }

        /// <summary>
        /// The transmit refusal for this plan, or null when there is nothing
        /// to refuse: unkeyed, or keyed on a plan that loads nothing, which is
        /// a read-only report and runs. When it refuses, its reason names what
        /// the run would actually have loaded.
        /// </summary>
        public static string ComparisonTransmitRefusal(ProfileWalkPlan plan, bool keyed)
        {
            if (!keyed || !plan.WillLoad) return null;
            return Lexicon.Get("settings.profile.report_not_while_transmitting",
                ("kinds", KindsPhrase(plan.TypesToLoad)));
        }

        /// <summary>
        /// The types the restore capture loads: global, TX and mic, each
        /// whenever the radio reported at least one stored — the capture's job
        /// is every stored profile, so a single one is walked — and none under
        /// the change-nothing hold. Pure, so a test can hold every case.
        /// </summary>
        internal static IReadOnlyList<ProfileTypes> TypesTheRestoreCaptureLoads(
            bool walkPermitted,
            IReadOnlyDictionary<ProfileTypes, FlexBase.RadioProfileList> lists)
        {
            var result = new List<ProfileTypes>();
            if (!walkPermitted || lists == null) return result;
            foreach (var t in new[] { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic })
            {
                if (lists.TryGetValue(t, out var l) && l != null && l.Reported && l.Names.Count >= 1)
                    result.Add(t);
            }
            return result;
        }

        internal static ProfileWalkPlan MakeRestorePlan(
            bool walkPermitted,
            IReadOnlyDictionary<ProfileTypes, FlexBase.RadioProfileList> lists)
            => new ProfileWalkPlan
            {
                ProfilesMayBeLoaded = walkPermitted,
                Lists = lists,
                TypesToLoad = TypesTheRestoreCaptureLoads(walkPermitted, lists),
            };

        /// <summary>
        /// Read the radio's lists and decide what the restore capture will
        /// load (#469, the same class as the comparison's start sentence: the
        /// capture said "every stored profile is loaded" whatever would load).
        /// The capture's file prints these same lists, so the file, the start
        /// sentence and the transmit refusal cannot disagree. Asks the radio,
        /// so never on the UI thread; read-only.
        /// </summary>
        public static ProfileWalkPlan PlanRestoreCapture(FlexBase rig)
        {
            var lists = new Dictionary<ProfileTypes, FlexBase.RadioProfileList>();
            foreach (var t in new[] { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic })
                lists[t] = ReadListSafely(rig, t);
            var plan = MakeRestorePlan(RestoreCaptureWouldWalk(rig), lists);
            Tracing.TraceLine("RestoreGradeExport: plan — "
                + (plan.WillLoad ? "loads " + string.Join(" then ", plan.TypesToLoad.Select(TypeLabel))
                    : plan.ProfilesMayBeLoaded ? "no stored profile could be read, loads nothing"
                    : "change nothing is on, loads nothing"), TraceLevel.Info);
            return plan;
        }

        /// <summary>
        /// The restore capture's start sentence, true to its plan: it names
        /// the kinds that will be loaded, or, when nothing will be, makes no
        /// promise of loading.
        /// </summary>
        public static string RestoreStartSentence(ProfileWalkPlan plan)
        {
            if (plan.WillLoad)
                return Lexicon.Get("settings.restore_export.start_walk",
                    ("kinds", KindsPhrase(plan.TypesToLoad)));
            return plan.ProfilesMayBeLoaded
                ? Lexicon.Get("settings.restore_export.start_nothing_to_walk")
                : Lexicon.Get("settings.restore_export.start_no_walk");
        }

        /// <summary>The restore capture's transmit refusal for this plan, or
        /// null: unkeyed, or keyed on a plan that loads nothing, which is a
        /// read-only capture and runs. When it refuses, it names the kinds the
        /// plan would load and nothing more (#469 round two, Codex's blocker
        /// 3: the legacy sentence said "every stored profile" even when some
        /// lists had not arrived).</summary>
        public static string RestoreTransmitRefusal(ProfileWalkPlan plan, bool keyed)
            => keyed && plan.WillLoad
                ? Lexicon.Get("settings.restore_export.not_while_transmitting_kinds",
                    ("kinds", KindsPhrase(plan.TypesToLoad)))
                : null;

        /// <summary>The sentence for a refused restore capture after the
        /// settle, true to why it refused: the plan-specific transmit refusal,
        /// never the legacy "every stored profile" (#469 round three).</summary>
        public static string RestoreRefusedSentence(RestoreGradeExport export, ProfileWalkPlan plan)
            => export.Refused == WalkRefusal.Transmitting
                ? RestoreTransmitRefusal(plan, keyed: true)
                : Lexicon.Get("settings.restore_export.busy");

        /// <summary>What one comparison run produced.</summary>
        public sealed class ComparisonReport
        {
            /// <summary>The report text; null when it refused.</summary>
            public string Text;
            public WalkRefusal Refused;
            /// <summary>The radio was keyed during the walk, so it stopped and
            /// put nothing back.</summary>
            public bool StoppedForTransmit;
            /// <summary>Some walk requested at least one profile load. That is
            /// the whole test for the pointer sentence (#469 round six).</summary>
            public bool AnyLoadRequested;
        }

        /// <summary>The sentence for a refused run, true to why it refused.</summary>
        public static string ComparisonRefusedSentence(ComparisonReport report, ProfileWalkPlan plan)
            => report.Refused == WalkRefusal.Transmitting
                ? ComparisonTransmitRefusal(plan, keyed: true)
                : Lexicon.Get("settings.restore_export.busy");

        /// <summary>
        /// The sentence that answers the press when the report is saved. A run
        /// that requested any profile load ends with the manual route, and
        /// claims nothing about what reached the radio (#469 round six). After a transmit stop it says the
        /// comparison may be incomplete and gives the same manual route as the
        /// stop line, naming no profile and claiming nothing about where the
        /// radio was left (#469 round four).
        /// </summary>
        public static string ComparisonFinishedSentence(ComparisonReport report, string path)
        {
            if (!report.StoppedForTransmit)
            {
                if (!report.AnyLoadRequested)
                    return Lexicon.Get("settings.profile.report_saved", ("path", path));
                return Lexicon.Get("settings.profile.report_saved_loaded", ("path", path));
            }
            return Lexicon.Get("settings.profile.report_saved_stopped", ("path", path));
        }

        /// <summary>
        /// Generate a full report including profile comparisons and meter enumeration.
        /// Sprint 20: Enhanced report with load-snapshot profile comparison and meter listing.
        /// Reserves the walk itself and plans on the spot; returns null when it
        /// refused, and the overload with an <c>out</c> parameter says why.
        /// The menu uses the reservation overload instead, so it can reserve at
        /// the press and plan before it speaks.
        /// </summary>
        public static string GenerateReport(FlexBase rig, Action<string> progressCallback = null)
            => GenerateReport(rig, progressCallback, out _);

        /// <summary>
        /// <see cref="GenerateReport(FlexBase, Action{string})"/>, saying why
        /// it refused when it returns null.
        /// </summary>
        public static string GenerateReport(FlexBase rig, Action<string> progressCallback,
            out WalkRefusal refusal)
        {
            using (var reservation = TryReserveWalk())
            {
                if (reservation == null)
                {
                    refusal = WalkRefusal.WalkAlreadyRunning;
                    return null;
                }
                var report = GenerateReport(reservation, PlanComparison(rig), rig, progressCallback);
                refusal = report.Refused;
                return report.Text;
            }
        }

        /// <summary>
        /// Run the comparison report under a walk the caller has already
        /// reserved, doing exactly what <paramref name="plan"/> says. The
        /// caller still owns the reservation and releases it.
        /// </summary>
        /// <remarks>
        /// #469: this report walks profiles exactly as the restore capture
        /// does, so it runs under the SAME in-flight flag for its whole run.
        /// Until #469 it only CHECKED the flag (at the menu) and never set it,
        /// so a restore capture started during a comparison report, or a
        /// second report, sailed through the guard that exists to stop two
        /// walks restoring each other's wrong state. The flag covers the
        /// read-only sections too: they read the profile selection a walk
        /// moves. Keyed transmit is asked again here, after the menu's settle,
        /// and only when the plan loads something; a read-only report runs
        /// keyed, because nothing in it changes the radio.
        /// </remarks>
        public static ComparisonReport GenerateReport(
            WalkReservation reservation, ProfileWalkPlan plan, FlexBase rig,
            Action<string> progressCallback)
        {
            RequireHeld(reservation);
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            WalkHeldProbe?.Invoke("comparison report");

            if (plan.WillLoad && rig.Transmit)
            {
                Tracing.TraceLine("ProfileReporter: comparison report refused — the radio is transmitting and this run would load profiles", TraceLevel.Warning);
                return new ComparisonReport { Refused = WalkRefusal.Transmitting };
            }

            var result = new ComparisonReport();
            result.Text = GenerateReportCore(rig, plan, progressCallback, result);
            return result;
        }

        private static string GenerateReportCore(FlexBase rig, ProfileWalkPlan plan,
            Action<string> progressCallback, ComparisonReport result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"JJFlexRadio Profile Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Radio: {rig.RadioNickname} ({rig.RadioModel})");
            sb.AppendLine(new string('=', 60));

            // List all profiles by type — the RADIO's own lists (#418), each
            // with its three-state reading, as the plan read them. A type
            // whose list could not be read still gets its heading: a section
            // silently missing is the gap this report exists to prevent.
            var types = new[] {
                ProfileTypes.global,
                ProfileTypes.tx,
                ProfileTypes.mic
            };

            var readings = new Dictionary<ProfileTypes, FlexBase.RadioProfileList>();
            foreach (var ptype in types)
            {
                var list = plan.Lists != null && plan.Lists.TryGetValue(ptype, out var planned) && planned != null
                    ? planned
                    : ReadListSafely(rig, ptype);
                readings[ptype] = list;

                sb.AppendLine();
                sb.AppendLine($"--- {ptype.ToString().ToUpperInvariant()} Profiles (on the radio) ---");
                if (list.CouldNotAsk)
                    sb.AppendLine($"  Could not be read: {list.Problem}.");
                else if (!list.Reported)
                    sb.AppendLine($"  The radio never reported this list ({WaitedSecondsPhrase(list)}).");
                else if (list.Names.Count == 0)
                    sb.AppendLine("  The radio reports none stored.");
                else
                    foreach (var name in list.Names)
                    {
                        string suffix = name == list.Selection ? " (loaded now)" : "";
                        sb.AppendLine($"  {name}{suffix}");
                    }
                if (list.SelectionContradictsCount)
                    sb.AppendLine($"  Caution: the radio names '{list.Selection}' as loaded,"
                        + " so at least one exists that this list does not show.");
            }

            // What a profile actually holds, and what it does not (#225).
            //
            // The honest limitation, stated where somebody will read it rather
            // than only in a design document. An operator putting a radio back
            // — their own or somebody else's — needs to know that a profile
            // restores what a profile stores, and that the radio keeps a whole
            // class of setting outside profile scope which nothing here puts
            // back. Both halves are listed, because "what is covered" without
            // "what is not" reads as a promise.
            //
            // The list lives in ProfileStewardship.RestorePointCoverage() so
            // the report, the restore offer and the help cannot drift into
            // three different answers.
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("WHAT A PROFILE HOLDS");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("Loading a profile puts these back:");
            foreach (var entry in ProfileStewardship.RestorePointCoverage()
                                                    .Where(c => c.CoveredByAProfile))
            {
                sb.AppendLine("  " + entry.What
                    + (string.IsNullOrEmpty(entry.Note) ? "" : " - " + entry.Note));
            }
            sb.AppendLine();
            sb.AppendLine("It does NOT put these back. The radio keeps them whatever");
            sb.AppendLine("profile is loaded:");
            foreach (var entry in ProfileStewardship.RestorePointCoverage()
                                                    .Where(c => !c.CoveredByAProfile))
            {
                sb.AppendLine("  " + entry.What
                    + (string.IsNullOrEmpty(entry.Note) ? "" : " - " + entry.Note));
            }

            // Capture current state
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("CURRENT RADIO STATE");
            sb.AppendLine(new string('=', 60));

            var currentSnap = CaptureCurrentState(rig, "Current", "Active");
            FormatSnapshot(sb, currentSnap);

            // Profile comparison: load each profile and capture its state.
            //
            // NOT under the change-nothing hold. This pass looks like an
            // inspection and is not one — it walks the whole station through
            // every stored profile, visibly to every connected client, and
            // its restore is best-effort (#414). On a guarded radio the
            // read-only parts above still make a useful report; the report
            // says what was left out and why, so a shorter report reads as
            // the setting working rather than the feature breaking.
            // #414: A REPORT MUST NOT MUTATE, and this one did. Two refusals
            // now, and the second is the one the ruling added.
            //
            // The hold has always been checked here. What was not is the
            // per-radio profile answer (#450): on a radio the operator has
            // never opted in, walking every stored profile in turn is exactly
            // the write the opt-in exists to prevent, dressed as an
            // inspection. Worse than a plain write, because the verb is
            // "report" — nobody pressing it expects the radio to change, and
            // the walk's restore is best-effort by construction.
            //
            // The read-only sections above still run, so a shorter report
            // reads as the setting working rather than the feature breaking.
            if (!plan.ProfilesMayBeLoaded)
            {
                sb.AppendLine();
                sb.AppendLine(new string('=', 60));
                sb.AppendLine("PROFILE COMPARISON SKIPPED");
                sb.AppendLine(new string('=', 60));
                if (rig.ChangeNothingActive)
                {
                    sb.AppendLine("Change nothing is on for this radio. Comparing profiles");
                    sb.AppendLine("means loading each one on the radio in turn, so that part");
                    sb.AppendLine("of the report was left out. The setting is in Settings,");
                    sb.AppendLine("under Radios.");
                }
                else
                {
                    sb.AppendLine("Comparing profiles means loading each one on the radio in");
                    sb.AppendLine("turn, which changes what the radio is set to while it runs.");
                    sb.AppendLine("JJ Flexible has not been told it may load profiles on this");
                    sb.AppendLine("radio, so that part of the report was left out. Everything");
                    sb.AppendLine("above was read without changing anything. The answer is in");
                    sb.AppendLine("the Radio menu, under Load My Profiles on This Radio.");
                }
                Tracing.TraceLine(
                    "ProfileReporter: comparison pass skipped — "
                    + (rig.ChangeNothingActive
                        ? "change nothing is on for this radio"
                        : "this radio has not been opted in to profile loading"),
                    TraceLevel.Warning);
            }
            else
            // The plan's types and nothing else (#469): the start sentence
            // named exactly these, so the walk loads exactly these.
            foreach (var ptype in plan.TypesToLoad)
            {
                var list = readings[ptype];

                // Traced, not spoken (#469): the walker's first "Loading ...
                // profile 1 of N" line follows at once and would interrupt it,
                // so the first spoken progress is the first thing that takes
                // time — the restore capture's rule.
                Tracing.TraceLine($"ProfileReporter: comparing {TypeLabel(ptype)} profiles", TraceLevel.Info);
                var snapshots = CaptureAllProfiles(rig, ptype, out var walk, progressCallback, list);
                if (walk.LoadAttempts > 0) result.AnyLoadRequested = true;
                if (snapshots.Count >= 2)
                {
                    sb.AppendLine();
                    sb.AppendLine(new string('=', 60));
                    sb.AppendLine($"{ptype.ToString().ToUpperInvariant()} PROFILE COMPARISON");
                    sb.AppendLine(new string('=', 60));

                    FormatProfileComparison(sb, snapshots);
                }

                // What the app requested for the starting profile, and nothing
                // about whether it reached the radio (#469 round six).
                if (!walk.StoppedForTransmit) AppendComparisonPutBack(sb, walk);

                // #469: keyed part-way. The walk stopped loading, and no later
                // type is started; the report says where it stopped, and names
                // no profile as where the radio was left.
                if (walk.StoppedForTransmit)
                {
                    result.StoppedForTransmit = true;
                    AppendComparisonStopped(sb, walk);
                    break;
                }
            }

            // Meter enumeration. Instant, so nothing is spoken for it: the
            // next thing the operator hears is the saved path (#469).
            FormatMeterSection(sb, rig);

            return sb.ToString();
        }

        /// <summary>
        /// The comparison report's note on one kind's starting profile after
        /// an ordinary (unkeyed) walk that requested loads: <see cref="PutBackNote"/>
        /// and the manual route. Nothing when the walk requested no load.
        /// </summary>
        internal static void AppendComparisonPutBack(StringBuilder sb, ProfileWalkOutcome walk)
        {
            string note = PutBackNote(walk);
            if (note == null) return;
            sb.AppendLine();
            sb.AppendLine(note);
            sb.AppendLine("Your profile may not be the one you started with; load it again from Profiles, in the Radio menu, if needed.");
        }

        /// <summary>
        /// The comparison report's section for a walk stopped by a key-up
        /// (#469): what was not compared, and the labelled evidence. It does
        /// not say where the radio was left, because that is not known.
        /// </summary>
        internal static void AppendComparisonStopped(StringBuilder sb, ProfileWalkOutcome walk)
        {
            string label = TypeLabel(walk.ProfileType);
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("PROFILE LOADING STOPPED");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("The radio was keyed during the comparison, so no more profiles");
            sb.AppendLine("were requested and nothing was put back while transmitting.");
            sb.AppendLine(walk.StoppedBefore != null
                ? $"Loading stopped before {label} profile '{walk.StoppedBefore}' was requested; that profile and every one after it are not compared here."
                : $"The radio was keyed after the last {label} profile was requested, so nothing was put back.");
            // Evidence, each value labelled for what it is (#469 round four):
            // which profile the radio is on is NOT known, and is not claimed.
            foreach (var (key, value) in StoppedWalkEvidence(walk))
                sb.AppendLine(char.ToUpperInvariant(key[0]) + key.Substring(1) + ": " + value + ".");
            sb.AppendLine("Which profile the radio is on now is not known.");
            sb.AppendLine(TransmitStopSentence());
        }

        /// <summary>
        /// Format a snapshot into readable text.
        /// </summary>
        private static void FormatSnapshot(StringBuilder sb, ProfileSnapshot snap)
        {
            sb.AppendLine();
            sb.AppendLine($"Frequency & Mode");
            sb.AppendLine($"  Frequency:    {snap.Frequency}");
            sb.AppendLine($"  Mode:         {snap.Mode}");
            sb.AppendLine($"  Filter:       {snap.FilterLow} to {snap.FilterHigh} Hz");

            sb.AppendLine();
            sb.AppendLine($"DSP");
            sb.AppendLine($"  Neural NR:    {snap.NeuralNR}");
            sb.AppendLine($"  Spectral NR:  {snap.SpectralNR}");
            sb.AppendLine($"  Legacy NR:    {snap.LegacyNR}");
            sb.AppendLine($"  Noise Blank:  {snap.NoiseBlanker}");
            sb.AppendLine($"  Wideband NB:  {snap.WidebandNB}");
            sb.AppendLine($"  Auto-Notch:   {snap.AutoNotchFFT}");
            sb.AppendLine($"  Legacy ANF:   {snap.AutoNotchLegacy}");
            sb.AppendLine($"  APF:          {snap.APF}");

            sb.AppendLine();
            sb.AppendLine($"Transmission");
            sb.AppendLine($"  RF Power:     {snap.RFPower}");
            sb.AppendLine($"  Tune Power:   {snap.TunePower}");
            sb.AppendLine($"  VOX:          {snap.VOX}");

            sb.AppendLine();
            sb.AppendLine($"CW");
            sb.AppendLine($"  Pitch:        {snap.SidetonePitch} Hz");
            sb.AppendLine($"  Keyer Speed:  {snap.KeyerSpeed} WPM");

            sb.AppendLine();
            sb.AppendLine($"AGC");
            sb.AppendLine($"  Mode:         {snap.AGCMode}");
            sb.AppendLine($"  Threshold:    {snap.AGCThreshold}");

            sb.AppendLine();
            sb.AppendLine($"Audio");
            sb.AppendLine($"  Gain:         {snap.AudioGain}");
            sb.AppendLine($"  Pan:          {snap.AudioPan}");
            sb.AppendLine($"  Headphone:    {snap.HeadphoneGain}");
            sb.AppendLine($"  Line Out:     {snap.LineoutGain}");
            sb.AppendLine($"  Muted:        {(snap.Muted ? "Yes" : "No")}");

            sb.AppendLine();
            sb.AppendLine($"Receiver");
            sb.AppendLine($"  RF Gain:      {snap.RFGain}");
            sb.AppendLine($"  Squelch:      {snap.Squelch}");
            sb.AppendLine($"  Squelch Lvl:  {snap.SquelchLevel}");
        }

        /// <summary>
        /// Formats a comparison of multiple profile snapshots, showing differences.
        /// The first snapshot is treated as the baseline; subsequent profiles show only diffs.
        /// </summary>
        private static void FormatProfileComparison(StringBuilder sb, List<ProfileSnapshot> snapshots)
        {
            if (snapshots.Count == 0) return;

            var baseline = snapshots[0];
            var baseDict = baseline.ToDictionary();

            // Show baseline profile in full
            sb.AppendLine();
            sb.AppendLine($"--- {baseline.ProfileName} (baseline) ---");
            FormatSnapshot(sb, baseline);

            // Show each subsequent profile as diffs from baseline
            for (int i = 1; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var snapDict = snap.ToDictionary();

                sb.AppendLine();
                sb.AppendLine($"--- {snap.ProfileName} ---");

                var diffs = new List<(string Name, string BaseVal, string ThisVal)>();
                foreach (var kvp in snapDict)
                {
                    if (baseDict.TryGetValue(kvp.Key, out var baseVal) && baseVal != kvp.Value)
                    {
                        diffs.Add((kvp.Key, baseVal, kvp.Value));
                    }
                }

                if (diffs.Count == 0)
                {
                    sb.AppendLine("  (identical to baseline)");
                }
                else
                {
                    sb.AppendLine($"  Differences from {baseline.ProfileName}:");
                    // Find max key length for alignment
                    int maxKeyLen = diffs.Max(d => d.Name.Length);
                    foreach (var (name, baseVal, thisVal) in diffs)
                    {
                        sb.AppendLine($"    {name.PadRight(maxKeyLen)}  {thisVal,-15} (was {baseVal})");
                    }
                }
            }
        }

        /// <summary>
        /// Formats the meter enumeration section of the report.
        /// </summary>
        private static void FormatMeterSection(StringBuilder sb, FlexBase rig)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 60));
            sb.AppendLine("AVAILABLE METERS");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();

            var meters = rig.GetAllMeters();
            if (meters == null || meters.Count == 0)
            {
                sb.AppendLine("  No meters available.");
                return;
            }

            // Sort by source then name for readability
            var sorted = meters
                .Where(m => m.Name != null)
                .OrderBy(m => m.Source ?? "")
                .ThenBy(m => m.Name)
                .ToList();

            // Header
            sb.AppendLine($"{"Name",-20} {"Source",-6} {"Index",5}  {"Units",-10} {"Low",8} {"High",8} {"FPS",5}");
            sb.AppendLine($"{new string('-', 20)} {new string('-', 6)} {new string('-', 5)}  {new string('-', 10)} {new string('-', 8)} {new string('-', 8)} {new string('-', 5)}");

            foreach (var m in sorted)
            {
                string low = m.Low == double.MaxValue ? "n/a" : m.Low.ToString("F1");
                string high = m.High == double.MinValue ? "n/a" : m.High.ToString("F1");
                string fps = m.FPS == double.MinValue ? "n/a" : m.FPS.ToString("F0");

                sb.AppendLine($"{(m.Name ?? "?"),-20} {(m.Source ?? "?"),-6} {m.SourceIndex,5}  {m.Units,-10} {low,8} {high,8} {fps,5}");
            }

            sb.AppendLine();
            sb.AppendLine($"Total: {sorted.Count} meters available");
        }

        /// <summary>
        /// Save report to the standard reports directory.
        /// </summary>
        public static string SaveReport(string report)
        {
            var dir = Path.Combine(
                RadioConfig.AppDataRoot, "profile-reports");
            Directory.CreateDirectory(dir);

            var filename = $"profile-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, report);
            return path;
        }

        // ══ The station settings export (Sprint 35 Track I, #227) ══
        //
        // Before a factory reset, write everything the radio holds out as
        // PLAIN TEXT — because the reset takes it all, and a blind operator
        // has none of the sighted workarounds ("photograph the settings
        // screens", "write it down off the front panel"). The requirements,
        // from the task and in order of importance:
        //
        //   - Readable WITHOUT this app. If the reset goes badly, a file only
        //     our software can open is worthless at the exact moment it is
        //     needed. Hence text, in Documents, not a blob in AppData.
        //   - Readable by a screen reader straight through, in order. No
        //     tables, no ASCII art — labelled lines, grouped under headings.
        //   - Readable by SOMEBODY ELSE: a sighted helper at the radio site
        //     can have this read to them down a phone.
        //   - Copyable by hand. Restoration is assumed manual; an automated
        //     restore is a later, separate task and its absence must not
        //     block this.
        //
        // READ-ONLY, deliberately, which makes it different from
        // GenerateReport above: that one LOADS each profile to compare them,
        // changing live radio state while it runs. This one touches nothing,
        // so it is safe to run any time — and worth running routinely, since
        // an export taken while everything works is the baseline for later.
        //
        // WHERE A THING CANNOT BE CAPTURED, THE FILE SAYS SO. An export that
        // silently omits what it could not take is worse than one that lists
        // the gaps, because the operator finds out after the reset, when it
        // is unrecoverable. The gaps section at the end is load-bearing.
        //
        // Note on scope: FlexLib exposes NO factory reset command (checked
        // 2026-08-25 — the nearest thing, `client start_persistence off`, is
        // a different mechanism entirely). So this app's whole role is to
        // PREPARE for a reset somebody performs by other means, and this
        // export is that preparation.

        /// <summary>
        /// Build the plain-text station settings export. Read-only: no
        /// profile is loaded, nothing is written to the radio. Each section
        /// that fails to read reports its failure IN the text rather than
        /// vanishing.
        /// </summary>
        public static string GenerateStationSettingsExport(FlexBase rig)
        {
            var sb = new StringBuilder();
            var radio = rig.theRadio;

            sb.AppendLine("JJ Flexible Radio Settings Export");
            sb.AppendLine("=================================");
            sb.AppendLine();
            sb.AppendLine("What this file is: a plain-text record of everything JJ Flexible");
            sb.AppendLine("can read from this radio, taken so the radio's setup can be put");
            sb.AppendLine("back by hand — after a factory reset, a failed update, or on a");
            sb.AppendLine("replacement radio. Keep it somewhere safe. It is written to be");
            sb.AppendLine("read straight through with a screen reader, or aloud over the");
            sb.AppendLine("phone to someone at the radio.");
            sb.AppendLine();
            sb.AppendLine($"Taken: {DateTime.Now:yyyy-MM-dd HH:mm} (local time)");
            sb.AppendLine();

            Section(sb, "The radio", () =>
            {
                sb.AppendLine($"Model: {radio?.Model ?? rig.RadioModel ?? "unknown"}");
                sb.AppendLine($"Name: {radio?.Nickname ?? rig.RadioNickname ?? "unknown"}");
                sb.AppendLine($"Serial: {radio?.Serial ?? "unknown"}");
                string callsign = radio?.Callsign;
                if (!string.IsNullOrEmpty(callsign)) sb.AppendLine($"Callsign: {callsign}");
                string versions = radio?.Versions;
                if (!string.IsNullOrEmpty(versions))
                    sb.AppendLine($"Firmware and component versions: {versions}");
                var ip = radio?.IP;
                if (ip != null) sb.AppendLine($"Network address: {ip}");
                var staticIp = rig.CurrentStaticIP;
                sb.AppendLine(staticIp != null
                    ? $"Addressing: static IP {staticIp}"
                    : "Addressing: automatic (DHCP)");
                if (radio != null)
                    sb.AppendLine("Remote power on (REM ON): "
                        + (radio.RemoteOnEnabled ? "enabled" : "disabled"));
            });

            Section(sb, "Profiles stored on the radio", () =>
            {
                sb.AppendLine("A factory reset erases these. Only their NAMES can be exported —");
                sb.AppendLine("the radio offers no way to read a profile's contents without");
                sb.AppendLine("loading it — so the settings later in this file are the ones live");
                sb.AppendLine("right now, under the profiles currently loaded.");
                sb.AppendLine();

                AppendProfileNames(sb, ReadListSafely(rig, ProfileTypes.global), "Global profiles");
                AppendProfileNames(sb, ReadListSafely(rig, ProfileTypes.tx), "TX profiles");
                AppendProfileNames(sb, ReadListSafely(rig, ProfileTypes.mic), "Mic profiles");

                sb.AppendLine();
                sb.AppendLine("For a machine-restorable copy of every profile's contents, also");
                sb.AppendLine("run Tools, then Export Profiles: that produces the radio's own");
                sb.AppendLine("archive, which can be imported back after the reset — but only");
                sb.AppendLine("through SmartSDR or JJ Flexible, and only if the radio comes back");
                sb.AppendLine("healthy. This text file is the copy a person can always read.");
            });

            Section(sb, "Slices", () =>
            {
                var slices = radio?.SliceList?.Where(s => s != null)
                    .OrderBy(s => s.Letter, StringComparer.Ordinal).ToList();
                if (slices == null || slices.Count == 0)
                {
                    sb.AppendLine("No slices are open.");
                    return;
                }
                foreach (var s in slices)
                {
                    var flags = new List<string>();
                    if (s.Active) flags.Add("active");
                    if (s.IsTransmitSlice) flags.Add("transmit");
                    string station = null;
                    try { station = radio.FindGUIClientByClientHandle(s.ClientHandle)?.Station; }
                    catch { }
                    if (!string.IsNullOrEmpty(station)) flags.Add("station " + station);
                    string flagText = flags.Count > 0 ? " (" + string.Join(", ", flags) + ")" : "";

                    ulong hz = (ulong)(s.Freq * 1_000_000d);
                    sb.AppendLine($"Slice {s.Letter}{flagText}: "
                        + $"{RadioStatusBuilder.FormatFreqDisplay(hz)} {s.DemodMode}, "
                        + $"filter {s.FilterLow} to {s.FilterHigh} hertz, "
                        + $"RX antenna {s.RXAnt}, TX antenna {s.TXAnt}, "
                        + $"AGC {s.AGCMode} at threshold {s.AGCThreshold}, "
                        + (s.Mute ? "muted" : "not muted") + ".");
                }
            });

            Section(sb, "Transmit settings", () =>
            {
                if (radio == null) { sb.AppendLine("Not readable: no radio."); return; }
                sb.AppendLine($"RF power: {radio.RFPower} watts");
                sb.AppendLine($"Tune power: {radio.TunePower} watts");
                sb.AppendLine($"Mic gain: {radio.MicLevel}");
                sb.AppendLine($"Mic boost: {OnOff(radio.MicBoost)}");
                sb.AppendLine($"Mic bias: {OnOff(radio.MicBias)}");
                sb.AppendLine($"Speech processor: {OnOff(radio.SpeechProcessorEnable)}"
                    + $", level setting {(FlexBase.ProcessorSettings)radio.SpeechProcessorLevel}");
                sb.AppendLine($"Compander: {OnOff(radio.CompanderOn)} at level {radio.CompanderLevel}");
                sb.AppendLine($"TX filter: {radio.TXFilterLow} to {radio.TXFilterHigh} hertz");
                sb.AppendLine($"Transmit monitor: {OnOff(radio.TXMonitor)}"
                    + $", sideband level {radio.TXSBMonitorGain}, CW level {radio.TXCWMonitorGain}");
                sb.AppendLine($"VOX: {OnOff(radio.SimpleVOXEnable)}"
                    + $", gain {radio.SimpleVOXLevel}, delay {radio.SimpleVOXDelay * FlexBase.VoxDelayMS} milliseconds");
            });

            Section(sb, "CW settings", () =>
            {
                if (radio == null) { sb.AppendLine("Not readable: no radio."); return; }
                sb.AppendLine($"Keyer speed: {radio.CWSpeed} words per minute");
                sb.AppendLine($"Sidetone pitch: {radio.CWPitch} hertz");
                sb.AppendLine($"Sidetone: {OnOff(radio.CWSidetone)}");
                sb.AppendLine($"Break-in: {OnOff(radio.CWBreakIn)}"
                    + $", delay {radio.CWDelay} milliseconds");
                sb.AppendLine("Iambic keyer: " + OnOff(radio.CWIambic)
                    + (radio.CWIambic
                        ? (radio.CWIambicModeB ? ", mode B" : ", mode A")
                        : ""));
            });

            Section(sb, "Memories", () =>
            {
                var mems = radio?.MemoryList?.Where(m => m != null).ToList();
                if (mems == null || mems.Count == 0)
                {
                    sb.AppendLine("No memories are stored on the radio.");
                    return;
                }
                sb.AppendLine(mems.Count == 1
                    ? "1 memory is stored on the radio. A factory reset erases it."
                    : $"{mems.Count} memories are stored on the radio. A factory reset erases them.");
                foreach (var m in mems.OrderBy(m => m.Freq))
                {
                    ulong hz = (ulong)(m.Freq * 1_000_000d);
                    string name = string.IsNullOrEmpty(m.Name) ? "unnamed" : "\"" + m.Name + "\"";
                    string group = string.IsNullOrEmpty(m.Group) ? "" : $", group \"{m.Group}\"";
                    sb.AppendLine($"Memory {name}: {RadioStatusBuilder.FormatFreqDisplay(hz)} {m.Mode}{group}.");
                }
            });

            Section(sb, "Antennas and tuner", () =>
            {
                var rxList = rig.RXAntennaList;
                var txList = rig.TXAntennaList;
                sb.AppendLine("RX antenna ports on this radio: "
                    + (rxList.Count > 0 ? string.Join(", ", rxList) : "none reported"));
                sb.AppendLine("TX antenna ports on this radio: "
                    + (txList.Count > 0 ? string.Join(", ", txList) : "none reported"));
                if (radio != null)
                    sb.AppendLine("Antenna tuner fitted: " + (radio.ATUPresent ? "yes" : "no"));
            });

            Section(sb, "What this file cannot carry", () =>
            {
                sb.AppendLine("Listed so nothing is discovered missing AFTER a reset:");
                sb.AppendLine();
                sb.AppendLine("- The contents of profiles other than the ones loaded right now.");
                sb.AppendLine("  The radio only reveals a profile's settings by loading it, and");
                sb.AppendLine("  this export deliberately changes nothing. To capture another");
                sb.AppendLine("  profile in text: load it from the Radio menu, then export again.");
                sb.AppendLine("- DVK voice recordings. Audio cannot be text; if the radio holds");
                sb.AppendLine("  recorded voice messages, they do not survive a reset and are not");
                sb.AppendLine("  in this file.");
                sb.AppendLine("- The radio's SmartLink registration and account binding. After a");
                sb.AppendLine("  reset the radio may need to be registered again from SmartSDR.");
                sb.AppendLine("- TNF (tracking notch filter) placements and ATU tuner memories.");
                sb.AppendLine("- Anything set from SmartSDR that JJ Flexible does not read.");
            });

            sb.AppendLine();
            sb.AppendLine("End of export.");
            return sb.ToString();
        }

        /// <summary>
        /// Write one section: heading, underline, body from
        /// <paramref name="body"/> — and when the body throws, the failure is
        /// recorded IN the file, because a section silently missing is the
        /// exact defect the gaps section exists to prevent.
        /// </summary>
        private static void Section(StringBuilder sb, string title, Action body)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', title.Length));
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Tracing.TraceLine(
                    $"StationSettingsExport: section \"{title}\" failed: {ex.Message}",
                    TraceLevel.Warning);
                sb.AppendLine($"This section could not be read: {ex.Message}");
            }
            sb.AppendLine();
        }

        /// <summary>
        /// The text export's names-per-type lines, from the RADIO's own list
        /// (#418). Three states render three visibly different ways: the
        /// radio reporting none is a finding; the radio never reporting its
        /// list, or the reading failing, is a blind spot — and this file's
        /// promise is that a blind spot says so.
        /// </summary>
        internal static void AppendProfileNames(
            StringBuilder sb, FlexBase.RadioProfileList list, string label)
        {
            string typeWord = TypeLabel(list.ProfileType);
            if (list.CouldNotAsk)
            {
                sb.AppendLine($"{label}: could not be read — {list.Problem}."
                    + $" Any {typeWord} profiles the radio holds are not named here.");
                return;
            }
            string loaded = list.Selection == null
                ? "not readable"
                : (list.Selection.Length == 0 ? "none" : list.Selection);
            if (!list.Reported)
            {
                sb.AppendLine($"{label} (loaded now: {loaded}): the radio never reported"
                    + $" its list ({WaitedSecondsPhrase(list)})."
                    + $" Any {typeWord} profiles it holds are not named here.");
                AppendContradictionSentence(sb, list, typeWord);
                return;
            }
            if (list.Names.Count == 0)
            {
                sb.AppendLine($"{label} (loaded now: {loaded}): the radio reports none stored.");
                AppendContradictionSentence(sb, list, typeWord);
                return;
            }
            sb.AppendLine($"{label} (loaded now: {loaded}):");
            foreach (var name in list.Names)
            {
                sb.AppendLine($"  - {name}");
            }
            if (!list.FreshAnswer)
                sb.AppendLine($"  (the radio did not refresh this list within"
                    + $" {Math.Max(1, list.WaitedMs / 1000)} seconds; these names are from"
                    + " the list it sent earlier in this session)");
        }

        private static void AppendContradictionSentence(
            StringBuilder sb, FlexBase.RadioProfileList list, string typeWord)
        {
            if (!list.SelectionContradictsCount) return;
            sb.AppendLine($"  Caution: the radio names '{list.Selection}' as the loaded"
                + $" {typeWord} profile, so at least one {typeWord} profile exists that"
                + " this export cannot see.");
        }

        private static string OnOff(bool value) => value ? "on" : "off";

        /// <summary>
        /// Save the station settings export where a person can find it
        /// WITHOUT this app: Documents\JJFlexRadio. AppData would survive the
        /// radio's reset but fail the person — it is hidden, and the moment
        /// this file matters is the moment nothing else is working. The
        /// filename carries the radio's serial and the date, because Noel has
        /// more than one radio and so will others.
        /// </summary>
        public static string SaveStationSettingsExport(string report, string radioSerial)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "JJFlexRadio");
            Directory.CreateDirectory(dir);

            string serialPart = string.IsNullOrWhiteSpace(radioSerial)
                ? "radio"
                : RadioConfig.SanitizeRadioId(radioSerial);
            var filename = $"radio-settings-{serialPart}-{DateTime.Now:yyyy-MM-dd-HHmm}.txt";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, report);
            Tracing.TraceLine("StationSettingsExport: wrote " + path, TraceLevel.Info);
            return path;
        }

        // ══ The restore-grade capture (2026-08-30, before Don's 6300 reset) ══
        //
        // A radio's settings largely live INSIDE its profiles, and a factory
        // reset destroys them. The text export above deliberately reads only
        // live state, so it cannot see into a profile it has not loaded; the
        // comparison report walks, but writes a diff narrative nobody can
        // restore from. This one walks every profile — global, TX, mic — and
        // writes what each holds as "key = value" lines under [section]
        // headers: one file that reads aloud line by line, goes over a phone
        // to a helper at the radio, and parses by machine.
        //
        // Three rules, each load-bearing:
        //
        //   - Values are what the RADIO reports, read from the FlexLib radio
        //     and slice objects the radio's own status messages populate —
        //     never from FlexBase's optimistic caches, which hold our request
        //     whether or not the radio applied it (#164).
        //   - A fact that could not be read is WRITTEN DOWN as unreadable.
        //     After the reset the radio is gone; a silently missing line and
        //     a setting that was never captured must not look the same.
        //   - The walk puts the original profiles back and CHECKS. A restore
        //     the radio did not confirm is reported in the file and to the
        //     operator, not swallowed.

        /// <summary>
        /// One captured fact: the setting's name, and either its value or the
        /// reason it could not be read. Never both, never neither.
        /// </summary>
        internal sealed class SettingLine
        {
            public readonly string Key;
            public readonly string Value;
            public readonly string Problem;

            public SettingLine(string key, string value, string problem)
            {
                Key = key;
                Value = value;
                Problem = problem;
            }

            public bool Readable => Problem == null;
        }

        /// <summary>
        /// Write setting lines in the file's one format: "key = value", or
        /// "key = unreadable: reason" when the radio did not give the fact up.
        /// </summary>
        internal static void AppendSettingLines(StringBuilder sb, IEnumerable<SettingLine> lines)
        {
            foreach (var l in lines)
            {
                sb.AppendLine(l.Readable
                    ? $"{l.Key} = {l.Value}"
                    : $"{l.Key} = unreadable: {l.Problem}");
            }
        }

        /// <summary>
        /// Read every setting the capture records, from the radio's own
        /// reported state. Each read is guarded on its own: one setting the
        /// radio will not give up costs that line, not the section.
        /// </summary>
        internal static List<SettingLine> CaptureKeyedSettings(FlexBase rig)
        {
            var lines = new List<SettingLine>();
            var radio = rig?.theRadio;
            string noRadio = radio == null ? "no radio connection" : null;

            void Key(string key, Func<string> read)
            {
                if (noRadio != null)
                {
                    lines.Add(new SettingLine(key, null, noRadio));
                    return;
                }
                try
                {
                    string v = read();
                    lines.Add(new SettingLine(key, string.IsNullOrEmpty(v) ? "none" : v, null));
                }
                catch (Exception ex)
                {
                    lines.Add(new SettingLine(key, null, ex.Message));
                }
            }

            // Which TX and mic profile ride along. Inside a global profile's
            // section this records what loading that profile selects.
            Key("tx profile selected", () => radio.ProfileTXSelection);
            Key("mic profile selected", () => radio.ProfileMICSelection);

            // Transmit chain.
            Key("rf power", () => radio.RFPower + " watts");
            Key("tune power", () => radio.TunePower + " watts");
            Key("mic input", () => radio.MicInput);
            Key("mic gain", () => radio.MicLevel.ToString());
            Key("mic boost", () => OnOff(radio.MicBoost));
            Key("mic bias", () => OnOff(radio.MicBias));
            Key("speech processor", () => OnOff(radio.SpeechProcessorEnable));
            Key("speech processor level", () => ((FlexBase.ProcessorSettings)radio.SpeechProcessorLevel).ToString());
            Key("compander", () => OnOff(radio.CompanderOn));
            Key("compander level", () => radio.CompanderLevel.ToString());
            Key("tx filter low", () => radio.TXFilterLow + " hertz");
            Key("tx filter high", () => radio.TXFilterHigh + " hertz");
            Key("am carrier level", () => radio.AMCarrierLevel.ToString());
            Key("transmit monitor", () => OnOff(radio.TXMonitor));
            Key("transmit monitor level, sideband", () => radio.TXSBMonitorGain.ToString());
            Key("transmit monitor level, CW", () => radio.TXCWMonitorGain.ToString());
            Key("vox", () => OnOff(radio.SimpleVOXEnable));
            Key("vox gain", () => radio.SimpleVOXLevel.ToString());
            Key("vox delay", () => (radio.SimpleVOXDelay * FlexBase.VoxDelayMS) + " milliseconds");

            // CW.
            Key("cw speed", () => radio.CWSpeed + " words per minute");
            Key("cw sidetone pitch", () => radio.CWPitch + " hertz");
            Key("cw sidetone", () => OnOff(radio.CWSidetone));
            Key("cw break-in", () => OnOff(radio.CWBreakIn));
            Key("cw break-in delay", () => radio.CWDelay + " milliseconds");
            Key("cw iambic", () => OnOff(radio.CWIambic));
            Key("cw iambic mode", () => radio.CWIambic ? (radio.CWIambicModeB ? "B" : "A") : "not iambic");

            // Audio outputs.
            Key("lineout gain", () => radio.LineoutGain.ToString());
            Key("lineout muted", () => radio.LineoutMute ? "yes" : "no");
            Key("headphone gain", () => radio.HeadphoneGain.ToString());
            Key("headphone muted", () => radio.HeadphoneMute ? "yes" : "no");
            Key("binaural receive", () => OnOff(radio.BinauralRX));

            // Slices, in letter order. The list itself is one guarded read;
            // each slice's settings are then guarded line by line.
            List<Slice> slices = null;
            string sliceProblem = noRadio;
            if (sliceProblem == null)
            {
                try
                {
                    slices = radio.SliceList?.Where(s => s != null)
                        .OrderBy(s => s.Letter, StringComparer.Ordinal).ToList();
                }
                catch (Exception ex)
                {
                    sliceProblem = ex.Message;
                }
            }

            if (sliceProblem != null)
            {
                lines.Add(new SettingLine("slices open", null, sliceProblem));
                return lines;
            }

            lines.Add(new SettingLine("slices open", (slices?.Count ?? 0).ToString(), null));
            if (slices == null) return lines;

            foreach (var s in slices)
            {
                string p = "slice " + s.Letter + " ";
                Key(p + "frequency", () => RadioStatusBuilder.FormatFreqDisplay((ulong)(s.Freq * 1_000_000d)));
                Key(p + "mode", () => s.DemodMode);
                Key(p + "filter low", () => s.FilterLow + " hertz");
                Key(p + "filter high", () => s.FilterHigh + " hertz");
                Key(p + "rx antenna", () => s.RXAnt);
                Key(p + "tx antenna", () => s.TXAnt);
                Key(p + "transmit slice", () => s.IsTransmitSlice ? "yes" : "no");
                Key(p + "tune step", () => s.TuneStep + " hertz");
                Key(p + "locked", () => OnOff(s.Lock));
                Key(p + "agc mode", () => s.AGCMode.ToString());
                Key(p + "agc threshold", () => s.AGCThreshold.ToString());
                Key(p + "audio gain", () => s.AudioGain.ToString());
                Key(p + "audio pan", () => s.AudioPan.ToString());
                Key(p + "muted", () => s.Mute ? "yes" : "no");
                Key(p + "rf gain", () => s.RFGain.ToString());
                Key(p + "squelch", () => OnOff(s.SquelchOn));
                Key(p + "squelch level", () => s.SquelchLevel.ToString());
                Key(p + "rit", () => s.RITOn ? "on at " + s.RITFreq + " hertz" : "off");
                Key(p + "xit", () => s.XITOn ? "on at " + s.XITFreq + " hertz" : "off");
                Key(p + "dax channel", () => s.DAXChannel == 0 ? "none" : s.DAXChannel.ToString());
                Key(p + "noise reduction", () => s.NROn ? "on, level " + s.NRLevel : "off");
                Key(p + "spectral noise reduction", () => s.NRSOn ? "on, level " + s.NRSLevel : "off");
                Key(p + "legacy noise reduction", () => s.NRLOn ? "on, level " + s.NRL_Level : "off");
                Key(p + "noise reduction filter", () => s.NRFOn ? "on, level " + s.NRFLevel : "off");
                Key(p + "neural noise reduction", () => OnOff(s.RNNOn));
                Key(p + "noise blanker", () => s.NBOn ? "on, level " + s.NBLevel : "off");
                Key(p + "wideband noise blanker", () => s.WNBOn ? "on, level " + s.WNBLevel : "off");
                Key(p + "auto notch", () => OnOff(s.ANFTOn));
                Key(p + "legacy auto notch", () => s.ANFOn ? "on, level " + s.ANFLevel : "off");
                Key(p + "audio peaking filter", () => s.APFOn ? "on, level " + s.APFLevel : "off");
            }

            return lines;
        }

        /// <summary>
        /// One walked profile's section: header, "captured = yes", then its
        /// settings.
        /// </summary>
        internal static void AppendProfileSection(
            StringBuilder sb, string typeLabel, string name, List<SettingLine> lines)
        {
            sb.AppendLine($"[{typeLabel} profile: {name}]");
            sb.AppendLine("captured = yes");
            AppendSettingLines(sb, lines);
            sb.AppendLine();
        }

        /// <summary>
        /// The section for a profile the walk could not read. It gets a
        /// section anyway: after the reset, "this profile existed and could
        /// not be captured" is a fact worth exactly as much as a captured one.
        /// </summary>
        internal static void AppendUnreadableProfileSection(
            StringBuilder sb, string typeLabel, string name, string problem)
        {
            sb.AppendLine($"[{typeLabel} profile: {name}]");
            sb.AppendLine("captured = no");
            sb.AppendLine($"problem = {problem}; none of this profile's settings are in this file");
            sb.AppendLine();
        }

        /// <summary>
        /// What <see cref="GenerateRestoreGradeExport"/> hands back: the file
        /// text, plus the two facts the completion announcement needs.
        /// </summary>
        public sealed class RestoreGradeExport
        {
            public string Text;

            /// <summary>False when the walk was skipped — change nothing armed,
            /// so the file holds only live settings.</summary>
            public bool WalkRan;

            /// <summary>Some walk requested at least one profile load. False
            /// means no load was even attempted, which is the only case where
            /// "it was not moved" is provable (#469 round six).</summary>
            public bool AnyLoadRequested;

            /// <summary>Why the reservation overload declined to run (#469);
            /// <see cref="WalkRefusal.None"/> when it ran.</summary>
            public WalkRefusal Refused;

            /// <summary>The radio was keyed during the walk, so it stopped and
            /// put nothing back (#469); the file says what was not captured.</summary>
            public bool StoppedForTransmit;
        }

        private static int _walkInFlight;

        /// <summary>
        /// True while a profile walk is running. Both walking entry points —
        /// the restore capture and the comparison report — take this flag for
        /// their whole run and release it on every exit, because both load
        /// profiles on the radio and two walks at once would restore each
        /// other's wrong state. Until #469 only the restore capture set it.
        /// </summary>
        public static bool WalkInProgress
            => System.Threading.Volatile.Read(ref _walkInFlight) != 0;

        /// <summary>
        /// TEST SEAM ONLY. Called by each walking entry point immediately
        /// after it has taken the in-flight flag, with the entry point's name,
        /// so a test can start the OTHER entry point while the flag is
        /// genuinely held by production code and watch it refuse (#469). Null
        /// in production; nothing in the application assigns it.
        /// </summary>
        internal static Action<string> WalkHeldProbe;

        /// <summary>
        /// Whether this model has anywhere to SHOW the front-panel
        /// screensaver. Pure, so a test can hold it to the models we ship.
        /// </summary>
        /// <remarks>
        /// The flag is NOT the one with "FrontPanel" in its name.
        /// <c>HasBacklitFrontPanel</c> is true for every non-M 6400, 6600,
        /// 8400 and 8600 — they have a lit panel with no screen on it. Only
        /// the OLED models (6500, 6700, 6700R) and the M models can paint
        /// text. Reading the obvious flag, which is also the first one a grep
        /// finds, reports "shows a screensaver" for half the radios we
        /// support.
        /// </remarks>
        internal static bool CanShowScreensaver(string model)
        {
            try
            {
                var mi = ModelInfo.GetModelInfoForModel(model ?? string.Empty);
                return mi != null && (mi.HasOledDisplay || mi.IsMModel);
            }
            catch { return false; }
        }

        /// <summary>
        /// Build the restore-grade capture. Walks every global, TX and mic
        /// profile (in that order — a global load drags TX and mic selections
        /// with it, so the later walks re-right what the earlier ones moved),
        /// captures the radio's reported state under each, then puts the
        /// original profiles back and checks. Reserves the walk itself and
        /// returns null if one is already in flight; the menu reserves at the
        /// press and uses the reservation overload instead (#469).
        /// </summary>
        public static RestoreGradeExport GenerateRestoreGradeExport(
            FlexBase rig, Action<string> progressCallback = null)
        {
            using (var reservation = TryReserveWalk())
            {
                if (reservation == null)
                {
                    Tracing.TraceLine("RestoreGradeExport: refused — a profile walk is already running", TraceLevel.Warning);
                    return null;
                }
                return GenerateRestoreGradeExport(reservation, rig, progressCallback);
            }
        }

        /// <summary>
        /// The restore-grade capture, run under a walk the caller has already
        /// reserved; the caller still owns the reservation and releases it.
        /// Never null. Refuses, with <see cref="RestoreGradeExport.Refused"/>
        /// set and no text, when the radio is keyed and the walk would run —
        /// asked HERE as well as at the press, because the menu's settle is
        /// four seconds long (#469). Under the change-nothing hold the capture
        /// is read-only and runs keyed.
        /// </summary>
        public static RestoreGradeExport GenerateRestoreGradeExport(
            WalkReservation reservation, FlexBase rig, Action<string> progressCallback)
        {
            RequireHeld(reservation);
            return GenerateRestoreGradeExport(reservation, PlanRestoreCapture(rig), rig, progressCallback);
        }

        /// <summary>
        /// The restore-grade capture under a reservation the caller holds,
        /// doing exactly what <paramref name="plan"/> (from
        /// <see cref="PlanRestoreCapture"/>) says: the menu plans first so its
        /// start sentence names what will load, and the file then prints the
        /// same lists the sentence was decided from.
        /// </summary>
        public static RestoreGradeExport GenerateRestoreGradeExport(
            WalkReservation reservation, ProfileWalkPlan plan, FlexBase rig, Action<string> progressCallback)
        {
            RequireHeld(reservation);
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            WalkHeldProbe?.Invoke("restore capture");
            if (plan.WillLoad && rig.Transmit)
            {
                Tracing.TraceLine("RestoreGradeExport: refused — the radio is transmitting and the walk would load profiles", TraceLevel.Warning);
                return new RestoreGradeExport { Refused = WalkRefusal.Transmitting };
            }
            return GenerateRestoreGradeExportCore(rig, plan, progressCallback);
        }

        /// <summary>
        /// Whether the restore capture walks on this radio: always, except
        /// under the change-nothing hold. (It does not ask the #450 opt-in;
        /// that difference from the comparison report is recorded as a
        /// question for Noel, not decided here.)
        /// </summary>
        public static bool RestoreCaptureWouldWalk(FlexBase rig) => !rig.ChangeNothingActive;

        /// <summary>
        /// The sentence that answers the restore-capture press when the file
        /// is written: the existing three, plus the one for a walk stopped
        /// because the radio was keyed, which names no profile and claims
        /// nothing about where the radio was left (#469 round four).
        /// </summary>
        public static string RestoreFinishedSentence(RestoreGradeExport export, string path)
        {
            string key;
            if (!export.WalkRan) key = "settings.restore_export.saved_no_walk";
            else if (export.StoppedForTransmit)
                return Lexicon.Get("settings.restore_export.saved_stopped", ("path", path));
            else if (!export.AnyLoadRequested)
                key = "settings.restore_export.saved_nothing_moved";
            else
                key = "settings.restore_export.saved_loaded";
            return Lexicon.Get(key, ("path", path));
        }

        private static RestoreGradeExport GenerateRestoreGradeExportCore(
            FlexBase rig, ProfileWalkPlan plan, Action<string> progressCallback)
        {
            var result = new RestoreGradeExport();
            var sb = new StringBuilder();
            var radio = rig.theRadio;

            sb.AppendLine("JJ Flexible restore-grade settings capture");
            sb.AppendLine("==========================================");
            sb.AppendLine();
            sb.AppendLine("One fact per line, \"key = value\", grouped under [section] headers —");
            sb.AppendLine("readable straight through, over a phone, or by a parser. A value of");
            sb.AppendLine("\"unreadable: reason\" means the radio did not give that fact up; the");
            sb.AppendLine("line stays, so a gap is never mistaken for a captured setting.");
            sb.AppendLine("\"none\" means the radio answered with nothing.");
            sb.AppendLine();

            // No progress line for the live capture: it is instant, and the
            // start announcement is still speaking. The first spoken progress
            // is the first profile load — the first thing that takes time.

            // ── [capture] — identity, and where the radio stood at the start ──
            // "no radio connection" beats a null-collapsed "none" here: with
            // no radio, "radio name = none" claims a fact nobody read.
            string noRadio = radio == null ? "no radio connection" : null;
            var capture = new List<SettingLine>();
            void Cap(string key, Func<string> read, bool needsRadio = true)
            {
                if (needsRadio && noRadio != null)
                {
                    capture.Add(new SettingLine(key, null, noRadio));
                    return;
                }
                try
                {
                    string v = read();
                    capture.Add(new SettingLine(key, string.IsNullOrEmpty(v) ? "none" : v, null));
                }
                catch (Exception ex)
                {
                    capture.Add(new SettingLine(key, null, ex.Message));
                }
            }

            Cap("taken", () => DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " local", needsRadio: false);
            Cap("radio model", () => radio.Model ?? rig.RadioModel);
            Cap("radio name", () => radio.Nickname ?? rig.RadioNickname);
            Cap("radio serial", () => radio.Serial);
            Cap("firmware", () => radio.Versions);
            Cap("antenna tuner fitted", () => radio.ATUPresent ? "yes" : "no");
            Cap("rx antenna ports", () => string.Join(", ", rig.RXAntennaList ?? new List<string>()));
            Cap("tx antenna ports", () => string.Join(", ", rig.TXAntennaList ?? new List<string>()));
            Cap("global profile loaded at start", () => radio.ProfileGlobalSelection);
            Cap("tx profile loaded at start", () => radio.ProfileTXSelection);
            Cap("mic profile loaded at start", () => radio.ProfileMICSelection);

            // #418: the counts come from the RADIO's own lists, read once here
            // and reused by the walk below so the file cannot disagree with
            // itself about what the radio holds. Each line renders one of
            // three states — a count the radio reported (0 included), "the
            // radio never reported its list", or "we could not ask" — because
            // this file's promise is that a blind spot says so, and until
            // 2026-08-30 all three rendered as the operator's own count, "0"
            // on a radio full of profiles its owner never named.
            var profileTypes = new[] { ProfileTypes.global, ProfileTypes.tx, ProfileTypes.mic };
            var lists = new Dictionary<ProfileTypes, FlexBase.RadioProfileList>();
            foreach (var t in profileTypes)
            {
                // #469: the plan's reading, so the file and the start sentence
                // were decided from the same lists.
                lists[t] = plan.Lists != null && plan.Lists.TryGetValue(t, out var planned) && planned != null
                    ? planned
                    : ReadListSafely(rig, t);
                capture.Add(ProfileCountLine(lists[t]));
                var crossCheck = ProfileCrossCheckLine(lists[t]);
                if (crossCheck != null) capture.Add(crossCheck);
            }

            sb.AppendLine("[capture]");
            AppendSettingLines(sb, capture);
            sb.AppendLine();

            // ── [radio-wide settings] — not inside any profile, and a factory
            //    reset takes them just the same ──
            sb.AppendLine("[radio-wide settings]");
            var radioWide = new List<SettingLine>();
            void Wide(string key, Func<string> read)
            {
                if (noRadio != null)
                {
                    radioWide.Add(new SettingLine(key, null, noRadio));
                    return;
                }
                try
                {
                    string v = read();
                    radioWide.Add(new SettingLine(key, string.IsNullOrEmpty(v) ? "none" : v, null));
                }
                catch (Exception ex)
                {
                    radioWide.Add(new SettingLine(key, null, ex.Message));
                }
            }
            Wide("remote power on (REM ON)", () => radio.RemoteOnEnabled ? "enabled" : "disabled");

            // ── The front-panel screensaver, and the trap in its second half ──
            //
            // FlexLib's `Radio.Callsign` is NOT the operator's station
            // callsign. Its own summary says so: "the Callsign string to be
            // stored in the radio to be shown on the front display if the
            // Callsign ScreensaverMode is selected". It is the screensaver's
            // text, it sits between Screensaver and Nickname in FlexLib, and
            // the only command behind it is "radio callsign".
            //
            // This capture used to write it into [capture] as bare
            // "callsign", among model, name and serial — which are identity.
            // Noel read the resulting "callsign = none" on his own 8600 the
            // morning after it shipped and asked what had gone wrong. Nothing
            // had. (The first version of this comment said the 8600 "has no
            // front display", which is loose: FlexLib has it as
            // HasBacklitFrontPanel = true. It has a lit panel and no SCREEN —
            // see the display check below.) The field is legitimately empty
            // on his radio, so the DATA was right
            // and the KEY lied, which is the worse of the two failures,
            // because the file's whole promise is that it can be read
            // straight through and believed.
            //
            // The mode came with the rename: capturing the text without the
            // mode captures half a setting, and a restore made from it would
            // silently drop which of Model / Name / Callsign / None the
            // operator had chosen.
            // Can this radio SHOW a screensaver at all? Noel asked the
            // question the moment he understood the setting, and FlexLib
            // already answers it: ModelInfo is a per-model capability table
            // (ModelInfo.cs), keyed by the model string.
            //
            // THE FLAG IS NOT THE ONE WITH "FrontPanel" IN THE NAME. His own
            // 8600 is HasBacklitFrontPanel = TRUE — it has an illuminated
            // panel — and HasOledDisplay = false, IsMModel = false, so there
            // is no SCREEN on it and nothing can paint text. Only the OLED
            // models (6500, 6700, 6700R) and the M models have somewhere to
            // show one. Reading HasBacklitFrontPanel here, which is the
            // obvious name and the one a grep finds first, would report
            // "this radio shows a screensaver" for every non-M 6400, 6600,
            // 8400 and 8600 we support.
            //
            // Both values are still captured when there is no display: the
            // radio stores them either way, and dropping a stored setting
            // because we judged it cosmetic is how a restore file loses
            // something quietly. The applicability line goes ABOVE them, so
            // the reader meets it before the values.
            // radio may be null here — Wide() guards its LAMBDAS, not the
            // setup around them, and this line runs either way.
            bool canShowScreensaver = radio != null && CanShowScreensaver(radio.Model);

            Wide("front panel display", () => canShowScreensaver
                ? "yes — this model can show the screensaver below"
                : "none — this model has no screen, so the screensaver below is stored but never shown");
            Wide("front panel screensaver mode",
                 () => radio.Screensaver.ToString().ToLowerInvariant());
            Wide("front panel callsign text", () => radio.Callsign);
            Wide("network addressing", () =>
            {
                var ip = rig.CurrentStaticIP;
                return ip != null ? "static IP " + ip : "automatic (DHCP)";
            });
            AppendSettingLines(sb, radioWide);
            sb.AppendLine();

            // ── [settings now] — the radio as found, before any profile is
            //    loaded, including anything changed since a profile was last
            //    saved ──
            sb.AppendLine("[settings now]");
            AppendSettingLines(sb, CaptureKeyedSettings(rig));
            sb.AppendLine();

            // ── The walk ──
            var outcomes = new List<ProfileWalkOutcome>();
            if (!plan.ProfilesMayBeLoaded)
            {
                // Same reasoning as the comparison report: walking means
                // loading every profile on a radio the operator asked us to
                // leave alone. The file still holds everything readable.
                sb.AppendLine("[profile walk skipped]");
                sb.AppendLine("reason = change nothing is on for this radio, and walking means loading every stored profile on it");
                sb.AppendLine("this file holds = live settings only; no profile contents");
                sb.AppendLine("to capture profiles = turn the setting off in Settings, under Radios, then run this export again");
                sb.AppendLine();
                Tracing.TraceLine("RestoreGradeExport: walk skipped — change nothing is on for this radio", TraceLevel.Warning);
                result.WalkRan = false;
            }
            else
            {
                result.WalkRan = true;
                foreach (var t in profileTypes)
                {
                    string label = TypeLabel(t);
                    var outcome = WalkProfiles(rig, t,
                        name => AppendProfileSection(sb, label, name, CaptureKeyedSettings(rig)),
                        progressCallback,
                        (name, problem) => AppendUnreadableProfileSection(sb, label, name, problem),
                        lists[t]);
                    outcomes.Add(outcome);
                    // A type with nothing to walk still gets a section: after
                    // the reset, the difference between "the radio had none"
                    // and "we could not see them" is the whole point (#418).
                    if (!lists[t].Reported || lists[t].Names.Count == 0)
                        AppendUnwalkedTypeSection(sb, lists[t]);

                    // #469: keyed part-way. No later type is started, and the
                    // file says what is missing, so a capture taken before a
                    // reset cannot pass for a complete one.
                    if (outcome.StoppedForTransmit)
                    {
                        result.StoppedForTransmit = true;
                        var notWalked = profileTypes
                            .SkipWhile(x => x != t).Skip(1)
                            .Select(x => TypeLabel(x) + " profiles").ToList();
                        sb.AppendLine("[profile walk stopped]");
                        sb.AppendLine("reason = the radio was keyed during the walk, so no more profiles were requested and nothing was put back while transmitting");
                        sb.AppendLine(outcome.StoppedBefore != null
                            ? "stopped before = " + TypeLabel(t) + " profile " + outcome.StoppedBefore
                              + " was requested; it and every " + TypeLabel(t) + " profile after it are not in this file"
                            : "stopped after = the last " + TypeLabel(t) + " profile was requested, so nothing was put back");
                        foreach (var (key, value) in StoppedWalkEvidence(outcome))
                            sb.AppendLine(key + " = " + value);
                        sb.AppendLine("profile the radio is on now = not known");
                        sb.AppendLine("not walked at all = " + (notWalked.Count == 0 ? "none" : string.Join(", ", notWalked)));
                        sb.AppendLine("to capture them = unkey, then run this export again");
                        sb.AppendLine();
                        break;
                    }
                }
                // Only a capture that requested no load at all may say the
                // radio was not moved (#469 round six).
                result.AnyLoadRequested = outcomes.Any(o => o.LoadAttempts > 0);
            }

            // ── [memories] — not profile contents, but the reset takes them ──
            sb.AppendLine("[memories]");
            var memLines = new List<SettingLine>();
            List<Memory> mems = null;
            string memProblem = radio == null ? "no radio connection" : null;
            if (memProblem == null)
            {
                try { mems = radio.MemoryList?.Where(m => m != null).OrderBy(m => m.Freq).ToList(); }
                catch (Exception ex) { memProblem = ex.Message; }
            }
            if (memProblem != null)
            {
                memLines.Add(new SettingLine("memories stored", null, memProblem));
            }
            else
            {
                memLines.Add(new SettingLine("memories stored", (mems?.Count ?? 0).ToString(), null));
                int n = 0;
                foreach (var m in mems ?? new List<Memory>())
                {
                    n++;
                    string p = "memory " + n + " ";
                    void Mem(string key, Func<string> read)
                    {
                        try
                        {
                            string v = read();
                            memLines.Add(new SettingLine(key, string.IsNullOrEmpty(v) ? "none" : v, null));
                        }
                        catch (Exception ex)
                        {
                            memLines.Add(new SettingLine(key, null, ex.Message));
                        }
                    }
                    Mem(p + "name", () => m.Name);
                    Mem(p + "group", () => m.Group);
                    Mem(p + "frequency", () => RadioStatusBuilder.FormatFreqDisplay((ulong)(m.Freq * 1_000_000d)));
                    Mem(p + "mode", () => m.Mode);
                    Mem(p + "tune step", () => m.Step + " hertz");
                    Mem(p + "repeater offset direction", () => m.OffsetDirection.ToString());
                    Mem(p + "repeater offset", () => m.RepeaterOffset + " megahertz");
                    Mem(p + "tone mode", () => m.ToneMode.ToString());
                    Mem(p + "tone value", () => m.ToneValue);
                    Mem(p + "squelch", () => m.SquelchOn ? "on, level " + m.SquelchLevel : "off");
                    Mem(p + "rf power", () => m.RFPower + " watts");
                    Mem(p + "rx filter low", () => m.RXFilterLow + " hertz");
                    Mem(p + "rx filter high", () => m.RXFilterHigh + " hertz");
                }
            }
            AppendSettingLines(sb, memLines);
            sb.AppendLine();

            // ── [after the walk] — what was requested for each starting profile;
        //    whether it reached the radio is not known (#469 round six) ──
            sb.AppendLine("[after the walk]");
            if (!result.WalkRan)
            {
                sb.AppendLine("nothing was loaded = the walk was skipped, so the radio was not touched");
            }
            else
            {
                foreach (var o in outcomes)
                {
                    sb.AppendLine(TypeLabel(o.ProfileType) + " profile put back = " + AfterTheWalkValue(o));
                }
            }
            sb.AppendLine();
            sb.AppendLine("End of capture.");

            result.Text = sb.ToString();
            return result;
        }

        /// <summary>
        /// The restore capture's "[after the walk]" value for one kind: what
        /// became of its starting profile, said only as far as it is known.
        /// Nothing here says a put-back reached the radio (#469 round six).
        /// Pure, so a test can hold every case.
        /// </summary>
        internal static string AfterTheWalkValue(ProfileWalkOutcome o)
        {
            string typeWord = TypeLabel(o.ProfileType);
            if (o.RadioList != null && o.RadioList.CouldNotAsk)
                return "no load was ever sent — the " + typeWord
                    + " profile list could not be read (" + o.RadioList.Problem
                    + "), so the radio was not moved";
            if (o.RadioList != null && !o.RadioList.Reported)
                return "no load was ever sent — the radio never reported its "
                    + typeWord + " profile list, so the radio was not moved";
            if (o.RadioList != null && o.RadioList.Names.Count == 0)
                return "nothing was loaded — the radio reports no stored "
                    + typeWord + " profiles, so there was nothing to put back";
            if (o.StoppedForTransmit)
                return o.LastRequested == null
                    ? "nothing was requested, so nothing needed putting back"
                    : "NO — the radio was keyed, so nothing was put back while transmitting; "
                      + "which profile the radio is on now is not known. " + TransmitStopSentence();
            if (o.LoadAttempts == 0 && o.Unreadable.Count > 0)
                return "no load was ever sent — there was no radio connection, so the radio was not moved";
            if (o.LoadAttempts == 0)
                return "nothing was loaded, so there was nothing to put back";
            return PutBackNote(o) + " Your profile may not be the one you started with; load it again from Profiles, in the Radio menu, if needed";
        }

        /// <summary>
        /// Save the restore-grade capture beside the text export, in
        /// Documents\JJFlexRadio — findable without this app, which is the
        /// moment the file matters. INI extension because the file is one:
        /// Notepad opens it, and so does a parser.
        /// </summary>
        public static string SaveRestoreGradeExport(string text, string radioSerial)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "JJFlexRadio");
            Directory.CreateDirectory(dir);

            string serialPart = string.IsNullOrWhiteSpace(radioSerial)
                ? "radio"
                : RadioConfig.SanitizeRadioId(radioSerial);
            var filename = $"radio-restore-{serialPart}-{DateTime.Now:yyyy-MM-dd-HHmm}.ini";
            var path = Path.Combine(dir, filename);
            File.WriteAllText(path, text);
            Tracing.TraceLine("RestoreGradeExport: wrote " + path, TraceLevel.Info);
            return path;
        }
    }
}
