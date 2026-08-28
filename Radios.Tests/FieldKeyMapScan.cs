using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radios.Tests
{
    /// <summary>
    /// Reads the per-field Home key maps out of SOURCE and reconciles the two
    /// that already exist and that nothing compared: the ACTUAL map the field
    /// handlers implement, and the DECLARED map <c>KeyInventory</c> publishes
    /// to its six surfaces (#339).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHAT THIS PROVES, AND WHAT IT CANNOT.</b> It proves a key is BOUND —
    /// that a handler contains a branch that tests for it. It cannot prove the
    /// key WORKS. The Alt+L binding that shipped completely dead on 2026-08-13
    /// was statically perfect: it tested <c>e.Key == Key.L</c>, which is never
    /// true while Alt is held, because WPF reports <c>Key.System</c> and puts
    /// the real key in <c>e.SystemKey</c>. A scan of that source would have
    /// called Alt+L bound. So this complements PRESS THE KEY; it does not
    /// replace it, and the keyboard audit keeps its final step.
    /// </para>
    /// <para>
    /// <b>Why Roslyn and not a regex.</b> Every other source-reading test in
    /// this project scans text, and that is the right tool for literal strings
    /// people wrote. It is the wrong tool here: the handlers nest modifier
    /// tests inside character tests — <c>case Key.Left:</c> whose body then
    /// branches on <c>Keyboard.Modifiers</c>, a range test ANDed with a local
    /// <c>bool unmodified</c>, a shared RIT/XIT body whose <c>'='</c> branch is
    /// guarded by the <c>isRIT</c> parameter its two callers bind differently.
    /// A textual scan is least reliable exactly where the bugs are.
    /// </para>
    /// <para>
    /// <b>Nothing here is hardcoded.</b> The dispatch table is read from the
    /// <c>field.Key</c> switches in MainWindow, the handled keys from the
    /// handler bodies, and the declared keys from the inventory tables. The
    /// only literal knowledge is a lexicon of KEY NAMES — that "Page Down"
    /// names a key and <c>Key.Back</c> is Backspace — which is not the map.
    /// </para>
    /// </remarks>
    internal static class FieldKeyMapScan
    {
        internal const string HandlersFile = "JJFlexWpf/FreqOutHandlers.cs";
        internal const string DispatchFile = "JJFlexWpf/MainWindow.xaml.cs";
        internal const string InventoryFile = "JJFlexWpf/KeyInventory.cs";

        /// <summary>The inventory tables that describe a Home FIELD.</summary>
        internal const string FieldTable = "FieldKeys";

        /// <summary>The inventory table for keys that work on EVERY Home field.</summary>
        internal const string UniversalTable = "UniversalHome";

        /// <summary>
        /// The inventory table for keys owned by the Home surface itself
        /// (cursor movement, Shift+M, Shift+Comma, the '?' speak-keys key).
        /// These are handled above or beside the field handlers, so they
        /// SUPPRESS a "handled but not declared" finding without being owed a
        /// handler in every field.
        /// </summary>
        internal const string SurfaceTable = "HomeNavigation";

        // ────────────────────────────────────────────────────────────────
        //  Model
        // ────────────────────────────────────────────────────────────────

        /// <summary>One key, normalised: a modifier set and a key token.</summary>
        internal readonly record struct Binding(string Mods, string Token)
        {
            /// <summary>How the binding is written in a report.</summary>
            internal string Display => Mods.Length == 0 ? Token : Mods + "+" + Token;
        }

        /// <summary>One binding as a HANDLER implements it.</summary>
        internal sealed class Claim
        {
            internal Binding Binding { get; init; }

            /// <summary>
            /// The branch reads <c>Keyboard.Modifiers</c> in a way this scan
            /// could not decompose, so the binding may or may not carry a
            /// modifier. Such a claim matches a declared row with ANY modifier
            /// — deliberately generous, because the alternative is inventing a
            /// discrepancy out of the scanner's own blind spot.
            /// </summary>
            internal bool ModsUnresolved { get; init; }

            /// <summary>
            /// The branch is guarded by state this scan cannot evaluate (a
            /// private field such as <c>_inQuickType</c>, or a rig capability).
            /// The key IS bound, so a gated claim satisfies a declared row —
            /// but it is not counted against the free-letter budget, because
            /// the letter is only claimed while that state holds.
            /// </summary>
            internal bool StateGated { get; init; }

            /// <summary>
            /// The private mode fields that gate the branch, named so the
            /// report can say WHICH mode claims the key rather than leaving a
            /// reader to guess. Empty unless <see cref="StateGated"/>.
            /// </summary>
            internal string Gate { get; init; } = "";

            /// <summary>The handler method the branch was found in.</summary>
            internal string Method { get; init; } = "";
        }

        /// <summary>One row of <c>KeyInventory</c>, expanded to bindings.</summary>
        internal sealed class DeclaredRow
        {
            internal string Table { get; init; } = "";
            internal string Context { get; init; } = "";
            internal string KeyDisplay { get; init; } = "";
            internal List<Binding> Bindings { get; } = new();
            internal List<string> Unparsed { get; } = new();
        }

        /// <summary>Everything known about one Home field.</summary>
        internal sealed class FieldMap
        {
            internal string FieldKey { get; init; } = "";

            /// <summary>
            /// The tuning modes that dispatch this field to this handler.
            /// Most fields are in both; a context declared per mode (the
            /// Frequency field) appears once per mode with a different handler.
            /// </summary>
            internal List<string> Modes { get; } = new();

            internal string Context { get; init; } = "";
            internal string Handler { get; init; } = "";
            internal List<Claim> Claims { get; } = new();
            internal List<DeclaredRow> FieldRows { get; } = new();
            internal List<DeclaredRow> UniversalRows { get; } = new();

            internal string Where => Modes.Count == 0
                ? Context
                : Context + " (" + string.Join(", ", Modes) + ")";
        }

        /// <summary>The whole reconciliation, ready to assert on or print.</summary>
        internal sealed class Result
        {
            internal List<FieldMap> Fields { get; } = new();
            internal List<DeclaredRow> SurfaceRows { get; } = new();

            /// <summary>Contexts the inventory declares that no field dispatches to.</summary>
            internal List<string> OrphanContexts { get; } = new();

            /// <summary>Anything the scan met and could not read. Never silent.</summary>
            internal List<string> Notes { get; } = new();
        }

        /// <summary>A discrepancy, in the direction that names who is wrong.</summary>
        internal sealed record Finding(string Context, string Direction, string Key, string Detail)
        {
            internal string Line => Direction + " · " + Context + " · " + Key + " — " + Detail;
        }

        // ────────────────────────────────────────────────────────────────
        //  Entry points
        // ────────────────────────────────────────────────────────────────

        internal static Result ScanRepository()
            => Scan(ReadSource(HandlersFile), ReadSource(DispatchFile), ReadSource(InventoryFile));

        internal static Result Scan(string handlersSource, string dispatchSource, string inventorySource)
        {
            var result = new Result();

            var declared = ReadInventory(inventorySource, result.Notes);
            var byContext = declared
                .GroupBy(r => r.Context, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var row in declared.Where(r => r.Table == SurfaceTable))
                result.SurfaceRows.Add(row);

            var universal = declared.Where(r => r.Table == UniversalTable).ToList();

            var dispatch = ReadDispatch(dispatchSource, declared, result.Notes);
            var handlers = ReadHandlerMethods(handlersSource);

            var usedContexts = new HashSet<string>(StringComparer.Ordinal);

            // A field dispatched from BOTH tuning-mode switches to the same
            // handler is ONE map, not two. Reporting it twice would double
            // every finding on it and read as two separate defects.
            var resolved = dispatch
                .Select(d => (Context: ResolveContext(d.FieldKey, d.Mode, byContext), Entry: d))
                .ToList();

            foreach (var group in resolved.GroupBy(
                         x => (x.Context, x.Entry.Handler),
                         ContextHandlerComparer.Instance))
            {
                string context = group.Key.Context;
                usedContexts.Add(context);

                var map = new FieldMap
                {
                    FieldKey = group.First().Entry.FieldKey,
                    Context = context,
                    Handler = group.Key.Handler,
                };
                map.Modes.AddRange(group.Select(x => x.Entry.Mode)
                    .Distinct(StringComparer.Ordinal)
                    .Where(m => m.Length > 0)
                    .OrderBy(m => m, StringComparer.Ordinal));

                if (handlers.TryGetValue(group.Key.Handler, out var method))
                {
                    var walker = new HandlerWalker(handlers, result.Notes);
                    walker.Walk(method, new Dictionary<string, bool>(StringComparer.Ordinal));
                    map.Claims.AddRange(walker.Claims);
                }
                else
                {
                    result.Notes.Add("dispatch names " + group.Key.Handler
                        + " but no such method was found in " + HandlersFile);
                }

                if (byContext.TryGetValue(context, out var rows))
                    map.FieldRows.AddRange(rows.Where(r => r.Table == FieldTable));
                map.UniversalRows.AddRange(universal);

                result.Fields.Add(map);
            }

            // Two handlers behind one context is a real finding: the inventory
            // can then only be right about one of them.
            foreach (var clash in result.Fields.GroupBy(f => f.Context, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                result.Notes.Add("context " + clash.Key + " is dispatched to more than one handler ("
                    + string.Join(", ", clash.Select(f => f.Handler + " in " + string.Join("/", f.Modes)))
                    + ") — the inventory has one table for both");
            }

            foreach (var context in byContext.Keys
                         .Where(c => declared.Any(r => r.Table == FieldTable && r.Context == c))
                         .Where(c => !usedContexts.Contains(c))
                         .OrderBy(c => c, StringComparer.Ordinal))
            {
                result.OrphanContexts.Add(context);
            }

            return result;
        }

        private sealed class ContextHandlerComparer : IEqualityComparer<(string Context, string Handler)>
        {
            internal static readonly ContextHandlerComparer Instance = new();

            public bool Equals((string Context, string Handler) x, (string Context, string Handler) y)
                => string.Equals(x.Context, y.Context, StringComparison.Ordinal)
                && string.Equals(x.Handler, y.Handler, StringComparison.Ordinal);

            public int GetHashCode((string Context, string Handler) obj)
                => HashCode.Combine(obj.Context, obj.Handler);
        }

        /// <summary>
        /// A field key plus a tuning mode names an inventory context. The rule
        /// is read off the inventory, not written down here: use
        /// "&lt;field&gt;.&lt;mode&gt;" when the inventory declares one, and
        /// the bare field key otherwise.
        /// </summary>
        private static string ResolveContext(string fieldKey, string mode,
            Dictionary<string, List<DeclaredRow>> byContext)
        {
            string qualified = fieldKey + "." + mode;
            return byContext.ContainsKey(qualified) ? qualified : fieldKey;
        }

        // ────────────────────────────────────────────────────────────────
        //  Findings
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// A key a handler implements that no surface mentions — undiscoverable,
        /// which is BlindCat anti-pattern number one.
        /// </summary>
        internal static List<Finding> HandledNotDeclared(Result result)
        {
            var findings = new List<Finding>();
            var surface = Expand(result.SurfaceRows);

            foreach (var field in result.Fields)
            {
                var declared = new HashSet<Binding>(Expand(field.FieldRows));
                declared.UnionWith(Expand(field.UniversalRows));
                declared.UnionWith(surface);

                foreach (var claim in field.Claims
                             .Where(c => !c.StateGated)
                             .GroupBy(c => c.Binding)
                             .Select(g => g.First())
                             .OrderBy(c => c.Binding.Display, StringComparer.Ordinal))
                {
                    if (Satisfies(declared, claim)) continue;
                    findings.Add(new Finding(field.Context, "HANDLED-NOT-DECLARED",
                        claim.Binding.Display,
                        "the handler " + claim.Method + " claims it and no surface mentions it"));
                }
            }
            return findings;
        }

        /// <summary>
        /// A key six surfaces advertise that no handler implements. THE WORSE
        /// DIRECTION: nobody finds it by using the app, because you only
        /// discover it by trying a key you were told about and getting silence.
        /// </summary>
        internal static List<Finding> DeclaredNotHandled(Result result)
        {
            var findings = new List<Finding>();

            foreach (var field in result.Fields)
            {
                foreach (var row in field.FieldRows.Concat(field.UniversalRows))
                {
                    foreach (var binding in row.Bindings.Distinct()
                                 .OrderBy(b => b.Display, StringComparer.Ordinal))
                    {
                        if (field.Claims.Any(c => Matches(c, binding))) continue;
                        findings.Add(new Finding(field.Context, "DECLARED-NOT-HANDLED",
                            binding.Display,
                            "declared by \"" + row.KeyDisplay + "\" in " + row.Table
                            + " and no branch of " + field.Handler + " tests for it"));
                    }
                }
            }
            return findings;
        }

        private static bool Satisfies(HashSet<Binding> declared, Claim claim)
        {
            if (declared.Contains(claim.Binding)) return true;
            if (!claim.ModsUnresolved) return false;
            return declared.Any(d => string.Equals(d.Token, claim.Binding.Token, StringComparison.Ordinal));
        }

        private static bool Matches(Claim claim, Binding declared)
        {
            if (!string.Equals(claim.Binding.Token, declared.Token, StringComparison.Ordinal))
                return false;
            return claim.ModsUnresolved
                || string.Equals(claim.Binding.Mods, declared.Mods, StringComparison.Ordinal);
        }

        private static IEnumerable<Binding> Expand(IEnumerable<DeclaredRow> rows)
            => rows.SelectMany(r => r.Bindings);

        private static string Describe(Claim claim)
            => claim.Binding.Display + (claim.ModsUnresolved ? "+" : "");

        // ────────────────────────────────────────────────────────────────
        //  Free letters — the payoff
        // ────────────────────────────────────────────────────────────────

        internal sealed class FreeLetters
        {
            internal string Context { get; init; } = "";
            internal string Where { get; init; } = "";
            internal List<char> Free { get; } = new();
            internal List<char> Taken { get; } = new();

            /// <summary>
            /// Letters claimed only while some mode is on, keyed by the mode
            /// field that gates them. Free to bind, and worth reading first.
            /// </summary>
            internal SortedDictionary<string, SortedSet<char>> Conditional { get; } =
                new(StringComparer.Ordinal);
        }

        /// <summary>
        /// Per field: which of the twenty-six letters nothing claims. A letter
        /// counts as taken when a handler claims it unmodified, or when any
        /// inventory row advertises it unmodified on that field. Letters
        /// claimed only inside a mode (quick-type accumulation, scale-adjust)
        /// are listed separately rather than spent — they are free to bind and
        /// worth knowing about before you do.
        /// </summary>
        internal static List<FreeLetters> FreeLettersPerField(Result result)
        {
            var list = new List<FreeLetters>();
            var surface = Expand(result.SurfaceRows).ToList();

            foreach (var field in result.Fields)
            {
                var taken = new HashSet<char>();
                var conditional = new List<(string Gate, char Letter)>();

                foreach (var claim in field.Claims)
                {
                    if (!IsPlainLetter(claim.Binding, out char letter)) continue;
                    if (claim.StateGated) conditional.Add((claim.Gate, letter));
                    else taken.Add(letter);
                }

                foreach (var binding in Expand(field.FieldRows)
                             .Concat(Expand(field.UniversalRows)).Concat(surface))
                {
                    if (IsPlainLetter(binding, out char letter)) taken.Add(letter);
                }

                var free = new FreeLetters { Context = field.Context, Where = field.Where };
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    if (taken.Contains(c)) free.Taken.Add(c);
                    else free.Free.Add(c);
                }
                foreach (var (gate, letter) in conditional)
                {
                    if (taken.Contains(letter)) continue;
                    string key = gate.Length > 0 ? gate : "a mode this scan could not name";
                    if (!free.Conditional.TryGetValue(key, out var set))
                        free.Conditional[key] = set = new SortedSet<char>();
                    set.Add(letter);
                }
                list.Add(free);
            }
            return list;
        }

        private static bool IsPlainLetter(Binding binding, out char letter)
        {
            letter = '\0';
            if (binding.Mods.Length != 0) return false;
            if (binding.Token.Length != 1) return false;
            char c = binding.Token[0];
            if (c < 'A' || c > 'Z') return false;
            letter = c;
            return true;
        }

        // ────────────────────────────────────────────────────────────────
        //  The report — prose and bullets, never a table (screen readers)
        // ────────────────────────────────────────────────────────────────

        internal static string Report(Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Per-field Home key map, reconciled from source.");
            sb.AppendLine();
            sb.AppendLine("WHAT THIS PROVES: that a key is BOUND — a handler branch tests for it.");
            sb.AppendLine("WHAT IT CANNOT PROVE: that the key WORKS. A binding can be statically");
            sb.AppendLine("perfect and completely dead, which is what shipped on 2026-08-13. Press");
            sb.AppendLine("the key on a real build before calling any of this verified.");
            sb.AppendLine();

            var handled = HandledNotDeclared(result);
            var missing = DeclaredNotHandled(result);

            sb.AppendLine("Declared but not handled (" + missing.Count
                + ") — six surfaces telling an operator to press a dead key:");
            if (missing.Count == 0) sb.AppendLine("  none");
            foreach (var f in missing) sb.AppendLine("  " + f.Line);
            sb.AppendLine();

            sb.AppendLine("Handled but not declared (" + handled.Count
                + ") — a key that works and that nothing tells you about:");
            if (handled.Count == 0) sb.AppendLine("  none");
            foreach (var f in handled) sb.AppendLine("  " + f.Line);
            sb.AppendLine();

            sb.AppendLine("The map itself — every key each field claims, read out of the handlers.");
            sb.AppendLine("A key in brackets is claimed only while the mode named beside the field");
            sb.AppendLine("is on. A key marked with a plus sign is bound both with and without a");
            sb.AppendLine("modifier: its branch reads Keyboard.Modifiers somewhere this scan could");
            sb.AppendLine("not decompose, so it matches a declared row carrying any modifier.");
            sb.AppendLine("The maps are disjoint by field on purpose: S sounds a slice on Slice");
            sb.AppendLine("Operations, opens the step picker on the Modern frequency field, and");
            sb.AppendLine("turns split on in Classic — no collision, because they never meet.");
            foreach (var field in result.Fields)
            {
                var resting = field.Claims.Where(c => !c.StateGated)
                    .Select(Describe).Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                var gated = field.Claims.Where(c => c.StateGated)
                    .Select(Describe).Distinct(StringComparer.Ordinal)
                    .Except(resting, StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                sb.AppendLine("  " + field.Where + " via " + field.Handler + ": "
                    + string.Join(", ", resting)
                    + (gated.Count == 0 ? "" : "; in a mode: [" + string.Join(", ", gated) + "]"));
            }
            sb.AppendLine();

            sb.AppendLine("Free letters per field — of twenty-six, the ones nothing claims:");
            foreach (var free in FreeLettersPerField(result))
            {
                sb.AppendLine("  " + free.Where + " — " + free.Free.Count + " free: "
                    + string.Join(" ", free.Free));
                sb.AppendLine("    taken (" + free.Taken.Count + "): " + string.Join(" ", free.Taken));
                foreach (var gate in free.Conditional)
                {
                    sb.AppendLine("    free, but claimed while " + gate.Key + " is set: "
                        + string.Join(" ", gate.Value));
                }
            }
            sb.AppendLine();

            if (result.OrphanContexts.Count > 0)
            {
                sb.AppendLine("Inventory contexts no field dispatches to: "
                    + string.Join(", ", result.OrphanContexts));
                sb.AppendLine();
            }

            var unparsed = result.Fields
                .SelectMany(f => f.FieldRows.Concat(f.UniversalRows))
                .Concat(result.SurfaceRows)
                .SelectMany(r => r.Unparsed.Select(u => r.Context + " \"" + r.KeyDisplay + "\": " + u))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            if (unparsed.Count > 0)
            {
                sb.AppendLine("Key names in the inventory this scan could not read ("
                    + unparsed.Count + ") — each one is a hole in the check above:");
                foreach (var u in unparsed) sb.AppendLine("  " + u);
                sb.AppendLine();
            }

            if (result.Notes.Count > 0)
            {
                sb.AppendLine("Scanner notes (" + result.Notes.Count + "):");
                foreach (var n in result.Notes.Distinct(StringComparer.Ordinal)
                             .OrderBy(s => s, StringComparer.Ordinal))
                {
                    sb.AppendLine("  " + n);
                }
            }
            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────────
        //  Reading the DISPATCH table out of MainWindow
        // ────────────────────────────────────────────────────────────────

        internal sealed record DispatchEntry(string FieldKey, string Mode, string Handler);

        internal static List<DispatchEntry> ReadDispatch(string source,
            List<DeclaredRow> declared, List<string> notes)
        {
            var entries = new List<DispatchEntry>();
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            // Every switch whose governing expression is the field's key. The
            // method name is not assumed — the switch on `field.Key` IS the
            // dispatch, wherever it lives.
            var switches = root.DescendantNodes().OfType<SwitchStatementSyntax>()
                .Where(s => s.Expression.ToString().EndsWith(".Key", StringComparison.Ordinal)
                         && s.Sections.Any(sec => sec.Labels.OfType<CaseSwitchLabelSyntax>()
                                .Any(l => l.Value is LiteralExpressionSyntax)))
                .ToList();

            if (switches.Count == 0)
            {
                notes.Add("no `switch (field.Key)` dispatch found in " + DispatchFile);
                return entries;
            }

            // Mode names come from the enum comparison that guards a switch.
            // The unguarded switch is the OTHER mode, and which mode that is
            // comes from the inventory's own qualified contexts.
            var modeSuffixes = declared
                .Where(r => r.Table == FieldTable && r.Context.Contains('.', StringComparison.Ordinal))
                .Select(r => r.Context[(r.Context.IndexOf('.', StringComparison.Ordinal) + 1)..])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var guarded = new List<(SwitchStatementSyntax Switch, string Mode)>();
            SwitchStatementSyntax? unguarded = null;

            foreach (var sw in switches)
            {
                string? mode = GuardingMode(sw, modeSuffixes);
                if (mode != null) guarded.Add((sw, mode));
                else if (unguarded == null) unguarded = sw;
                else notes.Add("more than one unguarded `switch (field.Key)` — dispatch shape changed");
            }

            string? otherMode = null;
            if (unguarded != null)
            {
                var remaining = modeSuffixes
                    .Except(guarded.Select(g => g.Mode), StringComparer.Ordinal).ToList();
                if (remaining.Count == 1) otherMode = remaining[0];
                else if (modeSuffixes.Count == 0) otherMode = "";
                else
                {
                    notes.Add("could not name the mode of the unguarded dispatch switch; "
                        + "inventory offers [" + string.Join(", ", modeSuffixes)
                        + "] and guarded switches took [" + string.Join(", ", guarded.Select(g => g.Mode)) + "]");
                }
            }

            foreach (var (sw, mode) in guarded) Collect(sw, mode);
            if (unguarded != null && otherMode != null) Collect(unguarded, otherMode);

            return entries;

            void Collect(SwitchStatementSyntax sw, string mode)
            {
                foreach (var section in sw.Sections)
                {
                    var handler = section.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Select(i => i.Expression)
                        .OfType<MemberAccessExpressionSyntax>()
                        .Select(m => m.Name.Identifier.ValueText)
                        .FirstOrDefault();
                    if (handler == null) continue;

                    foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                    {
                        if (label.Value is LiteralExpressionSyntax lit
                            && lit.Token.Value is string fieldKey)
                        {
                            entries.Add(new DispatchEntry(fieldKey, mode, handler));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The mode an <c>if</c> ancestor pins this switch to, named by the
        /// enum member it compares against — or null when nothing guards it.
        /// </summary>
        private static string? GuardingMode(SyntaxNode node, List<string> modeSuffixes)
        {
            foreach (var ancestor in node.Ancestors().OfType<IfStatementSyntax>())
            {
                // Only a guard when the switch is in the THEN branch.
                if (ancestor.Else != null && ancestor.Else.Span.Contains(node.Span)) continue;

                foreach (var access in ancestor.Condition.DescendantNodesAndSelf()
                             .OfType<MemberAccessExpressionSyntax>())
                {
                    string member = access.Name.Identifier.ValueText;
                    if (modeSuffixes.Contains(member, StringComparer.Ordinal)) return member;
                }
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────────
        //  Reading the HANDLER methods
        // ────────────────────────────────────────────────────────────────

        internal static Dictionary<string, MethodDeclarationSyntax> ReadHandlerMethods(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var map = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                string name = method.Identifier.ValueText;
                if (!map.ContainsKey(name)) map[name] = method;
            }
            return map;
        }

        // ────────────────────────────────────────────────────────────────
        //  Reading the INVENTORY tables
        // ────────────────────────────────────────────────────────────────

        internal static List<DeclaredRow> ReadInventory(string source, List<string> notes)
        {
            var rows = new List<DeclaredRow>();
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    string table = variable.Identifier.ValueText;
                    if (table != FieldTable && table != UniversalTable && table != SurfaceTable)
                        continue;

                    var initializer = variable.Initializer?.Value;
                    if (initializer is not InitializerExpressionSyntax array)
                    {
                        notes.Add("inventory table " + table + " is no longer an array initializer");
                        continue;
                    }

                    foreach (var element in array.Expressions)
                    {
                        var args = ConstructorArgs(element);
                        if (args == null || args.Count < 3)
                        {
                            notes.Add("inventory row in " + table
                                + " has a shape this scan cannot read: " + Shorten(element.ToString()));
                            continue;
                        }
                        string? context = LiteralString(args[0]);
                        string? display = LiteralString(args[2]);
                        if (context == null || display == null)
                        {
                            notes.Add("inventory row in " + table
                                + " has non-literal context or key: " + Shorten(element.ToString()));
                            continue;
                        }

                        var row = new DeclaredRow
                        {
                            Table = table,
                            Context = context,
                            KeyDisplay = display,
                        };
                        row.Bindings.AddRange(ParseKeyDisplay(display, row.Unparsed));
                        rows.Add(row);
                    }
                }
            }

            if (rows.Count == 0) notes.Add("no inventory rows found in " + InventoryFile);
            return rows;
        }

        private static List<ArgumentSyntax>? ConstructorArgs(ExpressionSyntax element) => element switch
        {
            ImplicitObjectCreationExpressionSyntax i => i.ArgumentList.Arguments.ToList(),
            ObjectCreationExpressionSyntax o when o.ArgumentList != null
                => o.ArgumentList.Arguments.ToList(),
            _ => null,
        };

        private static string? LiteralString(ArgumentSyntax arg)
            => arg.Expression is LiteralExpressionSyntax lit && lit.Token.Value is string s ? s : null;

        private static string Shorten(string s)
        {
            s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return s.Length <= 90 ? s : s[..90] + "...";
        }

        // ────────────────────────────────────────────────────────────────
        //  KeyDisplay → bindings
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Key NAMES, not a key map. "Page Down" names a key; which field it
        /// works on is read from the tables, never from here.
        /// </summary>
        private static readonly Dictionary<string, string[]> KeyNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["space"] = new[] { "Space" },
                ["up"] = new[] { "Up" },
                ["down"] = new[] { "Down" },
                ["left"] = new[] { "Left" },
                ["right"] = new[] { "Right" },
                ["home"] = new[] { "Home" },
                ["end"] = new[] { "End" },
                ["page up"] = new[] { "Page Up" },
                ["pageup"] = new[] { "Page Up" },
                ["page down"] = new[] { "Page Down" },
                ["pagedown"] = new[] { "Page Down" },
                ["escape"] = new[] { "Escape" },
                ["esc"] = new[] { "Escape" },
                ["enter"] = new[] { "Enter" },
                ["return"] = new[] { "Enter" },
                ["tab"] = new[] { "Tab" },
                ["delete"] = new[] { "Delete" },
                ["del"] = new[] { "Delete" },
                ["backspace"] = new[] { "Backspace" },
                ["back"] = new[] { "Backspace" },
                ["insert"] = new[] { "Insert" },
                ["period"] = new[] { "." },
                ["dot"] = new[] { "." },
                ["comma"] = new[] { "," },
                ["plus"] = new[] { "+" },
                ["minus"] = new[] { "-" },
                ["equals"] = new[] { "=" },
                ["slash"] = new[] { "/" },
                ["digits"] = Enumerable.Range(0, 10).Select(i => i.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                ["digit"] = Enumerable.Range(0, 10).Select(i => i.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            };

        private static readonly string[] Multiword = { "page up", "page down" };

        /// <summary>
        /// Expand one written key phrase into bindings. Phrases are prose
        /// written for humans — "Space, Up, Down, or Q", "0-7 or A-H", "Plus
        /// then digits" — so anything unreadable is APPENDED TO
        /// <paramref name="unparsed"/> rather than dropped. A silently dropped
        /// token is a hole in the check that reads as a clean result.
        /// </summary>
        internal static List<Binding> ParseKeyDisplay(string display, List<string> unparsed)
        {
            var bindings = new List<Binding>();
            string normalised = display
                .Replace(" through ", ",", StringComparison.OrdinalIgnoreCase)
                .Replace(" then ", ",", StringComparison.OrdinalIgnoreCase)
                .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
                .Replace("/", ",", StringComparison.Ordinal);

            foreach (string rawChunk in normalised.Split(','))
            {
                string chunk = rawChunk.Trim();
                if (chunk.Length == 0) continue;

                foreach (string token in SplitChunk(chunk))
                {
                    string t = token.Trim();
                    if (t.Length == 0) continue;
                    AddToken(t, bindings, unparsed);
                }
            }
            return bindings;
        }

        private static IEnumerable<string> SplitChunk(string chunk)
        {
            if (!chunk.Contains(' ', StringComparison.Ordinal)) return new[] { chunk };
            if (Multiword.Contains(chunk, StringComparer.OrdinalIgnoreCase)) return new[] { chunk };

            // "Shift+Page Down" — one modified multiword key.
            int plus = chunk.LastIndexOf('+');
            if (plus > 0 && Multiword.Contains(chunk[(plus + 1)..].Trim(), StringComparer.OrdinalIgnoreCase))
                return new[] { chunk };

            // "1 2 3 4" and the like: a run of separate keys.
            return chunk.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private static void AddToken(string token, List<Binding> bindings, List<string> unparsed)
        {
            var mods = new List<string>();
            while (true)
            {
                int plus = token.IndexOf('+', StringComparison.Ordinal);
                if (plus <= 0) break;
                string prefix = token[..plus];
                if (!prefix.Equals("Shift", StringComparison.OrdinalIgnoreCase)
                    && !prefix.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    && !prefix.Equals("Control", StringComparison.OrdinalIgnoreCase)
                    && !prefix.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                mods.Add(prefix.Equals("Control", StringComparison.OrdinalIgnoreCase)
                    ? "Ctrl"
                    : char.ToUpperInvariant(prefix[0]) + prefix[1..].ToLowerInvariant());
                token = token[(plus + 1)..];
            }
            string modifiers = NormaliseMods(mods);

            foreach (string key in ExpandKey(token, unparsed))
                bindings.Add(new Binding(modifiers, key));
        }

        private static IEnumerable<string> ExpandKey(string token, List<string> unparsed)
        {
            if (token.Length == 0) yield break;

            if (KeyNames.TryGetValue(token, out var named))
            {
                foreach (string n in named) yield return n;
                yield break;
            }

            // A written range: "A-H", "0-7", "5-9".
            if (token.Length == 3 && token[1] == '-'
                && char.IsLetterOrDigit(token[0]) && char.IsLetterOrDigit(token[2]))
            {
                char lo = char.ToUpperInvariant(token[0]);
                char hi = char.ToUpperInvariant(token[2]);
                if (lo <= hi && ((char.IsDigit(lo) && char.IsDigit(hi))
                                 || (char.IsLetter(lo) && char.IsLetter(hi))))
                {
                    for (char c = lo; c <= hi; c++) yield return c.ToString();
                    yield break;
                }
            }

            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                yield return c.ToString();
                yield break;
            }

            unparsed.Add(token);
        }

        internal static string NormaliseMods(IEnumerable<string> mods)
        {
            var set = new SortedSet<string>(mods, StringComparer.Ordinal);
            return set.Count == 0 ? "" : string.Join("+", set);
        }

        // ────────────────────────────────────────────────────────────────
        //  Source loading
        // ────────────────────────────────────────────────────────────────

        internal static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(),
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new FileNotFoundException("source not found", path);
            return File.ReadAllText(path);
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
