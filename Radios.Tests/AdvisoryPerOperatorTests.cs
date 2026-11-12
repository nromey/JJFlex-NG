using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1073: every "don't show this again" belongs to the operator (ruled by
    /// Noel 2026-10-19 and 2026-11-11 19:42). One per-operator store behind the
    /// facade every box uses, following the operator change (#1072); the
    /// Windows-account set is the store until an operator is chosen, and each
    /// operator's own store starts from a copy of it, once.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class AdvisoryPerOperatorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjflex-1073-" + Guid.NewGuid().ToString("N"));
        private readonly string _a = "A_" + Guid.NewGuid().ToString("N");
        private readonly string _b = "B_" + Guid.NewGuid().ToString("N");

        private static readonly AdvisoryKey One = new("test-1073-one", "The first test message");
        private static readonly AdvisoryKey Two = new("test-1073-two", "The second test message");

        public AdvisoryPerOperatorTests()
        {
            Directory.CreateDirectory(_dir);
            OperatorChange.ResetForTests();
            ForgetAppStores();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            ForgetAppStores();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>Remove what these tests wrote under the process's settings root (a throwaway tree).</summary>
        private void ForgetAppStores()
        {
            string root = RadioConfig.AppDataRoot;
            foreach (string op in new[] { _a, _b })
                try { File.Delete(AdvisorySuppression.OperatorFile(root, op)); } catch { }
            try { AdvisorySuppression.Default.Unsuppress(One.Value); AdvisorySuppression.Default.Unsuppress(Two.Value); } catch { }
        }

        private static AdvisorySuppressionStore Store(string path, string? carry = null) =>
            new(path, carry, describe: k => "label of " + k);

        // ---- the store: carried over once ----

        [Fact]
        public void An_operators_first_store_starts_from_the_windows_account_set_and_leaves_that_file_alone()
        {
            string account = Path.Combine(_dir, "account.json");
            Store(account).Suppress(One);
            string accountBefore = File.ReadAllText(account);

            var mine = Store(Path.Combine(_dir, "mine.json"), carry: account);

            Assert.True(mine.IsSuppressed(One));
            Assert.True(File.Exists(Path.Combine(_dir, "mine.json")), "the carry-over must be written at once, so it is made only once");
            Assert.Equal(accountBefore, File.ReadAllText(account));
        }

        [Fact]
        public void A_later_restore_is_never_undone_by_the_carry_over()
        {
            string account = Path.Combine(_dir, "account.json");
            string minePath = Path.Combine(_dir, "mine.json");
            Store(account).Suppress(One);

            var mine = Store(minePath, carry: account);
            Assert.True(mine.IsSuppressed(One));
            Assert.Equal(1, mine.Clear());

            // Next session: the account set still lists it; the operator's own does not.
            var again = Store(minePath, carry: account);
            Assert.False(again.IsSuppressed(One));
            Assert.Equal(0, again.Count);
        }

        [Fact]
        public void With_no_windows_account_set_the_operators_store_starts_empty_and_exists()
        {
            string minePath = Path.Combine(_dir, "mine.json");
            var mine = Store(minePath, carry: Path.Combine(_dir, "none.json"));
            Assert.Equal(0, mine.Count);
            Assert.True(File.Exists(minePath));
        }

        [Fact]
        public void A_save_leaves_no_temporary_file_and_a_whole_store()
        {
            string path = Path.Combine(_dir, "mine.json");
            File.WriteAllText(path + ".tmp", "left by a write that never finished");
            var s = Store(path);
            s.Suppress(One);
            s.Suppress(Two);
            Assert.False(File.Exists(path + ".tmp"));
            var reread = Store(path);
            Assert.True(reread.IsSuppressed(One) && reread.IsSuppressed(Two));
        }

        [Fact]
        public void The_store_writes_through_a_temporary_file_and_a_replace()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "AdvisorySuppression.cs"));
            int save = src.IndexOf("private void Save()", StringComparison.Ordinal);
            string body = src.Substring(save, src.IndexOf("catch (Exception ex)", save, StringComparison.Ordinal) - save);
            Assert.DoesNotContain("File.WriteAllText(_filePath", body);
            Assert.Matches(new Regex(@"File\.WriteAllText\(temp,[\s\S]*File\.Move\(temp, _filePath, overwrite: true\)"), body);
        }

        // ---- the facade follows the operator ----

        [Fact]
        public void Before_any_operator_the_windows_account_file_is_the_store_in_use()
        {
            Assert.Equal(AdvisorySuppression.AccountFile(RadioConfig.AppDataRoot), AdvisorySuppression.Default.FilePath);
        }

        /// <summary>
        /// Silence a message as A; switch to B, with or without a radio: B sees
        /// it. Back to A: silenced. Each operator's file is under the settings
        /// root this run uses.
        /// </summary>
        [Fact]
        public void A_message_one_operator_silenced_still_shows_for_another_and_stays_silenced_for_the_first()
        {
            string root = RadioConfig.AppDataRoot;
            OperatorChange.Establish(root, _a);
            Assert.Equal(AdvisorySuppression.OperatorFile(root, _a), AdvisorySuppression.Default.FilePath);
            AdvisorySuppression.Suppress(One);
            Assert.True(AdvisorySuppression.IsSuppressed(One));

            OperatorChange.Announce(root, _b);
            Assert.Equal(AdvisorySuppression.OperatorFile(root, _b), AdvisorySuppression.Default.FilePath);
            Assert.False(AdvisorySuppression.IsSuppressed(One));
            Assert.Empty(AdvisorySuppression.Snapshot().Where(s => s.Key == One.Value));

            OperatorChange.Announce(root, _a);
            Assert.True(AdvisorySuppression.IsSuppressed(One));
            Assert.Contains(AdvisorySuppression.Snapshot(), s => s.Key == One.Value);

            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(AdvisorySuppression.Default.FilePath), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Each_operators_first_store_carries_over_the_windows_account_set()
        {
            string root = RadioConfig.AppDataRoot;
            AdvisorySuppression.Suppress(Two);   // no operator yet: the Windows-account set
            try
            {
                OperatorChange.Establish(root, _a);
                Assert.True(AdvisorySuppression.IsSuppressed(Two));
                OperatorChange.Announce(root, _b);
                Assert.True(AdvisorySuppression.IsSuppressed(Two));
            }
            finally
            {
                OperatorChange.ResetForTests();
                AdvisorySuppression.Unsuppress(Two.Value);
            }
        }

        // ---- Silenced Messages, and its receipt ----

        [Fact]
        public void Silenced_Messages_reads_the_facade_and_its_receipt_names_the_message_alone()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "SilencedMessagesDialog.xaml.cs"));
            Assert.Contains("_store = store ?? AdvisorySuppression.Default;", src);
            int restore = src.IndexOf("private void RestoreButton_Click(", StringComparison.Ordinal);
            string body = src.Substring(restore, src.IndexOf("settings.silenced.restored_one", restore, StringComparison.Ordinal) - restore);
            Assert.Contains("string label = row.Label;", body);
            Assert.DoesNotContain("row.Text", body);
        }
    }
}
