#nullable enable
using System;
using System.IO;
using System.Linq;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The #197 queue-depth rule as a HARNESS rule: run over the transcript a
    /// radiocheck pass just recorded, not only over a stored fixture.
    ///
    /// <para><b>Why the rule needed a harness home at all.</b> #197 said to
    /// build it as a harness rule rather than a one-off script, "so it runs on
    /// every radiocheck pass over the recorded transcript". The analyzer and
    /// its unit tests landed; nothing ever pointed them at a live run. A rule
    /// that only ever sees a fixture is a rule that can only find the bug it
    /// was written from — and the bug it was written from is one that recurs
    /// by construction, because every new announcement is a new chance to
    /// queue a Critical utterance behind two seconds of narration.</para>
    ///
    /// <para><b>How radiocheck drives it.</b> The smoke tier spawns the app
    /// with <c>--record</c> and produces <c>transcript.jsonl</c>; radiocheck
    /// then sets <see cref="TranscriptEnvVar"/> to that path and runs this one
    /// test. No new hosting, no second implementation of the rule in
    /// PowerShell — which would be the description-drift defect this codebase
    /// keeps paying to remove, planted in the instrument meant to catch
    /// it.</para>
    ///
    /// <para><b>The positive control runs FIRST, every time, in both
    /// modes.</b> This rule's healthy answer is "I found nothing", and "I
    /// looked and found nothing" also claims the tool would have SEEN it. So
    /// before the recorded run is judged, the rule is made to find something
    /// known to be there. Without that, a rule broken by a schema change would
    /// report a clean bill of health on every run forever, which is exactly
    /// the shape #197 exists to describe.</para>
    /// </summary>
    public class SpeechQueueDepthHarnessTests
    {
        /// <summary>
        /// Set by radiocheck to the transcript of the run under test. Absent in
        /// an ordinary <c>dotnet test</c>, where this test is the positive
        /// control alone.
        /// </summary>
        public const string TranscriptEnvVar = "JJFLEX_TRANSCRIPT_UNDER_TEST";

        /// <summary>
        /// The permanent regression fixture: a real bench session in which a
        /// correct, fully-recorded Critical warning was never heard, because
        /// key-down had queued three utterances two seconds earlier. The rule
        /// must flag its 84,038 ms event.
        /// </summary>
        private static string FixturePath =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures",
                         "transcript-20260822-203451-p32012.jsonl");

        [Fact]
        public void QueueDepthRule_OverTheTranscriptUnderTest()
        {
            // ── Positive control, unconditionally ──
            Assert.True(File.Exists(FixturePath),
                $"the #197 regression fixture is missing at {FixturePath}. Without it this "
                + "rule cannot be shown to detect anything, and a clean result means nothing.");

            var control = SpeechQueueDepthRule.AnalyzeFile(FixturePath);
            Assert.True(control.Count > 0,
                "the queue-depth rule found NOTHING in the known-bad fixture. The instrument "
                + "is broken, not the build under test: this file contains a Critical warning "
                + "emitted behind two seconds of pending speech, and a rule that cannot see it "
                + "cannot see any other. Every clean run this rule has ever reported is void "
                + "until this passes again.");

            string? path = Environment.GetEnvironmentVariable(TranscriptEnvVar);
            if (string.IsNullOrWhiteSpace(path))
            {
                // No run supplied. The control above IS the whole test, and it
                // asserted something real — this is deliberately not a skip,
                // because xUnit 2.x cannot report a runtime skip and a test
                // that returns without asserting is indistinguishable from a
                // test that passed.
                return;
            }

            // ── The run under test ──
            Assert.True(File.Exists(path),
                $"{TranscriptEnvVar} points at '{path}', which does not exist. A rule pointed "
                + "at nothing reports a clean run, so this is a broken harness rather than a "
                + "clean build.");

            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length > 0,
                $"the transcript at '{path}' is empty. By the transcript contract an empty "
                + "recording is a dead recorder, never 'the app correctly said nothing'.");

            var findings = SpeechQueueDepthRule.Analyze(lines);
            Assert.True(findings.Count == 0,
                $"{findings.Count} Critical utterance(s) in {Path.GetFileName(path)} were "
                + "emitted into more than "
                + $"{SpeechQueueDepthRule.ThresholdMs} ms of pending speech without an "
                + "interrupting intent, so a person would very likely not have heard them. "
                + "The fix is usually one of two things: shorten what precedes it, or give it "
                + "an interrupting intent (Interrupt, or Urgent where stale queued text must "
                + "die too). Note the rule keys on INTENT, not level — a Critical utterance "
                + "with no intent at all is the 2026-08-22 failure exactly."
                + Environment.NewLine
                + string.Join(Environment.NewLine, findings.Select(f => "  " + f)));
        }
    }
}
