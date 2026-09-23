using System;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The roster relaxation is scoped to the owner ruling (Track G2
    /// re-review, section 4), and the two startup station-global writes
    /// have a gate (section 2). Pure rules over the production guard.
    /// </summary>
    public sealed class AuthorityScopeTests
    {
        private static RosterSnapshot OneIdentityBearingHandle()
        {
            var clock = new FakeStationClock();
            var roster = new RosterTracker(clock);
            roster.Reset(1);
            roster.ClientAdded(new RosterEntry(StationHarness.OurHandle, "our-client-id", true, "K5NER", "JJFlex"), 1);
            roster.OwnHandleEstablished(StationHarness.OurHandle, 1);
            return roster.Snapshot();
        }

        [Fact]
        public void TheSameOneHandleSnapshot_IsOnlyUsForTheOwner_AndUnknownForTheGuest()
        {
            var snapshot = OneIdentityBearingHandle();
            var policies = StationPolicies.Defaults();

            var owner = RosterGuard.ForAutomaticWrite(snapshot, policies.RosterAuthority);
            var guest = RosterGuard.ForAutomaticWrite(snapshot, policies.GuestSharedWriteAuthority);

            Assert.Equal(RosterVerdict.OnlyUs, owner.Verdict);
            Assert.Equal(RosterVerdict.Unknown, guest.Verdict);
            Assert.False(guest.MayChange, "Unknown by policy: waiting cannot change it");
            Assert.Contains("authority is not established", guest.Reason);
        }

        [Fact]
        public void ATestPositiveControl_CanOpenTheGuestAuthority_ProductionDoesNot()
        {
            var snapshot = OneIdentityBearingHandle();
            var opened = new StationPolicies { GuestSharedWriteAuthority = RosterAuthorityByLiveMembershipPolicy.Instance };
            Assert.Equal(RosterVerdict.OnlyUs, RosterGuard.ForAutomaticWrite(snapshot, opened.GuestSharedWriteAuthority).Verdict);
            Assert.Same(RosterAuthorityUnknownPolicy.Instance, StationPolicies.Current.GuestSharedWriteAuthority);
        }

        // ── the shack speaker follows PC audio, and nothing else ──
        //
        // RULED by Noel 2026-09-22 21:33, in his words: "mute the shack
        // speaker if you're going PC audio, unmute it if you're not using it.
        // If for some really weird reason you want to have the speaker
        // unmuted while you're PC audio connected, then cool. Why make it
        // complicated." Track G3 had the two writes behind the owner gate, so
        // company on the owner's radio stopped the mute; that is reverted.
        //
        // The three writes are lines inside Connect and remoteAudioProc, so
        // the instrument is the production source, as it is for every other
        // connect-path write in ChangeNothingGuardTests. The behavioural
        // proof is a physical press, and it is on the bench list.

        private static string FlexBaseSource()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Radios", "FlexBase.cs"));
        }

        [Theory]
        [InlineData("IsMuteLocalAudioWhenRemoteOn=true on remote audio start", true)]
        [InlineData("IsMuteLocalAudioWhenRemoteOn=false on local connect", false)]
        [InlineData("IsMuteLocalAudioWhenRemoteOn=false on remote audio stop", false)]
        public void EachMuteWrite_TakesTheHoldAndTheValuePcAudioAsksFor(string guard, bool muted)
        {
            string text = FlexBaseSource();
            int at = text.IndexOf("if (!GuardSkips(\"" + guard + "\"))", StringComparison.Ordinal);
            Assert.True(at > 0, "the guarded write '" + guard + "' is gone");
            string body = text.Substring(at, 200);
            Assert.Contains("IsMuteLocalAudioWhenRemoteOn = " + (muted ? "true" : "false") + ";", body, StringComparison.Ordinal);
        }

        [Fact]
        public void CompanyOnTheRadio_CannotChangeTheAnswer_BecauseNoWriteAsks()
        {
            // "Muted with company" and "muted without" are the same case, and
            // this is what makes them the same case: the decision has no
            // roster, ownership or company term in it at all. A remote client
            // cannot know who is in the ROOM, which is who the speaker is
            // actually shared with.
            string text = FlexBaseSource();
            foreach (var guard in new[]
            {
                "IsMuteLocalAudioWhenRemoteOn=true on remote audio start",
                "IsMuteLocalAudioWhenRemoteOn=false on local connect",
                "IsMuteLocalAudioWhenRemoteOn=false on remote audio stop",
            })
            {
                int at = text.IndexOf("if (!GuardSkips(\"" + guard + "\"))", StringComparison.Ordinal);
                string body = text.Substring(at, 200);
                foreach (var forbidden in new[] { "Roster", "OnlyUs", "Ownership", "OwnerShared" })
                {
                    Assert.DoesNotContain(forbidden, body, StringComparison.Ordinal);
                }
            }
            Assert.DoesNotContain("OwnerSharedWriteGate", text, StringComparison.Ordinal);
        }

        // ── TNF: the owner's connect sets it; a non-owner alone may set it
        //    only if it is off ──
        //
        // REFINED by Noel 2026-09-22 21:37, in his words: "A non-owner could
        // set it if they're the only person on, but if the TNF is enabled,
        // i.e. turned on by the owner, don't allow a change. If it's
        // disabled, then the non-owner should be able to turn it on and set
        // it temporarily. Connect will help with all this junk."

        private static StationPolicyFacts NonOwner() => new StationPolicyFacts
        {
            Connected = true, Ownership = RadioOwnership.SomeoneElses,
            Intent = ProfileGuestIntent.LoadMineAndPutBack, Serial = "1234",
        };

        private static RosterJudgement OnlyUs() => new RosterJudgement(RosterVerdict.OnlyUs, "test", 1);

        [Fact]
        public void ANonOwnerAloneOnTheRadio_WithTnfOff_MaySetIt()
        {
            Assert.Null(NonOwnerTnfGate.Refusal(NonOwner(), OnlyUs(), tnfAlreadyOn: false));
        }

        [Fact]
        public void ANonOwnerAloneOnTheRadio_WithTnfAlreadyOn_LeavesIt()
        {
            string why = NonOwnerTnfGate.Refusal(NonOwner(), OnlyUs(), tnfAlreadyOn: true);
            Assert.NotNull(why);
            // The assumption is written into the refusal itself, because the
            // app sees only on or off and never who turned it on.
            Assert.Contains("cannot tell who", why, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("owner present")]
        [InlineData("roster unknown")]
        public void ANonOwnerWithAnyoneElseOnTheRadio_LeavesTnfAlone(string what)
        {
            var roster = what == "owner present"
                ? new RosterJudgement(RosterVerdict.OthersPresent, "K5NER", 1)
                : new RosterJudgement(RosterVerdict.Unknown, "not established", 1);
            Assert.NotNull(NonOwnerTnfGate.Refusal(NonOwner(), roster, tnfAlreadyOn: false));
        }

        [Fact]
        public void TheOwnersOwnRadio_IsNotThisGatesBusiness()
        {
            // The owner's TNF is RunOwnerInitialization's first write, under
            // its own gate; this one must never be the path that sets it.
            var f = NonOwner();
            f.Ownership = RadioOwnership.Mine;
            Assert.NotNull(NonOwnerTnfGate.Refusal(f, OnlyUs(), tnfAlreadyOn: false));
        }

        [Theory]
        [InlineData("hold")]
        [InlineData("not connected")]
        public void ANonOwnersTnfWrite_StillTakesTheHoldAndTheConnection(string what)
        {
            var f = NonOwner();
            if (what == "hold") f.HoldArmed = true; else f.Connected = false;
            Assert.NotNull(NonOwnerTnfGate.Refusal(f, OnlyUs(), tnfAlreadyOn: false));
        }

        [Fact]
        public void InProduction_TheNonOwnerTnfPathIsClosed_BecauseTheGuestAuthorityIsUnknown()
        {
            // The guest's shared-write authority is Unknown until a bench
            // establishes it (Track G3), and a non-owner's TNF is a
            // station-global write of exactly that class. So this path is
            // built to the ruling and does not run in production yet. Said
            // out loud here so a green suite does not read as a live feature.
            var snapshot = OneIdentityBearingHandle();
            var guest = RosterGuard.ForAutomaticWrite(snapshot, StationPolicies.Current.GuestSharedWriteAuthority);
            Assert.Equal(RosterVerdict.Unknown, guest.Verdict);
            Assert.NotNull(NonOwnerTnfGate.Refusal(NonOwner(), guest, tnfAlreadyOn: false));
        }
    }
}
