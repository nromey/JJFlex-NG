using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// <b>How "bound to that command" is decided, from source: by ACTIONS,
    /// arguments included.</b> Both sides are reduced to the actions their code
    /// performs: each call, written as its name and its arguments
    /// (<c>CycleMode(1)</c>, <c>BandJump(m160)</c>,
    /// <c>AudioWorkshopDialog.ShowOrFocus(*,0)</c>), and each property it
    /// writes, with the value when the value is a constant
    /// (<c>Mode="USB"</c>, <c>NeuralNoiseReduction=*</c>). A literal, a
    /// negative literal and an enum member are kept, so a direction or an
    /// index that changes is a different action; a local or a handle is
    /// <c>*</c>. Each side is read at its own level and one call deeper
    /// (into KeyCommands, MainWindow, NativeMenuBar and the noise capture
    /// narrator), with the callee's parameters bound to the caller's
    /// constants, so <c>ToggleScreenFieldsCategory(0)</c> is read through to
    /// <c>ToggleCategory(0)</c> and <c>SetMode("USB")</c> through to
    /// <c>Mode="USB"</c>. The two AGREE when an action one side performs at its
    /// own level is among the actions the other performs at either level.
    /// </para>
    /// <para>
    /// <b>Why that rule and not a shared name.</b> The first version of this
    /// test (round one, 2026-10-25) compared the SETS OF NAMES the two sides
    /// used, so "Next Mode" changed from <c>CycleMode(1)</c> to
    /// <c>CycleMode(-1)</c> still shared <c>CycleMode</c> and stayed green, and
    /// seven rows were excused by reading because no name overlapped. Codex
    /// found it on review. Requiring one side's own action to appear on the
    /// other side keeps two handlers that merely reach the same helper deep
    /// down from agreeing by coincidence.
    /// </para>
    /// <para>
    /// A flat chord resolves through <c>KeyCommands._defaultKeys</c> to its
    /// <c>CommandValues</c> rows in <c>BuildKeyTable</c>; a JJ key chord
    /// through its arm of <c>DoLeaderCommand</c>; a layer chord
    /// ("Ctrl+J, N, R") through the leader arm that opens the layer, to the
    /// statement in that layer's builder naming the second key. A menu item
    /// that names a command, through <c>AddCommand</c> or through a
    /// MainWindow callback that <c>ApplicationEvents.vb</c> wires to
    /// <c>Commands.Lookup(CommandValues.X)</c>, is held to that command
    /// exactly on a flat chord, and to that command's handler on a JJ key one.
    /// Where the two really disagree and the right meaning is not ruled, the
    /// item is listed in <see cref="KnownDrift"/> with its task, for Noel,
    /// and that list fails the build the day an entry stops being needed.
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
        private const string HandlersFile = "JJFlexWpf/FreqOutHandlers.cs";

        /// <summary>
        /// The menu's shortcut count on 2026-10-25: 57 literals carrying a tab,
        /// counted by this reader and by a literal grep of NativeMenuBar.cs.
        /// (Round one's report said 60; that was a miscount.) The reader must
        /// find at least this many, so a reader that quietly stops seeing a
        /// shape of row cannot pass by finding fewer.
        /// </summary>
        private const int ShortcutFloor = 57;

        /// <summary>
        /// Real disagreements whose right meaning is NOT ruled in the register,
        /// so this test records them rather than choosing (#463's instruction).
        /// Key: menu label, a tab, the chord as the menu writes it. The value
        /// names the task and what each side does.
        /// </summary>
        private static readonly Dictionary<string, string> KnownDrift = new(StringComparer.Ordinal)
        {
            // Empty since 2026-10-25. Its last entry was "Speak Current Step,
            // Shift+S": a key of the Modern Frequency field only, shown in the
            // menu as if it worked anywhere. Noel ruled at 20:51 that Shift+S
            // works from every Home field where it applies (Modern tuning), and
            // this test now holds the row through the Home keys.
        };

        // ────────────────────────────────────────────────────────────────
        //  The checks
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_menu_shortcut_is_bound_to_what_the_menu_item_does()
        {
            var menu = ReadMenuShortcuts(Source(MenuFile));
            var keys = KeyBindings.Load();
            var problems = new List<string>();

            foreach (var item in menu)
            {
                var verdict = keys.Judge(item);
                _out.WriteLine(item.Id + "  ->  " + verdict.Describe());

                if (KnownDrift.ContainsKey(item.Id))
                {
                    if (verdict.Agrees)
                        problems.Add("STALE EXCEPTION " + item.Id + ": it agrees now; remove it from KnownDrift");
                    continue;
                }
                if (!verdict.Agrees) problems.Add(item.Id + "  ->  " + verdict.Describe());
            }

            foreach (string id in KnownDrift.Keys)
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

        [Fact]
        public void No_menu_shortcut_is_written_where_this_test_cannot_read_it()
        {
            // An interpolated string carrying a tab is a shortcut this reader
            // cannot resolve, so it would be judged by nobody. Write it as a
            // literal, or teach the reader its shape.
            var unread = ReadInterpolatedShortcuts(Source(MenuFile));
            Assert.True(unread.Count == 0,
                "menu text with a shortcut column built by interpolation, which this test cannot read: "
                + string.Join("; ", unread));
        }

        /// <summary>
        /// The instruments, before trusting their silence: the scan finds the
        /// menu's shortcuts at all, reads known pairs as agreeing, and reads
        /// every planted drift as a drift — including the shapes the
        /// name-overlap version passed: the same method with a different
        /// argument, and a different index into the same panel.
        /// </summary>
        [Fact]
        public void The_scan_sees_what_it_claims_to_see()
        {
            var menu = ReadMenuShortcuts(Source(MenuFile));
            Assert.True(menu.Count >= ShortcutFloor, "only " + menu.Count + " menu shortcuts found — the reader is broken");
            Assert.Contains(menu, m => m.ChordText.StartsWith("Ctrl+J, N, ", StringComparison.Ordinal));
            Assert.Contains(menu, m => m.Command != null);
            Assert.Contains(menu, m => m.Bound.Count > 0);   // the Mode submenu's switch shape

            var keys = KeyBindings.Load();
            foreach (string id in new[]
                     {
                         "Next Mode\tAlt+M", "Previous Mode\tAlt+Shift+M", "USB\tAlt+U",
                         "Antenna\tCtrl+Shift+A", "Speak Status\tCtrl+Shift+S", "Status Dialog\tCtrl+Alt+S",
                         "On-Radio Neural NR (RNN)\tCtrl+J, N, R", "Log Statistics\tCtrl+J, Alt+L",
                         "Speak Current Step\tShift+S",
                     })
            {
                var known = Assert.Single(menu, m => m.Id == id);
                var verdict = keys.Judge(known);
                Assert.True(verdict.Agrees, "a known agreeing pair reads as a drift: " + id + " -> " + verdict.Describe());
            }

            var planted = ReadMenuShortcuts(@"class M { void B() {
                AddWired(modeSub, ""Next Mode\tAlt+U"", () => _window.CycleMode(1));
                AddCommand(log, ""New Entry\tAlt+Up"", Radios.CommandValues.NewLogEntry);
                AddWired(x, ""Phantom\tCtrl+Shift+Q"", () => _window.Nothing());
                AddWired(x, ""Ghost layer\tCtrl+J, N, Z"", () => _window.Nothing());
                AddWired(modeSub, ""Next Mode\tAlt+M"", () => _window.CycleMode(-1));
                AddWired(parent, ""Band Up\tAlt+Up"", () => _window.BandNavigate(-1));
                AddChecked(parent, ""160m\tF3"", () => _window.BandJump(Bands.BandNames.m80), () => true);
                AddWired(screenFields, ""Antenna\tCtrl+Shift+A"", () => _window.FieldsPanel.ToggleCategory(3));
                AddWired(tools, ""Status Dialog\tCtrl+Alt+S"", () => _window.SpeakStatusCallback?.Invoke());
                AddChecked(nbSub, ""Noise Blanker (NB)\tCtrl+J, N, B"", () =>
                    ToggleDSP(""Noise Blanker"", () => Rig.WidebandNoiseBlanker, v => Rig.WidebandNoiseBlanker = v), () => true);
                AddWired(tuningSub, ""Speak Current Step\tShift+S"", () => _window.SpeakFrequency());
                AddWired(tuningSub, ""Speak Current Step\tShift+Q"", () => _window.TuningMenuSpeakStep());
            }
            void C() {
                foreach (string modeName in RigCaps.ModeTable)
                {
                    string m = modeName;
                    string accel = m switch { ""USB"" => ""\tAlt+L"", _ => """" };
                    AddRadioChecked(modeSub, m + accel, () => { Rig.Mode = m; }, () => true);
                }
            } }");
            Assert.Equal(13, planted.Count);
            Assert.All(planted, p => Assert.False(keys.Judge(p).Agrees, p.Id + " was read as agreeing: " + keys.Judge(p).Describe()));

            // And the same plants written correctly agree, so the reds above
            // are about the planted difference and not about the shape.
            var right = ReadMenuShortcuts(@"class M { void B() {
                AddWired(modeSub, ""Next Mode\tAlt+M"", () => _window.CycleMode(1));
                AddWired(parent, ""Band Up\tAlt+Up"", () => _window.BandNavigate(1));
                AddChecked(parent, ""160m\tF3"", () => _window.BandJump(Bands.BandNames.m160), () => true);
                AddWired(screenFields, ""Antenna\tCtrl+Shift+A"", () => _window.FieldsPanel.ToggleCategory(4));
                AddChecked(nbSub, ""Noise Blanker (NB)\tCtrl+J, N, B"", () =>
                    ToggleDSP(""Noise Blanker"", () => Rig.NoiseBlanker, v => Rig.NoiseBlanker = v), () => true);
            }
            void C() {
                foreach (string modeName in RigCaps.ModeTable)
                {
                    string m = modeName;
                    string accel = m switch { ""LSB"" => ""\tAlt+L"", _ => """" };
                    AddRadioChecked(modeSub, m + accel, () => { Rig.Mode = m; }, () => true);
                }
            } }");
            Assert.Equal(6, right.Count);
            Assert.All(right, p => Assert.True(keys.Judge(p).Agrees, p.Id + " was read as a drift: " + keys.Judge(p).Describe()));
        }

        // ────────────────────────────────────────────────────────────────
        //  The menu side
        // ────────────────────────────────────────────────────────────────

        /// <param name="Bound">Names the handler uses whose value the menu row
        /// fixes: the Mode submenu's loop variable is "USB" on the USB row.</param>
        internal sealed record MenuShortcut(string Label, string ChordText, SyntaxNode? Handler,
            string? Command, IReadOnlyDictionary<string, string> Bound)
        {
            internal string Id => Label + "\t" + ChordText;
        }

        private static readonly string[] Adders = { "AddWired", "AddChecked", "AddRadioChecked", "AddCommand" };
        private static readonly IReadOnlyDictionary<string, string> NoBindings = new Dictionary<string, string>();

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
                // by the arm's pattern, and the item's code is the adder that
                // follows, with the switch's variable bound to the pattern.
                if (literal.Parent is SwitchExpressionArmSyntax arm
                    && arm.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax pattern })
                {
                    string name = pattern.Token.ValueText;
                    var bound = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (arm.Parent is SwitchExpressionSyntax { GoverningExpression: IdentifierNameSyntax governing })
                        bound[governing.Identifier.ValueText] = pattern.Token.Text;
                    items.Add(new MenuShortcut(name, chord, AdderHandler(arm), null, bound));
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
                    command, NoBindings));
            }
            return items;
        }

        /// <summary>Interpolated strings carrying a tab: shortcut columns the
        /// reader above cannot resolve.</summary>
        internal static List<string> ReadInterpolatedShortcuts(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            return root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>()
                .Where(s => s.Contents.OfType<InterpolatedStringTextSyntax>().Any(t => t.TextToken.ValueText.Contains('\t')))
                .Select(s => s.ToString())
                .ToList();
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

        /// <summary>What one side does: its own actions, and those one call deeper.</summary>
        private sealed record Side(HashSet<string> Own, HashSet<string> All);

        internal sealed class KeyBindings
        {
            private const string KeyClass = "KeyCommands";
            private const string MenuClass = "NativeMenuBar";
            private static readonly string[] KeyHomes = { KeyClass, "MainWindow" };
            private static readonly string[] MenuHomes = { MenuClass, "MainWindow" };
            private const string HomeClass = "FreqOutHandlers";
            private static readonly string[] HomeHomes = { HomeClass, "MainWindow" };

            /// <summary>Every file whose methods a side may be read one call
            /// into. A class not here is read at its call only.</summary>
            private static readonly string[] IndexedFiles =
            {
                KeysFile, MenuFile,
                "JJFlexWpf/MainWindow.xaml.cs", "JJFlexWpf/MainWindow.AutoUpdate.cs",
                "JJFlexWpf/MainWindow.FirmwareAdvisory.cs", "JJFlexWpf/MainWindow.Recorder.cs",
                "JJFlexWpf/NoiseProfiles.cs", HandlersFile,
            };

            private readonly Dictionary<Keys, List<string>> _commandsByChord = new();
            private readonly Dictionary<string, List<SyntaxNode>> _handlerByCommand = new(StringComparer.Ordinal);
            private readonly Dictionary<string, List<MethodDeclarationSyntax>> _keyMethods = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Type, string Method), List<MethodDeclarationSyntax>> _index = new();
            private readonly HashSet<string> _indexedTypes = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _callbackCommand;
            private readonly List<LeaderSourceScan.SwitchArm> _leaderArms;
            private readonly MethodDeclarationSyntax? _universalHome;

            internal static KeyBindings Load() => new(
                Source(KeysFile),
                IndexedFiles.Select(Source),
                Source("ApplicationEvents.vb"));

            internal KeyBindings(string keyCommandsSource, IEnumerable<string> indexedSources, string applicationEventsVb)
            {
                var root = CSharpSyntaxTree.ParseText(keyCommandsSource).GetRoot();
                foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (!_keyMethods.TryGetValue(m.Identifier.ValueText, out var list))
                        _keyMethods[m.Identifier.ValueText] = list = new List<MethodDeclarationSyntax>();
                    list.Add(m);
                }

                foreach (string src in indexedSources)
                {
                    foreach (var m in CSharpSyntaxTree.ParseText(src).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        string type = DeclaringType(m);
                        _indexedTypes.Add(type);
                        var key = (type, m.Identifier.ValueText);
                        if (!_index.TryGetValue(key, out var list)) _index[key] = list = new List<MethodDeclarationSyntax>();
                        list.Add(m);
                    }
                }

                // _defaultKeys: new(Keys.M | Keys.Alt, CommandValues.ModeNext, KeyScope.Radio)
                var defaults = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
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
                var table = _keyMethods["BuildKeyTable"].Single();
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

                // ApplicationEvents.vb: WpfMainWindow.SpeakStatusCallback = Sub()
                //     Dim kt = Commands.Lookup(CommandValues.SpeakStatus)
                _callbackCommand = Regex.Matches(applicationEventsVb,
                        @"WpfMainWindow\.(\w+Callback)\s*=\s*Sub\(\)\s*\r?\n\s*Dim\s+\w+\s*=\s*Commands\.Lookup\(CommandValues\.(\w+)\)")
                    .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

                _leaderArms = LeaderSourceScan.SwitchArms(keyCommandsSource, "DoLeaderCommand");
                _universalHome = Methods(HomeClass, "TryHandleUniversalHomeKey").FirstOrDefault();
            }

            /// <summary>
            /// The branch of <c>FreqOutHandlers.TryHandleUniversalHomeKey</c>
            /// that answers <paramref name="chord"/>: an <c>if</c> testing the
            /// key by name (<c>key == Key.S</c>) and exactly the modifiers the
            /// chord carries (<c>Keyboard.Modifiers == ModifierKeys.Shift</c>).
            /// Null when no branch does.
            /// </summary>
            private SyntaxNode? HomeKeyBranch(Keys chord)
            {
                if (_universalHome == null) return null;
                var mods = new List<string>();
                if ((chord & Keys.Control) != 0) mods.Add("Control");
                if ((chord & Keys.Shift) != 0) mods.Add("Shift");
                if ((chord & Keys.Alt) != 0) mods.Add("Alt");
                if (mods.Count == 0) return null;   // bare letters are read through ch, not this shape
                string keyTest = "key == Key." + (chord & Keys.KeyCode);
                string modTest = "Keyboard.Modifiers == " + string.Join(" | ", mods.Select(m => "ModifierKeys." + m));
                return _universalHome.DescendantNodes().OfType<IfStatementSyntax>()
                    .FirstOrDefault(i => i.Condition.ToString().Contains(keyTest, StringComparison.Ordinal)
                                         && i.Condition.ToString().Contains(modTest, StringComparison.Ordinal))
                    ?.Statement;
            }

            internal Verdict Judge(MenuShortcut item)
            {
                // ── what the menu item does ──
                string? command = item.Command;
                Side? menu = null;
                if (command == null && item.Handler != null)
                {
                    menu = Read(item.Handler, item.Bound, MenuClass, MenuHomes);
                    // A MainWindow callback the shell wires to a command IS
                    // that command (ApplicationEvents.vb).
                    var wired = menu.Own.Select(a => a.EndsWith("()", StringComparison.Ordinal) ? a[..^2] : null)
                        .Where(n => n != null && _callbackCommand.ContainsKey(n!)).ToList();
                    if (wired.Count == 1) command = _callbackCommand[wired[0]!];
                }
                // A menu item that runs a command does what that command's
                // handler does.
                if (command != null) menu = CommandSide(command);
                if (menu == null) return new Verdict(false, "the menu item has no handler this test can read");

                // ── what the chord does ──
                string chordText = item.ChordText.Trim();
                const string Leader = "Ctrl+J, ";
                Side key;
                string where;
                if (chordText.StartsWith(Leader, StringComparison.Ordinal))
                {
                    string[] steps = chordText.Substring(Leader.Length).Split(", ");
                    if (!Radios.LeaderChordParser.TryParseChord(steps[0], out Keys first))
                        return new Verdict(false, "cannot read the JJ key chord '" + chordText + "'");
                    var arm = _leaderArms.FirstOrDefault(a => a.Labels.Contains(first));
                    if (arm == null) return new Verdict(false, "no DoLeaderCommand arm handles " + steps[0]);
                    var armNode = SyntaxFactory.ParseStatement("{" + arm.Body + "}");
                    if (steps.Length == 1)
                    {
                        key = Read(armNode, NoBindings, KeyClass, KeyHomes);
                        where = "the JJ key arm for " + steps[0];
                    }
                    else
                    {
                        if (!Radios.LeaderChordParser.TryParseChord(steps[1], out Keys second))
                            return new Verdict(false, "cannot read the layer key '" + steps[1] + "'");
                        var statements = LayerStatements(armNode, second);
                        if (statements.Count == 0)
                            return new Verdict(false, "the layer opened by Ctrl+J, " + steps[0] + " has no statement naming " + steps[1]);
                        key = Union(statements.Select(s => Read(s, NoBindings, KeyClass, KeyHomes)));
                        where = "the layer statement for " + steps[1] + " under Ctrl+J, " + steps[0];
                    }
                }
                else
                {
                    if (!Radios.LeaderChordParser.TryParseChord(chordText, out Keys chord))
                        return new Verdict(false, "cannot read the chord '" + chordText + "'");
                    if (!_commandsByChord.TryGetValue(chord, out var commands))
                    {
                        // Not a registry chord: it may be a universal Home key,
                        // which the Home fields answer themselves (Shift+S).
                        var homeKey = HomeKeyBranch(chord);
                        if (homeKey == null)
                            return new Verdict(false, chordText + " is in no _defaultKeys row and is no universal Home key");
                        if (command != null)
                            return new Verdict(false, chordText + " is a Home key, but the menu item runs the command " + command);
                        key = Read(homeKey, NoBindings, HomeClass, HomeHomes);
                        where = "the universal Home key " + chordText;
                        goto compare;
                    }
                    if (command != null)
                    {
                        // The menu names its command: held to it exactly.
                        return commands.Contains(command)
                            ? new Verdict(true, chordText + " runs " + command + ", the command the menu item runs")
                            : new Verdict(false, chordText + " runs " + string.Join(" or ", commands)
                                + ", but the menu item runs " + command);
                    }
                    key = Union(commands.Select(CommandSide));
                    where = chordText + " (" + string.Join(", ", commands) + ")";
                }

            compare:
                var shared = menu.Own.Intersect(key.All).Union(key.Own.Intersect(menu.All))
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                return shared.Count > 0
                    ? new Verdict(true, where + " also does " + string.Join(", ", shared))
                    : new Verdict(false, where + " does none of what the menu item does. Menu: "
                        + string.Join(", ", menu.Own.OrderBy(s => s, StringComparer.Ordinal))
                        + ". Key: " + string.Join(", ", key.Own.OrderBy(s => s, StringComparer.Ordinal)));
            }

            /// <summary>A command's BuildKeyTable handlers, read as the key side reads them.</summary>
            private Side CommandSide(string command) =>
                _handlerByCommand.TryGetValue(command, out var hs)
                    ? Union(hs.Select(h => Read(h, NoBindings, KeyClass, KeyHomes)))
                    : new Side(new HashSet<string>(), new HashSet<string>());

            /// <summary>
            /// A piece of handler code: its own actions, and its actions plus
            /// those one call deeper. A bare method group (a handler written
            /// as <c>ATUTuneHandler</c>) IS its body, so its body is its own
            /// level.
            /// </summary>
            private Side Read(SyntaxNode node, IReadOnlyDictionary<string, string> bound, string owner, string[] homes)
            {
                if (node is IdentifierNameSyntax group)
                {
                    var bodies = homes.SelectMany(h => Methods(h, group.Identifier.ValueText)).ToList();
                    if (bodies.Count > 0) return Union(bodies.Select(b => Read(Body(b), bound, DeclaringType(b), homes)));
                }

                var own = Actions.Of(node, bound, owner);
                var all = new HashSet<string>(own, StringComparer.Ordinal);
                foreach (var call in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    string? name = NameOf(call);
                    if (name == null) continue;
                    string? type = ReceiverType(call);
                    IEnumerable<MethodDeclarationSyntax> targets = type != null && _indexedTypes.Contains(type)
                        ? Methods(type, name)
                        : (call.Expression is IdentifierNameSyntax
                            ? Methods(owner, name).Concat(homes.Where(h => h != owner).SelectMany(h => Methods(h, name)))
                            : homes.SelectMany(h => Methods(h, name)));
                    foreach (var m in targets.Distinct())
                    {
                        var calleeBound = Bind(m, call, bound);
                        all.UnionWith(Actions.Of(Body(m), calleeBound, DeclaringType(m)));
                    }
                }
                return new Side(own, all);
            }

            private IEnumerable<MethodDeclarationSyntax> Methods(string type, string name) =>
                _index.TryGetValue((type, name), out var list) ? list : Enumerable.Empty<MethodDeclarationSyntax>();

            /// <summary>The callee's parameters bound to the caller's constant arguments.</summary>
            private static IReadOnlyDictionary<string, string> Bind(MethodDeclarationSyntax m, InvocationExpressionSyntax call,
                IReadOnlyDictionary<string, string> bound)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var parameters = m.ParameterList.Parameters;
                var args = call.ArgumentList.Arguments;
                for (int i = 0; i < parameters.Count && i < args.Count; i++)
                {
                    string value = Actions.Arg(args[i].Expression, bound);
                    if (value != Actions.Any) result[parameters[i].Identifier.ValueText] = value;
                }
                return result;
            }

            /// <summary>The class an invocation's receiver names, when it names
            /// one: <c>NoiseCaptureNarrator.Toggle(...)</c> is NoiseCaptureNarrator's.</summary>
            private static string? ReceiverType(InvocationExpressionSyntax call) =>
                call.Expression is MemberAccessExpressionSyntax ma ? LastName(ma.Expression) : null;

            private static SyntaxNode Body(MethodDeclarationSyntax m) => (SyntaxNode?)m.Body ?? m.ExpressionBody ?? (SyntaxNode)m;

            private static Side Union(IEnumerable<Side> sides)
            {
                var own = new HashSet<string>(StringComparer.Ordinal);
                var all = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in sides) { own.UnionWith(s.Own); all.UnionWith(s.All); }
                return new Side(own, all);
            }

            /// <summary>The statements, in the methods a layer arm calls, that
            /// name <paramref name="second"/> as a key — the layer's own entry
            /// for it (RadioSwitch("apf", Keys.P, ...) and its kind).</summary>
            private List<SyntaxNode> LayerStatements(SyntaxNode armNode, Keys second)
            {
                var found = new List<SyntaxNode>();
                foreach (var name in armNode.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(NameOf))
                {
                    if (name == null || !_keyMethods.TryGetValue(name, out var methods)) continue;
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
        }

        /// <summary>
        /// The actions a piece of code performs, as strings that compare
        /// equal exactly when the action is the same: a call with its
        /// arguments, a property write with its value, a dialog constructed.
        /// </summary>
        internal static class Actions
        {
            /// <summary>An argument whose value this reading cannot know: a local, a handle, an expression.</summary>
            internal const string Any = "*";

            /// <summary>Calls every handler makes whatever it does: speech, the
            /// lexicon, tracing, the window and radio handles, the dispatcher.</summary>
            private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
            {
                "Speak", "SpeakNoRadio", "SpeakAfterMenuClose", "Get", "TraceLine", "Trace", "ToString", "Equals",
                "GetMainWindow", "GetRigControl", "GetActiveUIMode", "GetPower", "BeginInvoke", "Delay",
                "Dispatcher", "Format", "StartsWith", "IsNullOrEmpty", "Contains", "OnMainDispatcher",
                "LeaderNoRadio", "nameof",
            };

            /// <summary>Verbs too general to mean anything without their
            /// receiver: two dialogs both "ShowDialog". These carry the
            /// receiver's name.</summary>
            private static readonly HashSet<string> GenericVerbs = new(StringComparer.Ordinal)
            {
                "Show", "ShowDialog", "ShowOrFocus", "Open", "Start", "Stop", "Cancel", "Toggle", "ToggleAsync",
                "Present", "Focus", "Activate", "Close", "Run",
            };

            internal static HashSet<string> Of(SyntaxNode node, IReadOnlyDictionary<string, string> bound, string owner)
            {
                var actions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var call in node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
                {
                    string? name = CallName(call, owner);
                    if (name == null) continue;
                    actions.Add(name + "(" + string.Join(",", call.ArgumentList.Arguments.Select(a => Arg(a.Expression, bound))) + ")");
                }
                foreach (var write in node.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>()
                             .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
                {
                    string? target = write.Left switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                        MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
                        _ => null,   // a local or a field of the handler's own: not an effect on anything
                    };
                    if (target == null || Plumbing.Contains(target)) continue;
                    actions.Add(target + "=" + Value(write.Right, target, bound));
                }
                foreach (var creation in node.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
                {
                    string type = TypeName(creation.Type);
                    if (type.EndsWith("Dialog", StringComparison.Ordinal) || type.EndsWith("Window", StringComparison.Ordinal))
                        actions.Add("new " + type);
                }
                return actions;
            }

            private static string? CallName(InvocationExpressionSyntax call, string owner)
            {
                string? name = NameOf(call);
                if (name == null) return null;
                ExpressionSyntax? receiver = call.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Expression,
                    MemberBindingExpressionSyntax => call.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                        .FirstOrDefault(c => c.WhenNotNull.Span.Contains(call.Span))?.Expression,
                    _ => null,
                };

                // A delegate invoked: the action is the delegate's name.
                if (name == "Invoke")
                {
                    if (receiver == null) return null;
                    name = LastName(receiver);
                    if (name == null) return null;
                    receiver = null;
                }
                if (Plumbing.Contains(name) || name.EndsWith("Tone", StringComparison.Ordinal)) return null;
                if (GenericVerbs.Contains(name))
                    return (receiver == null ? owner : (LastName(receiver) ?? Any)) + "." + name;
                return name;
            }

            /// <summary>A literal as written, a negative literal, an enum member
            /// or a property by its own name, a bound name by its value; anything
            /// else is <see cref="Any"/>.</summary>
            internal static string Arg(ExpressionSyntax e, IReadOnlyDictionary<string, string> bound) => e switch
            {
                LiteralExpressionSyntax l => l.Token.Text,
                PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax l } => "-" + l.Token.Text,
                ParenthesizedExpressionSyntax p => Arg(p.Expression, bound),
                CastExpressionSyntax c => Arg(c.Expression, bound),
                IdentifierNameSyntax id => bound.TryGetValue(id.Identifier.ValueText, out var v) ? v : Any,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                _ => Any,
            };

            /// <summary>A written value. "X = !X" toggles X, which is the same
            /// action as setting X from a runtime value.</summary>
            private static string Value(ExpressionSyntax right, string target, IReadOnlyDictionary<string, string> bound)
            {
                if (right is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                    && LastName(not.Operand) == target)
                    return Any;
                return Arg(right, bound);
            }

            private static string TypeName(TypeSyntax t) => t switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.ValueText,
                SimpleNameSyntax s => s.Identifier.ValueText,
                _ => t.ToString(),
            };
        }

        // ────────────────────────────────────────────────────────────────
        //  Small readers
        // ────────────────────────────────────────────────────────────────

        private static string DeclaringType(SyntaxNode node) =>
            node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";

        private static string? NameOf(InvocationExpressionSyntax i) => i.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            _ => null,
        };

        private static string? LastName(ExpressionSyntax e) => e switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            ParenthesizedExpressionSyntax p => LastName(p.Expression),
            ConditionalAccessExpressionSyntax c => LastName(c.WhenNotNull),
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            ObjectCreationExpressionSyntax o => "new " + o.Type,
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
