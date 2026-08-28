using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// The check nobody was doing (#339): the per-field Home key maps against
    /// what <c>KeyInventory</c> tells six surfaces they are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The invariant existed in writing and was enforced by nobody.</b>
    /// KeyInventory's own doc comment says "If a field handler in
    /// FreqOutHandlers gains or loses a key, update the tables here — the six
    /// surfaces above follow automatically." That is an instruction to a
    /// human, and a handler that gains a key lies to all six at once, in
    /// silence. This is the same sentence, addressed to the build.
    /// </para>
    /// <para>
    /// <b>WHAT IT PROVES AND WHAT IT CANNOT.</b> It proves a key is BOUND. It
    /// cannot prove the key WORKS — the Alt+L binding that shipped completely
    /// dead on 2026-08-13 was statically perfect. This complements PRESS THE
    /// KEY; the keyboard audit keeps its final step, and a green run here is
    /// not a substitute for pressing anything.
    /// </para>
    /// <para>
    /// <b>Nothing is hardcoded, deliberately.</b> The dispatch table, the
    /// handled keys and the declared keys are all read from source on every
    /// run, so the assertions hold whatever the maps become. That is what
    /// makes this a gate on the next person to touch a field handler rather
    /// than a snapshot of one afternoon.
    /// </para>
    /// </remarks>
    public sealed class FieldKeyMapTests
    {
        private readonly ITestOutputHelper _output;

        public FieldKeyMapTests(ITestOutputHelper output) => _output = output;

        // ────────────────────────────────────────────────────────────────
        //  Positive controls, before anything else
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A scanner that finds nothing and a codebase with nothing wrong
        /// produce the same output. So the scanner is made to find, in a
        /// synthetic handler, every shape the real ones use — including the
        /// two a text scan gets wrong: a modifier chain hung off a character
        /// case, and a shared body whose branch is guarded by a parameter its
        /// callers bind differently.
        /// </summary>
        [Fact]
        public void The_scanner_finds_the_shapes_it_claims_to_find()
        {
            const string handlers = @"
using System.Windows.Input;
class FreqOutHandlers
{
    public void AdjustAlpha(DisplayField field, KeyEventArgs e)
    {
        var key = RawKey(e);
        char ch = KeyToChar(e);
        bool unmodified = Keyboard.Modifiers == ModifierKeys.None;
        switch (key)
        {
            case Key.Left:
            case Key.Right:
            {
                int direction = 1;
                if (Keyboard.Modifiers == ModifierKeys.Alt) { Widen(direction); }
                else if (Keyboard.Modifiers == ModifierKeys.Shift) { Narrow(direction); }
                break;
            }
            default:
                if (ch == 'S' && Keyboard.Modifiers == ModifierKeys.Shift) { Speak(); }
                else if (ch >= 'A' && ch <= 'C' && unmodified) { Jump(ch); }
                else if (ch == 'Z' && _inSomeMode) { Rare(); }
                break;
        }
        if (!e.Handled) TryHandleUniversalHomeKey(e);
    }

    public void AdjustBeta(DisplayField field, KeyEventArgs e) { Shared(true, e); }
    public void AdjustGamma(DisplayField field, KeyEventArgs e) { Shared(false, e); }

    private void Shared(bool isBeta, KeyEventArgs e)
    {
        char ch = KeyToChar(e);
        if (ch == '=' && isBeta) { CopyOver(); }
        if (ch == 'W') { Both(); }
        if (!e.Handled) TryHandleUniversalHomeKey(e);
    }

    private bool TryHandleUniversalHomeKey(KeyEventArgs e)
    {
        char ch = KeyToChar(e);
        if (ch == 'M') { Mute(); return true; }
        return false;
    }
}";
            const string dispatch = @"
class MainWindow
{
    public enum UIMode { Classic, Modern }
    private void FreqOut_FieldKeyDown(DisplayField field, KeyEventArgs e)
    {
        if (ActiveUIMode == UIMode.Modern)
        {
            switch (field.Key)
            {
                case ""Alpha"": _h.AdjustAlpha(field, e); break;
            }
            return;
        }
        switch (field.Key)
        {
            case ""Alpha"": _h.AdjustAlpha(field, e); break;
            case ""Beta"": _h.AdjustBeta(field, e); break;
            case ""Gamma"": _h.AdjustGamma(field, e); break;
        }
    }
}";
            const string inventory = @"
public static class KeyInventory
{
    private static readonly FixedKeyEntry[] UniversalHome =
    {
        new(""Home"", ""Any Home field"", ""M"", ""Mute"", new[] { ""mute"" }),
    };
    private static readonly FixedKeyEntry[] FieldKeys =
    {
        new(""Alpha.Modern"", ""Alpha"", ""Alt+Left / Alt+Right"", ""Size"", new[] { ""size"" }),
        new(""Alpha.Modern"", ""Alpha"", ""Shift+Left / Shift+Right"", ""Size fine"", new[] { ""size"" }),
        new(""Alpha.Modern"", ""Alpha"", ""Shift+S"", ""Speak"", new[] { ""speak"" }),
        new(""Alpha.Modern"", ""Alpha"", ""A-C"", ""Jump"", new[] { ""jump"" }),
        new(""Alpha.Classic"", ""Alpha"", ""Alt+Left / Alt+Right"", ""Size"", new[] { ""size"" }),
        new(""Alpha.Classic"", ""Alpha"", ""Shift+Left / Shift+Right"", ""Size fine"", new[] { ""size"" }),
        new(""Alpha.Classic"", ""Alpha"", ""Shift+S"", ""Speak"", new[] { ""speak"" }),
        new(""Alpha.Classic"", ""Alpha"", ""A-C"", ""Jump"", new[] { ""jump"" }),
        new(""Beta"", ""Beta"", ""="", ""Copy"", new[] { ""copy"" }),
        new(""Beta"", ""Beta"", ""W"", ""Both"", new[] { ""both"" }),
        new(""Gamma"", ""Gamma"", ""W"", ""Both"", new[] { ""both"" }),
    };
    private static readonly FixedKeyEntry[] HomeNavigation =
    {
        new(""HomeNav"", ""Home"", ""Left / Right"", ""Move"", new[] { ""move"" }),
    };
}";
            var result = FieldKeyMapScan.Scan(handlers, dispatch, inventory);
            _output.WriteLine(FieldKeyMapScan.Report(result));

            // Both tuning modes were derived, and neither was written down here.
            Assert.Equal(
                new[] { "Alpha.Classic", "Alpha.Modern", "Beta", "Gamma" },
                result.Fields.Select(f => f.Context).OrderBy(c => c, StringComparer.Ordinal).ToArray());

            var alpha = result.Fields.Single(f => f.Context == "Alpha.Modern");
            var bound = alpha.Claims.Select(c => c.Binding.Display).ToHashSet(StringComparer.Ordinal);

            // The modifier chain hung off a character case: four bindings from
            // two labels, which is the shape a grep reads as two.
            Assert.Contains("Alt+Left", bound);
            Assert.Contains("Alt+Right", bound);
            Assert.Contains("Shift+Left", bound);
            Assert.Contains("Shift+Right", bound);
            Assert.DoesNotContain("Left", bound);

            Assert.Contains("Shift+S", bound);
            Assert.Contains("A", bound);
            Assert.Contains("B", bound);
            Assert.Contains("C", bound);

            // Followed through `if (!e.Handled) TryHandleUniversalHomeKey(e)`.
            Assert.Contains("M", bound);

            // Claimed only inside a private mode field: bound, and not spent
            // from the free-letter budget.
            Assert.Contains(alpha.Claims, c => c.Binding.Display == "Z" && c.StateGated);

            // The parameter-guarded branch: '=' on Beta, absent on Gamma,
            // from one shared body walked twice.
            var beta = result.Fields.Single(f => f.Context == "Beta");
            var gamma = result.Fields.Single(f => f.Context == "Gamma");
            Assert.Contains(beta.Claims, c => c.Binding.Display == "=");
            Assert.DoesNotContain(gamma.Claims, c => c.Binding.Display == "=");
            Assert.Contains(gamma.Claims, c => c.Binding.Display == "W");

            // A clean tree reports clean, in both directions.
            Assert.Empty(FieldKeyMapScan.DeclaredNotHandled(result));
            Assert.Empty(FieldKeyMapScan.HandledNotDeclared(result));

            // Free letters: Z is claimed only inside a mode, so it stays free
            // and is named as conditional rather than silently spent.
            var free = FieldKeyMapScan.FreeLettersPerField(result)
                .Single(f => f.Context == "Alpha.Modern");
            Assert.DoesNotContain('A', free.Free);
            Assert.DoesNotContain('M', free.Free);
            Assert.Contains('S', free.Free);        // only Shift+S is taken
            Assert.Contains('Z', free.Free);
            Assert.Contains('Z', free.Conditional["_inSomeMode"]);
        }

        /// <summary>
        /// The same scanner, made to FAIL on a tree that is wrong — because a
        /// checker that cannot report a discrepancy will report none.
        /// </summary>
        [Fact]
        public void The_scanner_reports_a_discrepancy_when_there_is_one()
        {
            const string handlers = @"
class FreqOutHandlers
{
    public void AdjustAlpha(DisplayField field, KeyEventArgs e)
    {
        char ch = KeyToChar(e);
        if (ch == 'J') { Undocumented(); }
    }
}";
            const string dispatch = @"
class MainWindow
{
    private void FreqOut_FieldKeyDown(DisplayField field, KeyEventArgs e)
    {
        switch (field.Key) { case ""Alpha"": _h.AdjustAlpha(field, e); break; }
    }
}";
            const string inventory = @"
public static class KeyInventory
{
    private static readonly FixedKeyEntry[] UniversalHome = { };
    private static readonly FixedKeyEntry[] HomeNavigation = { };
    private static readonly FixedKeyEntry[] FieldKeys =
    {
        new(""Alpha"", ""Alpha"", ""P"", ""Advertised and dead"", new[] { ""p"" }),
    };
}";
            var result = FieldKeyMapScan.Scan(handlers, dispatch, inventory);

            var missing = FieldKeyMapScan.DeclaredNotHandled(result);
            Assert.Single(missing);
            Assert.Equal("P", missing[0].Key);

            var extra = FieldKeyMapScan.HandledNotDeclared(result);
            Assert.Single(extra);
            Assert.Equal("J", extra[0].Key);
        }

        // ────────────────────────────────────────────────────────────────
        //  The real tree
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The scan reached the real dispatch and the real inventory. Without
        /// this, every assertion below could pass on an empty read.
        /// </summary>
        [Fact]
        public void The_scan_reaches_the_real_home_fields()
        {
            var result = Real;

            Assert.True(result.Fields.Count >= 12,
                "only " + result.Fields.Count + " dispatched fields found — the "
                + "`switch (field.Key)` shape in MainWindow has changed and this "
                + "check is no longer reading the map it thinks it is");

            Assert.Contains(result.Fields, f => f.Context.EndsWith(".Modern", StringComparison.Ordinal));
            Assert.Contains(result.Fields, f => f.Context.EndsWith(".Classic", StringComparison.Ordinal));
            Assert.All(result.Fields, f => Assert.NotEmpty(f.Claims));
            Assert.All(result.Fields, f => Assert.NotEmpty(f.FieldRows.Concat(f.UniversalRows)));
        }

        /// <summary>
        /// Every key name written in the inventory's field tables is one this
        /// scan can read. A name it cannot read is a hole in the two checks
        /// below, and a hole reads exactly like a clean result.
        /// </summary>
        [Fact]
        public void Every_declared_key_name_is_one_this_scan_can_read()
        {
            var result = Real;
            var unreadable = result.Fields
                .SelectMany(f => f.FieldRows.Concat(f.UniversalRows))
                .Concat(result.SurfaceRows)
                .Where(r => r.Unparsed.Count > 0)
                .Select(r => r.Context + " \"" + r.KeyDisplay + "\" → " + string.Join(", ", r.Unparsed))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

            Assert.True(unreadable.Count == 0,
                "key names this scan cannot read, each one a hole in the reconciliation:"
                + Environment.NewLine + string.Join(Environment.NewLine, unreadable));
        }

        /// <summary>
        /// THE WORSE DIRECTION. A key six surfaces advertise and no handler
        /// implements: the per-field help dialog, the '?' speak-keys handler,
        /// the Keys dialog, Command Finder rows, the generated manifest and
        /// Ctrl+F1 all tell an operator to press it, and it does nothing.
        /// Nobody finds this by using the app — you only find it by trying a
        /// key you were told about and getting silence, which is
        /// indistinguishable from a key that is merely broken.
        /// </summary>
        [Fact]
        public void No_surface_advertises_a_key_that_no_handler_implements()
        {
            AssertAgainstBaseline(FieldKeyMapScan.DeclaredNotHandled(Real), "DECLARED-NOT-HANDLED");
        }

        /// <summary>
        /// A key that works and that nothing tells you about — BlindCat
        /// anti-pattern number one, and the thing this project exists to
        /// avoid.
        /// </summary>
        [Fact]
        public void No_handler_implements_a_key_no_surface_mentions()
        {
            AssertAgainstBaseline(FieldKeyMapScan.HandledNotDeclared(Real), "HANDLED-NOT-DECLARED");
        }

        /// <summary>
        /// The answer Noel asked the question for: which of the twenty-six
        /// letters nothing claims, per field. "We had to choose weird ones
        /// because we didn't have many to pick from" has an arithmetic answer
        /// and there was never anywhere to read it.
        /// </summary>
        [Fact]
        public void The_free_letters_per_field_are_reported()
        {
            var result = Real;
            _output.WriteLine(FieldKeyMapScan.Report(result));

            var free = FieldKeyMapScan.FreeLettersPerField(result);
            Assert.NotEmpty(free);

            // Not an aesthetic check: a field reporting all twenty-six letters
            // free means the walk found no letters at all, which is what a
            // broken scan looks like.
            Assert.All(free, f => Assert.True(f.Taken.Count > 0,
                f.Context + " claims no letter at all — the handler walk found nothing"));
        }

        // ────────────────────────────────────────────────────────────────
        //  Baseline
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// What was already true when this checker was written, and therefore
        /// what it is not this check's job to make green.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A list to SHRINK, not to maintain</b>, in the shape
        /// <see cref="IntegrationPassBaseline"/> already uses. The gate fails
        /// when an entry stops being found, so putting one right forces a
        /// deletion here rather than leaving a claim nobody rechecks.
        /// </para>
        /// <para>
        /// <b>Nothing may be added here to make a red build green.</b> An
        /// entry added by whoever caused the finding is a suppression, and a
        /// suppression outlives the memory of why.
        /// </para>
        /// </remarks>
        private static readonly string[] Baseline =
        {
            // Slice, digits 8 and 9. `AdjustSlice` tests `ch >= '0' && ch <= '9'`
            // and speaks "no such slice" for anything past the last slice; the
            // inventory row says "0-7 or A-H". Both are defensible on their own
            // — the handler is obeying no-silent-keystrokes, the row is naming
            // the eight slices a 6700 can have — and they disagree. Nobody
            // chose the disagreement; it is what the check exists to surface.
            "HANDLED-NOT-DECLARED Slice 8",
            "HANDLED-NOT-DECLARED Slice 9",

            // Frequency field, Classic tuning, the minus key. `AdjustFreq`
            // answers it with "step entry is plus only" and marks it handled.
            // That is a good keystroke — it explains itself instead of going
            // quiet — and it is the one no surface mentions, so the operator
            // who would most benefit from hearing it is the one who never
            // learns the key exists.
            "HANDLED-NOT-DECLARED Freq.Classic -",
        };

        private void AssertAgainstBaseline(List<FieldKeyMapScan.Finding> findings, string direction)
        {
            var complaint = Reconcile(findings, Baseline, direction);
            Assert.True(complaint.Count == 0,
                string.Join(Environment.NewLine, complaint)
                + Environment.NewLine
                + "This proves a key is BOUND, never that it WORKS — press it on a real build.");
        }

        /// <summary>
        /// Compare today's findings with the baseline, in BOTH directions:
        /// a new finding fails, and so does a baseline entry that has stopped
        /// being found. Pure, so the gate itself can be proved to fire.
        /// </summary>
        internal static List<string> Reconcile(List<FieldKeyMapScan.Finding> findings,
            IEnumerable<string> baseline, string direction)
        {
            var known = new HashSet<string>(baseline, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            var fresh = new List<string>();
            foreach (var finding in findings)
            {
                string id = finding.Direction + " " + finding.Context + " " + finding.Key;
                seen.Add(id);
                if (!known.Contains(id)) fresh.Add(finding.Line);
            }

            var repaired = known
                .Where(k => k.StartsWith(direction, StringComparison.Ordinal))
                .Where(k => !seen.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            var complaint = new List<string>();
            if (fresh.Count > 0)
            {
                complaint.Add("NEW " + direction + " (" + fresh.Count + "):");
                complaint.AddRange(fresh.Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal).Select(s => "  " + s));
            }
            if (repaired.Count > 0)
            {
                complaint.Add("These baseline entries are no longer found. Good — delete them "
                    + "from FieldKeyMapTests.Baseline, because a baseline is a list of what is "
                    + "STILL true (" + repaired.Count + "):");
                complaint.AddRange(repaired.Select(s => "  " + s));
            }
            return complaint;
        }

        /// <summary>
        /// The gate itself, proved to fire — in both directions, because a
        /// baseline that only catches new findings quietly becomes a list of
        /// claims nobody rechecks.
        /// </summary>
        [Fact]
        public void The_baseline_fails_on_a_new_finding_and_on_a_repaired_one()
        {
            var findings = new List<FieldKeyMapScan.Finding>
            {
                new("Slice", "HANDLED-NOT-DECLARED", "8", "known"),
                new("Slice", "HANDLED-NOT-DECLARED", "J", "brand new"),
            };
            string[] baseline =
            {
                "HANDLED-NOT-DECLARED Slice 8",
                "HANDLED-NOT-DECLARED Slice 9",   // repaired: not found any more
            };

            var complaint = Reconcile(findings, baseline, "HANDLED-NOT-DECLARED");
            string all = string.Join(Environment.NewLine, complaint);

            Assert.Contains("brand new", all, StringComparison.Ordinal);
            Assert.Contains("HANDLED-NOT-DECLARED Slice 9", all, StringComparison.Ordinal);
            Assert.DoesNotContain("known", all, StringComparison.Ordinal);

            Assert.Empty(Reconcile(
                new List<FieldKeyMapScan.Finding>
                {
                    new("Slice", "HANDLED-NOT-DECLARED", "8", "known"),
                    new("Slice", "HANDLED-NOT-DECLARED", "9", "known"),
                },
                baseline, "HANDLED-NOT-DECLARED"));
        }

        // Read once per test class instance; the scan is pure source parsing
        // and touches no settings, no window and no radio.
        private static readonly FieldKeyMapScan.Result RealScan = FieldKeyMapScan.ScanRepository();

        private static FieldKeyMapScan.Result Real => RealScan;
    }
}
