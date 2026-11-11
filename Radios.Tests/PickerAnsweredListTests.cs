#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Radios;
using Radios.SmartLink;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #398, repair three (Codex's re-check of repair two, B1, first trigger):
    /// the radio picker's Network identity card says SmartLink does not list a
    /// radio only once a SmartLink list has ANSWERED without it. A session is
    /// connected before its first list arrives, and again after every
    /// reconnect until the new connection's list lands; repair two read
    /// "connected" as "answered", so in that window the card denied a listing
    /// nothing had answered.
    /// </summary>
    /// <remarks>
    /// <para>The created states come from a REAL <see cref="WanSessionOwner"/>
    /// over the in-memory <see cref="MockWanServer"/>: connected with no list
    /// yet, a genuine empty list, a list naming the radio, and a reconnect
    /// whose new connection has not answered. Nothing reaches a network, a
    /// radio, the coordinator singleton or a settings tree; the owner is
    /// built directly and disposed by each test.</para>
    /// <para>What the picker does with the rule (that its refresh stamps it
    /// from every session, and that a session event repaints the card) is
    /// held by <c>JJFlexWpf.Tests/PickerIdentityCardFollowsTests</c>.</para>
    /// </remarks>
    public sealed class PickerAnsweredListTests
    {
        private const string Account = "default-account";
        private const string Serial = "1234-5678-6300-0398";
        private const string Other = "1234-5678-8600-0398";

        /// <summary>
        /// Connected before the first list: not asked. Then a genuine empty
        /// list: not listed. Then a list naming the radio: not an answer
        /// without it. Then a list without it again, and a reconnect whose new
        /// connection has not answered: not asked again, until it answers.
        /// </summary>
        [Fact]
        public void The_card_denies_a_listing_only_once_the_live_connection_has_answered_without_it()
        {
            var (owner, wan) = NewSession();
            try
            {
                owner.Connect();
                WaitUntil(() => owner.IsConnected, "the mock session never reported connected");

                // Connected, and no list yet: the created state of the first
                // trigger, read from the real owner.
                var snapshot = owner.RadioListSnapshot;
                Assert.True(snapshot.SessionConnected);
                Assert.False(snapshot.ArrivedOnTheLiveConnection);
                Assert.Null(owner.LastRadioListUtc);
                Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "", Lists(owner)));
                Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, Account, Lists(owner)));
                Assert.Equal(PickerListing.NotAsked, Verdict(owner, ""));

                // A genuine empty list: SmartLink has answered, without it.
                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
                WaitUntil(() => owner.RadioListSnapshot.ArrivedOnTheLiveConnection,
                    "the empty list never registered as arrived on the live connection");
                Assert.True(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "", Lists(owner)));
                Assert.Equal(PickerListing.NotListed, Verdict(owner, ""));
                Assert.Equal(PickerListing.NotListed, Verdict(owner, Account));

                // A list that names the radio is not an answer without it,
                // whatever the row has caught up with yet.
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Serial) });
                WaitUntil(() => owner.RadioListSnapshot.Radios.Count == 1, "the second list never arrived");
                Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "", Lists(owner)));
                Assert.Equal(PickerListing.NotAsked, Verdict(owner, ""));

                // A list carrying another radio only: answered without it.
                wan.RaiseWanRadioRadioListReceived(new[] { WanRadio(Other) });
                WaitUntil(() => owner.RadioListSnapshot.Radios.Count == 1
                    && owner.RadioListSnapshot.Radios[0].Serial == Other, "the third list never arrived");
                Assert.Equal(PickerListing.NotListed, Verdict(owner, ""));

                // The ordinary reconnection: the transport drops, the monitor
                // dials a new connection, and that connection has not
                // answered. Connected again, but nothing has answered on it.
                int dialsBefore = wan.ConnectCallCount;
                wan.ForceIsConnected(false);
                WaitUntil(() => wan.ConnectCallCount > dialsBefore && owner.IsConnected,
                    "the session never dialled a new connection after the drop");
                Assert.True(owner.RadioListSnapshot.SessionConnected);
                Assert.False(owner.RadioListSnapshot.ArrivedOnTheLiveConnection);
                Assert.NotNull(owner.LastRadioListUtc);   // an old list is held, and is history
                Assert.Equal(PickerListing.NotAsked, Verdict(owner, ""));

                wan.RaiseWanRadioRadioListReceived(Array.Empty<Radio>());
                WaitUntil(() => owner.RadioListSnapshot.ArrivedOnTheLiveConnection,
                    "the new connection's list never registered as arrived");
                Assert.Equal(PickerListing.NotListed, Verdict(owner, ""));
            }
            finally
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// The rule across sessions, from snapshots built by hand: the card's
        /// denial is about SmartLink as a whole, so one connected session that
        /// has not answered holds it back whichever account it is; a session
        /// that is not connected is not asking and is passed over; with an
        /// account named for the row, that account's own list must be one of
        /// the answers.
        /// </summary>
        [Fact]
        public void Every_connected_session_must_have_answered_and_the_rows_account_among_them()
        {
            var answeredEmpty = Snapshot(connected: true, arrived: true);
            var waiting = Snapshot(connected: true, arrived: false);
            var gone = Snapshot(connected: false, arrived: false);
            var naming = Snapshot(connected: true, arrived: true, WanRadio(Serial));

            Assert.True(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "",
                new[] { ("a@example.com", answeredEmpty) }));
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "",
                new[] { ("a@example.com", answeredEmpty), ("b@example.com", waiting) }));
            Assert.True(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "",
                new[] { ("a@example.com", answeredEmpty), ("b@example.com", gone) }));
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "",
                new[] { ("a@example.com", answeredEmpty), ("b@example.com", naming) }));

            // Bound to b: b's own list must have answered.
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "b@example.com",
                new[] { ("a@example.com", answeredEmpty), ("b@example.com", gone) }));
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "b@example.com",
                new[] { ("a@example.com", answeredEmpty) }));
            Assert.True(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "B@Example.com",
                new[] { ("a@example.com", answeredEmpty), ("b@example.com", answeredEmpty) }));

            // No session, no serial, no lists: nothing has answered.
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "",
                Array.Empty<(string, SessionRadioListSnapshot)>()));
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout("", "",
                new[] { ("a@example.com", answeredEmpty) }));
            Assert.False(NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, "", null));
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private static PickerListing Verdict(WanSessionOwner owner, string boundAccount)
            => NetworkIdentityInfo.PickerListingFor(wanAvailable: false, wanUnconfirmed: false,
                discoverySettled: true, refreshInFlight: false,
                NetworkIdentityInfo.SmartLinkAnsweredWithout(Serial, boundAccount, Lists(owner)));

        private static (string, SessionRadioListSnapshot)[] Lists(WanSessionOwner owner)
            => new[] { (owner.AccountId, owner.RadioListSnapshot) };

        private static SessionRadioListSnapshot Snapshot(bool connected, bool arrived, params Radio[] radios)
            => new(radios, arrived ? DateTime.UtcNow : null, connected, arrived, arrived ? 1 : -1, "s-398")
            { ListRevision = arrived ? 1 : 0 };

        private static (WanSessionOwner owner, MockWanServer wan) NewSession()
        {
            var wan = new MockWanServer();
            var owner = new WanSessionOwner(
                sessionId: Guid.NewGuid().ToString("N").Substring(0, 12),
                accountId: Account,
                wanServer: wan,
                audioSink: new DirectPassthroughSink(),
                backoffScheduleMs: new[] { 50, 50, 50 });
            return (owner, wan);
        }

        private static Radio WanRadio(string serial) => SmartLinkRegistrationReplayTests.WanRadio(serial);

        private static void WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
            => Assert.True(SpinWait.SpinUntil(condition, timeoutMs), because);
    }
}
