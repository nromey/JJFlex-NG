#nullable enable

using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// What the system-wide push to talk watchdog says, verdict by verdict
    /// (ruled by Noel 2026-10-07; built 2026-11-08). Both speaking arms shared
    /// one sentence, "the release ... was not seen ... You are no longer on the
    /// air", which was false on both: a release that was not honoured WAS seen,
    /// and a key held too long is still down.
    /// </summary>
    /// <remarks>
    /// <c>SystemWideKeys.WatchTick</c> runs on a timer thread against the real
    /// keyboard and the PTT controller, so the arms are read from source; the
    /// verdicts themselves are driven in <c>SystemWidePttWatchTests</c>.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    // LEXICON_SCANNER_EXEMPT — the call sites below are quoted as text to
    // find in the source, not lookups.
    public sealed class SystemWideWatchdogSpeechTests
    {
        private static string Arm(string verdict)
        {
            string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "SystemWideKeys.cs"));
            string label = "case SystemWidePttWatch.Verdict." + verdict + ":";
            int at = src.IndexOf(label, StringComparison.Ordinal);
            Assert.True(at >= 0, "no arm for " + verdict);
            int end = src.IndexOf("break;", at, StringComparison.Ordinal);
            Assert.True(end > at);
            return src.Substring(at, end - at);
        }

        [Fact]
        public void A_release_not_honoured_says_Noels_sentence_verbatim()
        {
            Assert.Contains("Lexicon.Get(\"audio.ptt.systemwide_release_not_honoured\")", Arm("KillReleaseNotHonoured"));
            Assert.Equal("Heads up: you tried to unkey using the keystroke to unkey from anywhere, but the transmitter "
                         + "continued to send longer than expected.",
                Lexicon.Get("audio.ptt.systemwide_release_not_honoured"));
        }

        [Fact]
        public void A_key_held_too_long_still_drops_the_carrier_and_says_the_limit_was_reached()
        {
            string arm = Arm("KillHoldTooLong");
            Assert.Contains("KillTransmitNow(", arm);
            Assert.Contains("Lexicon.Get(\"audio.ptt.systemwide_held_too_long\")", arm);

            // Codex's words (2026-11-11). The old sentence said the key "was
            // held for 15 minutes", which this test pinned; that is untrue for
            // a key re-pressed inside the hold's bridge, which the watch still
            // counts from the earlier press (#1020, N1). The sentence now says
            // what the watch knows: the limit was reached and the key released.
            string said = Lexicon.Get("audio.ptt.systemwide_held_too_long");
            Assert.Equal("The push to talk time limit was reached. JJ Flexible Radio Access released its transmit key. "
                         + "Let go of the key before pressing it again.", said);
            Assert.DoesNotContain("was held for", said);
            Assert.DoesNotContain("not seen", said);
            Assert.DoesNotContain("{minutes}", said);
        }

        [Fact]
        public void A_missed_release_stays_silent()
        {
            string arm = Arm("UnkeyMissedRelease");
            Assert.DoesNotContain("Lexicon.Get(", arm);
            Assert.DoesNotContain("Speak", arm);
            Assert.DoesNotContain("KillTransmitNow", arm);
            Assert.Contains("SILENT ON PURPOSE", File.ReadAllText(
                Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "SystemWideKeys.cs")));
        }

        [Fact]
        public void The_shared_false_sentence_is_retired()
        {
            string src = File.ReadAllText(Path.Combine(CaptureMeterSetTests.RepoRoot(), "JJFlexWpf", "SystemWideKeys.cs"));
            Assert.DoesNotContain("systemwide_watchdog_unkeyed", src);
            Assert.DoesNotContain("\"audio.ptt.systemwide_watchdog_unkeyed\"", File.ReadAllText(
                Path.Combine(CaptureMeterSetTests.RepoRoot(), "Radios", "Lexicon", "audio.json")));
        }
    }
}
