using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 36 Track E, task #284: a leg that connects is not a leg that
    /// worked.
    ///
    /// <para><b>The loop this file exists to keep broken.</b> The connect walk
    /// used to write <c>"connected"</c> into the per-radio ring the moment
    /// <c>ReconnectRemote</c> returned true — which is up to a minute before
    /// anyone knows whether the radio opened. On 2026-08-26 four consecutive
    /// SmartLink attempts to a radio at 192.168.50.100 died in the
    /// station-name wait, and all four were written into
    /// <c>4925-1213-8600-6245\connect-history.json</c> as successes, with
    /// durations of 341, 1334, 350 and 913 ms — each one matching a
    /// <c>ReconnectRemote: END connected=True</c> line in the trace.</para>
    ///
    /// <para>Three successes in a row is a trend, so
    /// <see cref="ConnectPathPolicy"/> then recommended SmartLink for the next
    /// attempt, which failed the same way and reinforced it again. Every
    /// failure made the next failure more likely, and the store showed an
    /// unbroken run of success while the operator was reaching for
    /// Alt+F4.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ConnectOutcomeRecordingTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectOutcomeRecordingTests));

        public void Dispose()
        {
            ConnectionHistory.DiscardPendingOutcome();
            _scope.Dispose();
        }

        private const string Serial = "4925-1213-8600-6245";

        [Fact]
        public void ArmingRecordsNothingYet()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);

            Assert.True(ConnectionHistory.HasPendingOutcome);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void AnOpenThatSucceededRecordsASuccess()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 106);
            ConnectionHistory.CommitPendingOutcome(opened: true);

            var ring = ConnectionHistory.Load(Serial);
            var only = Assert.Single(ring);
            Assert.Equal(ConnectPathKind.Local.ToString(), only.Path);
            Assert.Equal(ConnectPathPolicy.ConnectedOutcome, only.Outcome);
            Assert.Equal(106, only.DurationMs);
            Assert.False(ConnectionHistory.HasPendingOutcome);
        }

        [Fact]
        public void AnOpenThatFailedIsNotRecordedAsASuccess()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.CommitPendingOutcome(opened: false);

            var only = Assert.Single(ConnectionHistory.Load(Serial));
            Assert.Equal(ConnectPathPolicy.OpenFailedOutcome, only.Outcome);
            Assert.NotEqual(ConnectPathPolicy.ConnectedOutcome, only.Outcome);

            // And the attempt is still THERE, with its duration — the ring is a
            // support tool as well as a policy input, and "how long did that
            // take before it fell over" is exactly what someone asks next.
            Assert.Equal(913, only.DurationMs);
            Assert.Equal(ConnectPathKind.SmartLink.ToString(), only.Path);
        }

        [Fact]
        public void FourFailedOpensDoNotTeachTheAppToPreferThatPath()
        {
            // The 2026-08-26 evening, replayed. Under the old behaviour this
            // ring held four "connected" entries and the policy came back
            // SmartLink, which is how the next attempt was steered onto the
            // path that had just failed four times running.
            foreach (var ms in new long[] { 341, 1334, 350, 913 })
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), ms);
                ConnectionHistory.CommitPendingOutcome(opened: false);
            }

            Assert.Equal(4, ConnectionHistory.Load(Serial).Count);
            Assert.Null(ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void AWalkThatFailsRemotelyAndThenOpensLocallyTeachesLocal()
        {
            // What the fixed walk actually writes: the SmartLink leg connected
            // and did not open, the Local leg connected and did open. The
            // second is the one that produced a working radio, and it is the
            // one the ring should be teaching from.
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
                ConnectionHistory.CommitPendingOutcome(opened: false);
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 80);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(ConnectPathKind.Local, ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void AnOpenFailureDoesNotBreakARunOfGenuineSuccesses()
        {
            // open_failed is not "connected", so it cannot teach. It is also
            // not trend-breaking, which keeps it consistent with how a failed
            // leg has always been treated — the rule that lets a genuinely
            // remote radio learn anything at all.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
            ConnectionHistory.CommitPendingOutcome(opened: false);
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void DiscardingLeavesNothingBehind()
        {
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.DiscardPendingOutcome();

            Assert.False(ConnectionHistory.HasPendingOutcome);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void CommittingTwiceRecordsOnce()
        {
            // openTheRadio commits, and a resumed walk commits per leg. The
            // second call must be a no-op rather than a duplicate.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(), 60);
            ConnectionHistory.CommitPendingOutcome(opened: true);
            ConnectionHistory.CommitPendingOutcome(opened: false);

            Assert.Single(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void CommittingWithNothingArmedRecordsNothing()
        {
            // openTheRadio commits unconditionally after Start(), and a local
            // connect that never got as far as a session has nothing armed.
            // (Auto-connect DOES arm now — task #286 — but it did not when this
            // was written.)
            ConnectionHistory.CommitPendingOutcome(opened: false);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        // ══════════════════════════════════════════════════════════════════
        // Task #287 — a force is not a preference
        // ══════════════════════════════════════════════════════════════════

        [Fact]
        public void AForcedAttemptIsStillRecorded()
        {
            // The ring is a support tool as well as a policy input, and "what
            // happened when I forced it" is the whole question a hole-punch test
            // is asking. Forced attempts are kept; they are just not taught from.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(),
                913, forced: true);
            ConnectionHistory.CommitPendingOutcome(opened: true);

            var only = Assert.Single(ConnectionHistory.Load(Serial));
            Assert.True(only.Forced);
            Assert.Equal(ConnectPathPolicy.ConnectedOutcome, only.Outcome);
            Assert.Equal(913, only.DurationMs);
        }

        [Fact]
        public void ForcingSmartLinkThreeTimesDoesNotTeachTheAppToPreferSmartLink()
        {
            // Noel's own workflow, and the reason this task exists: forcing
            // SmartLink from the context menu is how a hole punch gets tested
            // from inside the shack. Under the old behaviour those three
            // deliberate overrides were read as three preferences, and the next
            // ORDINARY connect went out to the internet for a radio one subnet
            // away. The diagnostic act reconfigured the thing being tested.
            for (int i = 0; i < 3; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(),
                    900, forced: true);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(3, ConnectionHistory.Load(Serial).Count);
            Assert.Null(ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void AForcedAttemptDoesNotBreakARunOfGenuineSuccesses()
        {
            // Skipped like a failure rather than treated as trend-breaking. A
            // genuinely remote operator who forces a path once must not lose the
            // habit the app legitimately learned from the connects around it.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
            ConnectionHistory.CommitPendingOutcome(opened: true);
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.Local.ToString(),
                80, forced: true);
            ConnectionHistory.CommitPendingOutcome(opened: true);
            for (int i = 0; i < 2; i++)
            {
                ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 900);
                ConnectionHistory.CommitPendingOutcome(opened: true);
            }

            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(Serial));
        }

        [Fact]
        public void HistoryWrittenBeforeTheFlagExistedStaysValid()
        {
            // Every file on disk today has no Forced property. It must
            // deserialise as false — the right default, since nothing older was
            // forced through a mechanism that recorded it — rather than making
            // the whole ring unreadable.
            var file = Path.Combine(RadioConfig.BaseDirectory, "radios",
                RadioConfig.SanitizeRadioId(Serial), "connect-history.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file,
                """
                [
                  { "TimestampUtc": "2026-08-20T14:02:11Z", "Path": "SmartLink", "Outcome": "connected", "DurationMs": 913 },
                  { "TimestampUtc": "2026-08-20T14:05:02Z", "Path": "SmartLink", "Outcome": "connected", "DurationMs": 880 },
                  { "TimestampUtc": "2026-08-20T14:09:44Z", "Path": "SmartLink", "Outcome": "connected", "DurationMs": 902 }
                ]
                """);

            var ring = ConnectionHistory.Load(Serial);
            Assert.Equal(3, ring.Count);
            Assert.All(ring, r => Assert.False(r.Forced));
            Assert.Equal(ConnectPathKind.SmartLink, ConnectPathPolicy.LearnForRadio(Serial));
        }

        // ══════════════════════════════════════════════════════════════════
        // Task #286 — no route may write a success before the radio opens
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>ConnectionHistory.Record</c> must never be handed a SUCCESS. The
        /// only thing entitled to write one is <c>CommitPendingOutcome</c>,
        /// which by construction runs after the open resolved.
        /// </summary>
        /// <remarks>
        /// <para>Sprint 36 removed this defect from the manual connect path.
        /// Task #286 then found it still standing in <c>TryAutoConnect</c>,
        /// which walks its own legs instead of going through the selector — so
        /// the fix landed on one route and the other kept writing false
        /// successes for another sprint.</para>
        /// <para>It cannot be checked by calling anything: both sites need a
        /// radio. So it is checked in SOURCE, which is also the only form of the
        /// check that would have caught the route that was missed.</para>
        /// </remarks>
        [Fact]
        public void NoCallerRecordsASuccessDirectly()
        {
            var offenders = new List<string>();
            foreach (var file in AuthoredSource())
            {
                string text = File.ReadAllText(file);
                foreach (var (call, offset) in RecordCalls(text))
                {
                    if (!WritesASuccess(call)) continue;
                    int line = text.Take(offset).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line}: {Condense(call)}");
                }
            }

            Assert.True(offenders.Count == 0,
                "a connect route is recording a success without waiting for the open "
                + "(tasks #284, #286) — arm it and let the open commit it:"
                + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }

        /// <summary>
        /// The scanner above, pointed at the defect as it actually stood in
        /// <c>TryAutoConnect</c> until this task.
        /// </summary>
        /// <remarks>
        /// A clean result from a scanner that has never found anything is not
        /// evidence. This is the positive control: the instrument must see the
        /// real specimen before its silence means anything.
        /// </remarks>
        [Fact]
        public void TheSourceCheckFindsTheDefectAsItActuallyStood()
        {
            const string specimen = """
                                    ConnectionHistory.Record(config.RadioSerial, path.ToString(),
                                        connected ? "connected" : (LastConnectFailureReport?.Class.ToString() ?? "failed"),
                                        legSw.ElapsedMilliseconds);
                                    """;

            var calls = RecordCalls(specimen).ToList();
            Assert.Single(calls);
            Assert.True(WritesASuccess(calls[0].call),
                "the source check did not recognise the #286 defect verbatim, so a clean "
                + "result from it would mean nothing");

            // And the shape the fix replaced it with is NOT flagged, or the
            // check would simply be forbidding the file from mentioning the word.
            const string fixedShape = """
                                      ConnectionHistory.Record(config.RadioSerial, path.ToString(),
                                          LastConnectFailureReport?.Class.ToString() ?? "failed",
                                          legSw.ElapsedMilliseconds);
                                      """;
            Assert.False(WritesASuccess(RecordCalls(fixedShape).Single().call));
        }

        /// <summary>Every <c>ConnectionHistory.Record(</c> call's argument text,
        /// paren-balanced so a call spanning lines arrives whole.</summary>
        private static IEnumerable<(string call, int offset)> RecordCalls(string text)
        {
            const string marker = "ConnectionHistory.Record(";
            int i = 0;
            while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
            {
                int open = i + marker.Length - 1;
                int depth = 0;
                int j = open;
                for (; j < text.Length; j++)
                {
                    if (text[j] == '(') depth++;
                    else if (text[j] == ')')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }
                yield return (text.Substring(open, Math.Min(j, text.Length - 1) - open + 1), i);
                i += marker.Length;
            }
        }

        private static bool WritesASuccess(string call) =>
            call.Contains("\"" + ConnectPathPolicy.ConnectedOutcome + "\"", StringComparison.Ordinal)
            || call.Contains("ConnectedOutcome", StringComparison.Ordinal);

        private static string Condense(string call) =>
            string.Join(" ", call.Split('\n').Select(l => l.Trim()));

        private static IEnumerable<string> AuthoredSource()
        {
            string root = RepoRoot();
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".vb", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                // Test projects seed rings with successes on purpose — that is
                // how a trend gets set up to be asserted about. The rule is
                // about CONNECT ROUTES: shipping code that decides an attempt
                // succeeded. Applying it to fixtures would only teach people to
                // write their seeds a different way.
                if (file.Contains(".Tests" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
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

        [Fact]
        public void ALegLostBetweenArmAndCommitTeachesNothingRatherThanTheWrongThing()
        {
            // A crash, or a session that never resolved. Losing the record is
            // the right way to lose it: a missing attempt teaches the policy
            // nothing, where a false success teaches it the opposite of the
            // truth.
            ConnectionHistory.ArmPendingOutcome(Serial, ConnectPathKind.SmartLink.ToString(), 913);
            ConnectionHistory.DiscardPendingOutcome();

            Assert.Null(ConnectPathPolicy.LearnForRadio(Serial));
            Assert.Empty(ConnectionHistory.Load(Serial).Where(
                r => r.Outcome == ConnectPathPolicy.ConnectedOutcome));
        }
    }
}
