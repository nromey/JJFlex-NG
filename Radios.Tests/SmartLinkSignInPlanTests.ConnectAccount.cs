#nullable enable

using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #405, ruled by Noel 2026-11-08 ~16:28: the radio list's "another
    /// account" flag and the Radio Associations row name "the account
    /// connecting will actually use (the first matching account saved on this
    /// computer), not the one chosen or last seen".
    /// <see cref="FlexBase.ConnectAccountFor"/> is that answer, from the
    /// connect's own plan, with no tier walk of its own.
    /// </summary>
    public sealed partial class SmartLinkSignInPlanTests
    {
        private const string ChosenNotSaved = "chosen-elsewhere@example.com";

        private static void Record(string preferred, string lastSeen)
            => Assert.True(RadioConfig.Update(Radio6300, cfg =>
            {
                cfg.PreferredAccount = preferred;
                cfg.LastSeenViaAccount = lastSeen;
            }));

        /// <summary>
        /// The case the ruling exists for: the chosen account is not saved
        /// here and the last-seen one is. The plan signs in as the last-seen
        /// account, so that is the one named, and the announcement agrees.
        /// </summary>
        [Fact]
        public void A_chosen_account_not_saved_here_gives_way_to_the_saved_last_seen_one()
        {
            Record(ChosenNotSaved, Owner);
            try
            {
                Assert.Equal(Owner, FlexBase.ConnectAccountFor(Radio6300, new[] { Operator, Owner }));
                Assert.Equal(Owner, FlexBase.AccountThatWillBroker(Radio6300, Operator, new[] { Operator, Owner }));
            }
            finally { Record("", ""); }
        }

        [Fact]
        public void A_saved_chosen_account_is_the_one_named()
        {
            Record(Owner, Operator);
            try { Assert.Equal(Owner, FlexBase.ConnectAccountFor(Radio6300, new[] { Operator, Owner })); }
            finally { Record("", ""); }
        }

        /// <summary>
        /// When the plan names no account (nothing saved lists the radio, or
        /// the connect signs in through the default), the answer is EMPTY, not
        /// the account in play: the callers then keep their own words rather
        /// than name an account connecting will not use. The announcement's
        /// resolver still falls back to the account in play, as before.
        /// </summary>
        [Fact]
        public void When_the_plan_names_no_account_the_answer_is_empty()
        {
            Record(ChosenNotSaved, "seen-elsewhere@example.com");
            try
            {
                Assert.Equal("", FlexBase.ConnectAccountFor(Radio6300, new[] { Operator, Owner }));
                Assert.Equal(Operator, FlexBase.AccountThatWillBroker(Radio6300, Operator, new[] { Operator, Owner }));
            }
            finally { Record("", ""); }
            Assert.Equal("", FlexBase.ConnectAccountFor(SomeOtherRadio, new[] { Operator, Owner }));
            Assert.Equal("", FlexBase.ConnectAccountFor("", new[] { Operator }));
        }
    }
}
