using System;
using System.IO;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// Track G review, section 1 step 10: the assessment decides from a
    /// radio-REPORTED selection, never a cached one; the repair rechecks at
    /// its send and is announced only when the radio reports the candidate.
    /// </summary>
    public sealed class SilentMicrophoneAssessmentTests
    {
        [Fact]
        public void AReportedEmptySelection_IsTheFailure()
        {
            Assert.Equal(SilentMicrophoneVerdict.SilentEmpty,
                SilentMicrophoneAssessment.Decide(new SilentMicrophoneFacts { ProfilesReported = true, ReportedSelection = "" }));
        }

        [Fact]
        public void AReportedNameIsHealthy()
        {
            Assert.Equal(SilentMicrophoneVerdict.Healthy,
                SilentMicrophoneAssessment.Decide(new SilentMicrophoneFacts { ProfilesReported = true, ReportedSelection = "Default" }));
        }

        [Fact]
        public void ACachedEmptyWithNoReport_IsUnreported_NotTheFailure()
        {
            // The review's exact defect: after 1.5 s the vendor's cached
            // selection reads "" because nothing has been parsed yet.
            Assert.Equal(SilentMicrophoneVerdict.Unreported,
                SilentMicrophoneAssessment.Decide(new SilentMicrophoneFacts { ProfilesReported = true, ReportedSelection = null }));
        }

        [Fact]
        public void NoProfilesListed_IsUnverified_NotTheFailure()
        {
            Assert.Equal(SilentMicrophoneVerdict.NoProfiles,
                SilentMicrophoneAssessment.Decide(new SilentMicrophoneFacts { ProfilesReported = false, ReportedSelection = "" }));
        }

        private static SilentMicrophoneRepairFacts Permitted() => new SilentMicrophoneRepairFacts
        {
            RadioIsOurs = true, PhasePermitsRepair = true, ReportedSelectionNow = "", Candidate = "Default", CandidateListed = true,
        };

        [Fact]
        public void TheRepairIsPermittedInTheCleanCase()
        {
            Assert.Null(SilentMicrophoneAssessment.RepairRefusal(Permitted()));
        }

        [Theory]
        [InlineData("operation ended")]
        [InlineData("not connected")]
        [InlineData("hold")]
        [InlineData("not ours")]
        [InlineData("not opted in")]
        [InlineData("company")]
        [InlineData("roster unknown")]
        [InlineData("phase uncertain")]
        [InlineData("selection unreported at send")]
        [InlineData("selection now filled")]
        [InlineData("candidate missing")]
        public void TheRepairIsRefusedWhenAnyConditionIsWithdrawnAtTheSend(string what)
        {
            var f = Permitted();
            switch (what)
            {
                case "operation ended": f.OperationLive = false; break;
                case "not connected": f.Connected = false; break;
                case "hold": f.HoldArmed = true; break;
                case "not ours": f.RadioIsOurs = false; break;
                case "not opted in": f.Intent = ProfileGuestIntent.UseMyTransmitAudio; break;
                case "company": f.StrictRoster = RosterVerdict.OthersPresent; break;
                case "roster unknown": f.StrictRoster = RosterVerdict.Unknown; break;
                case "phase uncertain": f.PhasePermitsRepair = false; break;
                case "selection unreported at send": f.ReportedSelectionNow = null; break;
                case "selection now filled": f.ReportedSelectionNow = "Default"; break;
                case "candidate missing": f.CandidateListed = false; break;
            }
            Assert.NotNull(SilentMicrophoneAssessment.RepairRefusal(f));
        }

        // ── the production check reads evidence and dispatches the repair through the gate ──

        private static string Read(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        [Fact]
        public void TheProductionCheckDecidesFromReportedEvidence_AndRepairsThroughACheckedDispatch()
        {
            var text = Read("Radios/FlexBase.cs");
            int at = text.IndexOf("private void CheckMicProfileForSilentTx(bool mayRepair)", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = text.Substring(at, 7000);
            Assert.Contains("SilentMicrophoneAssessment.Decide(", body, StringComparison.Ordinal);
            Assert.Contains("ReportedSelectionOf(ProfileTypes.mic)", body, StringComparison.Ordinal);
            Assert.DoesNotContain("!string.IsNullOrEmpty(theRadio.ProfileMICSelection), 1500", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectMicProfileIfPresent(candidate)", body, StringComparison.Ordinal);
            Assert.Contains("RepairSilentMicrophoneChecked(", body, StringComparison.Ordinal);

            int repair = text.IndexOf("private bool RepairSilentMicrophoneChecked(", StringComparison.Ordinal);
            Assert.True(repair >= 0);
            string repairBody = text.Substring(repair, 3500);
            int dispatch = repairBody.IndexOf("DispatchStationWork(", StringComparison.Ordinal);
            int refusal = repairBody.IndexOf("SilentMicrophoneAssessment.RepairRefusal(", StringComparison.Ordinal);
            // The write is the reply-bearing send, never the FlexLib setter:
            // the setter's cache pre-assignment makes the vendor skip the
            // confirming status (Track G3, group 1).
            int write = repairBody.IndexOf("SendRadioCommandWithReply(radio, ProfileLoadCommand(ProfileTypes.mic, candidate),", StringComparison.Ordinal);
            Assert.True(dispatch > 0 && refusal > dispatch && write > refusal, "the repair must recheck inside the dispatched delegate before writing");
            Assert.DoesNotContain("radio.ProfileMICSelection = candidate;", repairBody, StringComparison.Ordinal);
            Assert.Contains("ReportedSelectionOf(ProfileTypes.mic)", repairBody.Substring(write), StringComparison.Ordinal);
            Assert.Contains("finalReply.Acknowledged", repairBody.Substring(write), StringComparison.Ordinal);
        }
    }
}
