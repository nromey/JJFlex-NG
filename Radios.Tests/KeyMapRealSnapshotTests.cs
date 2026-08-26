using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The REAL damaged key map — the pre-2026-08-18 file from the NAS AppData
    /// snapshot — run through the real detector against the real, current
    /// defaults table (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a real fixture when KeyMapIntegrityTests already exists.</b>
    /// Its fixtures are synthetic, and synthetic fixtures share assumptions
    /// with the code under test: the first version of the detector checked
    /// the slip in the WRONG DIRECTION, the tests built their fixtures the
    /// same wrong way, and nine tests passed against a detector that could
    /// not see the real file. Only running it against the actual damaged
    /// snapshot caught it (commit 31853d9b). This test makes that run
    /// permanent.
    /// </para>
    /// <para>
    /// <b>The fixture</b> is every (id, key, savedDefault) triple from
    /// KeyDefs.xml inside
    /// <c>historical\appdata\appdata-20260821-100458.zip</c> — 114 entries,
    /// ids 0-118, Version 5, written by a build older than commit 40307951's
    /// mid-enum insertion of SpeakContextHelp at 96. Nothing else from the
    /// snapshot is reproduced here; a key map holds key codes and command
    /// numbers, nothing personal.
    /// </para>
    /// <para>
    /// <b>The defaults</b> come from parsing <c>_defaultKeys</c> out of
    /// JJFlexWpf/KeyCommands.cs SOURCE on every run (Radios.Tests cannot load
    /// the WPF assembly), so the fixture is always judged against the
    /// defaults table the shipping build actually carries. The corollary: if
    /// a default in the slipped range is ever deliberately reassigned, the
    /// counts asserted below DECAY — the snapshot's evidence for that id
    /// stops matching any neighbour. That failure means "update the expected
    /// numbers consciously", not "the detector broke".
    /// </para>
    /// <para>
    /// <b>19, not the measured 22.</b> The 2026-08-24 measurement diffed the
    /// snapshot against the operator's LIVE FILE and found 22 ids holding the
    /// previous command's key. Three of those entries carry no key and no
    /// recorded default at all (both zero) — no evidence, so the detector
    /// counts them untracked rather than guessed-at, exactly as designed.
    /// They also hold nothing an operator could lose: SetValues' None-key
    /// guard and MergeNewDefaults give those commands their proper defaults
    /// on load. 19 evidence-backed slips is the whole detectable damage, and
    /// every one is repairable.
    /// </para>
    /// </remarks>
    public class KeyMapRealSnapshotTests
    {
        /// <summary>
        /// id,key,savedDefault per entry, semicolon-separated, in id order —
        /// extracted verbatim from the snapshot's KeyDefs.xml (i, key,
        /// defaultKey elements). Note key == savedDefault on every row: the
        /// operator had zero customised bindings, which is why the 2026-08-18
        /// insertion damaged nothing on HIS machine and why every slipped
        /// entry here is silently repairable.
        /// </summary>
        private const string SnapshotTriples =
            "0,0,0;1,113,113;2,131142,131142;3,0,0;4,0,0;6,262212,262212;7,131159,131159;8,0,0;"
            + "9,262211,262211;10,262228,262228;11,262226,262226;12,262225,262225;13,262227,262227;14,262222,262222;15,0,0;16,0,0;"
            + "17,262213,262213;18,131150,131150;19,0,0;20,0,0;21,0,0;22,131162,131162;23,0,0;24,196678,196678;"
            + "25,0,0;26,196722,196722;27,196723,196723;28,123,123;31,131152,131152;32,0,0;33,0,0;34,0,0;"
            + "35,0,0;36,0,0;37,0,0;38,196721,196721;39,262234,262234;40,196675,196675;41,393298,393298;42,327768,327768;"
            + "43,262215,262215;45,0,0;46,0,0;47,0,0;48,131148,131148;49,0,0;50,0,0;51,0,0;"
            + "52,0,0;53,196724,196724;54,0,0;55,117,117;56,196686,196686;57,393292,393292;58,131263,131263;59,196691,196691;"
            + "60,393299,393299;61,327763,327763;62,114,114;63,115,115;64,65650,65650;65,116,116;66,65651,65651;67,117,117;"
            + "68,65652,65652;69,118,118;70,65653,65653;71,119,119;72,120,120;74,262182,262182;75,262184,262184;76,262221,262221;"
            + "77,327757,327757;78,262229,262229;79,262220,262220;80,262211,262211;81,262209,262209;82,262214,262214;83,262212,262212;84,327748,327748;"
            + "85,131155,131155;86,393293,393293;87,393296,393296;88,393302,393302;89,196827,196827;90,196829,196829;91,393435,393435;92,393437,393437;"
            + "93,0,0;94,196695,196695;95,112,112;96,196692,196692;97,131156,131156;98,131149,131149;99,327718,327718;100,327720,327720;"
            + "101,196686,196686;102,196693,196693;103,196690,196690;104,196696,196696;105,196673,196673;106,0,0;107,131187,131187;108,196694,196694;"
            + "109,0,0;110,0,0;111,65613,65613;112,65724,65724;113,196674,196674;114,0,0;115,196685,196685;116,196684,196684;"
            + "117,196678,196678;118,393286,393286";

        [Fact]
        public void The_real_damaged_file_registers_as_shifted_at_96_with_nothing_customised()
        {
            var defaults = CurrentDefaultsFromSource();
            var verdict = KeyMapIntegrity.Check(SnapshotBindings(),
                id => defaults.TryGetValue(id, out Keys k) ? k : Keys.None);

            Assert.True(verdict.LooksShifted,
                "The genuinely damaged file no longer registers as shifted. Either the detector "
                + "regressed, or enough defaults have been reassigned since 2026-08-24 that the "
                + "snapshot's evidence has decayed below the threshold — find out which before "
                + "touching anything. " + verdict.Describe());

            Assert.True(verdict.FirstSlippedId == 96,
                $"The slip must start at 96 — commit 40307951's insertion point — got "
                + $"{verdict.FirstSlippedId}. If a default at or near 96 was deliberately "
                + "reassigned, update this expectation consciously.");

            Assert.True(verdict.SlippedByOne == 19,
                $"Expected the 19 evidence-backed slips measured against the frozen defaults "
                + $"(3 more of the original 22 are untracked zero-key rows), got "
                + $"{verdict.SlippedByOne}. A deliberate default reassignment in the slipped "
                + "range decays this count — update it consciously if so. " + verdict.Describe());

            Assert.True(verdict.Unexplained == 0,
                "Every mismatch in this file is explained by the insertion; an unexplained one "
                + "means either a default changed (update the counts) or the detector drifted. "
                + verdict.Describe());
        }

        [Fact]
        public void Every_slip_in_the_real_file_is_repairable_and_none_is_customised()
        {
            // Don's exact case. key == savedDefault on every snapshot row, so
            // the repair path may fix ALL of it silently and persist — nothing
            // anybody chose is lost. If this ever reports a customised id,
            // the fixture was edited, and the silent-repair reasoning in
            // KeyCommands.RepairSlippedKeyMap no longer rests on evidence.
            var defaults = CurrentDefaultsFromSource();
            var verdict = KeyMapIntegrity.Check(SnapshotBindings(),
                id => defaults.TryGetValue(id, out Keys k) ? k : Keys.None);

            Assert.Empty(verdict.CustomisedIds);
            Assert.Equal(verdict.SlippedByOne, verdict.RepairableIds.Count);
            Assert.Contains(96, verdict.RepairableIds);
        }

        [Fact]
        public void The_defaults_parser_actually_read_the_real_table()
        {
            // Positive control on the source parse: a broken region-finder
            // would return an empty map, every lookup would answer None, the
            // whole fixture would count as untracked, and both tests above
            // would fail with misleading messages — or worse, a future edit
            // could make them pass vacuously. Prove the parser sees the table
            // by checking entries whose values are pinned by history.
            var defaults = CurrentDefaultsFromSource();

            Assert.True(defaults.Count >= 100,
                $"only {defaults.Count} defaults parsed from _defaultKeys — the region or regex is broken");
            Assert.Equal(Keys.F1, defaults[(int)CommandValues.ShowContextHelp]);
            Assert.Equal(Keys.F1 | Keys.Control, defaults[(int)CommandValues.SpeakContextHelp]);
            Assert.Equal(Keys.F2, defaults[(int)CommandValues.ShowFreq]);
        }

        // ────────────────────────────────────────────────────────────────

        private static List<KeyMapIntegrity.SavedBinding> SnapshotBindings()
        {
            var list = new List<KeyMapIntegrity.SavedBinding>();
            foreach (string t in SnapshotTriples.Split(';'))
            {
                string[] p = t.Split(',');
                list.Add(new KeyMapIntegrity.SavedBinding(
                    int.Parse(p[0]), (Keys)int.Parse(p[1]), (Keys)int.Parse(p[2])));
            }
            Assert.Equal(114, list.Count);   // the snapshot's exact entry count
            return list;
        }

        /// <summary>
        /// (int)CommandValues → default key, parsed from the _defaultKeys
        /// array in JJFlexWpf/KeyCommands.cs. Source, not reflection, because
        /// this test project cannot load a WPF assembly — and the enum names
        /// resolve through the REAL CommandValues in Radios, so a renamed or
        /// renumbered command breaks this loudly rather than silently.
        /// </summary>
        private static Dictionary<int, Keys> CurrentDefaultsFromSource()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "JJFlexWpf", "KeyCommands.cs"));

            int start = src.IndexOf("KeyDefType[] _defaultKeys", StringComparison.Ordinal);
            Assert.True(start >= 0, "_defaultKeys not found in KeyCommands.cs");
            int end = src.IndexOf("};", start, StringComparison.Ordinal);
            Assert.True(end > start, "_defaultKeys region end not found");
            string region = src.Substring(start, end - start);

            var map = new Dictionary<int, Keys>();
            foreach (Match m in Regex.Matches(region,
                @"new\s*\(\s*((?:Keys\.\w+\s*\|?\s*)+),\s*CommandValues\.(\w+)\s*,\s*KeyScope\.\w+\s*\)"))
            {
                Keys key = Keys.None;
                foreach (Match token in Regex.Matches(m.Groups[1].Value, @"Keys\.(\w+)"))
                {
                    Assert.True(Enum.TryParse(token.Groups[1].Value, out Keys part),
                        "unparseable Keys token in _defaultKeys: " + token.Value);
                    key |= part;
                }

                Assert.True(Enum.TryParse(m.Groups[2].Value, out CommandValues cmd),
                    "unknown CommandValues name in _defaultKeys: " + m.Groups[2].Value
                    + " — the enum and the table have drifted apart");
                map[(int)cmd] = key;
            }
            return map;
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
