using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1106, ruled by Noel 2026-11-12 08:01 (J1): a brand-new operator starts
    /// with nothing silenced; only operators that existed at the upgrade get the
    /// Windows account's silenced messages, once. The carry-over is made for
    /// every operator listed at the first start after the upgrade, then a marker
    /// is written and it never runs again; the store itself no longer carries
    /// anything over.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AdvisoryCarryOverAtUpgradeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-1106-" + Guid.NewGuid().ToString("N"));
        private static readonly AdvisoryKey One = new("test-1106-one", "The first test message");
        private static readonly AdvisoryKey Other = new("test-1106-other", "Another test message");

        public AdvisoryCarryOverAtUpgradeTests()
        {
            Directory.CreateDirectory(_root);
            OperatorChange.ResetForTests();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private AdvisorySuppressionStore Account() => new(AdvisorySuppression.AccountFile(_root), describe: k => k);
        private AdvisorySuppressionStore StoreFor(string op) => AdvisorySuppression.StoreFor(_root, op);

        [Fact]
        public void An_install_upgraded_from_before_per_operator_messages_carries_the_set_to_every_listed_operator()
        {
            Account().Suppress(One);
            string accountBefore = File.ReadAllText(AdvisorySuppression.AccountFile(_root));

            Assert.True(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op", "B_op" }));

            Assert.True(StoreFor("A_op").IsSuppressed(One));
            Assert.True(StoreFor("B_op").IsSuppressed(One));
            Assert.True(File.Exists(AdvisorySuppression.CarriedOverMarkerFile(_root)));
            Assert.Equal(accountBefore, File.ReadAllText(AdvisorySuppression.AccountFile(_root)));
        }

        [Fact]
        public void An_operator_added_after_the_upgrade_starts_with_nothing_silenced()
        {
            Account().Suppress(One);
            AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" });

            // Next year: a new operator, the account set still holding One.
            var next = StoreFor("C_new");
            Assert.False(next.IsSuppressed(One));
            Assert.Equal(0, next.Count);
        }

        [Fact]
        public void A_renamed_operator_does_not_take_the_account_set_again()
        {
            Account().Suppress(One);
            AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" });
            Assert.Equal(1, StoreFor("A_op").Clear());   // A restored it

            // A renamed (new callsign or name) is a new unique name, so a new file.
            Assert.False(StoreFor("A2_renamed").IsSuppressed(One));
        }

        [Fact]
        public void The_carry_over_runs_once_and_a_later_restore_is_never_undone()
        {
            Account().Suppress(One);
            Assert.True(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" }));
            Assert.Equal(1, StoreFor("A_op").Clear());

            // A second start, with an operator added in between: nothing is copied.
            Assert.False(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op", "D_added" }));
            Assert.False(StoreFor("A_op").IsSuppressed(One));
            Assert.False(StoreFor("D_added").IsSuppressed(One));
        }

        [Fact]
        public void An_operator_whose_own_file_already_exists_keeps_it()
        {
            Account().Suppress(One);
            // A's own file, written before the pass (a build between #1073 and
            // this one): a message silenced and then restored, so it holds nothing.
            var mine = StoreFor("A_op");
            mine.Suppress(Other);
            Assert.True(mine.Unsuppress(Other.Value));
            Assert.True(File.Exists(AdvisorySuppression.OperatorFile(_root, "A_op")));

            AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" });
            Assert.False(StoreFor("A_op").IsSuppressed(One));
        }

        [Fact]
        public void With_no_account_set_the_marker_is_still_written_and_nothing_is_created()
        {
            Assert.True(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" }));
            Assert.True(File.Exists(AdvisorySuppression.CarriedOverMarkerFile(_root)));
            Assert.False(File.Exists(AdvisorySuppression.OperatorFile(_root, "A_op")));

            // A set made later on the account (no operator chosen) is not carried to anyone.
            Account().Suppress(One);
            Assert.False(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op" }));
            Assert.False(StoreFor("A_op").IsSuppressed(One));
        }

        /// <summary>
        /// Batch 12 repair round 1 (Codex's Noted item on #1106): the marker
        /// was written even when an operator's copy failed, which made a
        /// partial carry-over permanent. Now the marker waits until every listed
        /// operator's file is there, so the next start makes the missing copy,
        /// and a copy that was written is never made again.
        /// </summary>
        [Fact]
        public void A_copy_that_could_not_be_written_is_made_at_the_next_start_and_a_written_one_never_twice()
        {
            Account().Suppress(One);
            // A's copy cannot be written: a folder stands where its file goes.
            string blocked = AdvisorySuppression.OperatorFile(_root, "A_op");
            Directory.CreateDirectory(blocked);

            Assert.True(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op", "B_op" }));
            Assert.False(File.Exists(AdvisorySuppression.CarriedOverMarkerFile(_root)));
            Assert.True(StoreFor("B_op").IsSuppressed(One));
            Assert.Equal(1, StoreFor("B_op").Clear());   // B restores it before the next start

            Directory.Delete(blocked);
            Assert.True(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op", "B_op" }));
            Assert.True(File.Exists(AdvisorySuppression.CarriedOverMarkerFile(_root)));
            Assert.True(StoreFor("A_op").IsSuppressed(One));
            Assert.False(StoreFor("B_op").IsSuppressed(One));   // never copied into twice
            Assert.False(AdvisorySuppression.CarryOverAtUpgrade(_root, new[] { "A_op", "B_op" }));
        }

        /// <summary>The application makes the carry-over at startup, before the operator in use is recorded.</summary>
        [Fact]
        public void Startup_makes_the_carry_over_before_the_operator_is_recorded()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "globals.vb"));
            int carry = src.IndexOf("Radios.AdvisorySuppression.CarryOverAtUpgrade(", StringComparison.Ordinal);
            int establish = src.IndexOf("Radios.OperatorChange.Establish(BaseConfigDir", StringComparison.Ordinal);
            Assert.True(carry > 0, "globals.vb makes the silenced-messages carry-over at startup");
            Assert.True(establish > carry, "the carry-over comes before the operator in use is recorded");
        }
    }
}
