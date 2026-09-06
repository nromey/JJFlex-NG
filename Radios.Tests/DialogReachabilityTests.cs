using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Every dialog either has somewhere that constructs it, or an entry here
    /// saying why it does not (#482).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this exists to catch.</b> The Tracking Notch Filter was
    /// wired to the radio and unreachable by a person for two sprints.
    /// <c>FlexBase</c> subscribed to its events and enabled it on every connect;
    /// Jim's WinForms <c>Radios/FlexTNF.cs</c> was deleted in <c>074b2c78</c>
    /// once its Sprint 9 replacement existed; and the replacement,
    /// <c>TNFDialog</c>, was never constructed anywhere. Both commits were
    /// correct. Nothing failed, because absent code is referenced by nothing —
    /// it compiles, it ships, and no test goes red for a feature that simply
    /// has no door.
    /// </para>
    /// <para>
    /// <b>Why the exemption carries a file path.</b> Most of the list below is
    /// not a defect at all: a WPF replacement was written, its WinForms original
    /// is still the live one, and the migration has not finished. That is a
    /// normal in-between state. What made TNF different is that the original was
    /// DELETED while the replacement was still unwired — and that is a
    /// transition no snapshot of either file can see. So an entry claiming a
    /// live original must name it, and this test checks the file still exists.
    /// The day someone deletes it, the exemption stops being true and says so,
    /// which is precisely the morning TNF needed somebody to be told.
    /// </para>
    /// <para>
    /// <b>Checked in both directions.</b> An exemption for a dialog that is now
    /// constructed is also a failure — otherwise a surface that gets wired later
    /// lingers on this list pretending to be deliberate, and the list stops
    /// meaning anything. Same discipline as
    /// <c>EarconCallerCoverageTests</c> (#483).
    /// </para>
    /// <para>
    /// <b>Reads source, not IL.</b> Reflection would mean loading JJFlexWpf and
    /// asking about types, and this project must never be the thing that puts a
    /// WPF window on a live desk. Source also catches the four construction
    /// routes an assembly scan sees poorly: XAML element instantiation, VB's
    /// default form instance, static entry points, and plain C# <c>new</c>.
    /// Every one of them has to NAME the type, which is what makes a name scan
    /// the complete method here — the <c>new X</c> search #482 warns about calls
    /// 49 live surfaces dead precisely because it looks for one route.
    /// </para>
    /// </remarks>
    public sealed class DialogReachabilityTests
    {
        /// <summary>Why a dialog has no construction site, and the evidence that keeps the claim honest.</summary>
        private sealed record Exemption(string Why, string LiveOriginal = "");

        /// <summary>
        /// Dialogs with no construction site anywhere, and the reason each is
        /// acceptable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three shapes are represented, and only the third is an emergency:
        /// </para>
        /// <para>
        /// <b>Migration in progress.</b> A WPF replacement exists, the WinForms
        /// original is still live, nothing is missing from the product. These
        /// name the original so its deletion cannot go unnoticed.
        /// </para>
        /// <para>
        /// <b>Honest dead end.</b> Both halves are dead AND the menus say so
        /// (<c>AddNotImplemented</c>). #482 rules that saying so out loud is the
        /// correct handling, and these should stay dead.
        /// </para>
        /// <para>
        /// <b>Never wired.</b> Written, never connected to anything, no original
        /// behind them. This is the TNF shape and each one is a feature an
        /// operator cannot reach. They are listed rather than fixed because
        /// reviving one is a piece of work with a product decision in it, not a
        /// wiring change — but they are listed so the number is a fact somebody
        /// chose to live with rather than a thing nobody knew.
        /// </para>
        /// </remarks>
        private static readonly Dictionary<string, Exemption> Exempt = new(StringComparer.Ordinal)
        {
            // ── Migration in progress: the WinForms original is still the live
            //    one. Deleting it without wiring the replacement is exactly what
            //    happened to the tracking notch filter. ──
            ["FindLogEntryDialog"] = new("WPF replacement; WinForms original still live.", "FindLogEntry.vb"),
            ["ListerDialog"] = new("WPF replacement; WinForms original still live.", "Lister.vb"),
            ["LogEntryDialog"] = new("WPF replacement; WinForms original still live.", "LogEntry.vb"),
            ["ManageGroupsDialog"] = new("WPF replacement; WinForms original still live.", "ManageGroups.vb"),
            ["MemoryGroupDialog"] = new("WPF replacement; WinForms original still live.", "MemoryGroup.vb"),
            ["MemoryScanDialog"] = new("WPF replacement; WinForms original still live.", "MemoryScan.vb"),
            ["PersonalInfoDialog"] = new("WPF replacement; WinForms original still live.", "PersonalInfo.vb"),
            ["ReverseBeaconDialog"] = new("WPF replacement; WinForms original still live.", "ReverseBeacon.vb"),
            ["ScanDialog"] = new("WPF replacement; WinForms original still live.", "scan.vb"),
            ["SelectScanDialog"] = new("WPF replacement; WinForms original still live.", "SelectScan.vb"),
            ["WelcomeDialog"] = new(
                "WPF replacement; the WinForms original is still live as a VB default form "
                + "instance — `Welcome.ShowDialog` at globals.vb:1987, which is why a `new` "
                + "search finds nothing. HelpLauncher already maps \"WelcomeDialog\" to "
                + "pages/getting-started.htm, so the F1 route was built expecting this one.",
                "Welcome.vb"),

            // ── Both halves dead. #482 ruled Import Log, Export Log and LOTW
            //    Merge honest dead ends: the menus carry AddNotImplemented, so
            //    the operator is told rather than left guessing. Do not revive.
            //    The others here have a WinForms counterpart with no callers
            //    either; whether each is an honest dead end or a silent one is
            //    NOT established by this test, and #482 flags CMDLine
            //    specifically as silent rather than honest. ──
            ["LOTWMergeDialog"] = new("Honest dead end (#482): both halves dead, AddNotImplemented on both menu bars.", "LOTWMerge.vb"),
            ["CMDLineDialog"] = new("Both halves dead. #482: no menu item at all — silent rather than honest. Needs a human eye.", "CMDLine.vb"),
            ["DefineCommandsDialog"] = new("Both halves dead; classification not individually established (#482).", "DefineCommands.vb"),
            ["LoginNameDialog"] = new("Both halves dead; classification not individually established (#482).", "LoginName.vb"),
            ["MenusDialog"] = new("Both halves dead; classification not individually established (#482).", "Menus.vb"),
            ["ScanNameDialog"] = new("Both halves dead; classification not individually established (#482).", "ScanName.vb"),

            // ── Never wired, no original behind them. The TNF shape. Each is a
            //    surface that exists, compiles and ships with no route to it. ──
            ["ATUMemoriesDialog"] = new(
                "NEVER WIRED, and the worst of these: the Radio menu's ATU Memories item "
                + "calls Rig.ShowMemoriesDialog() — the FREQUENCY memories — while "
                + "FlexBase.ShowATUMemoriesDialog, the delegate that would reach this one, "
                + "is assigned nowhere in the repository. The operator gets a plausible "
                + "wrong dialog rather than nothing, so the error does not announce itself."),
            ["PanListDialog"] = new("NEVER WIRED (#482). Two implementations of one idea; Radios/PanListForm.cs is the other and is also dead."),
            ["WattMeterConfigDialog"] = new(
                "NEVER WIRED, and it should stay that way — this one is a door onto an empty "
                + "room. #482 says \"the meter itself is live in globals.vb\"; what is live is "
                + "the plumbing. A W2 is constructed and disposed at startup and exit, the "
                + "reader thread polls forward power and SWR if a port is configured, and "
                + "NOTHING READS EITHER VALUE. Jim's two readers were in Form1, which was "
                + "deleted in 8f8413c9, and the configure menu item went with it. Reviving "
                + "this dialog would hand an operator a settings screen whose whole effect is "
                + "a serial port being polled into two strings nobody displays."),
            ["ComInfoDialog"] = new("NEVER WIRED (#482)."),
            ["GetFileDialog"] = new("NEVER WIRED (#482)."),
            ["LogTemplateDialog"] = new("NEVER WIRED (#482)."),
            ["ClusterDialog"] = new("NEVER WIRED. Named only by test doc comments about its old raw Console.Beep."),
            ["FilterPresetEntryDialog"] = new("NEVER WIRED; FilterPresetEditorDialog is live and may have absorbed it."),
            ["RenameAccountDialog"] = new("NEVER WIRED."),
            ["AboutProgramDialog"] = new(
                "No OPERATOR route. It is constructed, but only reflectively by the test "
                + "harness (JJFlexWpf.Tests DialogCatalog.Discover, StrategyProbe.Subjects), "
                + "which is why the name appears only inside string literals this scan strips. "
                + "AboutDialog is the live About surface and its WinForms counterpart "
                + "AboutProgram.vb has no callers either, so this looks superseded rather than "
                + "pending — but that is not established, and being a fixture in the harness "
                + "will keep it compiling forever regardless."),
        };

        [Fact]
        public void Every_dialog_is_constructed_somewhere_or_says_why_not()
        {
            var unreachable = Unreachable();
            var undeclared = unreachable.Where(d => !Exempt.ContainsKey(d)).OrderBy(d => d, StringComparer.Ordinal).ToList();

            Assert.True(undeclared.Count == 0,
                "These dialogs have a constructor and no construction site anywhere — no C# `new`, "
                + "no VB `New`, no XAML element, no default form instance, no static entry point:\n  "
                + string.Join("\n  ", undeclared)
                + "\n\nA dialog nobody constructs is a feature with no door, and nothing else will "
                + "ever fail for it: absent code is referenced by nothing, so it compiles and ships. "
                + "Either give it a route, or add it to the exemption list in this file with the "
                + "reason — and if the reason is that a WinForms original is still the live one, "
                + "NAME that file, so its deletion cannot pass unnoticed the way FlexTNF.cs did (#482).");
        }

        [Fact]
        public void No_exemption_outlives_the_absence_it_describes()
        {
            var unreachable = new HashSet<string>(Unreachable(), StringComparer.Ordinal);
            var stale = Exempt.Keys.Where(k => !unreachable.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.True(stale.Count == 0,
                "These are on the exemption list but ARE constructed now:\n  "
                + string.Join("\n  ", stale)
                + "\n\nDelete their entries. An exemption for a surface that has since been wired "
                + "lets the next one linger beside it pretending to be deliberate, and then the list "
                + "stops being evidence of anything.");
        }

        [Fact]
        public void Every_claimed_live_original_still_exists()
        {
            var missing = Exempt
                .Where(e => e.Value.LiveOriginal.Length > 0)
                .Where(e => !File.Exists(Path.Combine(RepoRoot(), e.Value.LiveOriginal.Replace('/', Path.DirectorySeparatorChar))))
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .ToList();

            Assert.True(missing.Count == 0,
                "These exemptions rest on a WinForms original that is no longer on disk:\n  "
                + string.Join("\n  ", missing.Select(e => e.Key + " named " + e.Value.LiveOriginal))
                + "\n\nThat is the exact transition that lost the tracking notch filter: the original "
                + "was deleted, truthfully dead at the time, while its replacement had never been "
                + "wired to anything — and the feature left the product between two correct commits. "
                + "Wire the replacement, or establish that the feature is genuinely gone on purpose "
                + "and rewrite the exemption to say so.");
        }

        /// <summary>
        /// Positive control, in both directions. A scanner that finds nothing
        /// reports perfect health for the same reason it reports anything else,
        /// and #483's first matcher called five live sounds dead before its own
        /// control caught it.
        /// </summary>
        [Fact]
        public void The_scan_tells_live_and_dead_apart()
        {
            var unreachable = new HashSet<string>(Unreachable(), StringComparer.Ordinal);

            Assert.True(unreachable.Count > 0,
                "The scan found NO unreachable dialogs. Before believing that, check it is still "
                + "reading source at all — an empty result and a broken scanner look identical.");

            // Known live: constructed by ScreenFieldsPanel and by TNFLauncher.
            Assert.False(unreachable.Contains("NoiseProfilesDialog"),
                "NoiseProfilesDialog is constructed in ScreenFieldsPanel.xaml.cs and the scan called "
                + "it dead. The scan is producing false positives and every other result here is suspect.");
            Assert.False(unreachable.Contains("TNFDialog"),
                "TNFDialog is constructed by TNFLauncher (#482). If this fails, either the launcher "
                + "was removed — the feature has no door again — or the scan has broken.");

            // Known dead, and deliberately so.
            Assert.Contains("WattMeterConfigDialog", unreachable);
        }

        // ────────────────────────────────────────────────────────────────

        private static IEnumerable<string> Unreachable()
        {
            var sources = SourceFiles().ToDictionary(p => p, p => Strip(File.ReadAllText(p), Path.GetExtension(p)));

            // Concrete JJFlexDialog subclasses. Abstract ones are skipped: an
            // abstract class is never constructed by anyone, so a construction
            // scan reports every one of them dead. #482's own inventory listed
            // AudioLevelsDialogBase as an unwired surface for exactly this
            // reason — both its subclasses are opened from the Radio menu.
            var dialogs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (path, text) in sources)
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match m in Regex.Matches(text, @"class\s+(\w+)\s*:\s*(?:Dialogs\.)?JJFlexDialog\b"))
                {
                    int from = Math.Max(0, m.Index - 60);
                    if (text.AsSpan(from, m.Index - from).Contains("abstract", StringComparison.Ordinal)) continue;
                    if (!dialogs.TryGetValue(m.Groups[1].Value, out var own))
                        dialogs[m.Groups[1].Value] = own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    own.Add(path);
                }
            }

            foreach (var (name, ownFiles) in dialogs)
            {
                // A type's own files cannot make it reachable — that is the
                // whole point. Its .xaml counts as its own too.
                var own = new HashSet<string>(ownFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var p in sources.Keys)
                    if (Path.GetFileName(p).StartsWith(name + ".", StringComparison.Ordinal)) own.Add(p);

                var word = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.CultureInvariant);
                bool named = sources.Any(kv => !own.Contains(kv.Key) && word.IsMatch(kv.Value));
                if (!named) yield return name;
            }
        }

        /// <summary>
        /// Remove comments and string literals. Roughly 40% of raw hits in
        /// #482's sweep were doc-comment cross-references — a dialog mentioned
        /// in a <c>&lt;see cref&gt;</c> reads as constructed if you do not strip
        /// them, which is the failure that makes a scan report health it has
        /// not verified.
        /// </summary>
        private static string Strip(string text, string extension)
        {
            if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                return Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline);

            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^[ \t]*(//|').*$", "", RegexOptions.Multiline);
            return Regex.Replace(text, "\"(?:[^\"\\\\\n]|\\\\.)*\"", "\"\"");
        }

        private static IEnumerable<string> SourceFiles()
        {
            // Vendor trees excluded: FlexLib and the audio wrappers are not ours
            // and name none of our dialogs.
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", "obj", "bin", "FlexLib_API", "P-Opus-master", "PortAudioSharp-src-0.19.3", "packages", "node_modules" };

            var stack = new Stack<string>();
            stack.Push(RepoRoot());
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (!skip.Contains(Path.GetFileName(sub))) stack.Push(sub);
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    string ext = Path.GetExtension(f);
                    if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
                        yield return f;
                }
            }
        }

        private static string RepoRoot()
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
