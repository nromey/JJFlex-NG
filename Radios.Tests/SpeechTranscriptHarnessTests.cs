#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The speech transcript harness, tested by driving the harness the
    /// operator actually runs.
    ///
    /// <para><b>Why these tests spawn a script instead of calling a class.</b>
    /// The instrument has to work at the bench, live, with NVDA running and no
    /// build in the loop - that is the whole point of seal step 3e, and a
    /// <c>dotnet test</c> between a keypress and its transcript would kill the
    /// loop the instrument exists to make cheap. So the implementation is
    /// PowerShell. Reimplementing the rules in C# so they could be unit tested
    /// would give two implementations of one rule, which
    /// <see cref="SpeechQueueDepthHarnessTests"/> names outright as "the
    /// description-drift defect this codebase keeps paying to remove, planted
    /// in the instrument meant to catch it". Driving the real script keeps one
    /// implementation and tests the artifact rather than a twin of it.</para>
    ///
    /// <para><b>The controls run in both directions.</b> Two known-bad fixtures
    /// prove the rules find what they were built to find; one known-good
    /// fixture proves they are not simply firing on everything. Either alone is
    /// worth very little - a rule that flags every input passes both known-bad
    /// tests, and a rule that flags nothing passes the known-good one.</para>
    ///
    /// <para><b>Every fixture is hand-authored.</b> An NVDA log records
    /// everything NVDA spoke in every application and echoes typed words, so it
    /// is personal data by construction, and this repository is public. See
    /// <c>tools/speech-transcript/fixtures/README.md</c>.</para>
    /// </summary>
    public class SpeechTranscriptHarnessTests
    {
        // ---------------------------------------------------------------
        // Locating things
        // ---------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("could not find the repository root from " + AppContext.BaseDirectory);
        }

        private static string HarnessScript => Path.Combine(RepoRoot(), "speech-scenario.ps1");

        private static string FixtureDir =>
            Path.Combine(RepoRoot(), "tools", "speech-transcript", "fixtures");

        private static string Fixture(string name) => Path.Combine(FixtureDir, name);

        /// <summary>
        /// Windows PowerShell 5.1 lives at a fixed absolute path on every
        /// Windows install, so it is the deterministic choice. PowerShell 7 is
        /// preferred when it is installed in the standard location; the module
        /// is written to run under both, and is verified under 5.1.
        /// </summary>
        private static string ResolveShell()
        {
            string pwsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwsh)) return pwsh;

            string ps51 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(ps51)) return ps51;

            throw new InvalidOperationException(
                "no PowerShell host found. This is a broken test environment, not a broken "
                + "harness - do not read the resulting failures as defects in the instrument.");
        }

        // ---------------------------------------------------------------
        // Driving the harness
        // ---------------------------------------------------------------

        private sealed class HarnessRun
        {
            public int ExitCode;
            public string StdOut = "";
            public string StdErr = "";
            public JsonElement Root;
        }

        private static HarnessRun Run(
            string fixtureFile,
            string scenario,
            string? baselinePath = null,
            string? stationAlias = null)
        {
            Assert.True(File.Exists(HarnessScript),
                $"the harness is missing at {HarnessScript}. Every result below would be "
                + "vacuous, so this is checked first.");
            Assert.True(File.Exists(Fixture(fixtureFile)),
                $"the fixture {fixtureFile} is missing from {FixtureDir}. A rule with nothing "
                + "to read reports a clean run, which is the failure this whole class exists "
                + "to make impossible.");

            var psi = new ProcessStartInfo
            {
                FileName = ResolveShell(),
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(HarnessScript);
            psi.ArgumentList.Add("-Scenario");
            psi.ArgumentList.Add(scenario);
            psi.ArgumentList.Add("-FromLog");
            psi.ArgumentList.Add(Fixture(fixtureFile));
            psi.ArgumentList.Add("-Json");
            if (baselinePath != null)
            {
                psi.ArgumentList.Add("-BaselinePath");
                psi.ArgumentList.Add(baselinePath);
            }
            if (stationAlias != null)
            {
                psi.ArgumentList.Add("-StationAlias");
                psi.ArgumentList.Add(stationAlias);
            }

            var run = new HarnessRun();
            using (var p = Process.Start(psi)!)
            {
                run.StdOut = p.StandardOutput.ReadToEnd();
                run.StdErr = p.StandardError.ReadToEnd();
                bool done = p.WaitForExit(120_000);
                Assert.True(done,
                    "the harness did not finish inside two minutes. It has hung before - a "
                    + "malformed comparison table spun the order walk and produced 21 MB of "
                    + "errors instead of returning - so a hang is a defect, not slowness.");
                run.ExitCode = p.ExitCode;
            }

            Assert.False(string.IsNullOrWhiteSpace(run.StdOut),
                "the harness produced no output at all."
                + Environment.NewLine + "stderr: " + run.StdErr);

            try
            {
                run.Root = JsonDocument.Parse(run.StdOut).RootElement.Clone();
            }
            catch (JsonException ex)
            {
                Assert.Fail("the harness did not emit valid JSON: " + ex.Message
                    + Environment.NewLine + "stdout: " + run.StdOut
                    + Environment.NewLine + "stderr: " + run.StdErr);
            }
            return run;
        }

        /// <summary>
        /// The prose report - what the operator reads at the bench. A separate
        /// code path from the JSON, so anything that must hold on both is
        /// checked on both.
        /// </summary>
        private static string RunProse(string fixtureFile, string scenario)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveShell(),
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(HarnessScript);
            psi.ArgumentList.Add("-Scenario");
            psi.ArgumentList.Add(scenario);
            psi.ArgumentList.Add("-FromLog");
            psi.ArgumentList.Add(Fixture(fixtureFile));

            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
            return output;
        }

        /// <summary>
        /// Windows PowerShell 5.1 serialises a one-element array as the element
        /// itself, so a result that happens to have exactly one entry would
        /// otherwise read as "no entries" - an absence that is not evidence.
        /// </summary>
        private static List<JsonElement> AsList(JsonElement e)
        {
            var list = new List<JsonElement>();
            if (e.ValueKind == JsonValueKind.Array) list.AddRange(e.EnumerateArray());
            else if (e.ValueKind == JsonValueKind.Object || e.ValueKind == JsonValueKind.String) list.Add(e);
            return list;
        }

        private static List<JsonElement> Flags(HarnessRun run, string flag) =>
            AsList(run.Root.GetProperty("Artifact").GetProperty("Flags"))
                .Where(f => f.GetProperty("Flag").GetString() == flag)
                .ToList();

        private static int FlagCount(HarnessRun run, string flag) =>
            run.Root.GetProperty("Artifact").GetProperty("FlagCounts").GetProperty(flag).GetInt32();

        private static List<string> Utterances(HarnessRun run) =>
            AsList(run.Root.GetProperty("Artifact").GetProperty("Utterances"))
                .Select(u => u.GetProperty("Text").GetString() ?? "")
                .ToList();

        // ---------------------------------------------------------------
        // The positive control for the whole instrument
        // ---------------------------------------------------------------

        /// <summary>
        /// #554's own capture, at its own measured clock. If this fails the
        /// instrument is broken and every clean result it has ever produced is
        /// void.
        ///
        /// <para><b>This test is why the salvage rule was rewritten.</b> The
        /// rule that shipped in <c>read-speech-capture.ps1</c> looked for
        /// identical text 400 to 1200 ms apart, measured utterance to
        /// utterance. Against #554's own numbers those gaps are 4,083 ms and
        /// 1,284 ms - one far too wide, the other just outside the window - so
        /// the rule found NOTHING in the capture it was written from. The
        /// 611 ms and 606 ms in the register are measured from the INTERRUPT,
        /// which is the salvage mechanism itself.</para>
        /// </summary>
        [Fact]
        public void PositiveControl_TheHarnessFindsTheSalvageLoopIn554sOwnCapture()
        {
            var run = Run("known-bad-554-salvage-loop.nvdalog", "connect");

            var salvage = Flags(run, "SALVAGE");
            Assert.True(salvage.Count == 2,
                $"the salvage rule found {salvage.Count} rescues in #554's own capture, and "
                + "there are two. This file carries the measured clock from the register: the "
                + "same block re-spoken 611 ms and 606 ms after an interrupt, against #503's "
                + "600 ms settle window. A harness that cannot flag the defect it was built "
                + "from is not an instrument."
                + Environment.NewLine + run.StdOut);

            var gaps = salvage.Select(f => f.GetProperty("GapMs").GetInt32()).OrderBy(x => x).ToList();
            Assert.Equal(new[] { 606, 611 }, gaps);

            Assert.True(FlagCount(run, "BURST") == 3,
                "the four connect lines are handed over inside 4 ms, which is three gaps below "
                + "the burst threshold. That block takes about 13 seconds to speak, so the "
                + "operator hears the first fragment and nothing else - emission is not "
                + "delivery, and this is that gap measured."
                + Environment.NewLine + run.StdOut);
        }

        /// <summary>
        /// The other direction. Without this, a rule that flagged every input
        /// would pass both known-bad tests and look like a working instrument.
        /// </summary>
        [Fact]
        public void NegativeControl_AWellBehavedConnectRaisesNothing()
        {
            var run = Run("known-good-connect.nvdalog", "connect");

            foreach (string flag in new[] { "BURST", "ECHO", "SALVAGE", "REPEAT", "CONTRADICTION" })
            {
                Assert.True(FlagCount(run, flag) == 0,
                    $"the {flag} rule fired on a connect that is spaced out, says each thing "
                    + "once, and never contradicts itself. A rule that flags a healthy run "
                    + "makes every flag it raises worthless."
                    + Environment.NewLine + run.StdOut);
            }

            Assert.Equal(6, Utterances(run).Count);
        }

        /// <summary>
        /// #521's signature: a connection claimed between two disconnect
        /// announcements, and the picker inviting a connection that has already
        /// begun. Every sentence involved is one the app is allowed to say, so
        /// a diff of words alone cannot see this.
        /// </summary>
        [Fact]
        public void TheContradictionRuleFinds521sSignature()
        {
            var run = Run("known-bad-521-contradiction.nvdalog",
                          "close-relaunch-connect-close", stationAlias: "benchradio");

            var found = Flags(run, "CONTRADICTION");
            Assert.True(found.Count == 2,
                $"the contradiction rule found {found.Count} of #521's two. One is 'Connected "
                + "to FLEX-8600' spoken straight after 'disconnected from radio' with no "
                + "connect attempt between; the other is 'Press Enter to connect' spoken to an "
                + "operator who already did."
                + Environment.NewLine + run.StdOut);

            var notes = found.Select(f => f.GetProperty("Note").GetString() ?? "").ToList();
            Assert.Contains(notes, n => n.Contains("disconnected from radio", StringComparison.Ordinal));
            Assert.Contains(notes, n => n.Contains("already begun", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------
        // The parser
        // ---------------------------------------------------------------

        /// <summary>
        /// The shapes NVDA actually writes, measured on 2026-09-06 against a
        /// live 24,801-line log. Each of these was got wrong by the regex that
        /// shipped before, and each failure is silent: an utterance that is
        /// never extracted looks exactly like an utterance that was never
        /// spoken.
        /// </summary>
        [Fact]
        public void TheParserExtractsTheShapesNvdaActuallyWrites()
        {
            var run = Run("parser-shapes.nvdalog", "connect");
            var said = Utterances(run);

            Assert.True(said.Count == 5,
                $"expected five utterances, got {said.Count}."
                + Environment.NewLine + string.Join(Environment.NewLine, said));

            // Python repr switches to DOUBLE quotes when the string contains an
            // apostrophe. 337 of 1,545 Speaking lines in the measured log do
            // this - 22 percent - and a single-quote-only regex loses all of
            // them.
            Assert.Equal("That slice isn't yours.", said[0]);

            // speakSsml splits one utterance into word fragments interleaved
            // with mark callbacks. One utterance is what a person hears.
            Assert.Equal("Connected to the radio", said[1]);

            // LangChangeCommand carries a quoted locale. Stripping the command
            // structurally beats filtering the locales we have happened to see:
            // this fixture uses en_GB precisely because the old filter only
            // knew en_US and en.
            Assert.Equal("Slice <s> selected", said[2]);

            // PitchCommand, CharacterModeCommand and a bare EndUtteranceCommand.
            Assert.Equal("A", said[3]);

            // Frequency and build version normalised; and the clock crosses
            // midnight between this line and the one before it, which must not
            // produce a negative gap.
            Assert.Equal("Tuned to <freq> on slice <s>, version <version>", said[4]);

            var utterances = AsList(run.Root.GetProperty("Artifact").GetProperty("Utterances"));
            int lastRelative = utterances.Last().GetProperty("RelativeMs").GetInt32();
            Assert.True(lastRelative == 11600,
                $"the last utterance is {lastRelative} ms after the first, and should be 11,600. "
                + "NVDA's log carries a clock but no date, so a session crossing midnight goes "
                + "backwards and every timing rule turns itself off without saying so.");
        }

        /// <summary>
        /// The operator's own keystrokes must never reach an artifact. NVDA
        /// logs them - 185 <c>speakTypedCharacters</c> lines in the measured
        /// log, each carrying the word that was typed - and this repository is
        /// public.
        /// </summary>
        [Fact]
        public void TypedCharactersAreCountedAndNeverRecorded()
        {
            var run = Run("parser-shapes.nvdalog", "connect");

            Assert.Equal(1, run.Root.GetProperty("Artifact").GetProperty("TypedCount").GetInt32());

            Assert.False(run.StdOut.Contains("notasecret", StringComparison.OrdinalIgnoreCase),
                "the harness emitted a typed word into its machine-readable output. The "
                + "fixture types 'notasecret' and no artifact may ever contain it - what the "
                + "operator types goes through this log too, and an artifact is something we "
                + "are willing to commit.");

            // And the same again through the prose report, which is what the
            // operator actually reads at the bench and is a different code path
            // from the JSON. The redaction has to hold on both.
            string prose = RunProse("parser-shapes.nvdalog", "connect");
            Assert.False(prose.Contains("notasecret", StringComparison.OrdinalIgnoreCase),
                "the timeline printed a typed word." + Environment.NewLine + prose);
            Assert.Contains("not recorded", prose, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------
        // The diff
        // ---------------------------------------------------------------

        /// <summary>
        /// The diff has to answer three separate questions, and order is one of
        /// them: #521 is entirely about things arriving in the wrong sequence,
        /// and a diff that reports a move as one removal plus one addition
        /// buries exactly that.
        /// </summary>
        [Fact]
        public void TheDiffSeparatesAdditionsRemovalsAndOrder()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "jjflex-speech-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string baseline = Path.Combine(temp, "connect.baseline.txt");

            try
            {
                // The known-good run says, in order:
                //   Connecting / Connected...slice / Connected...slices / mic / PC audio / Home
                // This baseline drops "Connected ... Waiting for slice", swaps
                // the mic and PC-audio lines, and adds a line the run does not
                // say. One of each, so each result can be told apart.
                File.WriteAllLines(baseline, new[]
                {
                    "# flags: burst 0, echo 0, salvage 0, repeat 0, contradiction 0",
                    "  1  Connecting to <station>. Trying SmartLink.",
                    "  2  Connected to <radio>, SmartLink, <n> slices.",
                    "  3  PC audio on.",
                    "  4  This radio had no mic profile, so I loaded Default.",
                    "  5  JJ Flexible Home, Modern tuning mode",
                    "  6  Antenna A selected.",
                });

                var run = Run("known-good-connect.nvdalog", "connect", baselinePath: baseline);

                Assert.True(run.ExitCode == 2,
                    $"a run that differs from its baseline must exit 2, not {run.ExitCode}.");

                var diff = run.Root.GetProperty("Diff");

                var added = AsList(diff.GetProperty("Added"))
                    .Select(x => x.GetProperty("Text").GetString()).ToList();
                Assert.Equal(new[] { "Connected to <station>. Waiting for slice..." }, added);

                var removed = AsList(diff.GetProperty("Removed"))
                    .Select(x => x.GetProperty("Text").GetString()).ToList();
                Assert.Equal(new[] { "Antenna A selected." }, removed);

                var moved = AsList(diff.GetProperty("Moved"))
                    .Select(x => x.GetProperty("Text").GetString()).ToList();
                Assert.True(moved.Count == 1 && moved[0] == "PC audio on.",
                    "a swap of two adjacent lines is ONE move. Reporting it as a removal and "
                    + "an addition, or cascading it into 'and everything after it also moved', "
                    + "makes an ordering defect unreadable - and ordering is the whole of #521."
                    + Environment.NewLine + string.Join(", ", moved));
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Flags are compared as well as words, because a run whose words are
        /// right but which now bursts, rescues or contradicts itself is a
        /// regression that no word diff can see.
        /// </summary>
        [Fact]
        public void ANewFlagIsARegressionEvenWhenTheWordsMatch()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "jjflex-speech-flagdiff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string baseline = Path.Combine(temp, "connect.baseline.txt");

            try
            {
                // Exactly the words #554's capture produces, in order, with a
                // clean flag record. Only the timing differs - which is the
                // entire defect.
                File.WriteAllLines(baseline, new[]
                {
                    "# flags: burst 0, echo 0, salvage 0, repeat 0, contradiction 0",
                    "  1  Connected to <radio>, SmartLink, <n> slices.",
                    "  2  This radio had no mic profile, so I loaded Default.",
                    "  3  PC audio on.",
                    "  4  JJ Flexible Home, Modern tuning mode",
                    "  5  Connected to <radio>, SmartLink, <n> slices.",
                    "  6  Connected to <radio>, SmartLink, <n> slices.",
                });

                var run = Run("known-bad-554-salvage-loop.nvdalog", "connect", baselinePath: baseline);
                var diff = run.Root.GetProperty("Diff");

                Assert.Empty(AsList(diff.GetProperty("Added")));
                Assert.Empty(AsList(diff.GetProperty("Removed")));
                Assert.Empty(AsList(diff.GetProperty("Moved")));

                var worse = AsList(diff.GetProperty("FlagRegression"))
                    .Select(x => x.GetProperty("Flag").GetString()).ToList();
                Assert.Contains("SALVAGE", worse);
                Assert.Contains("BURST", worse);

                Assert.False(diff.GetProperty("Matches").GetBoolean(),
                    "every word matched the baseline and the run still rescued the same block "
                    + "twice inside the settle window. If that reports as a match, the flags "
                    + "are decoration.");
                Assert.Equal(2, run.ExitCode);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// No baseline means UNKNOWN. It must never read as a pass - that is
        /// how an instrument reports a clean bill of health forever.
        /// </summary>
        [Fact]
        public void AMissingBaselineIsUnknownAndNeverAPass()
        {
            string missing = Path.Combine(Path.GetTempPath(),
                "jjflex-no-such-baseline-" + Guid.NewGuid().ToString("N") + ".txt");

            var run = Run("known-good-connect.nvdalog", "connect", baselinePath: missing);

            Assert.False(run.Root.GetProperty("HasBaseline").GetBoolean());
            Assert.True(run.ExitCode == 3,
                $"a scenario with no recorded baseline must exit 3 (unknown), not {run.ExitCode}. "
                + "Zero would say the run was verified against something, and nothing was.");
        }

        // ---------------------------------------------------------------
        // Guards on the fixtures themselves
        // ---------------------------------------------------------------

        /// <summary>
        /// The fixture set is an allow-list on purpose. A real capture dropped
        /// into this directory would work perfectly and leak everything NVDA
        /// spoke in every application into a public repository, and nothing
        /// else in the build would object.
        ///
        /// <para><b>Why the fixtures are .nvdalog and not .log.</b>
        /// <c>.gitignore</c> line 108 ignores <c>*.log</c>, which is a real
        /// safety net here: a genuine <c>nvda.log</c> dropped into the tree
        /// cannot be committed by accident. Committing these four as
        /// <c>.log</c> would have needed <c>git add -f</c>, and that produces
        /// a file that is TRACKED and IGNORED at once - exactly the
        /// contradiction #556 documents, where ignore rules stop applying to an
        /// already-tracked file and it drifts with nothing complaining. The
        /// extension keeps the safety net intact and needs no override.</para>
        ///
        /// <para>The scan below covers BOTH extensions anyway, so a real
        /// capture dropped in here fails this test even though git would
        /// already have ignored it.</para>
        /// </summary>
        [Fact]
        public void OnlyHandAuthoredFixturesLiveInTheRepository()
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "known-bad-554-salvage-loop.nvdalog",
                "known-bad-521-contradiction.nvdalog",
                "known-good-connect.nvdalog",
                "parser-shapes.nvdalog",
            };

            var present = Directory.GetFiles(FixtureDir, "*.nvdalog")
                                   .Concat(Directory.GetFiles(FixtureDir, "*.log"))
                                   .Select(Path.GetFileName)
                                   .Cast<string>()
                                   .ToList();

            var unexpected = present.Where(f => !allowed.Contains(f)).ToList();
            Assert.True(unexpected.Count == 0,
                "an undeclared transcript is sitting in " + FixtureDir + ": "
                + string.Join(", ", unexpected) + ". Every fixture here must be hand-authored "
                + "and read line by line. A captured NVDA log holds window titles, mail and "
                + "typed words from every application that was open, and this repository is "
                + "public. Live captures belong in JJFlex-private or on the NAS.");

            foreach (string f in present)
            {
                var info = new FileInfo(Path.Combine(FixtureDir, f));
                Assert.True(info.Length < 32 * 1024,
                    $"{f} is {info.Length} bytes. A hand-authored fixture is a few kilobytes; a "
                    + "real capture is megabytes. This size is the shape of a pasted-in log.");
            }
        }

        /// <summary>
        /// The scenarios named in the sprint brief must all still exist. A
        /// scenario that quietly disappears takes its baseline out of the merge
        /// verification with it, and nothing fails.
        /// </summary>
        [Fact]
        public void TheNamedScenariosAllExist()
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveShell(),
                WorkingDirectory = RepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(HarnessScript);
            psi.ArgumentList.Add("-List");

            string output;
            using (var p = Process.Start(psi)!)
            {
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(60_000);
            }

            foreach (string name in new[]
            {
                "startup-to-picker", "connect", "disconnect",
                "close-relaunch-connect-close", "open-audio-layer-and-escape",
            })
            {
                Assert.Contains(name, output, StringComparison.Ordinal);
            }
        }
    }
}
