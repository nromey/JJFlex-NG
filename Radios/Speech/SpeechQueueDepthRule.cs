#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Radios.Speech
{
    /// <summary>One Critical utterance the rule believes a human never heard in time.</summary>
    public sealed class SpeechQueueDepthFinding
    {
        /// <summary>When it was emitted (transcript monotonic clock).</summary>
        public double MonotonicMs { get; init; }

        /// <summary>The exact final text that queued.</summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>Estimated pending speech, in ms, ahead of it at emission.</summary>
        public double BacklogMs { get; init; }

        /// <summary>Call site, from the transcript's origin field.</summary>
        public string? Origin { get; init; }

        public override string ToString() =>
            $"{MonotonicMs:F0} ms: Critical '{Text}' queued behind ~{BacklogMs:F0} ms "
            + $"of pending speech without an interrupting intent ({Origin ?? "origin unknown"})";
    }

    /// <summary>
    /// The #197 queue-depth rule: **the transcript proves an utterance was
    /// EMITTED, not that it was HEARD.**
    ///
    /// Found at the bench on 2026-08-22. The reflected-power warning fired
    /// correctly into an open antenna port and the transcript recorded it
    /// perfectly — rendered, not gated, not suppressed, Critical, correct
    /// text. Every automated check said the feature worked. The operator
    /// missed it entirely and had to key a second time, because key-down two
    /// seconds earlier had queued three utterances and the warning took its
    /// place at the back of that queue. A warning emitted into a full queue is
    /// recorded identically to one emitted into silence.
    ///
    /// This rule closes that gap from data the transcript already carries:
    /// it replays the believed state of the reader's queue and fails any
    /// Critical utterance that lands behind more than
    /// <see cref="ThresholdMs"/> of pending speech WITHOUT carrying an
    /// interrupting intent.
    ///
    /// **Its speaking-rate estimate is deliberately NOT the salvage ledger's**
    /// (<see cref="SpeechArbiter.SalvageMsPerCharacter"/>). The ledger errs
    /// long because its failure costs are asymmetric: under-protection
    /// silently destroys speech, over-protection at worst repeats it. This
    /// rule's costs run the other way — an over-long estimate flags the
    /// routine keydown pair ("Transmitting, locked" followed by its queued
    /// reminder) on every single transmit, and a rule that always fails is a
    /// rule everyone learns to ignore. So the rule uses the realistic rate
    /// the app already waits out speech with (50 ms/char), and each side's
    /// behaviour is pinned by its own tests.
    ///
    /// **The rule keys on INTENT, not on level.** The 2026-08-22 warning was
    /// already Critical — level was never the problem. It queued because it
    /// carried no intent at all. A check that only looked at verbosity level
    /// would have passed it. The fix for a finding is usually one of two
    /// things: shorten what precedes it, or promote it to an interrupting
    /// intent (Interrupt, or Urgent where stale queue text must die too).
    ///
    /// Runs over a recorded transcript: no radio, no desk, no audio device,
    /// no ears — unit tier, though it is fundamentally about what a person
    /// hears. Positive control: transcript-20260822-203451-p32012.jsonl, a
    /// permanent fixture in Radios.Tests, in which this rule MUST flag the
    /// 84,038 ms warning; a pass on that file means the instrument is broken.
    /// </summary>
    public static class SpeechQueueDepthRule
    {
        /// <summary>
        /// How much believed backlog a Critical utterance may queue behind
        /// before it is presumed unheard. "Roughly a second" — specifically
        /// the codebase's existing one-utterance figure — <see
        /// cref="SpeechArbiter.GapCeilingMs"/>, which was the flat MinGapMs
        /// until 2026-08-27 — measured
        /// from a real trace on 2026-08-18: a typical value announcement runs
        /// about 1.2 s. Queuing behind the single utterance currently being
        /// spoken is normal sentence flow and must pass; queuing behind more
        /// than one utterance's worth is a queue, and a Critical in a queue is
        /// the 2026-08-22 failure.
        /// </summary>
        public const int ThresholdMs = 1200;

        /// <summary>
        /// Realistic per-character speaking estimate — the figure SpeakAndWait
        /// already uses to wait out speech. See the class doc for why this is
        /// deliberately NOT the salvage ledger's generous 80.
        /// </summary>
        public const int EstimateMsPerCharacter = 50;

        /// <summary>Floor per utterance for the estimate.</summary>
        public const int EstimateMinMs = 300;

        /// <summary>Analyze a transcript file. See <see cref="Analyze"/>.</summary>
        public static IReadOnlyList<SpeechQueueDepthFinding> AnalyzeFile(string path) =>
            Analyze(File.ReadLines(path));

        /// <summary>
        /// Replay the transcript's speech events against a model of the
        /// reader's queue and return every Critical utterance believed to have
        /// been emitted into more than <see cref="ThresholdMs"/> of backlog
        /// without an interrupting intent. Empty list = rule passes.
        /// </summary>
        /// <param name="jsonLines">
        /// Transcript lines (JSONL, one event per line — the
        /// OutputChannelRecorder format). Unparseable lines are skipped: the
        /// transcript is append-only and flushed per line, so a torn final
        /// line after a crash is normal, not analysis-fatal.
        /// </param>
        public static IReadOnlyList<SpeechQueueDepthFinding> Analyze(IEnumerable<string> jsonLines)
        {
            var findings = new List<SpeechQueueDepthFinding>();

            // When the reader is believed to fall silent, on the transcript's
            // monotonic clock, counting only recorded traffic. The same
            // honesty caveat as the live ledger: the reader also speaks focus
            // announcements we never see, so real backlogs run LONGER than
            // modeled — a finding here is a floor, not an exaggeration.
            double busyUntilMs = 0;

            foreach (var line in jsonLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var evProp)) continue;
                    string? ev = evProp.GetString();

                    if (!root.TryGetProperty("monotonicMs", out var tProp)
                        || !tProp.TryGetDouble(out double t))
                    {
                        continue;
                    }

                    switch (ev)
                    {
                        case "session-start":
                            // A fresh session starts with a quiet reader.
                            busyUntilMs = 0;
                            continue;

                        case "silence":
                            // An explicit cut: the reader's queue is gone.
                            busyUntilMs = t;
                            continue;

                        case "speech":
                            break;

                        default:
                            // Earcons and CW play on our own mixer, braille is
                            // not speech, markers are not audio. None of them
                            // occupy the reader.
                            continue;
                    }

                    // Gated and suppressed utterances never reached the
                    // reader; they occupy nothing and can be behind nothing.
                    // (rendered is deliberately NOT required: radiocheck's
                    // smoke tier records with render off, and this rule must
                    // hold there exactly as it does at the bench.)
                    if (GetBool(root, "gated") || GetBool(root, "suppressed")) continue;

                    string text = root.TryGetProperty("text", out var textProp)
                        ? textProp.GetString() ?? string.Empty
                        : string.Empty;
                    int durMs = Math.Max(EstimateMinMs, text.Length * EstimateMsPerCharacter);

                    if (GetBool(root, "interrupt"))
                    {
                        // It cut ahead; everything believed pending is gone.
                        busyUntilMs = t + durMs;
                        continue;
                    }

                    double backlogMs = Math.Max(0, busyUntilMs - t);

                    string? level = root.TryGetProperty("level", out var levelProp)
                        && levelProp.ValueKind == JsonValueKind.String
                        ? levelProp.GetString()
                        : null;

                    if (backlogMs > ThresholdMs
                        && string.Equals(level, nameof(VerbosityLevel.Critical), StringComparison.Ordinal))
                    {
                        findings.Add(new SpeechQueueDepthFinding
                        {
                            MonotonicMs = t,
                            Text = text,
                            BacklogMs = backlogMs,
                            Origin = root.TryGetProperty("origin", out var originProp)
                                && originProp.ValueKind == JsonValueKind.String
                                ? originProp.GetString()
                                : null,
                        });
                    }

                    double startMs = Math.Max(t, busyUntilMs);
                    busyUntilMs = startMs + durMs;
                }
            }

            return findings;
        }

        private static bool GetBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    }
}
