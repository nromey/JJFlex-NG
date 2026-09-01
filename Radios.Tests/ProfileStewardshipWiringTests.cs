using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The stewardship decisions are only as good as the set of paths that
    /// consult them, and nothing else can fail when one stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>ChangeNothingGuardTests</c> lesson applied to the same
    /// class of writer: a decision function that three call sites consult and
    /// a fourth does not is worse than no decision function, because it will
    /// be trusted. If a future editor puts the old unconditional
    /// <c>SelectProfile</c> calls back into the connect path, or drops the
    /// disconnect restore, the build stays green, the merge stays clean, and
    /// somebody's radio quietly gets written to again. So the wiring is pinned
    /// as SOURCE, with a positive control proving the reader discriminates.
    /// </para>
    /// <para>
    /// Reading source rather than running the path is deliberate and is the
    /// only option here: the path needs a radio, and the radio it protects
    /// belongs to somebody else.
    /// </para>
    /// </remarks>
    public sealed class ProfileStewardshipWiringTests
    {
        private const string FlexBase = "Radios/FlexBase.cs";
        private const string Reporter = "Radios/ProfileReporter.cs";
        private const string Menu = "JJFlexWpf/NativeMenuBar.cs";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        // ------------------------------------------------------------------
        // The positive control, first. Everything below asserts that a string
        // IS present; without this, a broken reader would report every one of
        // them as fine.
        // ------------------------------------------------------------------

        [Fact]
        public void TheReaderFindsSomethingItIsSupposedToAndNotSomethingItIsNot()
        {
            var text = Read(FlexBase);
            Assert.Contains("public bool SelectProfile(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ThisStringIsNotInFlexBaseAnywhere", text, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Connect
        // ------------------------------------------------------------------

        [Fact]
        public void TheConnectPathAsksTheStewardshipRatherThanSelectingDirectly()
        {
            Assert.Contains("ApplyProfileStewardshipOnConnect()", Read(FlexBase),
                StringComparison.Ordinal);
        }

        [Fact]
        public void TheConnectPathNoLongerSelectsTheOperatorsDefaultsUnconditionally()
        {
            // The exact shape that shipped the defect (#403): three
            // SelectProfile calls fed from the operator's list, applied to
            // whatever radio connected, creating the transmit and microphone
            // ones on the radio when absent. If any of these three lines comes
            // back, the opt-in has been routed around.
            var text = Read(FlexBase);
            foreach (var gone in new[]
            {
                "crnt = GetProfilesByType(ProfileTypes.tx, GetDefaultProfiles());",
                "crnt = GetProfilesByType(ProfileTypes.mic, GetDefaultProfiles());",
                "if (crnt.Count > 0) SelectProfile(crnt[0]);",
            })
            {
                Assert.DoesNotContain(gone, text, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheRestorePointIsCapturedBeforeAnythingIsLoaded()
        {
            // The order is enforced by the plan and pinned by
            // ProfileStewardshipTests; this pins that the executor runs the
            // plan's actions IN ORDER rather than sorting or grouping them.
            var text = Read(FlexBase);
            int capture = text.IndexOf("case ProfileActionKind.CaptureRestorePoint:", StringComparison.Ordinal);
            int load = text.IndexOf("case ProfileActionKind.LoadOurs:", StringComparison.Ordinal);
            Assert.True(capture > 0 && load > capture,
                "RunProfileAction must handle a capture, and the plan's order must be preserved");
            Assert.Contains("foreach (var action in plan.Actions)", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AFailedCaptureAbandonsTheRestOfThePlanForThatType()
        {
            // Without the restore point, loading ours leaves the radio with no
            // record of what it was on and no way back.
            Assert.Contains(
                "plan.Actions.RemoveAll(a => a.ProfileType == action.ProfileType",
                Read(FlexBase), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // Disconnect — both paths, because they are different paths
        // ------------------------------------------------------------------

        [Fact]
        public void BothDisconnectPathsPutTheProfilesBack()
        {
            // Disconnect() is the operator's clean disconnect; Dispose is
            // teardown. They are reached independently, and a restore wired to
            // only one of them would work in testing and not in use.
            var text = Read(FlexBase);
            int occurrences = text.Split(new[] { "PutProfilesBackOnDisconnect()" }, StringSplitOptions.None).Length - 1;
            Assert.True(occurrences >= 3,
                "expected the definition plus a call from Disconnect and from Dispose, found "
                + occurrences);
        }

        [Fact]
        public void TheDisconnectReadDoesNotWaitOnAFreshAsk()
        {
            // Three types times a timeout is fifteen seconds added to a
            // disconnect the operator asked for, and a restore that makes
            // leaving feel broken is a restore that gets switched off.
            Assert.Contains("ReadProfileSituation(null, freshAsk: false)", Read(FlexBase),
                StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // The offer, and the fact that it is only ever an offer
        // ------------------------------------------------------------------

        [Fact]
        public void NothingRestoresAStrandedRestorePointWithoutTheOperator()
        {
            // RestoreStrandedProfiles is the ONLY route to
            // PlanOfferedRestore, and its only caller is a menu item a person
            // presses. If a connect-path or timer call ever appears here, the
            // clobber-the-owner's-own-repair failure is back.
            var flex = Read(FlexBase);
            int planned = flex.Split(new[] { "ProfileStewardship.PlanOfferedRestore" }, StringSplitOptions.None).Length - 1;
            Assert.Equal(1, planned);

            Assert.Contains("RestoreStrandedProfiles(types)", Read(Menu), StringComparison.Ordinal);
        }

        [Fact]
        public void TheOfferHasSomewhereToBeAccepted()
        {
            // A spoken sentence naming a place the operator cannot find is a
            // receipt for a dead end — the failure this codebase already names
            // for the profile-save procedure.
            var menu = Read(Menu);
            Assert.Contains("Put This Radio's Own Profiles Back", menu, StringComparison.Ordinal);
            Assert.Contains("Load My Profiles on This Radio", menu, StringComparison.Ordinal);

            // …and the item that would usually have nothing to do is only
            // built when it has something to do (#121, the stub-verb pattern).
            Assert.Contains("if (Rig.HasStrandedProfileRestorePoint)", menu, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // #414: a report must not mutate
        // ------------------------------------------------------------------

        [Fact]
        public void TheProfileReportDoesNotWalkARadioThatIsNotOptedIn()
        {
            // GenerateReport loads every stored profile in turn to compare
            // them. That is a write wearing the word "report", and its restore
            // is best-effort by construction.
            Assert.Contains("rig.ProfileIntent != ProfileGuestIntent.LoadMineAndPutBack",
                Read(Reporter), StringComparison.Ordinal);
        }
    }
}
