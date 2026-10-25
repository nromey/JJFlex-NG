using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Radios.Tests
{
    /// <summary>
    /// #463: every shortcut a menu PROMISES is a chord that is bound, and bound
    /// to the thing the menu item does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> <c>NativeMenuBar</c> builds native Win32 menus,
    /// and the text after the tab in a menu item is PAINTED, never keyed. There
    /// is no accelerator table: every shortcut an operator reads in a menu is a
    /// string literal, and the registry that owns the binding has no idea the
    /// string exists. So a menu could promise a chord that does something else,
    /// or nothing, and no build, test or conflict would notice. The operator
    /// finds out by pressing the key the menu promised.
    /// </para>
    /// <para>
    /// <b>How "bound to that command" is decided, from source.</b> Each menu
    /// item's handler and each chord's binding are reduced to the names and
    /// strings their code uses, one level of method calls deep, and the two
    /// must share a meaningful one. A flat chord resolves through
    /// <c>KeyCommands._defaultKeys</c> to its <c>CommandValues</c> entry in
    /// <c>BuildKeyTable</c>; a JJ key chord through the arm of
    /// <c>DoLeaderCommand</c> that handles it; a chord inside a layer
    /// ("Ctrl+J, N, R") through the leader arm that opens the layer, to the
    /// statement in that layer's builder naming the second key. A menu item
    /// built with <c>AddCommand</c> names its command outright, so it is held
    /// to that command exactly. Where the shared-name test cannot see an
    /// agreement that a person reading both sides can, the pair is listed in
    /// <see cref="AgreesByReading"/> with what was read; where the two really
    /// disagree and the right meaning is not ruled, it is listed in
    /// <see cref="KnownDrift"/> with its task, for Noel. Both lists fail the
    /// build the day an entry stops being needed.
    /// </para>
    /// <para>
    /// Precedents: <see cref="LeaderDocCoverageTests"/> holds the help pages to
    /// the leader table; <see cref="FieldKeyMapTests"/> holds the Home fields'
    /// handlers to their declarations. This proves a key is BOUND to the
    /// right code, never that it WORKS — press it on a real build.
    /// </para>
    /// </remarks>
    public sealed class MenuShortcutAgreementTests
    {
        private readonly ITestOutputHelper _out;
        public MenuShortcutAgreementTests(ITestOutputHelper output) => _out = output;

        private const string MenuFile = "JJFlexWpf/NativeMenuBar.cs";
        private const string KeysFile = "JJFlexWpf/KeyCommands.cs";

        /// <summary>
        /// Pairs whose agreement a person established by reading both sides,
        /// because the two share no name the comparison can see. Key: menu
        /// label, a tab, the chord as the menu writes it. Every entry says what
        /// was read.
        /// </summary>
        private static readonly Dictionary<string, string> AgreesByReading = new(StringComparer.Ordinal)
        {
            // The five Screen Fields items: the menu calls
            // _window.FieldsPanel.ToggleCategory(i) and each chord's command
            // calls MainWindow.ToggleScreenFieldsCategory(i), which is
            // FieldsPanel.ToggleCategory(i). Read 2026-10-25: the index is the
            // same on both sides for all five (DSP 0, Audio 1, Receiver 2,
            // Transmission 3, Antenna 4).
            ["Noise Reduction and DSP	Ctrl+Shift+N"] = "ToggleDspExpander: ToggleScreenFieldsCategory(0) = FieldsPanel.ToggleCategory(0)",
            ["Audio	Ctrl+Shift+U"] = "ToggleAudioExpander: ToggleScreenFieldsCategory(1) = FieldsPanel.ToggleCategory(1)",
            ["Receiver	Ctrl+Shift+R"] = "ToggleReceiverExpander: ToggleScreenFieldsCategory(2) = FieldsPanel.ToggleCategory(2)",
            ["Transmission	Ctrl+Shift+X"] = "ToggleTransmissionExpander: ToggleScreenFieldsCategory(3) = FieldsPanel.ToggleCategory(3)",
            ["Antenna	Ctrl+Shift+A"] = "ToggleAntennaExpander: ToggleScreenFieldsCategory(4) = FieldsPanel.ToggleCategory(4)",
            // The menu calls _window.SpeakStatusCallback and
            // ShowStatusDialogCallback, which ApplicationEvents.vb wires to
            // Commands.Lookup(CommandValues.SpeakStatus / ShowStatusDialog)
            // .Handler - the very commands the two chords run. Read 2026-10-25.
            ["Speak Status	Ctrl+Shift+S"] = "SpeakStatusCallback = the SpeakStatus command's own handler (ApplicationEvents.vb)",
            ["Status Dialog	Ctrl+Alt+S"] = "ShowStatusDialogCallback = the ShowStatusDialog command's own handler (ApplicationEvents.vb)",
        };

        /// <summary>
        /// Real disagreements whose right meaning is NOT ruled in the register,
        /// so this test records them rather than choosing (#463's instruction).
        /// Key as above; the value names the task and what each side does.
        /// </summary>
        private static readonly Dictionary<string, string> KnownDrift = new(StringComparer.Ordinal)
        {
            // #463, for Noel. The Tuning submenu's "Speak Current Step" shows
            // Shift+S, which is bound in NO scope the menu implies: it is a
            // key of the Frequency field in Modern tuning only (KeyInventory
            // "Freq.Modern", "Shift+S", "Speak the coarse and fine step
            // sizes"). Pressed anywhere else it is a letter with Shift that
            // does nothing on that field. The two do the same thing where both
            // work; whether the menu should name the field, the chord should
            // become global, or the hint should go, is not ruled.
            ["Speak Current Step	Shift+S"] = "#463: a Frequency-field key in Modern tuning, shown as if global",
        };

        // ────────────────────────────────────────────────────────────────
        //  The checks
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_menu_shortcut_is_bound_to_what_the_menu_item_does()
        {
            var menu = ReadMenuShortcuts(Source(MenuFile));
            var keys = new KeyBindings(Source(KeysFile));
            var problems = new List<string>();
            var stillNeeded = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in menu)
            {
                var verdict = keys.Judge(item);
                _out.WriteLine(item.Id + "  ->  " + verdict.Describe());

                if (KnownDrift.ContainsKey(item.Id) || AgreesByReading.ContainsKey(item.Id))
                {
                    if (verdict.Agrees)
                        problems.Add("STALE EXCEPTION " + item.Id + ": it agrees now; remove it from its list");
                    else
                        stillNeeded.Add(item.Id);
                    continue;
                }
                if (!verdict.Agrees) problems.Add(item.Id + "  ->  " + verdict.Describe());
            }

            foreach (string id in KnownDrift.Keys.Concat(AgreesByReading.Keys))
            {
                if (!menu.Any(m => m.Id == id))
                    problems.Add("STALE EXCEPTION " + id + ": no menu item carries it any more; remove it");
            }

            Assert.True(problems.Count == 0,
                "menu shortcuts that promise a chord bound to something else, or to nothing:"
                + Environment.NewLine + string.Join(Environment.NewLine, problems)
                + Environment.NewLine + "If the right meaning is not ruled, record it in KnownDrift with its "
                + "task for Noel; never pick one here (#463).");
        }

        /// <summary>
        /// The instruments, before trusting their silence: the scan finds the
        /// menu's shortcuts at all, reads a known pair as agreeing, and reads a
        /// planted drift as a drift.
        /// </summary>
        [Fact]
        public void The_scan_sees_what_it_claims_to_see()
        {
            var menu = ReadMenuShortcuts(Source(MenuFile));
            Assert.True(menu.Count >= 50, "only " + menu.Count + " menu shortcuts found — the reader is broken");
            Assert.Contains(menu, m => m.ChordText.StartsWith("Ctrl+J, N, ", StringComparison.Ordinal));
            Assert.Contains(menu, m => m.Command != null);

            var keys = new KeyBindings(Source(KeysFile));
            var next = Assert.Single(menu, m => m.Id == "Next Mode\tAlt+M");
            Assert.True(keys.Judge(next).Agrees, "a known agreeing pair reads as a drift: " + keys.Judge(next).Describe());

            // Planted: the Next Mode handler under USB's chord, and Band Up's
            // chord on a command the binding does not run.
            var planted = ReadMenuShortcuts(@"class M { void B() {
                AddWired(modeSub, ""Next Mode\tAlt+U"", () => _window.CycleMode(1));
                AddCommand(log, ""New Entry\tAlt+Up"", Radios.CommandValues.NewLogEntry);
                AddWired(x, ""Phantom\tCtrl+Shift+Q"", () => _window.Nothing());
                AddWired(x, ""Ghost layer\tCtrl+J, N, Z"", () => _window.Nothing());
            } }");
            Assert.Equal(4, planted.Count);
            Assert.All(planted, p => Assert.False(keys.Judge(p).Agrees, p.Id + " was read as agreeing"));
        }

        // ────────────────────────────────────────────────────────────────
        //  The menu side
        // ────────────────────────────────────────────────────────────────

        internal sealed record MenuShortcut(string Label, string ChordText, SyntaxNode? Handler,
            string? Command, IReadOnlyCollection<string> Extra)
        {
            internal string Id => Label + "\t" + ChordText;
        }

        private static readonly string[] Adders = { "AddWired", "AddChecked", "AddRadioChecked", "AddCommand" };

        internal static List<MenuShortcut> ReadMenuShortcuts(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var items = new List<MenuShortcut>();
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                         .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)
                                     && l.Token.ValueText.Contains('\t')))
            {
                string text = literal.Token.ValueText;
                int tab = text.IndexOf('\t');
                string chord = text.Substring(tab + 1);
                string label = text.Substring(0, tab);

                // A label composed elsewhere: "USB" => "\tAlt+U" names its item
                // by the arm's pattern, and the item's code is the arm's value.
                if (literal.Parent is SwitchExpressionArmSyntax arm
                    && arm.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax pattern })
                {
                    string name = pattern.Token.ValueText;
                    items.Add(new MenuShortcut(name, chord, AdderHandler(arm), null, new[] { name }));
                    continue;
                }

                var invocation = literal.Ancestors().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(i => Adders.Contains(NameOf(i)));
                if (invocation == null) continue;
                if (label.Length == 0)
                {
                    // "Lexicon.Get(key) + \"\tCtrl+J, Ctrl+X\"": the label is the key.
                    label = invocation.ArgumentList.Arguments[1].DescendantNodes().OfType<LiteralExpressionSyntax>()
                        .Select(l => l.Token.ValueText).FirstOrDefault(v => !v.Contains('\t')) ?? "";
                }
                var args = invocation.ArgumentList.Arguments;
                string? command = NameOf(invocation) == "AddCommand" && args.Count > 2
                    ? LastName(args[2].Expression) : null;
                items.Add(new MenuShortcut(label, chord, args.Count > 2 ? args[2].Expression : null,
                    command, Array.Empty<string>()));
            }
            return items;
        }

        /// <summary>The handler of the adder call that follows a switch
        /// expression building an accelerator (the Mode submenu's shape).</summary>
        private static SyntaxNode? AdderHandler(SwitchExpressionArmSyntax arm)
        {
            var block = arm.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            var call = block?.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => Adders.Contains(NameOf(i)));
            return call != null && call.ArgumentList.Arguments.Count > 2 ? call.ArgumentList.Arguments[2].Expression : null;
        }

        // ────────────────────────────────────────────────────────────────
        //  The key side
        // ────────────────────────────────────────────────────────────────

        internal sealed record Verdict(bool Agrees, string Detail)
        {
            internal string Describe() => (Agrees ? "AGREES: " : "DISAGREES: ") + Detail;
        }

        internal sealed class KeyBindings
        {
            private readonly Dictionary<Keys, List<string>> _commandsByChord = new();
            private readonly Dictionary<string, List<SyntaxNode>> _handlerByCommand = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<MethodDeclarationSyntax>> _methods = new(StringComparer.Ordinal);
            private readonly List<LeaderSourceScan.SwitchArm> _leaderArms;
            private readonly SyntaxNode _root;

            internal KeyBindings(string keyCommandsSource)
            {
                _root = CSharpSyntaxTree.ParseText(keyCommandsSource).GetRoot();
                foreach (var m in _root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!_methods.TryGetValue(m.Identifier.ValueText, out var list))
                        _methods[m.Identifier.ValueText] = list = new List<MethodDeclarationSyntax>();
                    list.Add(m);
                }

                // _defaultKeys: new(Keys.M | Keys.Alt, CommandValues.ModeNext, KeyScope.Radio)
                var defaults = _root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .First(v => v.Identifier.ValueText == "_defaultKeys");
                foreach (var creation in defaults.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                {
                    var a = creation.ArgumentList?.Arguments;
                    if (a == null || a.Value.Count < 2) continue;
                    if (!TryKeys(a.Value[0].Expression, out Keys chord)) continue;
                    string? command = LastName(a.Value[1].Expression);
                    if (command == null) continue;
                    if (!_commandsByChord.TryGetValue(chord, out var cmds)) _commandsByChord[chord] = cmds = new List<string>();
                    cmds.Add(command);
                }

                // BuildKeyTable: new(CommandValues.X, [KeyTypes.Y,] handler, ...)
                var table = _methods["BuildKeyTable"].Single();
                foreach (var creation in table.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                {
                    var a = creation.ArgumentList?.Arguments;
                    if (a == null || a.Value.Count < 2) continue;
                    if (a.Value[0].Expression.ToString().StartsWith("CommandValues.", StringComparison.Ordinal) is false) continue;
                    string command = LastName(a.Value[0].Expression)!;
                    var handler = a.Value[1].Expression.ToString().StartsWith("KeyTypes.", StringComparison.Ordinal)
                        ? (a.Value.Count > 2 ? a.Value[2].Expression : null)
                        : a.Value[1].Expression;
                    if (handler == null) continue;
                    if (!_handlerByCommand.TryGetValue(command, out var hs)) _handlerByCommand[command] = hs = new List<SyntaxNode>();
                    hs.Add(handler);
                }

                _leaderArms = LeaderSourceScan.SwitchArms(keyCommandsSource, "DoLeaderCommand");
            }

            internal Verdict Judge(MenuShortcut item)
            {
                string chordText = item.ChordText.Trim();
                const string Leader = "Ctrl+J, ";
                HashSet<string> keySide;
                string where;

                if (chordText.StartsWith(Leader, StringComparison.Ordinal))
                {
                    string[] steps = chordText.Substring(Leader.Length).Split(", ");
                    if (!LeaderChordParser.TryParseChord(steps[0], out Keys first))
                        return new Verdict(false, "cannot read the JJ key chord '" + chordText + "'");
                    var arm = _leaderArms.FirstOrDefault(a => a.Labels.Contains(first));
                    if (arm == null) return new Verdict(false, "no DoLeaderCommand arm handles " + steps[0]);
                    var armNode = SyntaxFactory.ParseStatement("{" + arm.Body + "}");
                    if (steps.Length == 1)
                    {
                        keySide = Names(armNode, expand: true);
                        where = "the JJ key arm for " + steps[0];
                    }
                    else
                    {
                        if (!LeaderChordParser.TryParseChord(steps[1], out Keys second))
                            return new Verdict(false, "cannot read the layer key '" + steps[1] + "'");
                        var statements = LayerStatements(armNode, second);
                        if (statements.Count == 0)
                            return new Verdict(false, "the layer opened by Ctrl+J, " + steps[0] + " has no statement naming " + steps[1]);
                        keySide = new HashSet<string>(statements.SelectMany(s => Names(s, expand: false)), StringComparer.Ordinal);
                        where = "the layer statement for " + steps[1] + " under Ctrl+J, " + steps[0];
                    }
                }
                else
                {
                    if (!LeaderChordParser.TryParseChord(chordText, out Keys chord))
                        return new Verdict(false, "cannot read the chord '" + chordText + "'");
                    if (!_commandsByChord.TryGetValue(chord, out var commands))
                        return new Verdict(false, chordText + " is in no _defaultKeys row");
                    if (item.Command != null)
                    {
                        return commands.Contains(item.Command)
                            ? new Verdict(true, chordText + " runs " + item.Command + ", the command the menu names")
                            : new Verdict(false, chordText + " runs " + string.Join(" or ", commands)
                                + ", but the menu item runs " + item.Command);
                    }
                    keySide = new HashSet<string>(commands.SelectMany(c =>
                        _handlerByCommand.TryGetValue(c, out var hs) ? hs.SelectMany(h => Names(h, expand: true)) : Enumerable.Empty<string>()),
                        StringComparer.Ordinal);
                    where = chordText + " (" + string.Join(", ", commands) + ")";
                }

                var menuSide = new HashSet<string>(item.Extra, StringComparer.Ordinal);
                if (item.Handler != null) menuSide.UnionWith(Names(item.Handler, expand: false));
                if (item.Command != null) menuSide.Add(item.Command);
                var shared = menuSide.Intersect(keySide, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
                return shared.Count > 0
                    ? new Verdict(true, where + " shares " + string.Join(", ", shared))
                    : new Verdict(false, where + " shares nothing with the menu item. Menu: "
                        + string.Join(", ", menuSide.OrderBy(s => s)) + ". Key: " + string.Join(", ", keySide.OrderBy(s => s)));
            }

            /// <summary>The statements, in the methods a layer arm calls, that
            /// name <paramref name="second"/> as a key — the layer's own entry
            /// for it (RadioSwitch("apf", Keys.P, ...) and its kind).</summary>
            private List<SyntaxNode> LayerStatements(SyntaxNode armNode, Keys second)
            {
                var found = new List<SyntaxNode>();
                foreach (var name in armNode.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(NameOf))
                {
                    if (name == null || !_methods.TryGetValue(name, out var methods)) continue;
                    foreach (var m in methods)
                    {
                        foreach (var statement in m.DescendantNodes().OfType<StatementSyntax>()
                                     .Where(s => s is LocalDeclarationStatementSyntax or ExpressionStatementSyntax))
                        {
                            bool names = statement.DescendantNodes().OfType<ExpressionSyntax>()
                                .Where(e => e is MemberAccessExpressionSyntax or BinaryExpressionSyntax)
                                .Any(e => TryKeys(e, out Keys k) && k == second);
                            if (names) found.Add(statement);
                        }
                    }
                }
                return found;
            }

            /// <summary>The names and strings a piece of code uses, minus the
            /// plumbing every handler shares, and one level into the
            /// KeyCommands methods it calls when <paramref name="expand"/>.</summary>
            private HashSet<string> Names(SyntaxNode node, bool expand)
            {
                var names = MeaningfulNames(node);
                if (!expand) return names;
                foreach (var called in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                             .Select(i => i.Identifier.ValueText).Distinct().ToList())
                {
                    if (_methods.TryGetValue(called, out var methods))
                        foreach (var m in methods) names.UnionWith(MeaningfulNames(m));
                }
                return names;
            }
        }

        /// <summary>Names every handler uses whatever it does: the radio and
        /// window handles, speech, the lexicon, tracing, and C#'s own words.</summary>
        private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
        {
            "Rig", "rig", "_window", "_context", "GetMainWindow", "GetRigControl", "RigControl", "Radios",
            "ScreenReaderOutput", "Speak", "SpeakNoRadio", "SpeakAfterMenuClose", "Lexicon", "Get",
            "VerbosityLevel", "Terse", "Chatty", "Critical", "SpeechIntent", "Interrupt", "SpeechSubject",
            "Tracing", "TraceLine", "TraceLevel", "Info", "Trace", "Invoke", "ToString", "Equals",
            "FlexBase", "OffOnValues", "on", "off", "v", "p", "m", "e", "k", "s", "x", "_", "var",
            "string", "bool", "int", "null", "true", "false", "StringComparison", "OrdinalIgnoreCase",
            "Ordinal", "EarconPlayer", "Keys", "Dispatcher", "BeginInvoke", "Action", "value",
        };

        private static HashSet<string> MeaningfulNames(SyntaxNode node)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                names.Add(id.Identifier.ValueText);
            foreach (var lit in node.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
                         .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                string v = lit.Token.ValueText;
                if (v.Length >= 2 && !v.Contains('.') && !v.Contains(' ')) names.Add(v);
            }
            names.ExceptWith(Plumbing);
            return names;
        }

        // ────────────────────────────────────────────────────────────────
        //  Small readers
        // ────────────────────────────────────────────────────────────────

        private static string? NameOf(InvocationExpressionSyntax i) => i.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            _ => null,
        };

        private static string? LastName(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null,
        };

        /// <summary>"Keys.M | Keys.Control | Keys.Alt" as a value.</summary>
        private static bool TryKeys(ExpressionSyntax e, out Keys keys)
        {
            keys = Keys.None;
            switch (e)
            {
                case ParenthesizedExpressionSyntax p:
                    return TryKeys(p.Expression, out keys);
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.BitwiseOrExpression):
                    if (!TryKeys(b.Left, out Keys l) || !TryKeys(b.Right, out Keys r)) return false;
                    keys = l | r;
                    return true;
                case MemberAccessExpressionSyntax ma when ma.Expression.ToString() is "Keys" or "WinFormsKeys":
                    return Enum.TryParse(ma.Name.Identifier.ValueText, out keys) && keys != Keys.None;
                default:
                    return false;
            }
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));
    }
}
