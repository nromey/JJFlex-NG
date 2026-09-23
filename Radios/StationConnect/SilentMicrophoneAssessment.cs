using System;

namespace Radios.StationConnect
{
    /// <summary>What the silent-microphone assessment concluded.</summary>
    public enum SilentMicrophoneVerdict
    {
        /// <summary>The radio reported a non-empty microphone selection.</summary>
        Healthy,
        /// <summary>The radio has not REPORTED a microphone selection this
        /// connection. The vendor's cached value may read empty, but a cached
        /// default is not an observation; nothing is announced.</summary>
        Unreported,
        /// <summary>The radio lists no microphone profiles at all: an
        /// unverified state, traced and not announced.</summary>
        NoProfiles,
        /// <summary>The radio REPORTED an empty selection: the pcap-confirmed
        /// silent-transmit failure. Announce; repair only when permitted.</summary>
        SilentEmpty,
    }

    /// <summary>The facts the assessment reads, all from evidence.</summary>
    public sealed class SilentMicrophoneFacts
    {
        public bool Connected = true;
        /// <summary>The radio's microphone profile list has been reported and is non-empty.</summary>
        public bool ProfilesReported;
        /// <summary>The latest RADIO-REPORTED microphone selection, or null when none was ever reported.</summary>
        public string ReportedSelection;
    }

    /// <summary>The facts the repair rechecks at its send.</summary>
    public sealed class SilentMicrophoneRepairFacts
    {
        public bool OperationLive = true;
        public bool Connected = true;
        public bool HoldArmed;
        public bool RadioIsOurs;
        public ProfileGuestIntent Intent = ProfileGuestIntent.LoadMineAndPutBack;
        public RosterVerdict StrictRoster = RosterVerdict.OnlyUs;
        /// <summary>The permission the post-station phase computed; false when
        /// any step ended uncertain.</summary>
        public bool PhasePermitsRepair;
        /// <summary>The latest RADIO-REPORTED microphone selection at the send, or null.</summary>
        public string ReportedSelectionNow;
        public string Candidate = "";
        public bool CandidateListed;
    }

    /// <summary>
    /// Design step 10 as a pure rule. Until Track G2 the production check
    /// equated an empty CACHED selection after 1.5 s with an empty REPORT
    /// (review step 10), sampled its repair permission before its reads,
    /// queued an unchecked setter and called it applied. This decides from
    /// radio-reported evidence only, and the repair rechecks everything at
    /// its send.
    /// </summary>
    public static class SilentMicrophoneAssessment
    {
        public static SilentMicrophoneVerdict Decide(SilentMicrophoneFacts f)
        {
            if (f == null || !f.Connected) return SilentMicrophoneVerdict.Unreported;
            if (!f.ProfilesReported) return SilentMicrophoneVerdict.NoProfiles;
            if (f.ReportedSelection == null) return SilentMicrophoneVerdict.Unreported;
            return f.ReportedSelection.Length == 0 ? SilentMicrophoneVerdict.SilentEmpty : SilentMicrophoneVerdict.Healthy;
        }

        /// <summary>Why the repair must not be sent now, or null.</summary>
        public static string RepairRefusal(SilentMicrophoneRepairFacts f)
        {
            if (f == null) return "no facts";
            if (!f.OperationLive) return "the operation ended";
            if (!f.Connected) return "not connected";
            if (f.HoldArmed) return "the change-nothing hold is armed";
            if (!f.RadioIsOurs) return "the radio is not declared ours; the selection is shared state";
            if (f.Intent != ProfileGuestIntent.LoadMineAndPutBack) return "the radio is not opted in (" + f.Intent + ")";
            if (f.StrictRoster != RosterVerdict.OnlyUs) return "roster: " + f.StrictRoster;
            if (!f.PhasePermitsRepair) return "the post-station phase did not permit an automatic repair (a step ended uncertain or was refused)";
            if (f.ReportedSelectionNow == null) return "the radio has not reported a microphone selection";
            if (f.ReportedSelectionNow.Length != 0) return "the radio now reports '" + f.ReportedSelectionNow + "' selected; nothing to repair";
            if (string.IsNullOrEmpty(f.Candidate)) return "no candidate profile";
            if (!f.CandidateListed) return "the candidate '" + f.Candidate + "' is not in the radio's list";
            return null;
        }
    }
}
