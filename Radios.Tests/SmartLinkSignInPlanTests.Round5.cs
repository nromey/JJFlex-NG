#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 5 (Codex's fourth re-check of #798/#613, B4): a waiting
    /// call is answered by its own session's push wherever the one display
    /// intake happens to be.
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        private static readonly MethodInfo BecomeTheIntake =
            typeof(FlexBase).GetMethod("becomeThePresenceIntake", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static void Engage(FlexBase rig)
        {
            Assert.True(BecomeTheIntake != null, "FlexBase.becomeThePresenceIntake is not where this test reaches it.");
            BecomeTheIntake.Invoke(rig, null);
        }

        /// <summary>
        /// Rig R holds A's rows and connects under A again. While R's call is
        /// replaying a held account, another rig takes the intake, and A's
        /// newer list (same connection, still naming the radio) is accepted
        /// and forwarded in full, to that other rig. Until round 5 R's call
        /// never heard it: the push went only to the intake, R's held rows were
        /// from the older list, and a connect that had always succeeded ended
        /// ConnectFailed after ten seconds, which setupRemote answers by
        /// cycling the session.
        /// </summary>
        [Fact]
        public void A_waiting_connect_hears_its_sessions_push_after_another_rig_takes_the_intake()
        {
            var (aSession, aWan) = Listed(Operator, Radio6300);
            var r = NewRig();
            Assert.Equal("Success", Connect(r, "test-jwt", Operator).ToString());
            var other = NewRig();
            Engage(other);
            Listed(Owner, ThirdRadio);                     // consumed by the other rig: R will replay it
            long before = aSession.RadioListSnapshot.ListRevision;

            bool moved = false;
            r.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || moved) return;
                moved = true;
                Engage(other);                             // the other rig takes the intake
                aWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });   // accepted and forwarded, to it
                Assert.True(aSession.RadioListSnapshot.ListRevision > before, "A's newer list was never accepted");
                Assert.Same(other, FlexBase.PresenceIntake);
            };

            var sw = Stopwatch.StartNew();
            var result = Connect(r, "test-jwt", Operator);
            sw.Stop();

            Assert.True(moved, "the schedule never ran");
            Assert.Equal("Success", result.ToString());
            Assert.True(sw.ElapsedMilliseconds < 5000, $"The connect waited {sw.ElapsedMilliseconds} ms for an answer its session had already given.");
        }

        /// <summary>
        /// The single-rig control: the same newer list during the same call,
        /// with R keeping the intake. The connect succeeds at once, so the
        /// test above fails only through the move of the intake.
        /// </summary>
        [Fact]
        public void With_the_intake_kept_the_same_connect_succeeds_at_once()
        {
            var (aSession, aWan) = Listed(Operator, Radio6300);
            var r = NewRig();
            Assert.Equal("Success", Connect(r, "test-jwt", Operator).ToString());
            var other = NewRig();
            Engage(other);
            Listed(Owner, ThirdRadio);
            long before = aSession.RadioListSnapshot.ListRevision;

            bool raised = false;
            r.WanIntakeStageReached = stage =>
            {
                if (stage != FlexBase.WanIntakeStage.ReplaySnapshotTaken || raised) return;
                raised = true;
                aWan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Radio6300) });
                Assert.True(aSession.RadioListSnapshot.ListRevision > before);
                Assert.Same(r, FlexBase.PresenceIntake);
            };

            var sw = Stopwatch.StartNew();
            var result = Connect(r, "test-jwt", Operator);
            sw.Stop();

            Assert.True(raised, "the schedule never ran");
            Assert.Equal("Success", result.ToString());
            Assert.True(sw.ElapsedMilliseconds < 5000);
        }
    }
}
